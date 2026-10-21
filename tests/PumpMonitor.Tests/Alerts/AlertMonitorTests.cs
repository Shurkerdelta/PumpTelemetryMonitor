using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using PumpMonitor.Core.Alerts;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.Tests.Alerts;

public class AlertMonitorTests
{
    private readonly FakeTimeProvider _time = new();

    // The silence watch raises alerts from a timer thread, so collect them thread-safely.
    private readonly ConcurrentQueue<Alert> _alerts = new();

    private AlertMonitor CreateMonitor(AlertOptions? options = null, IEnumerable<string>? expected = null) =>
        new(_alerts.Enqueue, options, expected, _time);

    private static TelemetryReading Reading(long sequence, double pressure = 50, double temperature = 60, string deviceId = "pump-1") =>
        new(deviceId, DateTimeOffset.UnixEpoch, pressure, temperature, sequence);

    private static async Task Feed(AlertMonitor monitor, params TelemetryReading[] readings)
    {
        foreach (TelemetryReading reading in readings)
        {
            await monitor.HandleAsync(reading, CancellationToken.None);
        }
    }

    private AlertType[] Types() => _alerts.Select(a => a.Type).ToArray();

    [Fact]
    public async Task HighPressure_FiresOncePerExcursion_AndReArmsBelowDeadband()
    {
        var monitor = CreateMonitor(new AlertOptions { PressureThreshold = 60, PressureDeadband = 2 });

        await Feed(monitor,
            Reading(1, pressure: 59),
            Reading(2, pressure: 61),   // crosses: alert
            Reading(3, pressure: 65),   // still high: no repeat
            Reading(4, pressure: 59),   // below threshold but inside deadband: stays latched
            Reading(5, pressure: 61),   // so no new alert
            Reading(6, pressure: 57.5), // below 60 - 2: re-armed
            Reading(7, pressure: 62));  // crosses again: alert

        Assert.Equal([AlertType.HighPressure, AlertType.HighPressure], Types());
        Assert.Contains("61.00 MPa exceeds 60.00 MPa", _alerts.First().Message);
    }

    [Fact]
    public async Task HighTemperature_UsesItsOwnThreshold()
    {
        var monitor = CreateMonitor(new AlertOptions { TemperatureThreshold = 80 });

        await Feed(monitor, Reading(1, temperature: 79), Reading(2, temperature: 81));

        Alert alert = Assert.Single(_alerts);
        Assert.Equal(AlertType.HighTemperature, alert.Type);
    }

    [Fact]
    public async Task DefaultThresholds_DoNotFireForNormalRunningValues()
    {
        var monitor = CreateMonitor();

        await Feed(monitor, Reading(1, pressure: 55, temperature: 70), Reading(2, pressure: 68, temperature: 88));

        Assert.Empty(_alerts);
    }

    [Fact]
    public async Task ChangingOptionsAtRuntime_AppliesToLaterReadings()
    {
        var monitor = CreateMonitor();
        await Feed(monitor, Reading(1, pressure: 55));
        Assert.Empty(_alerts);

        monitor.Options = monitor.Options with { PressureThreshold = 50 };
        await Feed(monitor, Reading(2, pressure: 55));

        Assert.Equal([AlertType.HighPressure], Types());
    }

    [Fact]
    public void InvalidOptions_AreRejected()
    {
        var monitor = CreateMonitor();

        Assert.Throws<ArgumentException>(() => monitor.Options = new AlertOptions { SilenceTimeout = TimeSpan.Zero });
        Assert.Throws<ArgumentException>(() => CreateMonitor(new AlertOptions { PressureDeadband = -1 }));
    }

    [Fact]
    public async Task OutOfOrderAndDuplicateSequences_AreReportedPerDevice()
    {
        var monitor = CreateMonitor();

        await Feed(monitor,
            Reading(1),
            Reading(2),
            Reading(4),                     // gap is fine (e.g. dropped reading)
            Reading(3),                     // older than #4: out of order
            Reading(4),                     // duplicate of #4: out of order
            Reading(5),                     // still in order: #3/#4 didn't move the high-water mark back
            Reading(1, deviceId: "pump-2")); // other devices track their own sequence

        Assert.Equal([AlertType.OutOfOrderSequence, AlertType.OutOfOrderSequence], Types());
        Assert.Equal("Sequence #3 arrived after #4", _alerts.First().Message);
    }

    [Fact]
    public async Task SilentDevice_AlertsOnceAfterTimeout_AndReArmsWhenHeardFrom()
    {
        var monitor = CreateMonitor(new AlertOptions { SilenceTimeout = TimeSpan.FromSeconds(5) });
        await Feed(monitor, Reading(1));

        _time.Advance(TimeSpan.FromSeconds(5));
        monitor.CheckForSilentDevices();
        Assert.Empty(_alerts); // exactly at the limit is not "more than"

        _time.Advance(TimeSpan.FromMilliseconds(1));
        monitor.CheckForSilentDevices();
        monitor.CheckForSilentDevices();
        Assert.Equal([AlertType.DeviceSilent], Types()); // reported once, not on every check

        await Feed(monitor, Reading(2));
        _time.Advance(TimeSpan.FromSeconds(6));
        monitor.CheckForSilentDevices();
        Assert.Equal([AlertType.DeviceSilent, AlertType.DeviceSilent], Types());
    }

    [Fact]
    public void ExpectedDevice_ThatNeverReports_IsFlaggedSilent()
    {
        var monitor = CreateMonitor(expected: ["pump-1", "pump-2"]);

        _time.Advance(TimeSpan.FromSeconds(6));
        monitor.CheckForSilentDevices();

        Assert.Equal(["pump-1", "pump-2"], _alerts.Select(a => a.DeviceId).Order());
    }

    [Fact]
    public async Task SilenceWatch_RaisesAlertFromTimerWhileReadingsArriveConcurrently()
    {
        var monitor = CreateMonitor(expected: ["pump-1", "pump-2"]);
        using var cts = new CancellationTokenSource();
        Task watch = monitor.RunSilenceWatchAsync(TimeSpan.FromMilliseconds(500), cts.Token);

        // pump-1 keeps reporting from another thread; pump-2 never does.
        for (long seq = 1; seq <= 14; seq++)
        {
            await Task.Run(() => monitor.HandleAsync(Reading(seq), CancellationToken.None).AsTask());
            _time.Advance(TimeSpan.FromMilliseconds(500));
        }

        // Timer callbacks run on the thread pool; wait briefly for the alert to land.
        for (int i = 0; i < 100 && _alerts.IsEmpty; i++)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await watch.WaitAsync(TimeSpan.FromSeconds(5));

        Alert alert = Assert.Single(_alerts);
        Assert.Equal(("pump-2", AlertType.DeviceSilent), (alert.DeviceId, alert.Type));
        Assert.Equal(TaskStatus.RanToCompletion, watch.Status);
    }
}
