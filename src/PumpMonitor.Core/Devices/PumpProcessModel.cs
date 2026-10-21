namespace PumpMonitor.Core.Devices;

/// <summary>
/// Simulates a pump's pressure and temperature. Each value relaxes exponentially toward
/// a target (the running setpoint when on, the idle value when off) with random noise
/// added on top, so readings wander but still trace a warm-up / cool-down curve.
/// </summary>
/// <remarks>
/// This is a discretised Ornstein–Uhlenbeck process: the noise scales with √Δt so the
/// curve looks the same regardless of how often it is sampled. Not thread-safe; each
/// device should own its own instance.
/// </remarks>
public sealed class PumpProcessModel : IPumpProcess
{
    private readonly PumpProcessOptions _options;
    private readonly Random _random;
    private double _pressure;
    private double _temperature;

    public PumpProcessModel(PumpProcessOptions? options = null, Random? random = null)
    {
        _options = options ?? new PumpProcessOptions();
        _random = random ?? new Random();
        _pressure = _options.InitialPressure ?? _options.IdlePressure;
        _temperature = _options.InitialTemperature ?? _options.IdleTemperature;
    }

    public ProcessState Next(bool isRunning, TimeSpan elapsed)
    {
        double pressureTarget = isRunning ? _options.RunningPressure : _options.IdlePressure;
        double temperatureTarget = isRunning ? _options.RunningTemperature : _options.IdleTemperature;

        _pressure = Step(_pressure, pressureTarget, _options.PressureTimeConstant, _options.PressureNoise, elapsed);
        _temperature = Step(_temperature, temperatureTarget, _options.TemperatureTimeConstant, _options.TemperatureNoise, elapsed);

        // Gauge pressure can't go below zero, however noisy the simulation.
        _pressure = Math.Max(0, _pressure);

        return new ProcessState(_pressure, _temperature);
    }

    private double Step(double current, double target, TimeSpan timeConstant, double noise, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return current;
        }

        double approach = 1 - Math.Exp(-elapsed / timeConstant);
        double next = current + (target - current) * approach;
        return next + noise * Math.Sqrt(elapsed.TotalSeconds) * NextGaussian();
    }

    // Box–Muller transform; Random has no built-in normal distribution.
    private double NextGaussian()
    {
        double u1 = 1.0 - _random.NextDouble();
        double u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
