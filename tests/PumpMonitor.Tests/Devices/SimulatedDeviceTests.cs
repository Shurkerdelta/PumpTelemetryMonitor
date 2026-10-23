using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using PumpMonitor.Core.Devices;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.Tests.Devices;

public class SimulatedDeviceTests
{
    private static readonly SimulatedDeviceOptions FixedRate = new()
    {
        MinInterval = TimeSpan.FromMilliseconds(200),
        MaxInterval = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public void TakeReading_ReportsStubbedValuesWithIncreasingSequenceAndTimestamps()
    {
        var time = new FakeTimeProvider();
        var process = new StubPumpProcess(new ProcessState(100, 50), new ProcessState(250, 51));
        var device = new SimulatedDevice("pump-1", process, FixedRate, time);

        TelemetryReading first = device.TakeReading();
        time.Advance(TimeSpan.FromMilliseconds(200));
        TelemetryReading second = device.TakeReading();

        Assert.Equal(new TelemetryReading("pump-1", time.Start, 100, 50, 1), first);
        Assert.Equal(new TelemetryReading("pump-1", time.Start.AddMilliseconds(200), 250, 51, 2), second);
    }

    [Fact]
    public void TakeReading_PassesOnOffStateAndElapsedTimeToProcess()
    {
        var time = new FakeTimeProvider();
        var process = new StubPumpProcess(new ProcessState(0, 20));
        var device = new SimulatedDevice("pump-1", process, FixedRate with { StartOn = false }, time);

        device.TakeReading();
        device.TurnOn();
        time.Advance(TimeSpan.FromMilliseconds(300));
        device.TakeReading();
        device.TurnOff();
        time.Advance(TimeSpan.FromMilliseconds(150));
        device.TakeReading();

        Assert.Equal(
            [
                (false, TimeSpan.Zero),
                (true, TimeSpan.FromMilliseconds(300)),
                (false, TimeSpan.FromMilliseconds(150)),
            ],
            process.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void TakeReading_TimestampsFollowTheWallClock_ButElapsedTimeIgnoresClockSteps(int clockStepHours)
    {
        var time = new FakeTimeProvider();
        var clock = new ClockStepTimeProvider(time);
        var process = new StubPumpProcess(new ProcessState(0, 20));
        var device = new SimulatedDevice("pump-1", process, FixedRate, clock);

        device.TakeReading();
        clock.WallClockOffset = TimeSpan.FromHours(clockStepHours);
        time.Advance(TimeSpan.FromMilliseconds(200));
        TelemetryReading second = device.TakeReading();

        // A stepped clock fed to the process would look like an hour passed (or time ran backwards).
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromMilliseconds(200)], process.Calls.Select(c => c.Elapsed));
        Assert.Equal(time.Start.AddHours(clockStepHours).AddMilliseconds(200), second.Timestamp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsMissingId(string? deviceId)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SimulatedDevice(deviceId!));
    }

    [Fact]
    public void StartOn_SetsInitialState_AndTurnOnOffSwitchIt()
    {
        var device = new SimulatedDevice("pump-1", options: FixedRate with { StartOn = false });
        Assert.False(device.IsOn);

        device.TurnOn();
        Assert.True(device.IsOn);

        device.TurnOff();
        Assert.False(device.IsOn);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-100, 100)]
    [InlineData(300, 200)]
    public void Constructor_RejectsInvalidIntervals(int minMs, int maxMs)
    {
        var options = new SimulatedDeviceOptions
        {
            MinInterval = TimeSpan.FromMilliseconds(minMs),
            MaxInterval = TimeSpan.FromMilliseconds(maxMs),
        };

        Assert.Throws<ArgumentException>(() => new SimulatedDevice("pump-1", options: options));
    }

    [Fact]
    public async Task RunAsync_StopsCleanlyWhenCancelled()
    {
        var time = new FakeTimeProvider();
        var channel = Channel.CreateUnbounded<TelemetryReading>();
        var device = new SimulatedDevice("pump-1", new StubPumpProcess(new ProcessState(1, 1)), FixedRate, time);
        using var cts = new CancellationTokenSource();

        Task run = device.RunAsync(channel.Writer, cts.Token);
        TelemetryReading first = await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, first.SequenceNumber);
        Assert.Equal(TaskStatus.RanToCompletion, run.Status);
    }

    [Fact]
    public async Task RunAsync_SpacesReadingsRandomlyBetweenMinAndMaxInterval()
    {
        var time = new FakeTimeProvider();
        var channel = Channel.CreateUnbounded<TelemetryReading>();
        var options = new SimulatedDeviceOptions { MinInterval = TimeSpan.FromMilliseconds(100), MaxInterval = TimeSpan.FromMilliseconds(500) };
        var device = new SimulatedDevice("pump-1", new StubPumpProcess(new ProcessState(1, 1)), options, time, new Random(3));
        using var cts = new CancellationTokenSource();

        Task run = device.RunAsync(channel.Writer, cts.Token);
        List<TelemetryReading> readings = await ReadingCollector.CollectAsync(time, channel.Reader, 6, TimeSpan.FromMilliseconds(20));
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        // Timestamps land on 20 ms steps, and a busy thread pool can lag a step or two, so the upper bound has slack.
        TimeSpan[] gaps = [.. readings.Zip(readings.Skip(1), (a, b) => b.Timestamp - a.Timestamp)];
        Assert.Equal(5, gaps.Length);
        Assert.All(gaps, g => Assert.InRange(g, options.MinInterval, options.MaxInterval + TimeSpan.FromMilliseconds(200)));
        Assert.True(gaps.Distinct().Count() > 1, $"gaps should vary: {string.Join(", ", gaps)}");
    }

    [Fact]
    public async Task RunAsync_ConcurrentDevicesSharingAChannel_EachEmitContiguousOrderedSequences()
    {
        var time = new FakeTimeProvider();
        var channel = Channel.CreateUnbounded<TelemetryReading>();
        SimulatedDevice[] devices = [.. Enumerable.Range(1, 4)
            .Select(i => new SimulatedDevice($"pump-{i}", timeProvider: time, random: new Random(i)))];
        using var cts = new CancellationTokenSource();

        Task[] runs = [.. devices.Select(d => d.RunAsync(channel.Writer, cts.Token))];
        for (int step = 0; step < 40; step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            // Delay continuations run on the thread pool; give them a moment to write.
            await Task.Delay(5);
        }

        cts.Cancel();
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(5));
        channel.Writer.Complete();
        List<TelemetryReading> readings = await channel.Reader.ReadAllAsync().ToListAsync();

        foreach (SimulatedDevice device in devices)
        {
            long[] sequence = [.. readings.Where(r => r.DeviceId == device.DeviceId).Select(r => r.SequenceNumber)];
            Assert.True(sequence.Length > 1, $"{device.DeviceId} emitted {sequence.Length} readings");
            Assert.Equal(Enumerable.Range(1, sequence.Length).Select(i => (long)i), sequence);
        }
    }
}
