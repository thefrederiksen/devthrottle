using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tests.Drivers;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// THE WEDGE THAT NEEDS NO LOAD (Voice Delivery mission, phase 6, task C). On 25 September 2026 a fresh, idle
/// session was wedged by two defects that both judged a prompt by counting copies of its text ANYWHERE on the
/// screen - in the conversation above the composer as much as in the composer itself:
///  - the echo check counted the copies already visible in the conversation and demanded a NEW one anywhere on
///    screen, so a repeated short prompt ("yes", "continue", "send it") could miss its echo when an old copy
///    scrolled off at the same moment, and was reported not-delivered with its text left in the composer;
///  - the retained-text check then read those same conversation copies as the orphan still being in the composer,
///    and every later send was refused while the composer on screen was empty.
/// Both must be judged from the COMPOSER REGION of the real screen - the rows between the two rules around the
/// prompt, which the composer reader already locates - never from occurrences of the text anywhere on the screen.
/// These tests draw a Claude Code screen with a transcript above the composer, the way the pseudo console does.
/// </summary>
public sealed class RepeatedPromptWedgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-repeated-prompt-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _cleanup = new();

    public RepeatedPromptWedgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private (Session Session, ScriptedAgentTerminal Terminal) NewClaudeSession(short cols, short rows,
        string composer = "")
    {
        var transcript = Path.Combine(_dir, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(AgentKind.ClaudeCode, transcript, _dir)
        {
            ConPtyPaint = true,
            ShowTranscript = true,
            Width = cols,
            Height = rows,
        };
        terminal.SetComposer(composer);
        _cleanup.Add(terminal);
        var session = new Session(Guid.NewGuid(), _dir, _dir, null, terminal, SessionBackendType.ConPty) { AgentKind = AgentKind.ClaudeCode };
        _cleanup.Add(session);
        session.Resize(cols, rows);
        session.UpdateClaudeSessionPointer(Guid.NewGuid().ToString(), transcript, "test");
        terminal.StartDrawing();
        return (session, terminal);
    }

    // ===== the echo check judges the composer region, never the whole screen ==============================

    [Fact]
    public async Task SendTextAsync_IdenticalPromptAlreadyVisibleInTheConversation_IsSentOnceAndAgainAfterIt()
    {
        // The owner's 09:26 case and QA's target 2 recipe: a prompt identical to text already visible on screen
        // (the same words in the conversation above the composer), sent once, and a second identical short
        // prompt after it. The echo is seen in the COMPOSER REGION - the copies in the conversation above are
        // not counted against it and never counted as its echo.
        using var machine = PinnedMachineMemory.Healthy();
        var (session, terminal) = NewClaudeSession(120, 30);
        const string prompt = "yes";

        // Act: three identical short prompts in a row; the first puts the text in the conversation above.
        await session.SendTextAsync(prompt, SessionTestDoors.TestDoor);
        await session.SendTextAsync(prompt, SessionTestDoors.TestDoor);
        await session.SendTextAsync(prompt, SessionTestDoors.TestDoor);

        // Assert: each was sent exactly once, the composer ended empty, and nothing was left behind.
        Assert.Equal(new[] { prompt, prompt, prompt }, terminal.Recorded);
        Assert.Equal("", terminal.Composer);
    }

    [Fact]
    public async Task SendTextAsync_AnOldCopyScrollsOffWhileTheEchoArrives_TheEchoInTheComposerRegionStillCounts()
    {
        // The defect's sharpest shape, found in the rig log of the 22:32 echo miss: the echo check counted copies
        // of the text ANYWHERE on screen and demanded a NEW one, so a copy scrolling off the top of the screen at
        // the same moment the composer drew the new text cancelled the echo. The whole-screen count never rose
        // above what it was before the typing, the send waited out its deadline and reported not-delivered (the
        // clear-and-retype rescue cannot run either: the agent is working, as the rig's was). Judged in the
        // composer region, the echo is there and the prompt is sent.
        using var machine = PinnedMachineMemory.Healthy();
        const short cols = 120, rows = 12;
        var (session, terminal) = NewClaudeSession(cols, rows);
        terminal.Working = true;
        terminal.SpinnerInterval = TimeSpan.FromSeconds(30); // the working frame stands; the terminal goes quiet
        const string prompt = "yes";

        // Arrange: a transcript that fills the screen, with the prompt's old copy on the TOP visible row - and
        // one more line landing on the next paint, as the previous turn's answer does, scrolling that copy off.
        terminal.TranscriptLines.Add("❯ " + prompt);
        for (var i = 0; i < rows - 6; i++) terminal.TranscriptLines.Add($"  older turn line {i}");
        terminal.Redraw();
        Assert.Contains("❯ yes", session.SnapshotScreenRows());
        terminal.TranscriptLineOnNextPaint = "  the previous turn finished";

        // Act
        await session.SendTextAsync(prompt, SessionTestDoors.TestDoor);

        // Assert: the old copy scrolled off and the new one arrived in the composer - sent once either way.
        Assert.DoesNotContain("❯ yes", session.SnapshotScreenRows().Take(1));
        Assert.Equal(new[] { prompt }, terminal.Recorded);
        Assert.Equal("", terminal.Composer);
    }

    // ===== the retained-text check reads the composer, not the conversation =================================

    [Fact]
    public async Task SendTextAsync_RetainedTextOnlyInTheConversationAbove_AnEmptyComposerIsNotRefused()
    {
        // An earlier send of the same words gave up and left its mark; the words are visible in the conversation
        // above the composer (the owner sent them themselves, or they match an earlier prompt there), and the
        // composer on screen is EMPTY. The retained-text check must not read the conversation as the composer:
        // no refusal, and the next prompt is typed and sent.
        using var machine = PinnedMachineMemory.Healthy();
        var (session, terminal) = NewClaudeSession(120, 30);
        const string orphan = "yes";
        terminal.TranscriptLines.Add("❯ " + orphan);
        terminal.Redraw();
        ComposerRetention.MarkMayHoldText(terminal, "ClaudeCode", orphan);
        const string next = "Reply with only the word OK. Marker: fresh quokka seventy.";

        // Act
        await session.SendTextAsync(next, SessionTestDoors.TestDoor);

        // Assert: no refusal, the next prompt went in once, and the composer ended empty.
        Assert.Equal(new[] { next }, terminal.Recorded);
        Assert.Equal("", terminal.Composer);
    }

    [Fact]
    public async Task SendTextAsync_RetainedTextOnlyInTheConversationAbove_TheOwnersOwnTextIsNeitherClearedNorTypedOver()
    {
        // The retained-text check used to search the WHOLE screen, so the orphan's words merely visible in the
        // conversation above licensed a CLEAR over whatever the composer really held - the owner's own words typed
        // since, on the word of copies that were never in the composer at all. Judged in the composer region, the
        // orphan is not there, the owner's text is not cleared, and nothing is typed over it (#3290): the send is
        // refused with what it read, and the owner's words stay theirs.
        using var machine = PinnedMachineMemory.Healthy();
        var (session, terminal) = NewClaudeSession(120, 30, composer: "keep my draft");
        const string orphan = "yes";
        terminal.TranscriptLines.Add("❯ " + orphan);
        terminal.Redraw();
        ComposerRetention.MarkMayHoldText(terminal, "ClaudeCode", orphan);
        const string next = "Reply with only the word OK. Marker: guarded quokka seventy.";

        // Act
        var refused = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => session.SendTextAsync(next, SessionTestDoors.TestDoor));

        // Assert: the owner's words are untouched, nothing was typed over them, and the refusal says what it read.
        Assert.Equal("keep my draft", terminal.Composer);
        Assert.Equal("", terminal.TypedText);
        Assert.Empty(terminal.Recorded);
        Assert.Contains("cannot account for", refused.Message);
        Assert.Contains("text='keep my draft'", refused.Message);
    }

    [Fact]
    public async Task SendTextAsync_ComposerReallyHoldsTheRetainedTextWithCopiesAbove_IsStillRefused()
    {
        // The #3290 guard still bites: the retained text really IS in the composer (a copy is in the conversation
        // above as well, so only the composer region can tell the two apart), and the clear cannot empty it.
        // Nothing is typed over text the Director cannot account for.
        using var machine = PinnedMachineMemory.Healthy();
        var (session, terminal) = NewClaudeSession(120, 30, composer: "yes");
        terminal.TranscriptLines.Add("❯ yes");
        terminal.Redraw();
        ComposerRetention.MarkMayHoldText(terminal, "ClaudeCode", "yes");
        terminal.IgnoreClearKeys = true;
        const string next = "Reply with only the word OK. Marker: refused quokka seventy.";

        // Act
        var refused = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => session.SendTextAsync(next, SessionTestDoors.TestDoor));

        // Assert: the refusal names what it read, and nothing was typed into text it cannot account for.
        Assert.Equal("yes", terminal.Composer);
        Assert.Equal("", terminal.TypedText);
        Assert.Empty(terminal.Recorded);
        Assert.Contains("reading=HoldsText", refused.Message);
        Assert.Contains("text='yes'", refused.Message);
    }
}
