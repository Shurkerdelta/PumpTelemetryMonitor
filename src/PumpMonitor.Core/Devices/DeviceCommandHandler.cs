using System.Diagnostics.CodeAnalysis;
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
/// Commands (case-insensitive): <c>on [id...]</c>, <c>off [id...]</c>, <c>status</c>, <c>readings [on|off]</c>,
/// <c>fault spike|overheat|silence|reorder [id...]</c>, <c>help</c>, <c>quit</c>/<c>exit</c>.
/// Omitting the ids applies on/off/fault to every device. Alert thresholds are fixed at startup; <c>status</c> shows them.
/// </remarks>
public sealed class DeviceCommandHandler
{
    public const string HelpText =
        "Commands: on [id...] | off [id...] | status | readings [on|off] | " +
        "fault <spike|overheat|silence|reorder> [id...] | help | quit   (omit ids to apply to all devices)";

    private readonly Dictionary<string, SimulatedDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly AlertOptions? _alertOptions;
    private readonly Toggle? _showReadings;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceCommandHandler"/> class.
    /// </summary>
    /// <param name="devices">Devices that on/off/status/fault act on.</param>
    /// <param name="alertOptions">Alert thresholds that <c>status</c> shows and faults are sized from, if alerts are enabled.</param>
    /// <param name="showReadings">Setting the <c>readings</c> command flips, if reading output can be toggled.</param>
    public DeviceCommandHandler(IEnumerable<SimulatedDevice> devices, AlertOptions? alertOptions = null, Toggle? showReadings = null)
    {
        _alertOptions = alertOptions;
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
            "readings" => Readings(parts[1..]),
            "fault" => Fault(parts[1..]),
            "help" => new CommandResult(HelpText),
            "quit" or "exit" => new CommandResult("Shutting down...", ExitRequested: true),
            _ => new CommandResult($"Unknown command '{parts[0]}'. {HelpText}"),
        };
    }

    private CommandResult Switch(string[] deviceIds, bool on)
    {
        if (!TryResolve(deviceIds, out SimulatedDevice[] targets, out CommandResult? error))
        {
            return error;
        }

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

        return new CommandResult($"Turned {(on ? "on" : "off")}: {Describe(deviceIds, targets)}");
    }

    private CommandResult Fault(string[] args)
    {
        const string usage = "Usage: fault <spike|overheat|silence|reorder> [id...]";
        DeviceFault? fault = args.Length == 0 ? null : args[0].ToLowerInvariant() switch
        {
            "spike" => DeviceFault.PressureSpike,
            "overheat" => DeviceFault.Overheat,
            "silence" => DeviceFault.Silence,
            "reorder" => DeviceFault.Reorder,
            _ => null,
        };

        if (fault is null)
        {
            return new CommandResult(usage);
        }

        string[] deviceIds = args[1..];
        if (!TryResolve(deviceIds, out SimulatedDevice[] targets, out CommandResult? error))
        {
            return error;
        }

        // Aim at the alert thresholds so the fault always alerts.
        AlertOptions limits = _alertOptions ?? new AlertOptions();
        foreach (SimulatedDevice target in targets)
        {
            target.InjectFault(fault.Value, limits);
        }

        return new CommandResult($"Injected {args[0].ToLowerInvariant()}: {Describe(deviceIds, targets)}");
    }

    /// <summary>
    /// Maps ids to devices (all devices when none are given). Resolves every id before anything is applied,
    /// so a typo doesn't leave a partial change.
    /// </summary>
    private bool TryResolve(
        string[] deviceIds,
        out SimulatedDevice[] targets,
        [NotNullWhen(false)] out CommandResult? error)
    {
        string[] unknown = [.. deviceIds.Where(id => !_devices.ContainsKey(id))];
        if (unknown.Length > 0)
        {
            string names = string.Join(", ", unknown.Select(id => $"'{id}'"));
            targets = [];
            error = new CommandResult(
                $"Unknown device{(unknown.Length > 1 ? "s" : "")} {names}. Known devices: {string.Join(", ", _devices.Keys)}");
            return false;
        }

        targets = deviceIds.Length == 0
            ? [.. _devices.Values]
            : [.. deviceIds.Select(id => _devices[id]).Distinct()];
        error = null;
        return true;
    }

    private static string Describe(string[] deviceIds, SimulatedDevice[] targets) =>
        deviceIds.Length == 0 ? "all devices" : string.Join(", ", targets.Select(d => d.DeviceId));

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
            $"  pressure:    {options.PressureThreshold:F2} MPa",
            $"  temperature: {options.TemperatureThreshold:F1} °C",
            $"  silence:     {options.SilenceTimeout.TotalSeconds:F1} s");

    private string Status()
    {
        string devices = string.Join(Environment.NewLine, _devices.Values.Select(d => $"  {d.DeviceId}: {(d.IsOn ? "ON" : "OFF")}"));
        return _alertOptions is null
            ? devices
            : string.Join(Environment.NewLine, devices, "Alert thresholds:", DescribeThresholds(_alertOptions));
    }
}
