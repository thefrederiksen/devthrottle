using CcDirector.Core.Account;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

public class FleetPreambleTests
{
    // The owner's rule: the code is his and no agent signs it. This is pinned as a test because the
    // preamble is the ONLY place the rule reaches every agent on every machine - a session gets it at
    // launch without having to discover a skill or read a file - and because several agent harnesses
    // instruct their model BY DEFAULT to add a co-authored-by trailer and a "generated with" line. A
    // default that must be overridden on every commit, in every repository, by every agent, is not a
    // rule anyone can be relied on to remember; it has to be told to them, every session, by us.
    // Silently dropping these lines would put a vendor's name on the owner's client deliverables.
    [Theory]
    [InlineData("NEVER SIGN IT")]
    [InlineData("Co-authored-by")]
    [InlineData("Generated with")]
    [InlineData("OVERRIDES")]
    [InlineData("are NOT to be rewritten")]
    public void Build_Always_CarriesTheNoAttributionRule(string required)
    {
        var text = FleetPreamble.Build(
            "a3dfb85e-49dd-442a-9e36-40fc44838783",
            "devthrottle",
            "MACHINE_A",
            @"C:\repos\devthrottle",
            new SignedInUser("soren@example.com", "Starlord"));

        Assert.Contains(required, text);
    }

    // The rule must reach a session that has nobody signed in too - the identity line is conditional,
    // the attribution rule is not.
    [Fact]
    public void Build_NoSignedInUser_StillCarriesTheNoAttributionRule()
    {
        var text = FleetPreamble.Build(
            "a3dfb85e-49dd-442a-9e36-40fc44838783",
            "devthrottle",
            "MACHINE_A",
            @"C:\repos\devthrottle",
            user: null);

        Assert.Contains("NEVER SIGN IT", text);
    }

    // Every agent DevThrottle drives is named, so no one reads the rule as Claude-only. The list is
    // asserted rather than described because "any assistant" is exactly the phrasing a model talks
    // itself past when its own harness told it otherwise.
    [Theory]
    [InlineData("Claude")]
    [InlineData("Codex")]
    [InlineData("Pi")]
    [InlineData("Gemini")]
    [InlineData("Copilot")]
    [InlineData("Cursor")]
    [InlineData("Grok")]
    public void Build_Always_NamesEveryAgentInTheAttributionRule(string agent)
    {
        var text = FleetPreamble.Build(
            "a3dfb85e-49dd-442a-9e36-40fc44838783",
            "devthrottle",
            "MACHINE_A",
            @"C:\repos\devthrottle",
            new SignedInUser("soren@example.com", "Starlord"));

        Assert.Contains(agent, text);
    }

    // Issue #1357: with a nickname set, the identity line names the user by the nickname and still
    // carries the email, and binds "me / my account / email me" to that user.
    [Fact]
    public void Build_SignedInUserWithNickname_ShowsNicknameAndEmailAndRule()
    {
        var text = FleetPreamble.Build(
            "a3dfb85e-49dd-442a-9e36-40fc44838783",
            "devthrottle",
            "MACHINE_A",
            @"C:\repos\devthrottle",
            new SignedInUser("soren@example.com", "Starlord"));

        Assert.Contains("The user of this session is Starlord (soren@example.com).", text);
        Assert.Contains("\"me / my account / email me\" means this user", text);
        Assert.Contains("do not guess identity from usage or the database", text);
    }

    // Issue #1357: with no nickname set, the line falls back to the email as the display name.
    [Fact]
    public void Build_SignedInUserWithoutNickname_ShowsEmailAsName()
    {
        var text = FleetPreamble.Build(
            "a3dfb85e-49dd-442a-9e36-40fc44838783",
            "devthrottle",
            "MACHINE_A",
            @"C:\repos\devthrottle",
            new SignedInUser("soren@example.com", Nickname: null));

        Assert.Contains("The user of this session is soren@example.com (soren@example.com).", text);
    }

