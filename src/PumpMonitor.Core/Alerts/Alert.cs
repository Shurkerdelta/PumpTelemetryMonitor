namespace PumpMonitor.Core.Alerts;

public enum AlertType
{
    HighPressure,
    HighTemperature,
    OutOfOrderSequence,
    DeviceSilent,

    /// <summary>A pressure or temperature value is NaN or infinite: a sensor or transmission fault, not a real measurement.</summary>
    InvalidReading,
}

/// <summary>An abnormal condition detected for one device.</summary>
/// <param name="DeviceId">The device the alert is about.</param>
/// <param name="Type">What kind of condition was detected.</param>
/// <param name="Message">Human-readable detail, including the offending value.</param>
/// <param name="Timestamp">When the condition was detected.</param>
public sealed record Alert(string DeviceId, AlertType Type, string Message, DateTimeOffset Timestamp);
