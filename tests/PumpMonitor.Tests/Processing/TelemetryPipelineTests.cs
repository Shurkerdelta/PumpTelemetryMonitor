using PumpMonitor.Core.Processing;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.Tests.Processing;

public class TelemetryPipelineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static TelemetryReading Reading(string deviceId, long sequence) =>
        new(deviceId, DateTimeOffset.UnixEpoch, 100, 50, sequence);

    /// <summary>Records every reading. Only read it after the pipeline has stopped.</summary>
    private sealed class RecordingHandler : ITelemetryHandler
    {
        public List<TelemetryReading> Readings { get; } = [];

        public ValueTask HandleAsync(TelemetryReading reading, CancellationToken cancellationToken)
        {
            Readings.Add(reading);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Blocks inside the first reading until released, simulating a stalled handler.</summary>
    private sealed class GatedHandler : ITelemetryHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SawCancellation { get; private set; }

        public void Release() => _release.TrySetResult();

        public async ValueTask HandleAsync(TelemetryReading reading, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }
        }
    }

    private sealed class ThrowingHandler(long failOnSequence) : ITelemetryHandler
    {
        public ValueTask HandleAsync(TelemetryReading reading, CancellationToken cancellationToken) =>
            reading.SequenceNumber == failOnSequence
                ? throw new InvalidOperationException("boom")
                : ValueTask.CompletedTask;
    }

    private static readonly Action<TelemetryReading, Exception> FailOnHandlerError =
        (reading, ex) => Assert.Fail($"Handler failed on {reading}: {ex}");

    [Fact]
    public async Task ConcurrentProducers_AllReadingsDeliveredInPerDeviceOrder()
    {
        var recorder = new RecordingHandler();
        await using var pipeline = new TelemetryPipeline([recorder], FailOnHandlerError, capacity: 10_000);
        pipeline.Start();

        const int perDevice = 500;
        Task[] producers = Enumerable.Range(1, 4).Select(d => Task.Run(async () =>
        {
            for (long seq = 1; seq <= perDevice; seq++)
            {
                await pipeline.Writer.WriteAsync(Reading($"pump-{d}", seq));
            }
        })).ToArray();
        await Task.WhenAll(producers).WaitAsync(Timeout);
        await pipeline.StopAsync().WaitAsync(Timeout);

        Assert.Equal(4 * perDevice, recorder.Readings.Count);
        foreach (IGrouping<string, TelemetryReading> device in recorder.Readings.GroupBy(r => r.DeviceId))
        {
            Assert.Equal(Enumerable.Range(1, perDevice).Select(i => (long)i), device.Select(r => r.SequenceNumber));
        }
    }

    [Fact]
    public async Task StalledHandler_NeverBlocksProducers_DropsOldestWhenFull()
    {
        var gate = new GatedHandler();
        var recorder = new RecordingHandler();
        await using var pipeline = new TelemetryPipeline([gate, recorder], FailOnHandlerError, capacity: 10);
        pipeline.Start();

        // Reading 1 is taken off the queue and stuck in the gated handler...
        await pipeline.Writer.WriteAsync(Reading("pump-1", 1));
        await gate.Entered.Task.WaitAsync(Timeout);

        // ...so 15 more overflow the 10-slot queue. Every write must still complete immediately.
        for (long seq = 2; seq <= 16; seq++)
        {
            ValueTask write = pipeline.Writer.WriteAsync(Reading("pump-1", seq));
            Assert.True(write.IsCompletedSuccessfully, $"write {seq} blocked");
        }

        gate.Release();
        await pipeline.StopAsync().WaitAsync(Timeout);

        Assert.Equal(5, pipeline.DroppedCount);
        long[] expected = [1, .. Enumerable.Range(7, 10).Select(i => (long)i)];
        Assert.Equal(expected, recorder.Readings.Select(r => r.SequenceNumber));
    }

    [Fact]
    public async Task StopAsync_DrainsQueuedReadingsAndRejectsNewOnes()
    {
        var gate = new GatedHandler();
        var recorder = new RecordingHandler();
        await using var pipeline = new TelemetryPipeline([gate, recorder], FailOnHandlerError);
        pipeline.Start();
        for (long seq = 1; seq <= 5; seq++)
        {
            await pipeline.Writer.WriteAsync(Reading("pump-1", seq));
        }

        await gate.Entered.Task.WaitAsync(Timeout);
        Task stop = pipeline.StopAsync();

        Assert.False(stop.IsCompleted, "stop should wait for queued readings");
        Assert.False(pipeline.Writer.TryWrite(Reading("pump-1", 6)));

        gate.Release();
        await stop.WaitAsync(Timeout);
        Assert.Equal([1L, 2, 3, 4, 5], recorder.Readings.Select(r => r.SequenceNumber));
    }

    [Fact]
    public async Task StopAsync_WhenCancelled_AbandonsRemainingReadings()
    {
        var gate = new GatedHandler();
        var recorder = new RecordingHandler();
        await using var pipeline = new TelemetryPipeline([gate, recorder], FailOnHandlerError);
        pipeline.Start();
        for (long seq = 1; seq <= 5; seq++)
        {
            await pipeline.Writer.WriteAsync(Reading("pump-1", seq));
        }

        await gate.Entered.Task.WaitAsync(Timeout);
        await pipeline.StopAsync(new CancellationToken(canceled: true)).WaitAsync(Timeout);

        Assert.True(gate.SawCancellation);
        Assert.Empty(recorder.Readings);
    }

    [Fact]
    public async Task ThrowingHandler_IsReported_AndProcessingContinues()
    {
        var errors = new List<(TelemetryReading Reading, Exception Error)>();
        var recorder = new RecordingHandler();
        await using var pipeline = new TelemetryPipeline(
            [new ThrowingHandler(failOnSequence: 2), recorder],
            (reading, ex) => errors.Add((reading, ex)));
        pipeline.Start();
        for (long seq = 1; seq <= 3; seq++)
        {
            await pipeline.Writer.WriteAsync(Reading("pump-1", seq));
        }

        await pipeline.StopAsync().WaitAsync(Timeout);

        (TelemetryReading reading, Exception error) = Assert.Single(errors);
        Assert.Equal(2, reading.SequenceNumber);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal([1L, 2, 3], recorder.Readings.Select(r => r.SequenceNumber));
    }

    [Fact]
    public async Task ReadingsWrittenBeforeStart_AreProcessed()
    {
        var recorder = new RecordingHandler();
        await using var pipeline = new TelemetryPipeline([recorder], FailOnHandlerError);
        await pipeline.Writer.WriteAsync(Reading("pump-1", 1));

        pipeline.Start();
        await pipeline.StopAsync().WaitAsync(Timeout);

        Assert.Single(recorder.Readings);
        Assert.Throws<InvalidOperationException>(pipeline.Start);
    }
}
