namespace PumpMonitor.Core.Telemetry;

/// <summary>A single telemetry sample emitted by a device.</summary>
/// <param name="DeviceId">Identifier of the emitting device.</param>
/// <param name="Timestamp">When the sample was taken.</param>
/// <param name="Pressure">Discharge pressure in psi.</param>
/// <param name="Temperature">Temperature in degrees Celsius.</param>
/// <param name="SequenceNumber">Per-device, monotonically increasing number starting at 1.</param>
public sealed record TelemetryReading(
    string DeviceId,
    DateTimeOffset Timestamp,
    double Pressure,
    double Temperature,
    long SequenceNumber);