    // Issue #1357: no signed-in user -> the identity line is omitted entirely (no blank line, no "null").
    [Fact]
    public void Build_NoSignedInUser_OmitsIdentityLine()
    {
        var text = FleetPreamble.Build(
            "a3dfb85e-49dd-442a-9e36-40fc44838783",
            "devthrottle",
            "MACHINE_A",
            @"C:\repos\devthrottle",
            user: null);

        Assert.DoesNotContain("The user of this session is", text);
        Assert.DoesNotContain("null", text);
    }

    // Issue #1357: the identity line stays ASCII when the user's values are ASCII.
    [Fact]
    public void Build_SignedInUser_IsAsciiOnly()
    {
        var text = FleetPreamble.Build(
            "603b2066-d587-40f2-a37c-a308cebb8038",
            "frontend",
            "MACHINE_A",
            @"C:\repos\devthrottle",
            new SignedInUser("person@example.com", "Ace"));

        Assert.All(text, ch => Assert.True(ch < 128, $"non-ASCII character U+{(int)ch:X4} in preamble"));
    }

    // Message Load mission, slice 5: every agent reads this text, so it must teach the queue - messages
    // are rare, queued, rung by one doorbell line and read from the inbox - and must never again say a
    // message interrupts or offer the removed blocking ask.
    [Fact]
    public void Build_TeachesTheQueuedInboxAndNotTheInterrupt()
    {
        var text = FleetPreamble.Build(
            "a3dfb85e-49dd-442a-9e36-40fc44838783",
            "devthrottle",
            "MACHINE_A",
            @"C:\repos\devthrottle");

        Assert.Contains("MESSAGES ARE RARE", text);
        Assert.Contains("Most sessions can message nobody", text);
        Assert.Contains("only the session that\nstarted you and the sessions you started, at most six an hour", text);
        Assert.Contains("Anything else goes in your report", text);
        Assert.Contains("never typed into a session mid-work", text);
        Assert.Contains("doorbell line tells it to run 'cc-devthrottle message inbox'", text);
        Assert.Contains("cc-devthrottle message inbox", text);
        Assert.Contains("--reply-wanted", text);
        Assert.Contains("cc-devthrottle message reply <id>", text);
        Assert.Contains("cc-devthrottle session raise", text);
        Assert.Contains("cc-devthrottle session report", text);
        Assert.DoesNotContain("message ask", text);
        Assert.DoesNotContain("interrupts", text);
        Assert.DoesNotContain("wait for its answer", text);
    }

    [Fact]
    public void Build_NamedSession_IncludesIdentityAndFleetCommands()
    {
        var text = FleetPreamble.Build(
            "a3dfb85e-49dd-442a-9e36-40fc44838783",
            "devthrottle",
            "MACHINE_A",
            @"C:\repos\devthrottle");

        // Identity: short id, name, machine, repo, and the full id are all present.
        Assert.Contains("a3dfb85e", text);
        Assert.Contains("devthrottle", text);
        Assert.Contains("MACHINE_A", text);
        Assert.Contains(@"C:\repos\devthrottle", text);
        Assert.Contains("a3dfb85e-49dd-442a-9e36-40fc44838783", text);

        // The canonical command is spelled out so the agent needs no skill lookup for
        // simple fleet operations.
        Assert.Contains("cc-devthrottle", text);
        Assert.Contains("session list", text);
        Assert.Contains("session whoami", text);
        Assert.Contains("session rename", text);
        Assert.Contains("message send", text);
        Assert.Contains("message inbox", text);
        Assert.DoesNotContain("message ask", text);
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
            "MACHINE_A",
            @"C:\repos\devthrottle");

        Assert.Contains("(unnamed)", text);
        Assert.Contains("603b2066", text);
    }

    [Fact]
    public void Build_IsAsciiOnly()
    {
        var text = FleetPreamble.Build(
            "603b2066-d587-40f2-a37c-a308cebb8038",
            "frontend",
            "MACHINE_A",
            @"C:\repos\devthrottle");

        // No Unicode: every character must be plain ASCII so it renders on every terminal.
        Assert.All(text, ch => Assert.True(ch < 128, $"non-ASCII character U+{(int)ch:X4} in preamble"));
    }
}
