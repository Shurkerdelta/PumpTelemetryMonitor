using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.Tests.Devices;

public static class ReadingCollector
{
    /// <summary>
    /// Advances fake time in <paramref name="step"/>s until <paramref name="count"/> readings arrive (or 200 steps
    /// pass). Delay continuations run on the thread pool, so each step waits briefly for the device loop to catch up;
    /// overshooting is harmless.
    /// </summary>
    public static async Task<List<TelemetryReading>> CollectAsync(
        FakeTimeProvider time, ChannelReader<TelemetryReading> reader, int count, TimeSpan step)
    {
        var readings = new List<TelemetryReading>();
        for (int i = 0; i < 200 && readings.Count < count; i++)
        {
            while (readings.Count < count && reader.TryRead(out TelemetryReading? reading))
            {
                readings.Add(reading);
            }

            if (readings.Count < count)
            {
                time.Advance(step);
                await Task.Delay(5);
            }
        }

        return readings;
    }
}
