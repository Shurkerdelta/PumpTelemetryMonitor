using System.Text;
using System.Threading.Channels;
using PumpMonitor.App;
using PumpMonitor.Core;
using PumpMonitor.Core.Alerts;
using PumpMonitor.Core.Devices;
using PumpMonitor.Core.Processing;
using PumpMonitor.Core.Telemetry;

Console.OutputEncoding = Encoding.UTF8;

// App-level settings. Per-component defaults (pump behaviour, reading intervals, alert thresholds)
// live on their options records: PumpProcessOptions, SimulatedDeviceOptions and AlertOptions.
// The device count (3–5) and naming live on DeviceFleet.
TimeSpan silenceCheckInterval = TimeSpan.FromMilliseconds(500); // bounds how late a silence alert can be
TimeSpan drainTimeout = TimeSpan.FromSeconds(5);                // how long shutdown waits for queued readings

// Every reading is too noisy for normal use, so they start hidden. Pass --show-readings (or -v) to start
// with them printed for debugging; the 'readings on|off' command flips this while running.
var showReadings = new Toggle(args.Any(a => a is "--show-readings" or "-v"));
string[] unknownArgs = [.. args.Where(a => a is not ("--show-readings" or "-v"))];

SimulatedDevice[] devices = DeviceFleet.Create();

// All console output goes through one object so log lines never break up the operator's typing.
var ui = new ConsoleUi();

var alertOptions = new AlertOptions();
Action<Alert> printAlert = alert => ui.WriteLine(
    $"!! ALERT {alert.Timestamp.ToLocalTime():HH:mm:ss.fff}  {alert.DeviceId,-7} {alert.Type}: {alert.Message}");
var alerts = new AlertMonitor(printAlert, alertOptions);
var silence = new SilenceMonitor(printAlert, alertOptions, devices.Select(d => d.DeviceId));
var commands = new DeviceCommandHandler(devices, alertOptions, showReadings);

// All devices write into the pipeline; its single consumer runs the handlers so producers never wait on them.
await using var pipeline = new TelemetryPipeline(
    [new ConsoleLogHandler(ui, showReadings), alerts],
    (reading, ex) => ui.WriteError($"Handler failed on {reading.DeviceId} #{reading.SequenceNumber}: {ex.Message}"));
pipeline.Start();

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Let Ctrl+C run the same graceful shutdown as 'quit' instead of killing the process. Once shutdown
    // has started, Ctrl+C kills the process as usual, in case shutdown hangs.
    if (!shutdown.IsCancellationRequested)
    {
        e.Cancel = true;
        shutdown.Cancel();
    }
};

// Readings count as heard from when they're queued, so a backed-up pipeline isn't mistaken for silent devices.
ChannelWriter<TelemetryReading> writer = silence.Track(pipeline.Writer);
Task[] producers = [.. devices.Select(d => RunDeviceAsync(d, writer, ui, shutdown.Token))];
Task silenceWatch = silence.RunAsync(
    silenceCheckInterval, ex => ui.WriteError($"Silence check failed: {ex.Message}"), shutdown.Token);

if (unknownArgs.Length > 0)
{
    ui.WriteError($"Ignoring unknown arguments: {string.Join(" ", unknownArgs)}. Supported: --show-readings (-v)");
}

ui.WriteLine($"Started {devices.Length} devices: {string.Join(", ", devices.Select(d => d.DeviceId))}");
ui.WriteLine(showReadings.IsOn
    ? "Printing every reading. Type 'readings off' to hide them."
    : "Readings are hidden; only alerts are printed. Type 'readings on' (or start with --show-readings) to see them.");
ui.WriteLine(DeviceCommandHandler.HelpText);

try
{
    await ReadCommandsAsync(commands, ui, shutdown);
}
finally
{
    // Graceful shutdown, even if the command loop failed: stop producers (and the silence watch, so stopped
    // devices aren't reported as silent), then let the pipeline drain what's queued (bounded by a timeout).
    ui.Close();
    shutdown.Cancel();
    await Task.WhenAll([.. producers, silenceWatch]);
    using var drainCts = new CancellationTokenSource(drainTimeout);
    await pipeline.StopAsync(drainCts.Token);
}

if (pipeline.DroppedCount > 0)
{
    ui.WriteLine($"{pipeline.DroppedCount} readings were dropped because processing fell behind.");
}

ui.WriteLine("All devices stopped.");

static async Task RunDeviceAsync(SimulatedDevice device, ChannelWriter<TelemetryReading> writer, ConsoleUi ui, CancellationToken cancellationToken)
{
    // Report a crashed device as soon as it happens, and never let it fault the producer task:
    // shutdown awaits all producers before completing the channel, so a throw there would skip the drain.
    try
    {
        await device.RunAsync(writer, cancellationToken);
    }
    catch (Exception ex)
    {
        ui.WriteError($"{device.DeviceId} stopped unexpectedly: {ex.Message}");
    }
}

static async Task ReadCommandsAsync(DeviceCommandHandler commands, ConsoleUi ui, CancellationTokenSource shutdown)
{
    // Reading input blocks and can't be cancelled, so run it in the background and race it
    // against shutdown (e.g. Ctrl+C). An abandoned read is harmless once the process exits.
    Task cancelled = Task.Delay(Timeout.Infinite, shutdown.Token);
    while (true)
    {
        Task<string?> read = Task.Run(ui.ReadLine);
        if (await Task.WhenAny(read, cancelled) == cancelled)
        {
            return;
        }

        string? line = await read;
        if (line is null)
        {
            return; // stdin closed
        }

        CommandResult result;
        try
        {
            result = commands.Execute(line);
        }
        catch (Exception ex)
        {
            // A failed command must not take the monitor down with it.
            ui.WriteError($"Command failed: {ex.Message}");
            continue;
        }

        if (result.Message.Length > 0)
        {
            ui.WriteLine(result.Message);
        }

        if (result.ExitRequested)
        {
            return;
        }
    }
}
