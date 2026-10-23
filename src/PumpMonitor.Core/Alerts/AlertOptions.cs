namespace PumpMonitor.Core.Alerts;

/// <summary>
/// Thresholds used by <see cref="AlertMonitor"/> and <see cref="SilenceMonitor"/>, fixed when they are created.
/// </summary>
public sealed record AlertOptions
{
    /// <summary>
    /// MPa. Readings above this raise <see cref="AlertType.HighPressure"/>. 69 MPa (~10,000 psi) is above
    /// normal treating pressure and well below a 103 MPa pump rating.
    /// </summary>
    public double PressureThreshold { get; init; } = 69;

    /// <summary>
    /// °C. Readings above this raise <see cref="AlertType.HighTemperature"/>: power-end lube oil running hot
    /// (normal is around 60–80 °C).
    /// </summary>
    public double TemperatureThreshold { get; init; } = 90;

    /// <summary>Raise <see cref="AlertType.DeviceSilent"/> when a device is quiet for longer than this.</summary>
    public TimeSpan SilenceTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Throws if any value is out of range.</summary>
    public void Validate()
    {
        // No reading is ever above NaN, so a NaN threshold would silently switch that alert off.
        if (!double.IsFinite(PressureThreshold) || !double.IsFinite(TemperatureThreshold))
        {
            throw new ArgumentException("PressureThreshold and TemperatureThreshold must be finite.");
        }

        if (SilenceTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("SilenceTimeout must be positive.");
        }
    }
}
