using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using PumpMonitor.Core.Alerts;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.Tests.Alerts;

public class SilenceMonitorTests
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(500);

    private readonly FakeTimeProvider _time = new();

    // RunAsync raises alerts from a timer thread, so collect them thread-safely.
    private readonly ConcurrentQueue<Alert> _alerts = new();

    // Nothing reads this channel, so readings pile up as if the pipeline's consumer had stalled.
    private readonly Channel<TelemetryReading> _queue = Channel.CreateUnbounded<TelemetryReading>();

    private SilenceMonitor CreateMonitor(IEnumerable<string>? expected = null, Action<Alert>? onAlert = null) =>
        new(onAlert ?? _alerts.Enqueue, new AlertOptions { SilenceTimeout = TimeSpan.FromSeconds(5) }, expected, _time);

    private static void Send(ChannelWriter<TelemetryReading> writer, string deviceId = "pump-1") =>
        Assert.True(writer.TryWrite(new TelemetryReading(deviceId, DateTimeOffset.UnixEpoch, 50, 60, 1)));

    private AlertType[] Types() => [.. _alerts.Select(a => a.Type)];

    /// <summary>
    /// Advances fake time a check interval at a time until <paramref name="condition"/> holds. Timer
    /// continuations run on the thread pool, so each step waits briefly for the check to run.
    /// </summary>
    private async Task AdvanceUntilAsync(Func<bool> condition)
    {
        for (int step = 0; step < 200 && !condition(); step++)
        {
            _time.Advance(CheckInterval);
            await Task.Delay(5);
        }

        Assert.True(condition(), "condition not met after 100 s of fake time");
    }

    [Fact]
    public void SilentDevice_AlertsOnceAfterTimeout_AndReArmsWhenHeardFrom()
    {
        SilenceMonitor monitor = CreateMonitor();
        ChannelWriter<TelemetryReading> writer = monitor.Track(_queue.Writer);
        Send(writer);

        _time.Advance(TimeSpan.FromSeconds(5));
        monitor.CheckForSilentDevices();
        Assert.Empty(_alerts); // exactly at the limit is not "more than"

        _time.Advance(TimeSpan.FromMilliseconds(1));
        monitor.CheckForSilentDevices();
        monitor.CheckForSilentDevices();
        Assert.Equal([AlertType.DeviceSilent], Types()); // reported once, not on every check

        Send(writer);
        _time.Advance(TimeSpan.FromSeconds(6));
        monitor.CheckForSilentDevices();
        Assert.Equal([AlertType.DeviceSilent, AlertType.DeviceSilent], Types());
    }

    [Fact]
    public void ReadingsStuckInTheQueue_StillCountAsHeardFrom()
    {
        SilenceMonitor monitor = CreateMonitor(expected: ["pump-1"]);
        ChannelWriter<TelemetryReading> writer = monitor.Track(_queue.Writer);

        // 20 s of readings that nobody consumes: the device is fine, only the pipeline is behind.
        for (int i = 0; i < 40; i++)
        {
            Send(writer);
            _time.Advance(CheckInterval);
            monitor.CheckForSilentDevices();
        }

        Assert.Empty(_alerts);
        Assert.Equal(40, _queue.Reader.Count); // every reading was passed on to the wrapped writer
    }

    [Fact]
    public void ExpectedDevice_ThatNeverReports_IsFlaggedSilent()
    {
        SilenceMonitor monitor = CreateMonitor(expected: ["pump-1", "pump-2"]);

        _time.Advance(TimeSpan.FromSeconds(6));
        monitor.CheckForSilentDevices();

        Assert.Equal(["pump-1", "pump-2"], _alerts.Select(a => a.DeviceId).Order());
    }

    [Fact]
    public void FailingAlertCallback_StillDeliversTheOtherAlerts()
    {
        var delivered = new List<string>();
        SilenceMonitor monitor = CreateMonitor(expected: ["pump-1", "pump-2"], onAlert: alert =>
        {
            if (alert.DeviceId == "pump-1")
            {
                throw new IOException("console write failed");
            }

            delivered.Add(alert.DeviceId);
        });

        _time.Advance(TimeSpan.FromSeconds(6));
        var ex = Assert.Throws<AggregateException>(monitor.CheckForSilentDevices);

        Assert.IsType<IOException>(Assert.Single(ex.InnerExceptions));
        Assert.Equal(["pump-2"], delivered);
    }

    [Fact]
    public async Task RunAsync_ReportsAFailedCheckAndKeepsChecking()
    {
        var errors = new ConcurrentQueue<Exception>();
        int calls = 0;
        SilenceMonitor monitor = CreateMonitor(expected: ["pump-1"], onAlert: alert =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new IOException("console write failed");
            }

            _alerts.Enqueue(alert);
        });
        ChannelWriter<TelemetryReading> writer = monitor.Track(_queue.Writer);
        using var cts = new CancellationTokenSource();
        Task watch = monitor.RunAsync(CheckInterval, errors.Enqueue, cts.Token);

        // The first silence alert fails to deliver...
        await AdvanceUntilAsync(() => !errors.IsEmpty);

        // ...then the device reports and goes quiet again, which the watch must still be running to notice.
        Send(writer);
        await AdvanceUntilAsync(() => !_alerts.IsEmpty);

        cts.Cancel();
        await watch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<IOException>(Assert.Single(Assert.IsType<AggregateException>(Assert.Single(errors)).InnerExceptions));
        Assert.Equal([AlertType.DeviceSilent], Types());
        Assert.Equal(TaskStatus.RanToCompletion, watch.Status);
    }

    [Fact]
    public async Task RunAsync_RaisesAlertFromTimerWhileReadingsArriveConcurrently()
    {
        var errors = new ConcurrentQueue<Exception>();
        SilenceMonitor monitor = CreateMonitor(expected: ["pump-1", "pump-2"]);
        ChannelWriter<TelemetryReading> writer = monitor.Track(_queue.Writer);
        using var cts = new CancellationTokenSource();
        Task watch = monitor.RunAsync(CheckInterval, errors.Enqueue, cts.Token);

        // pump-1 keeps reporting from another thread; pump-2 never does.
        for (int i = 0; i < 14; i++)
        {
            await Task.Run(() => Send(writer));
            _time.Advance(CheckInterval);
        }

        // Timer callbacks run on the thread pool; wait briefly for the alert to land.
        for (int i = 0; i < 100 && _alerts.IsEmpty; i++)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await watch.WaitAsync(TimeSpan.FromSeconds(5));

        Alert alert = Assert.Single(_alerts);
        Assert.Equal(("pump-2", AlertType.DeviceSilent), (alert.DeviceId, alert.Type));
        Assert.Equal(TaskStatus.RanToCompletion, watch.Status);
        Assert.Empty(errors);
    }
}
