namespace PumpMonitor.Core.Devices;

/// <summary>The pump's pressure and temperature at one instant.</summary>
public readonly record struct ProcessState(double Pressure, double Temperature);

/// <summary>
/// Models how a pump's pressure and temperature evolve over time. Separating this from
/// <see cref="SimulatedDevice"/> lets tests substitute scripted values while keeping
/// the device's timing, sequencing and on/off behaviour real.
/// </summary>
public interface IPumpProcess
{
    /// <summary>Advances the model and returns the new state.</summary>
    /// <param name="isRunning">Whether the pump is currently switched on.</param>
    /// <param name="elapsed">Time since the previous call (zero for the first one).</param>
    ProcessState Next(bool isRunning, TimeSpan elapsed);
}
