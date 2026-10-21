namespace PumpMonitor.Core.Devices;

/// <summary>Tuning for <see cref="PumpProcessModel"/>.</summary>
public sealed record PumpProcessOptions
{
    /// <summary>Pressure (psi) the pump settles at while running.</summary>
    public double RunningPressure { get; init; } = 120;

    /// <summary>Pressure (psi) the line settles at while the pump is off.</summary>
    public double IdlePressure { get; init; } = 0;

    /// <summary>Temperature (°C) the pump settles at while running.</summary>
    public double RunningTemperature { get; init; } = 65;

    /// <summary>Temperature (°C) the pump cools to while off.</summary>
    public double IdleTemperature { get; init; } = 20;

    /// <summary>Time to cover ~63% of the gap to the pressure target.</summary>
    public TimeSpan PressureTimeConstant { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Time to cover ~63% of the gap to the temperature target. Temperature lags pressure.</summary>
    public TimeSpan TemperatureTimeConstant { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Random pressure fluctuation, as a standard deviation (psi) per √second.</summary>
    public double PressureNoise { get; init; } = 1.5;

    /// <summary>Random temperature fluctuation, as a standard deviation (°C) per √second.</summary>
    public double TemperatureNoise { get; init; } = 0.2;

    /// <summary>Starting pressure; defaults to <see cref="IdlePressure"/>.</summary>
    public double? InitialPressure { get; init; }

    /// <summary>Starting temperature; defaults to <see cref="IdleTemperature"/>.</summary>
    public double? InitialTemperature { get; init; }
}
