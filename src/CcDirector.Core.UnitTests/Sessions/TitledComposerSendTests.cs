using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// THE WEDGE OF 29 SEPTEMBER 2026, ON THE SEND PATH (issue 3481). Session 121 was a named Claude Code session, so its
/// composer's top rule carried the name ("──── keynote deck slide reordering ─"). With a retention mark set, every send
/// cleared the composer and then asked the reader whether it was empty; the reader could not find a composer framed by
/// a titled rule, answered NotFound, the send took that for "still holds text" and refused - every send, for good.
/// These drive the real send through the real screen, drawn the way the captured frames show it, at the 161 by 41 size
/// that session had.
/// </summary>
public sealed class TitledComposerSendTests : IDisposable
{
    private const string Title = "keynote deck slide reordering";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-titled-send-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _cleanup = new();

    public TitledComposerSendTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private (Session Session, ScriptedAgentTerminal Terminal) NewTitledClaudeSession(string composer = "")
    {
        var transcript = Path.Combine(_dir, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(AgentKind.ClaudeCode, transcript, _dir)
        {
            ConPtyPaint = true,
            Width = 161,
            Height = 41,
            SessionTitle = Title,
        };
        terminal.SetComposer(composer);
        _cleanup.Add(terminal);
        var session = new Session(Guid.NewGuid(), _dir, _dir, null, terminal, SessionBackendType.ConPty) { AgentKind = AgentKind.ClaudeCode };
        _cleanup.Add(session);
        session.Resize(161, 41);
        session.UpdateClaudeSessionPointer(Guid.NewGuid().ToString(), transcript, "test");
        terminal.StartDrawing();
        return (session, terminal);
    }

    [Fact]
    public async Task SendTextAsync_TitledComposerHoldingTheRetainedText_ClearsItAndSends()
    {
        // Arrange: the case of 06:26 on 29 September - an earlier send's words are still in a named session's composer,
        // and the mark says so.
        const string orphan = "make a new video of version 36";
        var (session, terminal) = NewTitledClaudeSession(composer: orphan);
        ComposerRetention.MarkMayHoldText(terminal, "ClaudeCode", orphan);
        terminal.Redraw();

        // Act
        const string next = "Reply with only the word OK. Marker: titled quokka.";
        await session.SendTextAsync(next, SessionTestDoors.TestDoor);

        // Assert: the orphan was cleared and only the new prompt went in.
        Assert.Equal(new[] { next }, terminal.Recorded);
        Assert.Equal("", terminal.Composer);
        Assert.False(ComposerRetention.MayHoldText(terminal));
    }

    [Fact]
    public async Task SendTextAsync_TitledComposerEmptyWithAMark_Sends()
    {
        // Arrange: the case from 07:12:50 on - the mark stands, the composer on screen is empty.
        var (session, terminal) = NewTitledClaudeSession();
        ComposerRetention.MarkMayHoldText(terminal, "ClaudeCode", "make a new video of version 36");
        terminal.Redraw();

        // Act
        const string next = "Reply with only the word OK. Marker: empty titled quokka.";
        await session.SendTextAsync(next, SessionTestDoors.TestDoor);

        // Assert
        Assert.Equal(new[] { next }, terminal.Recorded);
    }
}
