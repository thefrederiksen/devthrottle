using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class GatewayAutostartTests
{
    [Fact]
    public void CommandLine_NoArguments_QuotesExeOnly()
    {
        Assert.Equal(
            "\"C:\\Users\\example\\AppData\\Local\\cc-director\\gateway\\cc-director-gateway.exe\"",
            GatewayAutostart.CommandLine(@"C:\Users\example\AppData\Local\cc-director\gateway\cc-director-gateway.exe"));
    }

    [Fact]
    public void CommandLine_WithArguments_AppendsThem()
    {
        Assert.Equal(
            "\"C:\\x\\cc-director-gateway.exe\" --managed",
            GatewayAutostart.CommandLine(@"C:\x\cc-director-gateway.exe", "--managed"));
    }

    [Fact]
    public void CommandLine_BlankArguments_TreatedAsNone()
    {
        Assert.Equal("\"C:\\x\\g.exe\"", GatewayAutostart.CommandLine(@"C:\x\g.exe", "  "));
    }

    [Fact]
    public void ValueName_IsStable()
    {
        // The Run-key value name is an on-machine contract (installer writes it, uninstaller
        // removes it, the app re-asserts it). Lock it against accidental rename.
        Assert.Equal("CcDirectorGateway", GatewayAutostart.ValueName);
    }
}

public class GatewayTrayInstallerTests
{
    [Fact]
    public void InstalledArguments_IsManaged()
    {
        // The installed tray app must run managed (supervise the Cockpit + self-update);
        // the relauncher and the autostart Run key both reuse this constant.
        Assert.Equal("--managed", GatewayTrayInstaller.InstalledArguments);
    }

    [Fact]
    public void DefaultPorts_AreCanonical()
    {
        Assert.Equal(7878, GatewayTrayInstaller.GatewayDefaultPort);
        Assert.Equal(7470, GatewayTrayInstaller.CockpitDefaultPort);
    }
}

public class UpdatePinsTests
{
    [Fact]
    public void Pin_BlocksMatchingVersion()
    {
        var pins = new UpdatePins();
        pins.Pin("cc-pdf", "1.2.0");

        Assert.True(pins.IsPinned("cc-pdf", "1.2.0"));
        Assert.True(pins.IsPinned("cc-pdf", "v1.2.0"));   // normalized compare
        Assert.False(pins.IsPinned("cc-pdf", "1.3.0"));   // a newer version is not pinned
        Assert.False(pins.IsPinned("cc-html", "1.2.0"));  // different component
    }

    [Fact]
    public void JsonRoundTrip_PreservesPins()
    {
        var pins = new UpdatePins();
        pins.Pin("director", "0.4.0");
        pins.Pin("cc-pdf", "1.2.0");

        var restored = UpdatePins.FromJson(pins.ToJson());

        Assert.True(restored.IsPinned("director", "0.4.0"));
        Assert.True(restored.IsPinned("cc-pdf", "1.2.0"));
    }

    [Fact]
    public void Clear_RemovesPin()
    {
        var pins = new UpdatePins();
        pins.Pin("cc-pdf", "1.2.0");
        pins.Clear("cc-pdf");
        Assert.False(pins.IsPinned("cc-pdf", "1.2.0"));
    }

    [Fact]
    public void FromJson_EmptyOrNull_IsEmpty()
    {
        Assert.False(UpdatePins.FromJson(null).IsPinned("x", "1.0.0"));
        Assert.False(UpdatePins.FromJson("").IsPinned("x", "1.0.0"));
    }
}
