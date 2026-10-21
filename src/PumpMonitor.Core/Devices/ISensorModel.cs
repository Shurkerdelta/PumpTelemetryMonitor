namespace PumpMonitor.Core.Devices;

/// <summary>Physical values measured by a device's sensors at one instant.</summary>
public readonly record struct SensorSample(double Pressure, double Temperature);

/// <summary>
/// Produces the pressure and temperature a device reports. Separating this from
/// <see cref="SimulatedDevice"/> lets tests substitute scripted values while keeping
/// the device's timing, sequencing and on/off behaviour real.
/// </summary>
public interface ISensorModel
{
    /// <summary>Advances the model and returns the next sample.</summary>
    /// <param name="isRunning">Whether the pump is currently switched on.</param>
    /// <param name="elapsed">Time since the previous sample (zero for the first one).</param>
    SensorSample Next(bool isRunning, TimeSpan elapsed);
}
