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

    private SimulatedDevice CreateDevice(TimeProvider? time = null) =>
        new("pump-1", new StubPumpProcess(new ProcessState(55, 70)), Options, time ?? _time);

    private Task<List<TelemetryReading>> CollectAsync(ChannelReader<TelemetryReading> reader, int count) =>
        ReadingCollector.CollectAsync(_time, reader, count, Interval);

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

    [Fact]
    public void InjectingAnActiveFault_RestartsIt()
    {
        SimulatedDevice device = CreateDevice();

        device.InjectFault(DeviceFault.PressureSpike);
        _time.Advance(TimeSpan.FromSeconds(0.6));
        device.InjectFault(DeviceFault.PressureSpike);
        _time.Advance(TimeSpan.FromSeconds(0.6));
        TelemetryReading pastFirstWindow = device.TakeReading(); // 1.2s after the first injection, 0.6s after the second
        _time.Advance(TimeSpan.FromSeconds(0.4));
        TelemetryReading pastSecondWindow = device.TakeReading();

        Assert.True(pastFirstWindow.Pressure > new AlertOptions().PressureThreshold, $"pressure {pastFirstWindow.Pressure}");
        Assert.Equal(55, pastSecondWindow.Pressure);
    }

    [Theory]
    [InlineData(1)]  // clock stepped forward: must not end the fault early
    [InlineData(-1)] // clock stepped back: must not stretch the fault out
    public void SpikeAndOverheat_LastFaultDuration_WhateverTheWallClockDoes(int clockStepHours)
    {
        var clock = new ClockStepTimeProvider(_time);
        SimulatedDevice device = CreateDevice(clock);

        device.InjectFault(DeviceFault.PressureSpike);
        device.InjectFault(DeviceFault.Overheat);
        clock.WallClockOffset = TimeSpan.FromHours(clockStepHours);
        _time.Advance(TimeSpan.FromSeconds(0.9));
        TelemetryReading during = device.TakeReading();
        _time.Advance(TimeSpan.FromSeconds(0.1));
        TelemetryReading after = device.TakeReading();

        Assert.Equal((new AlertOptions().PressureThreshold + 5, new AlertOptions().TemperatureThreshold + 5), (during.Pressure, during.Temperature));
        Assert.Equal((55, 70), (after.Pressure, after.Temperature));
    }

    [Fact]
    public async Task Silence_IsNotCutShortByTheWallClockSteppingForward()
    {
        var clock = new ClockStepTimeProvider(_time);
        SimulatedDevice device = CreateDevice(clock);
        var channel = Channel.CreateUnbounded<TelemetryReading>();
        using var cts = new CancellationTokenSource();

        // 1s timeout + 1s margin = silent for 2s of real time, however far the clock jumps.
        device.InjectFault(DeviceFault.Silence, new AlertOptions { SilenceTimeout = TimeSpan.FromSeconds(1) });
        clock.WallClockOffset = TimeSpan.FromHours(1);
        Task run = device.RunAsync(channel.Writer, cts.Token);
        List<TelemetryReading> readings = await CollectAsync(channel.Reader, 1);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        TelemetryReading first = Assert.Single(readings);
        Assert.True(first.Timestamp >= _time.Start.AddHours(1).AddSeconds(2), $"first reading at {first.Timestamp:O}");
    }

    [Fact]
    public void InjectFault_RejectsUnknownFault()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDevice().InjectFault((DeviceFault)99));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    [InlineData(-1, 5)]
    public void Constructor_RejectsNonPositiveMargins(double pressureMargin, double temperatureMargin)
    {
        var options = new SimulatedDeviceOptions
        {
            FaultPressureMargin = pressureMargin,
            FaultTemperatureMargin = temperatureMargin,
        };

        Assert.Throws<ArgumentException>(() => new SimulatedDevice("pump-1", options: options));
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(-1, 1000)]
    [InlineData(1000, 0)]
    [InlineData(1000, -1)]
    public void Constructor_RejectsNonPositiveFaultDurationOrSilenceMargin(int durationMs, int silenceMarginMs)
    {
        var options = new SimulatedDeviceOptions
        {
            FaultDuration = TimeSpan.FromMilliseconds(durationMs),
            FaultSilenceMargin = TimeSpan.FromMilliseconds(silenceMarginMs),
        };

        Assert.Throws<ArgumentException>(() => new SimulatedDevice("pump-1", options: options));
    }
}
