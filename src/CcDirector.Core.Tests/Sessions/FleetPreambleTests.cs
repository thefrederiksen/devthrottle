using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

public class FleetPreambleTests
{
    [Fact]
    public void Build_NamedSession_IncludesIdentityAndFleetCommands()
    {
        var text = FleetPreamble.Build(
            "a3dfb85e-49dd-442a-9e36-40fc44838783",
            "devthrottle",
            "SOREN_NORTH",
            @"D:\ReposFred\devthrottle");

        // Identity: short id, name, machine, repo, and the full id are all present.
        Assert.Contains("a3dfb85e", text);
        Assert.Contains("devthrottle", text);
        Assert.Contains("SOREN_NORTH", text);
        Assert.Contains(@"D:\ReposFred\devthrottle", text);
        Assert.Contains("a3dfb85e-49dd-442a-9e36-40fc44838783", text);

        // The canonical command is spelled out so the agent needs no skill lookup for
        // simple fleet operations.
        Assert.Contains("cc-devthrottle", text);
        Assert.Contains("session list", text);
        Assert.Contains("session whoami", text);
        Assert.Contains("session rename", text);
        Assert.Contains("message send", text);
        Assert.Contains("message ask", text);
        Assert.Contains("session spawn", text);
        Assert.DoesNotContain("cc-rename", text);
        Assert.DoesNotContain("cc-sessions", text);
        Assert.DoesNotContain("cc-whoami", text);
        Assert.DoesNotContain("cc-send", text);
        Assert.DoesNotContain("cc-ask", text);
        Assert.DoesNotContain("cc-spawn", text);
    }

    [Fact]
    public void Build_UnnamedSession_RendersUnnamedPlaceholder()
    {
        var text = FleetPreamble.Build(
            "603b2066-d587-40f2-a37c-a308cebb8038",
            name: null,
            "SOREN_NORTH",
            @"D:\ReposFred\devthrottle");

        Assert.Contains("(unnamed)", text);
        Assert.Contains("603b2066", text);
    }

    [Fact]
    public void Build_IsAsciiOnly()
    {
        var text = FleetPreamble.Build(
            "603b2066-d587-40f2-a37c-a308cebb8038",
            "frontend",
            "SOREN_NORTH",
            @"D:\ReposFred\devthrottle");

        // No Unicode: every character must be plain ASCII so it renders on every terminal.
        Assert.All(text, ch => Assert.True(ch < 128, $"non-ASCII character U+{(int)ch:X4} in preamble"));
    }
}
