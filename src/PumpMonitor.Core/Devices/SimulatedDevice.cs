using System.Threading.Channels;
using PumpMonitor.Core.Alerts;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.Core.Devices;

/// <summary>
/// A simulated pump that periodically emits <see cref="TelemetryReading"/>s.
/// The device owns timing, sequence numbers and the on/off state; the values themselves
/// come from an <see cref="IPumpProcess"/>, which tests can replace with a stub.
/// </summary>
/// <remarks>
/// Turning the pump off does not silence the device: its sensors keep reporting while
/// pressure and temperature fall back to idle. <see cref="TurnOn"/> and <see cref="TurnOff"/>
/// are safe to call from any thread while <see cref="RunAsync"/> is executing.
/// </remarks>
public sealed class SimulatedDevice
{
    private readonly IPumpProcess _process;
    private readonly SimulatedDeviceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random;
    private volatile bool _isOn;
    private long _sequenceNumber;
    private DateTimeOffset? _lastReadingAt;

    // Fault state is set from the command thread and read by the run loop, so it's guarded by a lock.
    private readonly Lock _faultGate = new();
    private DateTimeOffset _spikeUntil = DateTimeOffset.MinValue;
    private double _spikeThreshold;
    private DateTimeOffset _overheatUntil = DateTimeOffset.MinValue;
    private double _overheatThreshold;
    private DateTimeOffset _silentUntil = DateTimeOffset.MinValue;
    private bool _reorderPending;

    public SimulatedDevice(
        string deviceId,
        IPumpProcess? process = null,
        SimulatedDeviceOptions? options = null,
        TimeProvider? timeProvider = null,
        Random? random = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        _options = options ?? new SimulatedDeviceOptions();
        if (_options.MinInterval <= TimeSpan.Zero || _options.MaxInterval < _options.MinInterval)
        {
            throw new ArgumentException("Intervals must be positive and MinInterval <= MaxInterval.", nameof(options));
        }

        // Margins must be positive or a fault could land exactly on a threshold, which doesn't alert.
        if (_options.FaultDuration <= TimeSpan.Zero
            || _options.FaultPressureMargin <= 0
            || _options.FaultTemperatureMargin <= 0
            || _options.FaultSilenceMargin <= TimeSpan.Zero)
        {
            throw new ArgumentException("Fault duration and margins must be positive.", nameof(options));
        }

        DeviceId = deviceId;
        _random = random ?? new Random();
        _process = process ?? new PumpProcessModel(random: _random);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isOn = _options.StartOn;
    }

    public string DeviceId { get; }

    public bool IsOn => _isOn;

    public void TurnOn() => _isOn = true;

    public void TurnOff() => _isOn = false;

    /// <summary>
    /// Starts abnormal behaviour that is guaranteed to trip the matching alert under <paramref name="limits"/>
    /// (the alert thresholds in use; defaults if omitted). Safe to call from any thread.
    /// </summary>
    /// <remarks>
    /// Spikes and overheats report <c>max(actual, threshold) + margin</c>, so the value is always strictly above
    /// the threshold however it is set, while the pump's normal curve carries on underneath and readings return
    /// to normal when the fault ends. Silence lasts the silence timeout plus a margin that covers the monitor's
    /// check interval. Injecting a fault that is already active restarts it.
    /// </remarks>
    public void InjectFault(DeviceFault fault, AlertOptions? limits = null)
    {
        limits ??= new AlertOptions();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_faultGate)
        {
            switch (fault)
            {
                case DeviceFault.PressureSpike:
                    _spikeUntil = now + _options.FaultDuration;
                    _spikeThreshold = limits.PressureThreshold;
                    break;
                case DeviceFault.Overheat:
                    _overheatUntil = now + _options.FaultDuration;
                    _overheatThreshold = limits.TemperatureThreshold;
                    break;
                case DeviceFault.Silence:
                    _silentUntil = now + limits.SilenceTimeout + _options.FaultSilenceMargin;
                    break;
                case DeviceFault.Reorder:
                    _reorderPending = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
            }
        }
    }

    /// <summary>
    /// Takes a single reading immediately, including any active spike or overheat. Used by
    /// <see cref="RunAsync"/>; exposed so tests can drive the device step by step. Not intended to be
    /// called concurrently with itself or with <see cref="RunAsync"/>.
    /// </summary>
    public TelemetryReading TakeReading()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan elapsed = _lastReadingAt is { } last ? now - last : TimeSpan.Zero;
        _lastReadingAt = now;

        ProcessState state = _process.Next(_isOn, elapsed);
        double pressure = state.Pressure;
        double temperature = state.Temperature;
        lock (_faultGate)
        {
            if (now < _spikeUntil)
            {
                pressure = Math.Max(pressure, _spikeThreshold) + _options.FaultPressureMargin;
            }

            if (now < _overheatUntil)
            {
                temperature = Math.Max(temperature, _overheatThreshold) + _options.FaultTemperatureMargin;
            }
        }

        return new TelemetryReading(DeviceId, now, pressure, temperature, ++_sequenceNumber);
    }

    /// <summary>
    /// Emits a reading, waits a random interval, and repeats until <paramref name="cancellationToken"/>
    /// is cancelled. Cancellation is a normal shutdown and completes the task without throwing.
    /// The writer is not completed here because several devices typically share one channel.
    /// </summary>
    /// <remarks>
    /// Silence and reorder faults act here because they change delivery rather than values: while silent
    /// no readings are taken (so no sequence numbers are skipped), and a reorder holds one reading back
    /// and sends it after the next.
    /// </remarks>
    public async Task RunAsync(ChannelWriter<TelemetryReading> writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        TelemetryReading? heldBack = null;
        try
        {
            while (true)
            {
                if (!IsSilent())
                {
                    TelemetryReading reading = TakeReading();
                    if (heldBack is not null)
                    {
                        await writer.WriteAsync(reading, cancellationToken);
                        await writer.WriteAsync(heldBack, cancellationToken);
                        heldBack = null;
                    }
                    else if (TryStartReorder())
                    {
                        heldBack = reading;
                    }
                    else
                    {
                        await writer.WriteAsync(reading, cancellationToken);
                    }
                }

                await Task.Delay(NextInterval(), _timeProvider, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool IsSilent()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_faultGate)
        {
            return now < _silentUntil;
        }
    }

    private bool TryStartReorder()
    {
        lock (_faultGate)
        {
            bool pending = _reorderPending;
            _reorderPending = false;
            return pending;
        }
    }

    private TimeSpan NextInterval()
    {
        TimeSpan range = _options.MaxInterval - _options.MinInterval;
        return _options.MinInterval + (range * _random.NextDouble());
    }
}
