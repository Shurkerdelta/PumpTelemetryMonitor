namespace PumpMonitor.Core.Devices;

/// <summary>Timing and start-up behaviour for a <see cref="SimulatedDevice"/>.</summary>
public sealed record SimulatedDeviceOptions
{
    /// <summary>Shortest delay between readings.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Longest delay between readings. Set equal to <see cref="MinInterval"/> for a fixed rate.</summary>
    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Whether the pump is running when the device is created.</summary>
    public bool StartOn { get; init; } = true;
}
