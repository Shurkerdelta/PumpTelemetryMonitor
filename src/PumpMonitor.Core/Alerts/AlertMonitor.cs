using PumpMonitor.Core.Processing;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.Core.Alerts;

/// <summary>
/// Detects abnormal readings: pressure or temperature above a threshold, and out-of-order sequence numbers.
/// </summary>
/// <remarks>
/// <para>
/// High pressure and high temperature alert on every reading above the threshold, so consecutive high
/// readings each raise an alert. Out-of-order alerts fire per offending reading.
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

    public ValueTask HandleAsync(TelemetryReading reading, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        long lastSequence = _lastSequence.GetValueOrDefault(reading.DeviceId);
        if (reading.SequenceNumber <= lastSequence)
        {
            _onAlert(new Alert(reading.DeviceId, AlertType.OutOfOrderSequence,
                $"Sequence #{reading.SequenceNumber} arrived after #{lastSequence}", now));
        }
        else
        {
            _lastSequence[reading.DeviceId] = reading.SequenceNumber;
        }

        if (reading.Pressure > Options.PressureThreshold)
        {
            _onAlert(new Alert(reading.DeviceId, AlertType.HighPressure,
                $"Pressure {reading.Pressure:F2} MPa exceeds {Options.PressureThreshold:F2} MPa", now));
        }

        if (reading.Temperature > Options.TemperatureThreshold)
        {
            _onAlert(new Alert(reading.DeviceId, AlertType.HighTemperature,
                $"Temperature {reading.Temperature:F1} °C exceeds {Options.TemperatureThreshold:F1} °C", now));
        }

        return ValueTask.CompletedTask;
    }
}
