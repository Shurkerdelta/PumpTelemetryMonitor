using Microsoft.Extensions.Time.Testing;
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

        // Messages use the current culture's number format (e.g. "61,00" in fr-CA), so build the expectation the same way.
        Assert.All(_alerts, a => Assert.Equal(AlertType.HighPressure, a.Type));
        Assert.Equal(
            [$"{61.0:F2} MPa exceeds {60.0:F2} MPa", $"{65.0:F2} MPa exceeds {60.0:F2} MPa", $"{62.0:F2} MPa exceeds {60.0:F2} MPa"],
            _alerts.Select(a => a.Message.Replace("Pressure ", "")));
    }

    [Fact]
    public async Task Alerts_NameTheDevice_AreStampedWithDetectionTime_AndDescribeTheValue()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var monitor = new AlertMonitor(_alerts.Add, new AlertOptions { PressureThreshold = 60, TemperatureThreshold = 80 }, time);

        // The reading's own timestamp is deliberately different: alerts record when the condition was detected.
        await Feed(monitor, Reading(1, pressure: 61.239, temperature: 85.04, deviceId: "pump-7"));

        Assert.Equal(
            [
                new Alert("pump-7", AlertType.HighPressure, $"Pressure {61.24:F2} MPa exceeds {60.0:F2} MPa", time.GetUtcNow()),
                new Alert("pump-7", AlertType.HighTemperature, $"Temperature {85.0:F1} °C exceeds {80.0:F1} °C", time.GetUtcNow()),
            ],
            _alerts);
    }

    [Fact]
    public async Task OneReading_CanRaiseSeveralAlerts()
    {
        var monitor = CreateMonitor();

        await Feed(monitor, Reading(2), Reading(1, pressure: 100, temperature: 100));

        Assert.Equal([AlertType.OutOfOrderSequence, AlertType.HighPressure, AlertType.HighTemperature], Types());
    }

    [Theory]
    [InlineData(double.NaN, 60, "Pressure")]
    [InlineData(double.PositiveInfinity, 60, "Pressure")] // a fault, not a high pressure
    [InlineData(double.NegativeInfinity, 60, "Pressure")]
    [InlineData(50, double.NaN, "Temperature")]
    [InlineData(50, double.PositiveInfinity, "Temperature")]
    [InlineData(50, double.NegativeInfinity, "Temperature")]
    public async Task NonFiniteValue_RaisesInvalidReading_InsteadOfTheThresholdCheck(double pressure, double temperature, string quantity)
    {
        var time = new FakeTimeProvider();
        var monitor = new AlertMonitor(_alerts.Add, timeProvider: time);

        await Feed(monitor, Reading(1, pressure, temperature, deviceId: "pump-3"));

        double value = quantity == "Pressure" ? pressure : temperature;
        Assert.Equal(
            [new Alert("pump-3", AlertType.InvalidReading, $"{quantity} reading {value} is not a finite number", time.GetUtcNow())],
            _alerts);
    }

    [Fact]
    public async Task InvalidValue_DoesNotHideOtherAlerts_AndItsReadingStillCountsForSequencing()
    {
        var monitor = CreateMonitor();

        await Feed(monitor,
            Reading(1, pressure: double.NaN, temperature: 100), // bad pressure sensor, temperature still checked
            Reading(2, pressure: double.NaN, temperature: double.NaN),
            Reading(2));                                        // duplicate of the invalid reading's sequence number

        Assert.Equal(
            [
                AlertType.InvalidReading, AlertType.HighTemperature,
                AlertType.InvalidReading, AlertType.InvalidReading,
                AlertType.OutOfOrderSequence,
            ],
            Types());
    }

    [Fact]
    public void Constructor_RequiresCallback_AndDefaultsOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new AlertMonitor(null!));
        Assert.Equal(new AlertOptions(), CreateMonitor().Options);
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
        Assert.Throws<ArgumentException>(() => CreateMonitor(new AlertOptions { PressureThreshold = double.NegativeInfinity }));
        Assert.Throws<ArgumentException>(() => CreateMonitor(new AlertOptions { TemperatureThreshold = double.PositiveInfinity }));
        Assert.Throws<ArgumentException>(() => CreateMonitor(new AlertOptions { TemperatureThreshold = double.NaN }));
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
