using System.Text;
using System.Threading.Channels;
using PumpMonitor.App;
using PumpMonitor.Core.Devices;
using PumpMonitor.Core.Telemetry;

Console.OutputEncoding = Encoding.UTF8;

int deviceCount = Random.Shared.Next(3, 6);
SimulatedDevice[] devices = Enumerable.Range(1, deviceCount)
    .Select(i => new SimulatedDevice($"pump-{i}"))
    .ToArray();
var commands = new DeviceCommandHandler(devices);

// All console output goes through one object so log lines never break up the operator's typing.
var ui = new ConsoleUi();

// All devices write to one channel; a single consumer logs readings so producers never block on the console.
var channel = Channel.CreateUnbounded<TelemetryReading>(new UnboundedChannelOptions { SingleReader = true });

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Let Ctrl+C run the same graceful shutdown as 'quit' instead of killing the process.
    e.Cancel = true;
    shutdown.Cancel();
};

Task[] producers = devices.Select(d => RunDeviceAsync(d, channel.Writer, ui, shutdown.Token)).ToArray();
Task consumer = LogReadingsAsync(channel.Reader, ui);

ui.WriteLine($"Started {deviceCount} devices: {string.Join(", ", devices.Select(d => d.DeviceId))}");
ui.WriteLine(DeviceCommandHandler.HelpText);

await ReadCommandsAsync(commands, ui, shutdown);
ui.Close();

// Graceful shutdown: stop producers, then let the consumer drain whatever is left in the channel.
shutdown.Cancel();
await Task.WhenAll(producers);
channel.Writer.Complete();
await consumer;
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

static async Task LogReadingsAsync(ChannelReader<TelemetryReading> reader, ConsoleUi ui)
{
    await foreach (TelemetryReading r in reader.ReadAllAsync())
    {
        ui.WriteLine(
            $"{r.Timestamp.ToLocalTime():HH:mm:ss.fff}  {r.DeviceId,-7} #{r.SequenceNumber,-5} " +
            $"P={r.Pressure,6:F1} psi  T={r.Temperature,5:F1} °C");
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
