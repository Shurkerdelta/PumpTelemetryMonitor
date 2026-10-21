namespace PumpMonitor.Core.Devices;

/// <summary>Outcome of a console command.</summary>
/// <param name="Message">Text to show the operator.</param>
/// <param name="ExitRequested">True when the operator asked to shut down.</param>
public sealed record CommandResult(string Message, bool ExitRequested = false);

/// <summary>
/// Parses operator commands and applies them to a set of devices. Kept free of console I/O
/// so it can be unit tested.
/// </summary>
/// <remarks>
/// Commands (case-insensitive): <c>on [id...]</c>, <c>off [id...]</c>, <c>status</c>, <c>help</c>,
/// <c>quit</c>/<c>exit</c>. Omitting the ids applies on/off to every device.
/// </remarks>
public sealed class DeviceCommandHandler
{
    public const string HelpText =
        "Commands: on [id...] | off [id...] | status | help | quit   (omit ids to apply to all devices)";

    private readonly Dictionary<string, SimulatedDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

    public DeviceCommandHandler(IEnumerable<SimulatedDevice> devices)
    {
        foreach (SimulatedDevice device in devices)
        {
            // Ids are matched case-insensitively, so ids differing only by case would be ambiguous.
            if (!_devices.TryAdd(device.DeviceId, device))
            {
                throw new ArgumentException(
                    $"Device ids must be unique ignoring case: '{device.DeviceId}' conflicts with '{_devices[device.DeviceId].DeviceId}'.",
                    nameof(devices));
            }
        }
    }

    public CommandResult Execute(string? input)
    {
        string[] parts = (input ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return new CommandResult("");
        }

        string command = parts[0].ToLowerInvariant();
        string[] deviceIds = parts[1..];

        return command switch
        {
            "on" => Switch(deviceIds, on: true),
            "off" => Switch(deviceIds, on: false),
            "status" => new CommandResult(Status()),
            "help" => new CommandResult(HelpText),
            "quit" or "exit" => new CommandResult("Shutting down...", ExitRequested: true),
            _ => new CommandResult($"Unknown command '{parts[0]}'. {HelpText}"),
        };
    }

    private CommandResult Switch(string[] deviceIds, bool on)
    {
        // Resolve every id before switching anything, so a typo doesn't leave a partial change.
        string[] unknown = deviceIds.Where(id => !_devices.ContainsKey(id)).ToArray();
        if (unknown.Length > 0)
        {
            string names = string.Join(", ", unknown.Select(id => $"'{id}'"));
            return new CommandResult(
                $"Unknown device{(unknown.Length > 1 ? "s" : "")} {names}. Known devices: {string.Join(", ", _devices.Keys)}");
        }

        SimulatedDevice[] targets = deviceIds.Length == 0
            ? _devices.Values.ToArray()
            : deviceIds.Select(id => _devices[id]).Distinct().ToArray();

        foreach (SimulatedDevice target in targets)
        {
            if (on)
            {
                target.TurnOn();
            }
            else
            {
                target.TurnOff();
            }
        }

        string affected = deviceIds.Length == 0 ? "all devices" : string.Join(", ", targets.Select(d => d.DeviceId));
        return new CommandResult($"Turned {(on ? "on" : "off")}: {affected}");
    }

    private string Status() =>
        string.Join(Environment.NewLine, _devices.Values.Select(d => $"  {d.DeviceId}: {(d.IsOn ? "ON" : "OFF")}"));
}
