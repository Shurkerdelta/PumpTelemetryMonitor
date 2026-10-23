using PumpMonitor.Core.Alerts;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.Tests.Alerts;

public class AlertMonitorTests
{
    private readonly List<Alert> _alerts = [];

    private AlertMonitor CreateMonitor(AlertOptions? options = null) => new(_alerts.Add, options);

    private static TelemetryReading Reading(long sequence, double pressure = 50, double temperature = 60, string deviceId = "pump-1") =>
        new(deviceId, DateTimeOffset.UnixEpoch, pressure, temperature, sequence);

    private static async Task Feed(AlertMonitor monitor, params TelemetryReading[] readings)
    {
        foreach (TelemetryReading reading in readings)
        {
            await monitor.HandleAsync(reading, CancellationToken.None);
        }
    }

    private AlertType[] Types() => [.. _alerts.Select(a => a.Type)];

    [Fact]
    public async Task HighPressure_AlertsOnEveryReadingAboveThreshold()
    {
        var monitor = CreateMonitor(new AlertOptions { PressureThreshold = 60 });

        await Feed(monitor,
            Reading(1, pressure: 59),
            Reading(2, pressure: 61), // above: alert
            Reading(3, pressure: 65), // still above: alerts again
            Reading(4, pressure: 60), // exactly at the threshold is not above
            Reading(5, pressure: 62)); // above again: alert

        Assert.All(_alerts, a => Assert.Equal(AlertType.HighPressure, a.Type));
        Assert.Equal(
            ["61.00 MPa exceeds 60.00 MPa", "65.00 MPa exceeds 60.00 MPa", "62.00 MPa exceeds 60.00 MPa"],
            _alerts.Select(a => a.Message.Replace("Pressure ", "")));
    }

    [Fact]
    public async Task HighTemperature_AlertsOnEveryReadingAboveThreshold()
    {
        var monitor = CreateMonitor(new AlertOptions { TemperatureThreshold = 80 });

        await Feed(monitor, Reading(1, temperature: 81), Reading(2, temperature: 85), Reading(3, temperature: 79));

        Assert.Equal([AlertType.HighTemperature, AlertType.HighTemperature], Types());
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
    public void InvalidOptions_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => CreateMonitor(new AlertOptions { SilenceTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => CreateMonitor(new AlertOptions { SilenceTimeout = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentException>(() => CreateMonitor(new AlertOptions { PressureThreshold = double.NaN }));
        Assert.Throws<ArgumentException>(() => CreateMonitor(new AlertOptions { TemperatureThreshold = double.PositiveInfinity }));
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
        Assert.Equal("Sequence #3 arrived after #4", _alerts[0].Message);
    }
}
