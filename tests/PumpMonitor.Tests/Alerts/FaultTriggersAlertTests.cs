using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using PumpMonitor.Core.Alerts;
using PumpMonitor.Core.Devices;
using PumpMonitor.Core.Telemetry;
using PumpMonitor.Tests.Devices;

namespace PumpMonitor.Tests.Alerts;

/// <summary>
/// End-to-end: each injected fault, aimed at the alert options, must make the monitors raise the
/// matching alert, across a range of threshold settings.
/// </summary>
public class FaultTriggersAlertTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    private readonly FakeTimeProvider _time = new();
    private readonly ConcurrentQueue<Alert> _alerts = new();

    private (SimulatedDevice Device, AlertMonitor Monitor) Create(AlertOptions options)
    {
        var device = new SimulatedDevice(
            "pump-1",
            new StubPumpProcess(new ProcessState(55, 70)),
            new SimulatedDeviceOptions { MinInterval = Interval, MaxInterval = Interval },
            _time);
        var monitor = new AlertMonitor(_alerts.Enqueue, options, _time);
        return (device, monitor);
    }

    public static TheoryData<double, double> Thresholds => new()
    {
        { 69, 90 },  // defaults: well above the pump's normal 55 MPa / 70 °C
        { 56, 71 },  // just above normal
        { 300, 500 }, // far above anything the pump could reach
    };

    [Theory]
    [MemberData(nameof(Thresholds))]
    public async Task PressureSpike_RaisesHighPressure(double pressureThreshold, double temperatureThreshold)
    {
        var options = new AlertOptions { PressureThreshold = pressureThreshold, TemperatureThreshold = temperatureThreshold };
        (SimulatedDevice device, AlertMonitor monitor) = Create(options);
        await monitor.HandleAsync(device.TakeReading(), CancellationToken.None);

        device.InjectFault(DeviceFault.PressureSpike, monitor.Options);
        await monitor.HandleAsync(device.TakeReading(), CancellationToken.None);

        Assert.Equal([AlertType.HighPressure], _alerts.Select(a => a.Type));
    }

    [Theory]
    [MemberData(nameof(Thresholds))]
    public async Task Overheat_RaisesHighTemperature(double pressureThreshold, double temperatureThreshold)
    {
        var options = new AlertOptions { PressureThreshold = pressureThreshold, TemperatureThreshold = temperatureThreshold };
        (SimulatedDevice device, AlertMonitor monitor) = Create(options);
        await monitor.HandleAsync(device.TakeReading(), CancellationToken.None);

        device.InjectFault(DeviceFault.Overheat, monitor.Options);
        await monitor.HandleAsync(device.TakeReading(), CancellationToken.None);

        Assert.Equal([AlertType.HighTemperature], _alerts.Select(a => a.Type));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(12)]
    public async Task Silence_RaisesDeviceSilentBeforeReadingsResume(int timeoutSeconds)
    {
        var options = new AlertOptions { SilenceTimeout = TimeSpan.FromSeconds(timeoutSeconds) };
        (SimulatedDevice device, _) = Create(options);
        var silence = new SilenceMonitor(_alerts.Enqueue, options, ["pump-1"], _time);
        var channel = Channel.CreateUnbounded<TelemetryReading>();
        using var cts = new CancellationTokenSource();

        device.InjectFault(DeviceFault.Silence, options);
        Task run = device.RunAsync(silence.Track(channel.Writer), cts.Token);

        // Step time like the app does (check every 500ms) until the device speaks again.
        bool silentAlertBeforeResume = false;
        for (int step = 0; step < 400; step++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(2);
            if (channel.Reader.TryRead(out _))
            {
                break;
            }

            silence.CheckForSilentDevices();
            silentAlertBeforeResume |= _alerts.Any(a => a.Type == AlertType.DeviceSilent);
        }

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(silentAlertBeforeResume, "device resumed before the silence alert fired");
    }

    [Fact]
    public async Task Reorder_RaisesOutOfOrderSequence()
    {
        (SimulatedDevice device, AlertMonitor monitor) = Create(new AlertOptions());
        var channel = Channel.CreateUnbounded<TelemetryReading>();
        using var cts = new CancellationTokenSource();

        device.InjectFault(DeviceFault.Reorder, monitor.Options);
        Task run = device.RunAsync(channel.Writer, cts.Token);
        for (int handled = 0, step = 0; handled < 2 && step < 200; step++)
        {
            while (handled < 2 && channel.Reader.TryRead(out TelemetryReading? reading))
            {
                await monitor.HandleAsync(reading, CancellationToken.None);
                handled++;
            }

            _time.Advance(Interval);
            await Task.Delay(5);
        }

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([AlertType.OutOfOrderSequence], _alerts.Select(a => a.Type));
    }
}
