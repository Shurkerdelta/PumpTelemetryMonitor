using System.Threading.Channels;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.Core.Alerts;

/// <summary>
/// Raises <see cref="AlertType.DeviceSilent"/> when a device sends nothing for longer than
/// <see cref="AlertOptions.SilenceTimeout"/>.
/// </summary>
/// <remarks>
/// <para>
/// A device is heard from when one of its readings is written through <see cref="Track"/>, not when the
/// pipeline gets round to processing it, so a stalled or backed-up consumer isn't mistaken for silent devices.
/// A silent device sends nothing to react to, so <see cref="RunAsync"/> checks on a timer. Writes come from
/// the devices' threads and checks from the timer, so per-device state is shared under a lock.
/// </para>
/// <para>
/// Alerts once per silent period and re-arms when the device is heard from again.
/// </para>
/// </remarks>
public sealed class SilenceMonitor
{
    private readonly Action<Alert> _onAlert;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DeviceState> _devices = [];

    /// <param name="onAlert">Receives each alert. Called outside the internal lock, on the thread running the check.</param>
    /// <param name="options">Supplies the silence timeout; defaults to a new <see cref="AlertOptions"/>.</param>
    /// <param name="expectedDeviceIds">
    /// Devices to watch from the start, so one that never reports at all is still flagged as silent.
    /// </param>
    /// <param name="timeProvider">Clock for silence detection; tests pass a fake.</param>
    public SilenceMonitor(
        Action<Alert> onAlert,
        AlertOptions? options = null,
        IEnumerable<string>? expectedDeviceIds = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(onAlert);
        options ??= new AlertOptions();
        options.Validate();
        _onAlert = onAlert;
        _timeout = options.SilenceTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;

        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (string deviceId in expectedDeviceIds ?? [])
        {
            _devices[deviceId] = new DeviceState(now);
        }
    }

    /// <summary>
    /// Wraps <paramref name="writer"/> so each reading written through it marks its device as heard from.
    /// Devices should write to the result instead of the pipeline's writer.
    /// </summary>
    public ChannelWriter<TelemetryReading> Track(ChannelWriter<TelemetryReading> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return new TrackingWriter(writer, this);
    }

    /// <summary>
    /// Raises <see cref="AlertType.DeviceSilent"/> for every device quiet for longer than the timeout
    /// that hasn't already been reported. Called by <see cref="RunAsync"/>; public for tests.
    /// </summary>
    /// <exception cref="AggregateException">The alert callback threw. Every other alert was still delivered.</exception>
    public void CheckForSilentDevices()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<Alert> alerts = [];

        lock (_gate)
        {
            foreach ((string deviceId, DeviceState state) in _devices)
            {
                TimeSpan quiet = now - state.LastSeen;
                if (!state.Silent && quiet > _timeout)
                {
                    state.Silent = true;
                    alerts.Add(new Alert(deviceId, AlertType.DeviceSilent,
                        $"No readings for {quiet.TotalSeconds:F1}s (limit {_timeout.TotalSeconds:F1}s)", now));
                }
            }
        }

        Raise(alerts);
    }

    /// <summary>
    /// Checks for silent devices every <paramref name="interval"/> until cancelled. A check that fails because
    /// the alert callback threw is passed to <paramref name="onError"/>, and checking carries on.
    /// </summary>
    public async Task RunAsync(TimeSpan interval, Action<Exception> onError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onError);
        using var timer = new PeriodicTimer(interval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    CheckForSilentDevices();
                }
                catch (Exception ex)
                {
                    ReportError(onError, ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void HeardFrom(string deviceId)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_devices.TryGetValue(deviceId, out DeviceState? state))
            {
                state.LastSeen = now;
                state.Silent = false;
            }
            else
            {
                _devices[deviceId] = new DeviceState(now);
            }
        }
    }

    private void Raise(List<Alert> alerts)
    {
        // Silent is already set for each of these, so an alert skipped because an earlier one threw would
        // never be raised. Deliver them all, then report the failures.
        List<Exception> errors = [];
        foreach (Alert alert in alerts)
        {
            try
            {
                _onAlert(alert);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregateException(errors);
        }
    }

    private static void ReportError(Action<Exception> onError, Exception ex)
    {
        try
        {
            onError(ex);
        }
        catch
        {
            // A failing error callback must not end the watch, or no device would ever be reported silent again.
            // There's nowhere left to report it.
        }
    }

    private sealed class DeviceState(DateTimeOffset lastSeen)
    {
        public DateTimeOffset LastSeen = lastSeen;
        public bool Silent;
    }

    /// <summary>Forwards to the wrapped writer, marking the device of each accepted reading as heard from.</summary>
    private sealed class TrackingWriter(ChannelWriter<TelemetryReading> inner, SilenceMonitor monitor)
        : ChannelWriter<TelemetryReading>
    {
        public override bool TryWrite(TelemetryReading item)
        {
            if (!inner.TryWrite(item))
            {
                return false;
            }

            monitor.HeardFrom(item.DeviceId);
            return true;
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            inner.WaitToWriteAsync(cancellationToken);

        public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);
    }
}
