using PumpMonitor.Core.Devices;

namespace PumpMonitor.Tests.Devices;

/// <summary>
/// Returns scripted samples in order, repeating the last one once the script runs out,
/// and records every call so tests can assert what the device passed in.
/// </summary>
public sealed class StubPumpProcess : IPumpProcess
{
    private readonly Queue<ProcessState> _script;
    private ProcessState _last;

    public StubPumpProcess(params ProcessState[] script)
    {
        if (script.Length == 0)
        {
            throw new ArgumentException("Provide at least one sample.", nameof(script));
        }

        _script = new Queue<ProcessState>(script);
        _last = script[0];
    }

    public List<(bool IsRunning, TimeSpan Elapsed)> Calls { get; } = [];

    public ProcessState Next(bool isRunning, TimeSpan elapsed)
    {
        Calls.Add((isRunning, elapsed));
        if (_script.TryDequeue(out ProcessState next))
        {
            _last = next;
        }

        return _last;
    }
}
