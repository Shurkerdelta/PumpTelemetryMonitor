using PumpMonitor.Core.Processing;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.App;

/// <summary>Pipeline step that prints each reading above the command prompt.</summary>
internal sealed class ConsoleLogHandler(ConsoleUi ui) : ITelemetryHandler
{
    public ValueTask HandleAsync(TelemetryReading r, CancellationToken cancellationToken)
    {
        ui.WriteLine(
            $"{r.Timestamp.ToLocalTime():HH:mm:ss.fff}  {r.DeviceId,-7} #{r.SequenceNumber,-5} " +
            $"P={r.Pressure,6:F1} psi  T={r.Temperature,5:F1} °C");
        return ValueTask.CompletedTask;
    }
}
