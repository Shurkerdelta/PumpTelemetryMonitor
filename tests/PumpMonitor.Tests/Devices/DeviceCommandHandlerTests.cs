using PumpMonitor.Core.Devices;

namespace PumpMonitor.Tests.Devices;

public class DeviceCommandHandlerTests
{
    private readonly SimulatedDevice[] _devices =
    [
        new("pump-1"),
        new("pump-2"),
        new("pump-3"),
    ];

    private DeviceCommandHandler CreateHandler() => new(_devices);

    [Fact]
    public void OffAndOnWithoutId_SwitchEveryDevice()
    {
        DeviceCommandHandler handler = CreateHandler();

        handler.Execute("off");
        Assert.All(_devices, d => Assert.False(d.IsOn));

        handler.Execute("ON");
        Assert.All(_devices, d => Assert.True(d.IsOn));
    }

    [Fact]
    public void OffWithId_SwitchesOnlyThatDevice_CaseInsensitively()
    {
        CommandResult result = CreateHandler().Execute("off PUMP-2");

        Assert.Equal([true, false, true], _devices.Select(d => d.IsOn));
        Assert.False(result.ExitRequested);
        Assert.Equal("Turned off: pump-2", result.Message); // canonical id, not the typed casing
    }

    [Fact]
    public void OffWithSeveralIds_SwitchesEachListedDevice()
    {
        CommandResult result = CreateHandler().Execute("off pump-1 PUMP-3 pump-1");

        Assert.Equal([false, true, false], _devices.Select(d => d.IsOn));
        Assert.Equal("Turned off: pump-1, pump-3", result.Message);
    }

    [Fact]
    public void UnknownIdAmongSeveral_SwitchesNothingAndNamesEveryUnknownId()
    {
        CommandResult result = CreateHandler().Execute("off pump-1 pump-8 pump-9");

        Assert.All(_devices, d => Assert.True(d.IsOn));
        Assert.Contains("'pump-8', 'pump-9'", result.Message);
        Assert.DoesNotContain("'pump-1'", result.Message);
    }

    [Fact]
    public void Constructor_RejectsIdsThatDifferOnlyByCase()
    {
        var ex = Assert.Throws<ArgumentException>(() => new DeviceCommandHandler([new("pump-1"), new("PUMP-1")]));

        Assert.Contains("'PUMP-1' conflicts with 'pump-1'", ex.Message);
    }

    [Fact]
    public void UnknownDevice_LeavesDevicesUnchangedAndListsKnownIds()
    {
        CommandResult result = CreateHandler().Execute("off pump-9");

        Assert.All(_devices, d => Assert.True(d.IsOn));
        Assert.Contains("pump-9", result.Message);
        Assert.Contains("pump-1, pump-2, pump-3", result.Message);
    }

    [Theory]
    [InlineData("quit")]
    [InlineData("exit")]
    public void QuitAndExit_RequestShutdown(string command)
    {
        Assert.True(CreateHandler().Execute(command).ExitRequested);
    }

    [Fact]
    public void Status_ReportsEachDeviceState()
    {
        DeviceCommandHandler handler = CreateHandler();
        handler.Execute("off pump-3");

        string status = handler.Execute("status").Message;

        Assert.Contains("pump-1: ON", status);
        Assert.Contains("pump-3: OFF", status);
    }
}
