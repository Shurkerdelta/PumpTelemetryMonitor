namespace PumpMonitor.Core.Alerts;

/// <summary>
/// Thresholds used by <see cref="AlertMonitor"/>. Immutable: to change thresholds while running, build a
/// new instance (e.g. <c>monitor.Options = monitor.Options with { PressureThreshold = 60 }</c>).
/// </summary>
public sealed record AlertOptions
{
    /// <summary>MPa. Readings above this raise <see cref="AlertType.HighPressure"/>.</summary>
    public double PressureThreshold { get; init; } = Constants.Alerts.PressureThreshold;

    /// <summary>MPa. Pressure must drop to <c>threshold - deadband</c> before the alert re-arms.</summary>
    public double PressureDeadband { get; init; } = Constants.Alerts.PressureDeadband;

    /// <summary>°C. Readings above this raise <see cref="AlertType.HighTemperature"/>.</summary>
    public double TemperatureThreshold { get; init; } = Constants.Alerts.TemperatureThreshold;

    /// <summary>°C. Temperature must drop to <c>threshold - deadband</c> before the alert re-arms.</summary>
    public double TemperatureDeadband { get; init; } = Constants.Alerts.TemperatureDeadband;

    /// <summary>Raise <see cref="AlertType.DeviceSilent"/> when a device is quiet for longer than this.</summary>
    public TimeSpan SilenceTimeout { get; init; } = Constants.Alerts.SilenceTimeout;

    /// <summary>Throws if any value is out of range.</summary>
    public void Validate()
    {
        if (PressureDeadband < 0 || TemperatureDeadband < 0)
        {
            throw new ArgumentException("Deadbands cannot be negative.");
        }

        if (SilenceTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("SilenceTimeout must be positive.");
        }
    }
}
