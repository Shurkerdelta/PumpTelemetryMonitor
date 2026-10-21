using System.Threading.Channels;
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
    /// Takes a single reading immediately. Used by <see cref="RunAsync"/>; exposed so tests
    /// can drive the device step by step. Not intended to be called concurrently with itself
    /// or with <see cref="RunAsync"/>.
    /// </summary>
    public TelemetryReading TakeReading()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan elapsed = _lastReadingAt is { } last ? now - last : TimeSpan.Zero;
        _lastReadingAt = now;

        ProcessState state = _process.Next(_isOn, elapsed);
        return new TelemetryReading(DeviceId, now, state.Pressure, state.Temperature, ++_sequenceNumber);
    }

    /// <summary>
    /// Emits a reading, waits a random interval, and repeats until <paramref name="cancellationToken"/>
    /// is cancelled. Cancellation is a normal shutdown and completes the task without throwing.
    /// The writer is not completed here because several devices typically share one channel.
    /// </summary>
    public async Task RunAsync(ChannelWriter<TelemetryReading> writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        try
        {
            while (true)
            {
                await writer.WriteAsync(TakeReading(), cancellationToken);
                await Task.Delay(NextInterval(), _timeProvider, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private TimeSpan NextInterval()
    {
        TimeSpan range = _options.MaxInterval - _options.MinInterval;
        return _options.MinInterval + range * _random.NextDouble();
    }
}
