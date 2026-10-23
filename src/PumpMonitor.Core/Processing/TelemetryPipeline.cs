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
/// stalled devices or unbounded memory. Handlers never run on a producer's thread either: a write that
/// wakes the idle consumer only schedules it.
/// </para>
/// <para>
/// One consumer keeps readings in arrival order and lets handlers be stateful without locks.
/// </para>
/// <para>
/// Shutdown: stop the producers first, then call <see cref="StopAsync"/>, which closes the queue and
/// drains what is left. Writes after that fail, so a still-running producer would see the channel closed.
/// Only <see cref="StopAsync"/> can close the queue; producers can't complete <see cref="Writer"/>.
/// </para>
/// </remarks>
public sealed class TelemetryPipeline : IAsyncDisposable
{
    private readonly Channel<TelemetryReading> _channel;
    private readonly ProducerWriter _writer;
    private readonly ITelemetryHandler[] _handlers;
    private readonly Action<TelemetryReading, Exception> _onHandlerError;
    private readonly CancellationTokenSource _abort = new();
    private readonly Lock _lifecycle = new();
    private Task? _processing; // guarded by _lifecycle
    private long _droppedCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryPipeline"/> class.
    /// </summary>
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

        _handlers = [.. handlers];
        _onHandlerError = onHandlerError;
        _channel = Channel.CreateBounded<TelemetryReading>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                // The default, stated because producers depend on it: true would let a write resume the waiting
                // consumer inline, so the producer would run every handler, and block on any that block.
                AllowSynchronousContinuations = false,
            },
            _ => Interlocked.Increment(ref _droppedCount));
        _writer = new ProducerWriter(_channel.Writer);
    }

    /// <summary>
    /// Where producers write readings. Safe for concurrent writers. Completing it is refused
    /// (<see cref="ChannelWriter{T}.TryComplete"/> returns false), so one producer can't close the pipeline on the others.
    /// </summary>
    public ChannelWriter<TelemetryReading> Writer => _writer;

    /// <summary>Readings discarded because the queue was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>
    /// Starts the background consumer. Readings written before this are queued, not lost. Safe to call from
    /// any thread; only the first call starts it.
    /// </summary>
    public void Start()
    {
        // Check and start under one lock: two callers racing past the check would start two consumers on a
        // single-reader channel, breaking arrival order and the handlers' lock-free state.
        lock (_lifecycle)
        {
            if (_processing is not null)
            {
                throw new InvalidOperationException("The pipeline has already been started.");
            }

            _processing = Task.Run(ProcessAsync);
        }
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
        Task? processing;
        lock (_lifecycle)
        {
            processing = _processing;
        }

        if (processing is null)
        {
            return;
        }

        using (cancellationToken.Register(_abort.Cancel))
        {
            await processing;
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

    /// <summary>
    /// Forwards writes to the queue but refuses to complete it. A producer completing it would stop every other
    /// device's readings, and completing it with an error would fault the consumer so the drain never finishes.
    /// </summary>
    private sealed class ProducerWriter(ChannelWriter<TelemetryReading> inner) : ChannelWriter<TelemetryReading>
    {
        public override bool TryWrite(TelemetryReading item) => inner.TryWrite(item);

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            inner.WaitToWriteAsync(cancellationToken);

        public override ValueTask WriteAsync(TelemetryReading item, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(item, cancellationToken);

        public override bool TryComplete(Exception? error = null) => false;
    }
}
