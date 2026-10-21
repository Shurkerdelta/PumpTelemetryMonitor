using System.Text;
using System.Threading.Channels;
using PumpMonitor.App;
using PumpMonitor.Core;
using PumpMonitor.Core.Devices;
using PumpMonitor.Core.Processing;
using PumpMonitor.Core.Telemetry;

Console.OutputEncoding = Encoding.UTF8;

int deviceCount = Random.Shared.Next(Constants.Simulation.MinDeviceCount, Constants.Simulation.MaxDeviceCount + 1);
SimulatedDevice[] devices = Enumerable.Range(1, deviceCount)
    .Select(i => new SimulatedDevice($"{Constants.Simulation.DeviceIdPrefix}{i}"))
    .ToArray();
var commands = new DeviceCommandHandler(devices);

// All console output goes through one object so log lines never break up the operator's typing.
var ui = new ConsoleUi();

// All devices write into the pipeline; its single consumer runs the handlers so producers never wait on them.
await using var pipeline = new TelemetryPipeline(
    [new ConsoleLogHandler(ui)],
    (reading, ex) => ui.WriteError($"Handler failed on {reading.DeviceId} #{reading.SequenceNumber}: {ex.Message}"));
pipeline.Start();

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Let Ctrl+C run the same graceful shutdown as 'quit' instead of killing the process.
    e.Cancel = true;
    shutdown.Cancel();
};

Task[] producers = devices.Select(d => RunDeviceAsync(d, pipeline.Writer, ui, shutdown.Token)).ToArray();

ui.WriteLine($"Started {deviceCount} devices: {string.Join(", ", devices.Select(d => d.DeviceId))}");
ui.WriteLine(DeviceCommandHandler.HelpText);

await ReadCommandsAsync(commands, ui, shutdown);
ui.Close();

// Graceful shutdown: stop producers first, then let the pipeline drain what's queued (bounded by a timeout).
shutdown.Cancel();
await Task.WhenAll(producers);
using (var drainTimeout = new CancellationTokenSource(Constants.Pipeline.DrainTimeout))
{
    await pipeline.StopAsync(drainTimeout.Token);
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

        CommandResult result = commands.Execute(line);
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
