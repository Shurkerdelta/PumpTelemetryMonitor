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
    /// MPa added during a <see cref="DeviceFault.PressureSpike"/>. Lifts a running pump (~55 MPa) to ~80 MPa,
    /// past the default 69 MPa alert threshold; trips it once pressure is above 44 MPa (~8 s after start).
    /// </summary>
    public double SpikePressure { get; init; } = 25;

    /// <summary>
    /// °C added during a <see cref="DeviceFault.Overheat"/>. Large enough to pass the default 90 °C alert once the
    /// oil is above 40 °C (~13 s after start); a smaller step would need the slow warm-up to nearly finish.
    /// </summary>
    public double OverheatTemperature { get; init; } = 50;

    /// <summary>How long a pressure spike or overheat lasts.</summary>
    public TimeSpan FaultDuration { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a <see cref="DeviceFault.Silence"/> lasts; longer than the default 5 s silence alert.</summary>
    public TimeSpan SilenceDuration { get; init; } = TimeSpan.FromSeconds(8);
}
