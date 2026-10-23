using PumpMonitor.Core.Devices;

namespace PumpMonitor.Tests.Devices;

public class DeviceFleetTests
{
    [Fact]
    public void Create_MakesThreeToFiveDevices_AndEveryCountInThatRangeOccurs()
    {
        int[] counts = [.. Enumerable.Range(0, 100).Select(seed => DeviceFleet.Create(new Random(seed)).Length)];

        Assert.All(counts, count => Assert.InRange(count, 3, 5));
        Assert.Equal([3, 4, 5], counts.Distinct().Order());
    }

    [Fact]
    public void Create_NamesDevicesInOrderFromOne()
    {
        SimulatedDevice[] devices = DeviceFleet.Create(new Random(1));

        Assert.Equal(Enumerable.Range(1, devices.Length).Select(i => $"pump-{i}"), devices.Select(d => d.DeviceId));
    }
}
