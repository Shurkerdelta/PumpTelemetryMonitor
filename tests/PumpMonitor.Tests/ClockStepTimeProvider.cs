using Microsoft.Extensions.Time.Testing;

namespace PumpMonitor.Tests;

/// <summary>
/// Wraps a <see cref="FakeTimeProvider"/> so tests can step the wall clock (<see cref="GetUtcNow"/>) without
/// the monotonic timestamp or timers moving, as an NTP correction or a manual clock change would. Advance the
/// inner provider for time that really passes.
/// </summary>
public sealed class ClockStepTimeProvider(FakeTimeProvider inner) : TimeProvider
{
    /// <summary>How far the wall clock has been stepped away from real elapsed time.</summary>
    public TimeSpan WallClockOffset { get; set; }

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + WallClockOffset;

    public override long GetTimestamp() => inner.GetTimestamp();

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        inner.CreateTimer(callback, state, dueTime, period);
}
