using Microsoft.Extensions.Time.Testing;
using PumpMonitor.Core;
using PumpMonitor.Core.Alerts;
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

    [Fact]
    public void Threshold_UpdatesEachAlertThreshold()
    {
        var alerts = new AlertMonitor(_ => { });
        var handler = new DeviceCommandHandler(_devices, alerts);

        handler.Execute("threshold pressure 50.5");
        handler.Execute("THRESHOLD temperature 80");
        CommandResult result = handler.Execute("threshold silence 2.5");

        Assert.Equal(
            (50.5, 80.0, TimeSpan.FromSeconds(2.5)),
            (alerts.Options.PressureThreshold, alerts.Options.TemperatureThreshold, alerts.Options.SilenceTimeout));
        Assert.Contains("50.50 MPa", result.Message);
    }

    [Theory]
    [InlineData("threshold pressure")]
    [InlineData("threshold pressure abc")]
    [InlineData("threshold flow 10")]
    [InlineData("threshold silence 0")]
    [InlineData("threshold pressure NaN")]
    public void Threshold_InvalidInput_ShowsUsageAndLeavesOptionsUnchanged(string command)
    {
        var alerts = new AlertMonitor(_ => { });
        AlertOptions before = alerts.Options;

        CommandResult result = new DeviceCommandHandler(_devices, alerts).Execute(command);

        Assert.StartsWith("Usage:", result.Message);
        Assert.Same(before, alerts.Options);
    }

    [Fact]
    public void Threshold_WithoutArguments_ShowsCurrentValues_OrExplainsWhenAlertsAreOff()
    {
        var withAlerts = new DeviceCommandHandler(_devices, new AlertMonitor(_ => { }));

        string defaultPressure = $"{new AlertOptions().PressureThreshold:F2} MPa";
        Assert.Contains(defaultPressure, withAlerts.Execute("threshold").Message);
        Assert.Equal("Alerts are not enabled.", CreateHandler().Execute("threshold").Message);
    }

    [Fact]
    public void Readings_TurnsOutputOnAndOff_AndReportsState()
    {
        var showReadings = new Toggle();
        var handler = new DeviceCommandHandler(_devices, showReadings: showReadings);

        Assert.Equal("Readings are hidden.", handler.Execute("readings").Message);

        Assert.Equal("Readings are shown.", handler.Execute("READINGS on").Message);
        Assert.True(showReadings.IsOn);

        Assert.Equal("Readings are hidden.", handler.Execute("readings off").Message);
        Assert.False(showReadings.IsOn);
    }

    [Theory]
    [InlineData("readings maybe")]
    [InlineData("readings on now")]
    public void Readings_InvalidInput_ShowsUsageAndLeavesSettingUnchanged(string command)
    {
        var showReadings = new Toggle(isOn: true);

        CommandResult result = new DeviceCommandHandler(_devices, showReadings: showReadings).Execute(command);

        Assert.Equal("Usage: readings [on|off]", result.Message);
        Assert.True(showReadings.IsOn);
    }

    [Fact]
    public void Fault_InjectsIntoListedDevicesOnly()
    {
        var time = new FakeTimeProvider();
        SimulatedDevice CreateStubbed(string id) =>
            new(id, new StubPumpProcess(new ProcessState(55, 70)), timeProvider: time);
        SimulatedDevice[] devices = [CreateStubbed("pump-1"), CreateStubbed("pump-2")];
        var handler = new DeviceCommandHandler(devices);

        CommandResult result = handler.Execute("FAULT spike pump-2");

        Assert.Equal("Injected spike: pump-2", result.Message);
        Assert.Equal(55, devices[0].TakeReading().Pressure);
        Assert.Equal(
            new AlertOptions().PressureThreshold + new SimulatedDeviceOptions().FaultPressureMargin,
            devices[1].TakeReading().Pressure);
    }

    [Fact]
    public void Fault_AimsAtTheMonitorsCurrentThresholds()
    {
        var device = new SimulatedDevice("pump-1", new StubPumpProcess(new ProcessState(55, 70)), timeProvider: new FakeTimeProvider());
        var alerts = new AlertMonitor(_ => { });
        var handler = new DeviceCommandHandler([device], alerts);

        handler.Execute("threshold temperature 150");
        handler.Execute("fault overheat");

        Assert.Equal(150 + new SimulatedDeviceOptions().FaultTemperatureMargin, device.TakeReading().Temperature);
    }

    [Fact]
    public void Fault_WithoutIds_AppliesToAllDevices()
    {
        Assert.Equal("Injected reorder: all devices", CreateHandler().Execute("fault reorder").Message);
    }

    [Theory]
    [InlineData("fault")]
    [InlineData("fault meltdown pump-1")]
    public void Fault_UnknownType_ShowsUsage(string command)
    {
        Assert.StartsWith("Usage: fault", CreateHandler().Execute(command).Message);
    }

    [Fact]
    public void Fault_UnknownDevice_ListsKnownIds()
    {
        Assert.StartsWith("Unknown device 'pump-9'", CreateHandler().Execute("fault silence pump-1 pump-9").Message);
    }

    [Fact]
    public void Readings_WithoutToggle_ExplainsItIsUnavailable()
    {
        Assert.Equal("Reading output can't be toggled.", CreateHandler().Execute("readings on").Message);
    }
}
