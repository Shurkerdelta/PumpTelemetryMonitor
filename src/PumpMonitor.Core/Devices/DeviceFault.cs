namespace PumpMonitor.Core.Devices;

/// <summary>
/// Abnormal behaviour an operator can inject into a <see cref="SimulatedDevice"/> to exercise alerting.
/// Sizes and durations come from <see cref="SimulatedDeviceOptions"/>.
/// </summary>
public enum DeviceFault
{
    /// <summary>Pressure jumps by <see cref="SimulatedDeviceOptions.SpikePressure"/> for <see cref="SimulatedDeviceOptions.FaultDuration"/>.</summary>
    PressureSpike,

    /// <summary>Temperature rises by <see cref="SimulatedDeviceOptions.OverheatTemperature"/> for <see cref="SimulatedDeviceOptions.FaultDuration"/>.</summary>
    Overheat,

    /// <summary>The device sends nothing for <see cref="SimulatedDeviceOptions.SilenceDuration"/>, as if it went offline.</summary>
    Silence,

    /// <summary>The next two readings are delivered in swapped order, as a flaky network might.</summary>
    Reorder,
}
