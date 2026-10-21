namespace PumpMonitor.Core.Devices;

/// <summary>
/// Tuning for <see cref="PumpProcessModel"/>. Defaults model a hydraulic fracturing (frac) pump:
/// pressure is fluid-end discharge pressure, temperature is power-end lube oil temperature.
/// </summary>
public sealed record PumpProcessOptions
{
    /// <summary>
    /// Pressure (MPa) the pump settles at while running. 55 MPa is a typical treating pressure
    /// (~8,000 psi), well below a 103 MPa (15,000 psi) pump rating.
    /// </summary>
    public double RunningPressure { get; init; } = 55;

    /// <summary>
    /// Pressure (MPa) the line settles at while the pump is off: discharge falls back to the
    /// boost/suction pressure (~40 psi).
    /// </summary>
    public double IdlePressure { get; init; } = 0.3;

    /// <summary>Temperature (°C) the pump settles at while running. Power-end lube oil normally runs around 60–80 °C.</summary>
    public double RunningTemperature { get; init; } = 70;

    /// <summary>Temperature (°C) the pump cools to while off (ambient).</summary>
    public double IdleTemperature { get; init; } = 20;

    /// <summary>Time to cover ~63% of the gap to the pressure target. Bringing a pump online or offline takes seconds.</summary>
    public TimeSpan PressureTimeConstant { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Time to cover ~63% of the gap to the temperature target. Time-compressed for the demo: real lube oil
    /// takes 10–20 minutes to warm up, which would be too slow to watch. Still much slower than pressure,
    /// so temperature visibly lags.
    /// </summary>
    public TimeSpan TemperatureTimeConstant { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Random pressure fluctuation, as a standard deviation per √second expressed as a fraction of the
    /// current pressure (0.01 = 1%, ~0.55 MPa at 55 MPa), so the wobble shrinks along with the pressure
    /// when the pump is off. Plunger pulsation is a few percent of discharge pressure.
    /// </summary>
    public double RelativePressureNoise { get; init; } = 0.01;

    /// <summary>Random temperature fluctuation, as a standard deviation (°C) per √second.</summary>
    public double TemperatureNoise { get; init; } = 0.2;

    /// <summary>Starting pressure; defaults to <see cref="IdlePressure"/>.</summary>
    public double? InitialPressure { get; init; }

    /// <summary>Starting temperature; defaults to <see cref="IdleTemperature"/>.</summary>
    public double? InitialTemperature { get; init; }
}
