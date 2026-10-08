using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// THE REFUSED "go" OF 7 OCTOBER 2026, ON THE SESSION'S OWN SEND PATH (the Prompt Delivery mission). Session 110's
/// composer showed only Claude Code's faint guess at the next prompt, "yes, go on 2.18.0 after the follow-up merges".
/// Read as text, it made the echo of "go" uncountable and, after a clear that worked, read as "still holds text". These
/// drive <see cref="Session.SendTextAsync"/> - its own screen frames, its own composer readers - against a terminal that
/// draws the guess faint whenever its composer is empty, so a Session caller that went back to reading rows alone fails
/// here (review of pull request 3654, finding 1).
/// </summary>
public sealed class ClaudeSuggestionSendTests : IDisposable
{
    private const string Suggestion = "yes, go on 2.18.0 after the follow-up merges";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-suggestion-send-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _cleanup = new();

    public ClaudeSuggestionSendTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private (Session Session, ScriptedAgentTerminal Terminal) NewClaudeSessionShowingASuggestion()
    {
        var transcript = Path.Combine(_dir, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(AgentKind.ClaudeCode, transcript, _dir)
        {
            ConPtyPaint = true,
            Width = 124,
            Height = 41,
            Suggestion = Suggestion,
        };
        _cleanup.Add(terminal);
        var session = new Session(Guid.NewGuid(), _dir, _dir, null, terminal, SessionBackendType.ConPty) { AgentKind = AgentKind.ClaudeCode };
        _cleanup.Add(session);
        session.Resize(124, 41);
        session.UpdateClaudeSessionPointer(Guid.NewGuid().ToString(), transcript, "test");
        terminal.StartDrawing();
        return (session, terminal);
    }

    [Fact]
    public void SnapshotLiveFrame_OnlyTheSuggestion_ComposerReadsEmpty()
    {
        var (session, _) = NewClaudeSessionShowingASuggestion();

        var frame = session.SnapshotLiveFrame();

        Assert.Contains(frame.Rows, r => r.Contains(Suggestion, StringComparison.Ordinal));
        Assert.Equal(ComposerReading.Empty, DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, frame).Reading);
    }

    [Fact]
    public async Task SendTextAsync_PromptInsideTheSuggestion_ArrivesOnce()
    {
        var (session, terminal) = NewClaudeSessionShowingASuggestion();

        await session.SendTextAsync("go", SessionTestDoors.TestDoor);

        Assert.Equal(new[] { "go" }, terminal.Recorded);
        Assert.Equal(0, terminal.ClearKeysPressed);
    }

    [Fact]
    public async Task SendTextAsync_MarkSetAndTheSuggestionReturnsAfterTheClear_ClearsAndSends()
    {
        // The second half of session 110: an earlier send may have left "go", so the composer is cleared first, and the
        // empty composer then shows the guess again. That must read as the clear having worked.
        var (session, terminal) = NewClaudeSessionShowingASuggestion();
        ComposerRetention.MarkMayHoldText(terminal, "ClaudeCode", "go");
        terminal.Redraw();

        await session.SendTextAsync("go", SessionTestDoors.TestDoor);

        Assert.Equal(new[] { "go" }, terminal.Recorded);
        Assert.True(terminal.ClearKeysPressed > 0);
        Assert.False(ComposerRetention.MayHoldText(terminal));
    }
}
