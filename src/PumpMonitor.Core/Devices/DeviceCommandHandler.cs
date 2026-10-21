using System.Globalization;
using PumpMonitor.Core.Alerts;

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
/// Commands (case-insensitive): <c>on [id...]</c>, <c>off [id...]</c>, <c>status</c>,
/// <c>threshold [pressure|temperature|silence value]</c>, <c>readings [on|off]</c>, <c>help</c>,
/// <c>quit</c>/<c>exit</c>. Omitting the ids applies on/off to every device.
/// </remarks>
public sealed class DeviceCommandHandler
{
    public const string HelpText =
        "Commands: on [id...] | off [id...] | status | threshold [pressure|temperature|silence <value>] | " +
        "readings [on|off] | help | quit   (omit ids to apply to all devices)";

    private readonly Dictionary<string, SimulatedDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly AlertMonitor? _alerts;
    private readonly Toggle? _showReadings;

    /// <param name="devices">Devices that on/off/status act on.</param>
    /// <param name="alerts">Monitor whose thresholds the <c>threshold</c> command shows and adjusts, if any.</param>
    /// <param name="showReadings">Setting the <c>readings</c> command flips, if reading output can be toggled.</param>
    public DeviceCommandHandler(IEnumerable<SimulatedDevice> devices, AlertMonitor? alerts = null, Toggle? showReadings = null)
    {
        _alerts = alerts;
        _showReadings = showReadings;
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
            "threshold" => Threshold(parts[1..]),
            "readings" => Readings(parts[1..]),
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

    private CommandResult Threshold(string[] args)
    {
        if (_alerts is null)
        {
            return new CommandResult("Alerts are not enabled.");
        }

        if (args.Length == 0)
        {
            return new CommandResult(DescribeThresholds(_alerts.Options));
        }

        const string usage = "Usage: threshold pressure <MPa> | threshold temperature <°C> | threshold silence <seconds>";
        if (args.Length != 2
            || !double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
        {
            return new CommandResult(usage);
        }

        AlertOptions current = _alerts.Options;
        AlertOptions? updated = args[0].ToLowerInvariant() switch
        {
            "pressure" => current with { PressureThreshold = value },
            "temperature" => current with { TemperatureThreshold = value },
            "silence" when value > 0 => current with { SilenceTimeout = TimeSpan.FromSeconds(value) },
            _ => null,
        };

        if (updated is null)
        {
            return new CommandResult(usage);
        }

        _alerts.Options = updated;
        return new CommandResult($"Thresholds updated.{Environment.NewLine}{DescribeThresholds(updated)}");
    }

    private CommandResult Readings(string[] args)
    {
        if (_showReadings is null)
        {
            return new CommandResult("Reading output can't be toggled.");
        }

        string? arg = args.Length == 1 ? args[0].ToLowerInvariant() : null;
        switch (args.Length, arg)
        {
            case (0, _):
                break;
            case (1, "on"):
                _showReadings.IsOn = true;
                break;
            case (1, "off"):
                _showReadings.IsOn = false;
                break;
            default:
                return new CommandResult("Usage: readings [on|off]");
        }

        return new CommandResult($"Readings are {(_showReadings.IsOn ? "shown" : "hidden")}.");
    }

    private static string DescribeThresholds(AlertOptions options) =>
        string.Join(Environment.NewLine,
            $"  pressure:    {options.PressureThreshold:F2} MPa (re-arms below {options.PressureThreshold - options.PressureDeadband:F2})",
            $"  temperature: {options.TemperatureThreshold:F1} °C (re-arms below {options.TemperatureThreshold - options.TemperatureDeadband:F1})",
            $"  silence:     {options.SilenceTimeout.TotalSeconds:F1} s");

    private string Status() =>
        string.Join(Environment.NewLine, _devices.Values.Select(d => $"  {d.DeviceId}: {(d.IsOn ? "ON" : "OFF")}"));
}
