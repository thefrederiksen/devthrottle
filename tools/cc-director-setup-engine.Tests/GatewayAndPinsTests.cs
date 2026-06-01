using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class GatewayServiceCommandsTests
{
    [Fact]
    public void RestartSequence_IsStopThenStartThenStatus()
    {
        var seq = GatewayServiceCommands.RestartSequence(@"C:\nssm.exe");
        Assert.Equal(3, seq.Count);
        Assert.Equal(["stop", "cc-gateway-service"], seq[0].Args);
        Assert.Equal(["start", "cc-gateway-service"], seq[1].Args);
        Assert.Equal(["status", "cc-gateway-service"], seq[2].Args);
        Assert.All(seq, c => Assert.Equal(@"C:\nssm.exe", c.Exe));
    }

    [Fact]
    public void Commands_UseCustomServiceName()
    {
        var stop = GatewayServiceCommands.Stop(@"C:\nssm.exe", "custom-svc");
        Assert.Equal(["stop", "custom-svc"], stop.Args);
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
