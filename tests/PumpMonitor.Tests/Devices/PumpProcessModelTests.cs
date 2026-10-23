using PumpMonitor.Core.Devices;

namespace PumpMonitor.Tests.Devices;

public class PumpProcessModelTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    private static ProcessState RunFor(PumpProcessModel model, bool isRunning, TimeSpan duration, List<ProcessState>? trace = null)
    {
        ProcessState sample = default;
        for (TimeSpan t = TimeSpan.Zero; t < duration; t += Tick)
        {
            sample = model.Next(isRunning, Tick);
            trace?.Add(sample);
        }

        return sample;
    }

    [Fact]
    public void WarmsUpWhenOnAndCoolsDownWhenOff_SettlingNearTargetsDespiteNoise()
    {
        var options = new PumpProcessOptions();
        var model = new PumpProcessModel(options, new Random(42));

        // Run each phase for 6+ temperature time constants (30s) so both values have settled.
        ProcessState running = RunFor(model, isRunning: true, TimeSpan.FromSeconds(200));
        ProcessState stopped = RunFor(model, isRunning: false, TimeSpan.FromSeconds(200));

        // Noise keeps settled values wobbling by about ±1 MPa / ±1 °C, so allow a few units either side.
        Assert.InRange(running.Pressure, options.RunningPressure - 3, options.RunningPressure + 3);
        Assert.InRange(running.Temperature, options.RunningTemperature - 3, options.RunningTemperature + 3);
        Assert.InRange(stopped.Pressure, 0, options.IdlePressure + 3);
        Assert.InRange(stopped.Temperature, options.IdleTemperature - 3, options.IdleTemperature + 3);
    }

    [Fact]
    public void IdlePressure_StaysNearIdleTarget_BecauseNoiseScalesWithPressure()
    {
        var options = new PumpProcessOptions();
        var model = new PumpProcessModel(options, new Random(7));
        var idle = new List<ProcessState>();

        // Run, switch off, let pressure settle (12 time constants), then sample the idle phase.
        RunFor(model, isRunning: true, TimeSpan.FromSeconds(30));
        RunFor(model, isRunning: false, TimeSpan.FromSeconds(60));
        RunFor(model, isRunning: false, TimeSpan.FromSeconds(60), idle);

        // A fixed 0.5 MPa noise used to swamp the 0.3 MPa idle pressure: readings hit the zero clamp
        // and averaged more than double the target. Relative noise keeps them tight and unbiased.
        Assert.All(idle, s => Assert.InRange(s.Pressure, options.IdlePressure - 0.05, options.IdlePressure + 0.05));
        Assert.InRange(idle.Average(s => s.Pressure), options.IdlePressure - 0.01, options.IdlePressure + 0.01);
    }

    [Fact]
    public void WithoutNoise_FollowsMonotonicCurveAndTemperatureLagsPressure()
    {
        var options = new PumpProcessOptions { RelativePressureNoise = 0, TemperatureNoise = 0 };
        var model = new PumpProcessModel(options);
        var warmUp = new List<ProcessState>();
        var coolDown = new List<ProcessState>();

        RunFor(model, isRunning: true, TimeSpan.FromSeconds(30), warmUp);
        RunFor(model, isRunning: false, TimeSpan.FromSeconds(30), coolDown);

        Assert.All(warmUp.Zip(warmUp.Skip(1)), p => Assert.True(p.Second.Pressure > p.First.Pressure && p.Second.Temperature > p.First.Temperature));
        Assert.All(coolDown.Zip(coolDown.Skip(1)), p => Assert.True(p.Second.Pressure < p.First.Pressure && p.Second.Temperature < p.First.Temperature));

        // After one pressure time constant (5s = 20 ticks) pressure is ~63% of the way there; temperature much less.
        ProcessState at5s = warmUp[19];
        double pressureProgress = (at5s.Pressure - options.IdlePressure) / (options.RunningPressure - options.IdlePressure);
        double temperatureProgress = (at5s.Temperature - options.IdleTemperature) / (options.RunningTemperature - options.IdleTemperature);
        Assert.InRange(pressureProgress, 0.6, 0.66);
        Assert.True(temperatureProgress < 0.25, $"temperature progress {temperatureProgress:P0}");
    }

    [Fact]
    public void StartsFromInitialValues_OrIdleByDefault_AndZeroElapsedChangesNothing()
    {
        var options = new PumpProcessOptions { InitialPressure = 40, InitialTemperature = 65 };

        // Noise is on (the defaults), but no time has passed, so neither value may move.
        Assert.Equal(new ProcessState(40, 65), new PumpProcessModel(options, new Random(1)).Next(isRunning: true, TimeSpan.Zero));
        Assert.Equal(
            new ProcessState(options.IdlePressure, options.IdleTemperature),
            new PumpProcessModel(random: new Random(1)).Next(isRunning: true, TimeSpan.Zero));
    }

    [Fact]
    public void Pressure_NeverGoesBelowZero_HoweverLargeTheNoise()
    {
        // At the default 1% the clamp is practically never reached; 200% per √s drives about one step in six below zero.
        var model = new PumpProcessModel(new PumpProcessOptions { RelativePressureNoise = 2 }, new Random(5));
        var trace = new List<ProcessState>();

        RunFor(model, isRunning: true, TimeSpan.FromSeconds(30), trace);

        Assert.All(trace, s => Assert.True(s.Pressure >= 0, $"pressure {s.Pressure}"));
        Assert.Contains(trace, s => s.Pressure == 0); // the clamp was actually exercised
    }

    [Fact]
    public void NoiseMakesRunsDifferButKeepsPressureNonNegative()
    {
        var trace1 = new List<ProcessState>();
        var trace2 = new List<ProcessState>();

        RunFor(new PumpProcessModel(random: new Random(1)), isRunning: false, TimeSpan.FromSeconds(30), trace1);
        RunFor(new PumpProcessModel(random: new Random(2)), isRunning: false, TimeSpan.FromSeconds(30), trace2);

        Assert.NotEqual(trace1, trace2);
        Assert.All(trace1.Concat(trace2), s => Assert.True(s.Pressure >= 0));
    }
}
