namespace PumpMonitor.Core.Devices;

/// <summary>Tuning for <see cref="PumpProcessModel"/>.</summary>
public sealed record PumpProcessOptions
{
    /// <summary>Pressure (MPa) the pump settles at while running.</summary>
    public double RunningPressure { get; init; } = Constants.PumpProcess.RunningPressure;

    /// <summary>Pressure (MPa) the line settles at while the pump is off.</summary>
    public double IdlePressure { get; init; } = Constants.PumpProcess.IdlePressure;

    /// <summary>Temperature (°C) the pump settles at while running.</summary>
    public double RunningTemperature { get; init; } = Constants.PumpProcess.RunningTemperature;

    /// <summary>Temperature (°C) the pump cools to while off.</summary>
    public double IdleTemperature { get; init; } = Constants.PumpProcess.IdleTemperature;

    /// <summary>Time to cover ~63% of the gap to the pressure target.</summary>
    public TimeSpan PressureTimeConstant { get; init; } = Constants.PumpProcess.PressureTimeConstant;

    /// <summary>Time to cover ~63% of the gap to the temperature target. Temperature lags pressure.</summary>
    public TimeSpan TemperatureTimeConstant { get; init; } = Constants.PumpProcess.TemperatureTimeConstant;

    /// <summary>Random pressure fluctuation, as a standard deviation (MPa) per √second.</summary>
    public double PressureNoise { get; init; } = Constants.PumpProcess.PressureNoise;

    /// <summary>Random temperature fluctuation, as a standard deviation (°C) per √second.</summary>
    public double TemperatureNoise { get; init; } = Constants.PumpProcess.TemperatureNoise;

    /// <summary>Starting pressure; defaults to <see cref="IdlePressure"/>.</summary>
    public double? InitialPressure { get; init; }

    /// <summary>Starting temperature; defaults to <see cref="IdleTemperature"/>.</summary>
    public double? InitialTemperature { get; init; }
}
