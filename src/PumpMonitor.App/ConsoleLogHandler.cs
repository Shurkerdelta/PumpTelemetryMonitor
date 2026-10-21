using PumpMonitor.Core;
using PumpMonitor.Core.Processing;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.App;

/// <summary>
/// Pipeline step that prints each reading above the command prompt while <paramref name="enabled"/> is on.
/// The toggle can be flipped at any time (e.g. by the <c>readings</c> command).
/// </summary>
internal sealed class ConsoleLogHandler(ConsoleUi ui, Toggle enabled) : ITelemetryHandler
{
    public ValueTask HandleAsync(TelemetryReading r, CancellationToken cancellationToken)
    {
        if (enabled.IsOn)
        {
            ui.WriteLine(
                $"{r.Timestamp.ToLocalTime():HH:mm:ss.fff}  {r.DeviceId,-7} #{r.SequenceNumber,-5} " +
                $"P={r.Pressure,6:F2} MPa  T={r.Temperature,5:F1} °C");
        }

        return ValueTask.CompletedTask;
    }
}
