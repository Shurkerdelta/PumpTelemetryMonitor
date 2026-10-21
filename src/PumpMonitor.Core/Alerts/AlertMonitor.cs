using PumpMonitor.Core.Processing;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.Core.Alerts;

/// <summary>
/// Detects abnormal conditions: pressure or temperature above a threshold, out-of-order sequence
/// numbers, and devices that have gone silent.
/// </summary>
/// <remarks>
/// <para>
/// Readings arrive through <see cref="HandleAsync"/> as a pipeline step. Silence can't be detected from
/// readings (a silent device sends none), so <see cref="RunSilenceWatchAsync"/> checks on a timer.
/// The two run on different threads and share per-device state under a lock.
/// </para>
/// <para>
/// Each alert fires once when its condition starts, not on every reading. High pressure/temperature
/// re-arm once the value drops below <c>threshold - deadband</c> (so noise at the threshold doesn't
/// flap); silence re-arms when the device is heard from again.
/// </para>
/// <para>
/// Thresholds come from <see cref="Options"/>, which can be replaced at any time from any thread.
/// </para>
/// </remarks>
public sealed class AlertMonitor : ITelemetryHandler
{
    private readonly Action<Alert> _onAlert;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DeviceState> _devices = [];
    private AlertOptions _options;

    /// <param name="onAlert">Receives each alert. Called outside the internal lock, from either thread.</param>
    /// <param name="options">Thresholds; defaults to a new <see cref="AlertOptions"/>.</param>
    /// <param name="expectedDeviceIds">
    /// Devices to watch from the start, so one that never reports at all is still flagged as silent.
    /// </param>
    /// <param name="timeProvider">Clock for silence detection; tests pass a fake.</param>
    public AlertMonitor(
        Action<Alert> onAlert,
        AlertOptions? options = null,
        IEnumerable<string>? expectedDeviceIds = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(onAlert);
        _onAlert = onAlert;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new AlertOptions();
        _options.Validate();

        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (string deviceId in expectedDeviceIds ?? [])
        {
            _devices[deviceId] = new DeviceState(now);
        }
    }

    /// <summary>Current thresholds. Setting validates and takes effect from the next check.</summary>
    public AlertOptions Options
    {
        get => Volatile.Read(ref _options);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            Volatile.Write(ref _options, value);
        }
    }

    public ValueTask HandleAsync(TelemetryReading reading, CancellationToken cancellationToken)
    {
        AlertOptions options = Options;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<Alert> alerts = [];

        lock (_gate)
        {
            if (!_devices.TryGetValue(reading.DeviceId, out DeviceState? state))
            {
                state = new DeviceState(now);
                _devices[reading.DeviceId] = state;
            }

            state.LastSeen = now;
            state.Silent = false;

            if (reading.SequenceNumber <= state.LastSequence)
            {
                alerts.Add(new Alert(reading.DeviceId, AlertType.OutOfOrderSequence,
                    $"Sequence #{reading.SequenceNumber} arrived after #{state.LastSequence}", now));
            }
            else
            {
                state.LastSequence = reading.SequenceNumber;
            }

            if (CrossedThreshold(ref state.PressureHigh, reading.Pressure, options.PressureThreshold, options.PressureDeadband))
            {
                alerts.Add(new Alert(reading.DeviceId, AlertType.HighPressure,
                    $"Pressure {reading.Pressure:F2} MPa exceeds {options.PressureThreshold:F2} MPa", now));
            }

            if (CrossedThreshold(ref state.TemperatureHigh, reading.Temperature, options.TemperatureThreshold, options.TemperatureDeadband))
            {
                alerts.Add(new Alert(reading.DeviceId, AlertType.HighTemperature,
                    $"Temperature {reading.Temperature:F1} °C exceeds {options.TemperatureThreshold:F1} °C", now));
            }
        }

        Raise(alerts);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Raises <see cref="AlertType.DeviceSilent"/> for every device quiet for longer than the timeout
    /// that hasn't already been reported. Called by <see cref="RunSilenceWatchAsync"/>; public for tests.
    /// </summary>
    public void CheckForSilentDevices()
    {
        TimeSpan timeout = Options.SilenceTimeout;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<Alert> alerts = [];

        lock (_gate)
        {
            foreach ((string deviceId, DeviceState state) in _devices)
            {
                TimeSpan quiet = now - state.LastSeen;
                if (!state.Silent && quiet > timeout)
                {
                    state.Silent = true;
                    alerts.Add(new Alert(deviceId, AlertType.DeviceSilent,
                        $"No readings for {quiet.TotalSeconds:F1}s (limit {timeout.TotalSeconds:F1}s)", now));
                }
            }
        }

        Raise(alerts);
    }

    /// <summary>Checks for silent devices every <paramref name="interval"/> until cancelled.</summary>
    public async Task RunSilenceWatchAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                CheckForSilentDevices();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Updates a latched high flag and returns true only on the reading that first goes above the threshold.
    /// </summary>
    private static bool CrossedThreshold(ref bool isHigh, double value, double threshold, double deadband)
    {
        if (!isHigh && value > threshold)
        {
            isHigh = true;
            return true;
        }

        if (isHigh && value <= threshold - deadband)
        {
            isHigh = false;
        }

        return false;
    }

    private void Raise(List<Alert> alerts)
    {
        foreach (Alert alert in alerts)
        {
            _onAlert(alert);
        }
    }

    private sealed class DeviceState(DateTimeOffset lastSeen)
    {
        public DateTimeOffset LastSeen = lastSeen;
        public long LastSequence;
        public bool PressureHigh;
        public bool TemperatureHigh;
        public bool Silent;
    }
}
