using PumpMonitor.Core.Devices;

namespace PumpMonitor.Tests.Devices;

/// <summary>
/// Returns scripted samples in order, repeating the last one once the script runs out,
/// and records every call so tests can assert what the device passed in.
/// </summary>
public sealed class StubSensorModel : ISensorModel
{
    private readonly Queue<SensorSample> _script;
    private SensorSample _last;

    public StubSensorModel(params SensorSample[] script)
    {
        if (script.Length == 0)
        {
            throw new ArgumentException("Provide at least one sample.", nameof(script));
        }

        _script = new Queue<SensorSample>(script);
        _last = script[0];
    }

    public List<(bool IsRunning, TimeSpan Elapsed)> Calls { get; } = [];

    public SensorSample Next(bool isRunning, TimeSpan elapsed)
    {
        Calls.Add((isRunning, elapsed));
        if (_script.TryDequeue(out SensorSample next))
        {
            _last = next;
        }

        return _last;
    }
}
