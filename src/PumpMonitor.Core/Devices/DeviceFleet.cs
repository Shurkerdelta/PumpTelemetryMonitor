namespace PumpMonitor.Core.Devices;

/// <summary>Creates the set of simulated pumps the monitor watches.</summary>
public static class DeviceFleet
{
    public const int MinDeviceCount = 3;
    public const int MaxDeviceCount = 5;
    public const string DeviceIdPrefix = "pump-";

    /// <summary>
    /// Creates a random number of devices, from <see cref="MinDeviceCount"/> to <see cref="MaxDeviceCount"/>
    /// inclusive, named pump-1, pump-2, … Each device owns its random source, so they can run concurrently.
    /// </summary>
    /// <param name="random">Picks the count; tests pass a seeded one. Defaults to <see cref="Random.Shared"/>.</param>
    public static SimulatedDevice[] Create(Random? random = null)
    {
        int count = (random ?? Random.Shared).Next(MinDeviceCount, MaxDeviceCount + 1);
        return [.. Enumerable.Range(1, count).Select(i => new SimulatedDevice($"{DeviceIdPrefix}{i}"))];
    }
}
