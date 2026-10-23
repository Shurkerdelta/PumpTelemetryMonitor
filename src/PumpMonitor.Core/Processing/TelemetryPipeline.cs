using System.Threading.Channels;
using PumpMonitor.Core.Telemetry;

namespace PumpMonitor.Core.Processing;

/// <summary>
/// Receives readings from any number of concurrent producers and runs them through a list of
/// <see cref="ITelemetryHandler"/>s on a single background consumer.
/// </summary>
/// <remarks>
/// <para>
/// Producers never block: the queue is bounded, and when it is full the oldest queued reading is
/// dropped (counted in <see cref="DroppedCount"/>). A slow handler therefore costs stale data, not
/// stalled devices or unbounded memory.
/// </para>
/// <para>
/// One consumer keeps readings in arrival order and lets handlers be stateful without locks.
/// </para>
/// <para>
/// Shutdown: stop the producers first, then call <see cref="StopAsync"/>, which closes the queue and
/// drains what is left. Writes after that fail, so a still-running producer would see the channel closed.
/// </para>
/// </remarks>
public sealed class TelemetryPipeline : IAsyncDisposable
{
    private readonly Channel<TelemetryReading> _channel;
    private readonly ITelemetryHandler[] _handlers;
    private readonly Action<TelemetryReading, Exception> _onHandlerError;
    private readonly CancellationTokenSource _abort = new();
    private Task? _processing;
    private long _droppedCount;

    /// <param name="handlers">Steps run for each reading, in the order given.</param>
    /// <param name="onHandlerError">
    /// Called when a handler throws. The pipeline carries on with the remaining handlers and readings.
    /// Exceptions thrown by the callback itself are ignored.
    /// </param>
    /// <param name="capacity">
    /// Maximum queued readings before the oldest are dropped. 3–5 devices reporting every 100–500 ms send
    /// 10–17 readings a second, so 100 is 6–10 s of readings: room for brief stalls without holding a
    /// backlog of stale data.
    /// </param>
    public TelemetryPipeline(
        IEnumerable<ITelemetryHandler> handlers,
        Action<TelemetryReading, Exception> onHandlerError,
        int capacity = 100)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(onHandlerError);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _handlers = handlers.ToArray();
        _onHandlerError = onHandlerError;
        _channel = Channel.CreateBounded<TelemetryReading>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref _droppedCount));
    }

    /// <summary>Where producers write readings. Safe for concurrent writers.</summary>
    public ChannelWriter<TelemetryReading> Writer => _channel.Writer;

    /// <summary>Readings discarded because the queue was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>Starts the background consumer. Readings written before this are queued, not lost.</summary>
    public void Start()
    {
        if (_processing is not null)
        {
            throw new InvalidOperationException("The pipeline has already been started.");
        }

        _processing = Task.Run(ProcessAsync);
    }

    /// <summary>
    /// Closes the queue and waits for queued readings to be processed. If <paramref name="cancellationToken"/>
    /// fires first, processing is abandoned: the in-flight handler's token is cancelled, no further handlers
    /// run, and the remaining readings are discarded. It still waits for the in-flight handler to return, so a
    /// handler that ignores its token delays shutdown. Safe to call more than once.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _channel.Writer.TryComplete();
        if (_processing is null)
        {
            return;
        }

        using (cancellationToken.Register(_abort.Cancel))
        {
            await _processing;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _abort.Dispose();
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (TelemetryReading reading in _channel.Reader.ReadAllAsync(_abort.Token))
            {
                foreach (ITelemetryHandler handler in _handlers)
                {
                    // ReadAllAsync only checks the token while waiting for readings, and handlers may ignore it,
                    // so without this an abandoned drain would still run every queued reading through every handler.
                    _abort.Token.ThrowIfCancellationRequested();
                    try
                    {
                        await handler.HandleAsync(reading, _abort.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !_abort.IsCancellationRequested)
                    {
                        ReportHandlerError(reading, ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
        }
    }

    private void ReportHandlerError(TelemetryReading reading, Exception ex)
    {
        try
        {
            _onHandlerError(reading, ex);
        }
        catch
        {
            // A failing error callback must not kill the consumer: producers would keep writing into a
            // queue nobody reads, silently dropping everything until shutdown. There's nowhere left to report it.
        }
    }
}
