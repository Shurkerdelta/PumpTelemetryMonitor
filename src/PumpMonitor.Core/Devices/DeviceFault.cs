namespace PumpMonitor.Core.Devices;

/// <summary>
/// Abnormal behaviour an operator can inject into a <see cref="SimulatedDevice"/> to exercise alerting.
/// Each fault is sized from the alert settings passed to <see cref="SimulatedDevice.InjectFault"/>, so it is
/// guaranteed to trip the matching alert; margins and durations come from <see cref="SimulatedDeviceOptions"/>.
/// </summary>
public enum DeviceFault
{
    /// <summary>Pressure reads above the pressure alert threshold for <see cref="SimulatedDeviceOptions.FaultDuration"/>.</summary>
    PressureSpike,

    /// <summary>Temperature reads above the temperature alert threshold for <see cref="SimulatedDeviceOptions.FaultDuration"/>.</summary>
    Overheat,

    /// <summary>The device sends nothing for longer than the silence alert timeout, as if it went offline.</summary>
    Silence,

    /// <summary>The next two readings are delivered in swapped order, as a flaky network might.</summary>
    Reorder,
}
