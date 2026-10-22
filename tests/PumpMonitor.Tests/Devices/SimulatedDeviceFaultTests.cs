using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using PumpMonitor.Core.Alerts;
using PumpMonitor.Core.Devices;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.Tests.Devices;

public class SimulatedDeviceFaultTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    private static readonly SimulatedDeviceOptions Options = new()
    {
        MinInterval = Interval,
        MaxInterval = Interval,
        FaultPressureMargin = 5,
        FaultTemperatureMargin = 5,
        FaultSilenceMargin = TimeSpan.FromSeconds(1),
        FaultDuration = TimeSpan.FromSeconds(1),
    };

    private readonly FakeTimeProvider _time = new();

    private SimulatedDevice CreateDevice() =>
        new("pump-1", new StubPumpProcess(new ProcessState(55, 70)), Options, _time);

    /// <summary>
    /// Advances fake time in interval steps until <paramref name="count"/> readings arrive. Delay continuations
    /// run on the thread pool, so each step waits briefly for the device loop to catch up; overshooting is harmless.
    /// </summary>
    private async Task<List<TelemetryReading>> CollectAsync(ChannelReader<TelemetryReading> reader, int count)
    {
        var readings = new List<TelemetryReading>();
        for (int step = 0; step < 200 && readings.Count < count; step++)
        {
            while (readings.Count < count && reader.TryRead(out TelemetryReading? reading))
            {
                readings.Add(reading);
            }

            if (readings.Count < count)
            {
                _time.Advance(Interval);
                await Task.Delay(5);
            }
        }

        return readings;
    }

    [Theory]
    [InlineData(69, 74)] // threshold above actual (55): reads threshold + margin
    [InlineData(20, 60)] // threshold below actual: reads actual + margin, still above both
    public void PressureSpike_ReadsAboveThresholdUntilFaultDurationEnds(double threshold, double expected)
    {
        SimulatedDevice device = CreateDevice();

        device.InjectFault(DeviceFault.PressureSpike, new AlertOptions { PressureThreshold = threshold });
        TelemetryReading during = device.TakeReading();
        _time.Advance(TimeSpan.FromSeconds(1));
        TelemetryReading after = device.TakeReading();

        Assert.Equal((expected, 70), (during.Pressure, during.Temperature));
        Assert.Equal((55, 70), (after.Pressure, after.Temperature));
    }

    [Theory]
    [InlineData(90, 95)]
    [InlineData(40, 75)]
    public void Overheat_ReadsAboveThresholdUntilFaultDurationEnds(double threshold, double expected)
    {
        SimulatedDevice device = CreateDevice();

        device.InjectFault(DeviceFault.Overheat, new AlertOptions { TemperatureThreshold = threshold });
        TelemetryReading during = device.TakeReading();
        _time.Advance(TimeSpan.FromSeconds(1));
        TelemetryReading after = device.TakeReading();

        Assert.Equal((55, expected), (during.Pressure, during.Temperature));
        Assert.Equal((55, 70), (after.Pressure, after.Temperature));
    }

    [Fact]
    public void InjectFault_WithoutLimits_UsesDefaultAlertOptions()
    {
        SimulatedDevice device = CreateDevice();

        device.InjectFault(DeviceFault.PressureSpike);

        Assert.Equal(new AlertOptions().PressureThreshold + 5, device.TakeReading().Pressure);
    }

    [Fact]
    public async Task Silence_LastsSilenceTimeoutPlusMargin_ThenResumesWithoutSkippingSequence()
    {
        SimulatedDevice device = CreateDevice();
        var channel = Channel.CreateUnbounded<TelemetryReading>();
        using var cts = new CancellationTokenSource();

        // 1s timeout + 1s margin = silent for 2s.
        device.InjectFault(DeviceFault.Silence, new AlertOptions { SilenceTimeout = TimeSpan.FromSeconds(1) });
        Task run = device.RunAsync(channel.Writer, cts.Token);
        List<TelemetryReading> readings = await CollectAsync(channel.Reader, 2);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([1L, 2], readings.Select(r => r.SequenceNumber));
        Assert.True(readings[0].Timestamp >= _time.Start.AddSeconds(2), $"first reading at {readings[0].Timestamp:O}");
    }

    [Fact]
    public async Task Reorder_SwapsTheNextTwoReadings_ThenCarriesOnInOrder()
    {
        SimulatedDevice device = CreateDevice();
        var channel = Channel.CreateUnbounded<TelemetryReading>();
        using var cts = new CancellationTokenSource();

        device.InjectFault(DeviceFault.Reorder);
        Task run = device.RunAsync(channel.Writer, cts.Token);
        List<TelemetryReading> readings = await CollectAsync(channel.Reader, 4);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([2L, 1, 3, 4], readings.Select(r => r.SequenceNumber));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    public void Constructor_RejectsNonPositiveMargins(double pressureMargin, double temperatureMargin)
    {
        var options = new SimulatedDeviceOptions
        {
            FaultPressureMargin = pressureMargin,
            FaultTemperatureMargin = temperatureMargin,
        };

        Assert.Throws<ArgumentException>(() => new SimulatedDevice("pump-1", options: options));
    }
}
