using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.Core.Processing;

/// <summary>
/// A processing step run by <see cref="TelemetryPipeline"/> for every reading, e.g. logging or alert detection.
/// </summary>
/// <remarks>
/// The pipeline calls handlers from a single consumer, one reading at a time and in arrival order,
/// so implementations can keep per-device state without locking.
/// </remarks>
public interface ITelemetryHandler
{
    ValueTask HandleAsync(TelemetryReading reading, CancellationToken cancellationToken);
}
