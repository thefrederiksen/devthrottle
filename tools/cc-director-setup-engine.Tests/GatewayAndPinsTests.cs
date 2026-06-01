using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class GatewayServiceCommandsTests
{
    [Fact]
    public void Stop_Start_Delete_UseScExe()
    {
        Assert.Equal("sc.exe", GatewayServiceCommands.Stop().Exe);
        Assert.Equal("stop cc-gateway-service", GatewayServiceCommands.Stop().Arguments);
        Assert.Equal("start cc-gateway-service", GatewayServiceCommands.Start().Arguments);
        Assert.Equal("delete cc-gateway-service", GatewayServiceCommands.Delete().Arguments);
    }

    [Fact]
    public void Create_QuotesSpaceContainingExePathInsideBinPath()
    {
        var cmd = GatewayServiceCommands.Create(@"C:\Program Files\CC Director\gateway\cc-director-gateway.exe", 7878);
        Assert.Equal("sc.exe", cmd.Exe);
        // binPath wraps the exe path in inner escaped quotes and appends --port.
        Assert.Contains(@"binPath= ""\""C:\Program Files\CC Director\gateway\cc-director-gateway.exe\"" --port 7878""", cmd.Arguments);
        Assert.Contains("start= auto", cmd.Arguments);
        Assert.Contains("obj= LocalSystem", cmd.Arguments);
    }

    [Fact]
    public void SetEnvironment_WritesRegMultiSzWithExpectedVars()
    {
        var cmd = GatewayServiceCommands.SetEnvironment(
            @"C:\Users\example\AppData\Local\cc-director", "sk-test", @"C:\Program Files\CC Director\cockpit\cc-director-cockpit.exe");
        Assert.Equal("reg.exe", cmd.Exe);
        Assert.Contains(@"HKLM\SYSTEM\CurrentControlSet\Services\cc-gateway-service", cmd.Arguments);
        Assert.Contains("REG_MULTI_SZ", cmd.Arguments);
        Assert.Contains(@"CC_DIRECTOR_ROOT=C:\Users\example\AppData\Local\cc-director\0OPENAI_API_KEY=sk-test\0CC_COCKPIT_MANAGED=1\0CC_COCKPIT_EXE=", cmd.Arguments);
    }

    [Fact]
    public void Commands_UseCustomServiceName()
    {
        var stop = GatewayServiceCommands.Stop("custom-svc");
        Assert.Equal("stop custom-svc", stop.Arguments);
        Assert.Contains("custom-svc", stop.Display);
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
