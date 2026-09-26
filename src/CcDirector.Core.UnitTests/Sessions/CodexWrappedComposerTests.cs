using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tests.Drivers;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// THE CODEX COMPOSER READS THE WHOLE BLOCK, NOT ONE ROW (Voice Delivery mission, phase 6, review finding 1). A Codex
/// prompt longer than the terminal is wide wraps onto continuation rows indented by the two columns the glyph and its
/// separator take (captured from Codex 0.157.1, fixture codex-wrapped-composer); the cursor sits at the end of the
/// LAST row. The reader read only the cursor's row, so a wrapped prompt read as NotFound, the region witness went
/// blind, and the send fell into the clear-and-retype rescue that phase 3 measured leaving a fragment of the text in
/// the composer of a send reported delivered. These tests draw a Codex screen the way the real one wraps.
/// </summary>
public sealed class CodexWrappedComposerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-codex-wrapped-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _cleanup = new();

    public CodexWrappedComposerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private (Session Session, ScriptedAgentTerminal Terminal) NewCodexSession(short cols, short rows)
    {
        var transcript = Path.Combine(_dir, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(AgentKind.Codex, transcript, _dir)
        {
            Width = cols,
            Height = rows,
            ShowTranscript = true,
        };
        _cleanup.Add(terminal);
        var session = new Session(Guid.NewGuid(), _dir, _dir, null, terminal, SessionBackendType.ConPty) { AgentKind = AgentKind.Codex };
        _cleanup.Add(session);
        session.Resize(cols, rows);
        session.UpdateClaudeSessionPointer(Guid.NewGuid().ToString(), transcript, "test");
        terminal.StartDrawing();
        // The established pattern for a scripted Codex session (BusyAgentSendTests): the Codex arrival proof reads
        // real Codex rollout files, which a scripted terminal has none of, so the session is given the Working
        // state and the send is confirmed the way a working session's is - by the composer releasing the text. The
        // screen itself stays idle (no working marker), so the echo and region judgements run exactly as written.
        session.ApplyTerminalActivityState(ActivityState.Working);
        return (session, terminal);
    }

    /// <summary>The prompt of every send: long enough to wrap on the narrow screens these tests use.</summary>
    private const string Prompt =
        "Reply with only the word OK. Marker: fresh quokka seventy one two three four five six seven eight " +
        "nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen";

    private static int CountOf(string hay, string needle) =>
        (hay.Length - hay.Replace(needle, "", StringComparison.Ordinal).Length) / needle.Length;

    [Fact]
    public async Task SendTextAsync_WrappedCodexPromptWhoseWordsAreAlreadyVisibleAbove_IsSentOnceWithNoClearAndNoRetype()
    {
        // The review's concrete case and issue #1592's: a wrapped Codex prompt whose words are already visible in the
        // conversation above (the same prompt sent before), so the byte stream cannot be trusted as the echo's witness
        // and the composer region alone must prove it. The one-row reader read the wrapped composer as NotFound, so the
        // region could never answer and the send fell into the clear-and-retype rescue.
        using var machine = PinnedMachineMemory.Healthy();
        const short cols = 60, rows = 20;
        var (session, terminal) = NewCodexSession(cols, rows);
        terminal.ReplyBytesAfterEnter = 4000; // the agent streams its answer, as a real one does
        terminal.TranscriptLines.Add("› " + Prompt);
        terminal.Redraw();
        var (rowsNow, cursorRow, cursorCol, visible, _) = session.SnapshotLiveScreen();
        Assert.Contains(rowsNow, r => r.StartsWith("› Reply with only the word OK.", StringComparison.Ordinal)); // the prompt's old copy is visible in the conversation above
        Assert.Equal(ComposerReading.Empty, DoorbellSafety.ReadComposer(AgentKind.Codex,
            new ScreenFrame(rowsNow, cursorRow, cursorCol, visible))); // an empty composer: the wrap is the send's own doing

        // Act
        await session.SendTextAsync(Prompt, SessionTestDoors.TestDoor);

        // Assert: one Enter, the prompt typed ONCE, nothing cleared, and the send is in the records exactly once.
        Assert.Equal(new[] { Prompt }, terminal.Recorded);
        Assert.Equal(1, terminal.EntersAccepted);
        Assert.Equal(1, CountOf(terminal.TypedText, Prompt));
        Assert.Equal(0, terminal.ClearKeysPressed);
        Assert.Equal("", terminal.Composer);
    }

    [Fact]
    public async Task SendTextAsync_WrappedCodexComposerFillingUpInStages_TheFillingIsProgressAndTheSendWaitsForIt()
    {
        // The "composer filling up is progress" rule (issue #3290) judged in the region: a loaded Codex takes its
        // characters in stages, and the region showing MORE of the text than before is the agent taking the typing in.
        // The cursor is already on a CONTINUATION row while the composer fills, so a reader that cannot see those
        // rows sees nothing at all: the prefix never grows, the filling counts as no progress, and the send clears
        // and retypes while the rest of the text is still on its way.
        using var machine = PinnedMachineMemory.Healthy();
        const short cols = 60, rows = 20;
        var (session, terminal) = NewCodexSession(cols, rows);
        terminal.ReplyBytesAfterEnter = 4000; // the agent streams its answer, as a real one does
        terminal.WrappedRowsDrawDelay = TimeSpan.FromSeconds(10); // the last row lands ten seconds after the rest
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Act
        await session.SendTextAsync(Prompt, SessionTestDoors.TestDoor);

        // Assert: the send waited for the filling composer and pressed Enter once - no clear, no retype, one copy.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"the send took {sw.Elapsed.TotalSeconds:F1}s");
        Assert.Equal(new[] { Prompt }, terminal.Recorded);
        Assert.Equal(1, terminal.EntersAccepted);
        Assert.Equal(1, CountOf(terminal.TypedText, Prompt));
        Assert.Equal(0, terminal.ClearKeysPressed);
        Assert.Equal("", terminal.Composer);
    }
}
