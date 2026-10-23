using PumpMonitor.Core.Processing;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.Core.Alerts;

/// <summary>
/// Detects abnormal readings: pressure or temperature above a threshold or not a finite number, and
/// out-of-order sequence numbers.
/// </summary>
/// <remarks>
/// <para>
/// High pressure and high temperature alert on every reading above the threshold, so consecutive high
/// readings each raise an alert. Out-of-order alerts fire per offending reading. A reading can raise several
/// alerts; all of them are delivered even if the callback throws for one.
/// </para>
/// <para>
/// A NaN or infinite value raises <see cref="AlertType.InvalidReading"/> instead of being checked against its
/// threshold: no NaN is ever above a threshold, so a failed sensor would otherwise pass silently, and an
/// infinite value is a fault, not a real pressure or temperature.
/// </para>
/// <para>
/// A device that stops reporting sends no readings to check, so silence is detected separately by
/// <see cref="SilenceMonitor"/>.
/// </para>
/// </remarks>
public sealed class AlertMonitor : ITelemetryHandler
{
    private readonly Action<Alert> _onAlert;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, long> _lastSequence = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="AlertMonitor"/> class.
    /// </summary>
    /// <param name="onAlert">Receives each alert, on the thread calling <see cref="HandleAsync"/>.</param>
    /// <param name="options">Thresholds; defaults to a new <see cref="AlertOptions"/>.</param>
    /// <param name="timeProvider">Clock for alert timestamps; tests pass a fake.</param>
    public AlertMonitor(Action<Alert> onAlert, AlertOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(onAlert);
        _onAlert = onAlert;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Options = options ?? new AlertOptions();
        Options.Validate();
    }

    public AlertOptions Options { get; }

    /// <exception cref="AggregateException">The alert callback threw. Every other alert was still delivered.</exception>
    public ValueTask HandleAsync(TelemetryReading reading, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<Alert> alerts = [];

        long lastSequence = _lastSequence.GetValueOrDefault(reading.DeviceId);
        if (reading.SequenceNumber <= lastSequence)
        {
            alerts.Add(new Alert(reading.DeviceId, AlertType.OutOfOrderSequence,
                $"Sequence #{reading.SequenceNumber} arrived after #{lastSequence}", now));
        }
        else
        {
            _lastSequence[reading.DeviceId] = reading.SequenceNumber;
        }

        if (!double.IsFinite(reading.Pressure))
        {
            alerts.Add(InvalidReading(reading.DeviceId, "Pressure", reading.Pressure, now));
        }
        else if (reading.Pressure > Options.PressureThreshold)
        {
            alerts.Add(new Alert(reading.DeviceId, AlertType.HighPressure,
                $"Pressure {reading.Pressure:F2} MPa exceeds {Options.PressureThreshold:F2} MPa", now));
        }

        if (!double.IsFinite(reading.Temperature))
        {
            alerts.Add(InvalidReading(reading.DeviceId, "Temperature", reading.Temperature, now));
        }
        else if (reading.Temperature > Options.TemperatureThreshold)
        {
            alerts.Add(new Alert(reading.DeviceId, AlertType.HighTemperature,
                $"Temperature {reading.Temperature:F1} °C exceeds {Options.TemperatureThreshold:F1} °C", now));
        }

        // The reading is gone once this returns, so an alert skipped because an earlier one threw would never be raised.
        AlertDelivery.DeliverAll(_onAlert, alerts);
        return ValueTask.CompletedTask;
    }

    private static Alert InvalidReading(string deviceId, string quantity, double value, DateTimeOffset now) =>
        new(deviceId, AlertType.InvalidReading, $"{quantity} reading {value} is not a finite number", now);
}
