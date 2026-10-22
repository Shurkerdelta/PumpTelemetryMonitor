namespace PumpMonitor.Core.Devices;

/// <summary>Timing, start-up and fault-injection behaviour for a <see cref="SimulatedDevice"/>.</summary>
public sealed record SimulatedDeviceOptions
{
    /// <summary>Shortest delay between readings.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Longest delay between readings. Set equal to <see cref="MinInterval"/> for a fixed rate.</summary>
    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Whether the pump is running when the device is created.</summary>
    public bool StartOn { get; init; } = true;

    /// <summary>
    /// MPa a <see cref="DeviceFault.PressureSpike"/> reports above the alert threshold (or above the actual
    /// pressure, if that is already higher). Must be positive so the spike is strictly over the threshold.
    /// </summary>
    public double FaultPressureMargin { get; init; } = 5;

    /// <summary>
    /// °C an <see cref="DeviceFault.Overheat"/> reports above the alert threshold (or above the actual
    /// temperature, if that is already higher). Must be positive so the overheat is strictly over the threshold.
    /// </summary>
    public double FaultTemperatureMargin { get; init; } = 5;

    /// <summary>
    /// How much longer than the alert's silence timeout a <see cref="DeviceFault.Silence"/> lasts. Must exceed
    /// the monitor's silence check interval (0.5 s in the app) so the silence is noticed before readings resume.
    /// </summary>
    public TimeSpan FaultSilenceMargin { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a pressure spike or overheat lasts.</summary>
    public TimeSpan FaultDuration { get; init; } = TimeSpan.FromSeconds(3);
}
