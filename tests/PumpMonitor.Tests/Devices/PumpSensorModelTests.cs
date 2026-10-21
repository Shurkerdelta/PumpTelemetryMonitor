using PumpMonitor.Core.Devices;

namespace PumpMonitor.Tests.Devices;

public class PumpSensorModelTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    private static SensorSample RunFor(PumpSensorModel model, bool isRunning, TimeSpan duration, List<SensorSample>? trace = null)
    {
        SensorSample sample = default;
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
        var options = new PumpSensorOptions();
        var model = new PumpSensorModel(options, new Random(42));

        SensorSample running = RunFor(model, isRunning: true, TimeSpan.FromSeconds(90));
        SensorSample stopped = RunFor(model, isRunning: false, TimeSpan.FromSeconds(120));

        Assert.InRange(running.Pressure, options.RunningPressure - 10, options.RunningPressure + 10);
        Assert.InRange(running.Temperature, options.RunningTemperature - 3, options.RunningTemperature + 3);
        Assert.InRange(stopped.Pressure, options.IdlePressure, options.IdlePressure + 10);
        Assert.InRange(stopped.Temperature, options.IdleTemperature - 3, options.IdleTemperature + 3);
    }

    [Fact]
    public void WithoutNoise_FollowsMonotonicCurveAndTemperatureLagsPressure()
    {
        var options = new PumpSensorOptions { PressureNoise = 0, TemperatureNoise = 0 };
        var model = new PumpSensorModel(options);
        var warmUp = new List<SensorSample>();
        var coolDown = new List<SensorSample>();

        RunFor(model, isRunning: true, TimeSpan.FromSeconds(30), warmUp);
        RunFor(model, isRunning: false, TimeSpan.FromSeconds(30), coolDown);

        Assert.All(warmUp.Zip(warmUp.Skip(1)), p => Assert.True(p.Second.Pressure > p.First.Pressure && p.Second.Temperature > p.First.Temperature));
        Assert.All(coolDown.Zip(coolDown.Skip(1)), p => Assert.True(p.Second.Pressure < p.First.Pressure && p.Second.Temperature < p.First.Temperature));

        // After one pressure time constant (3s) pressure is ~63% of the way there; temperature much less.
        SensorSample at3s = warmUp[11];
        double pressureProgress = (at3s.Pressure - options.IdlePressure) / (options.RunningPressure - options.IdlePressure);
        double temperatureProgress = (at3s.Temperature - options.IdleTemperature) / (options.RunningTemperature - options.IdleTemperature);
        Assert.InRange(pressureProgress, 0.6, 0.66);
        Assert.True(temperatureProgress < 0.25, $"temperature progress {temperatureProgress:P0}");
    }

    [Fact]
    public void NoiseMakesRunsDifferButKeepsPressureNonNegative()
    {
        var trace1 = new List<SensorSample>();
        var trace2 = new List<SensorSample>();

        RunFor(new PumpSensorModel(random: new Random(1)), isRunning: false, TimeSpan.FromSeconds(30), trace1);
        RunFor(new PumpSensorModel(random: new Random(2)), isRunning: false, TimeSpan.FromSeconds(30), trace2);

        Assert.NotEqual(trace1, trace2);
        Assert.All(trace1.Concat(trace2), s => Assert.True(s.Pressure >= 0));
    }
}
