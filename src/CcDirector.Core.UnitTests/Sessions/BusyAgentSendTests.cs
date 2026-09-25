using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// THE BUSY-AGENT SEND (Voice Delivery mission, phase 3), driven through the Director's own <see cref="Session"/> against
/// a scripted agent terminal that draws its screen the way Claude Code and Codex do: a composer, and while it works a
/// spinner that repaints without a break, so the terminal is never quiet. That is the screen that held a spoken prompt
/// for 122 seconds on 25 September 2026 at 09:05.
/// </summary>
public sealed class BusyAgentSendTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-busy-send-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _cleanup = new();

    public BusyAgentSendTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private (Session Session, ScriptedAgentTerminal Terminal) NewWorkingSession(AgentKind agent, string composer = "")
    {
        var transcript = Path.Combine(_dir, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(agent, transcript, _dir) { Working = true };
        terminal.SetComposer(composer);
        _cleanup.Add(terminal);
        var session = new Session(Guid.NewGuid(), _dir, _dir, null, terminal, SessionBackendType.ConPty) { AgentKind = agent };
        _cleanup.Add(session);
        session.UpdateClaudeSessionPointer(Guid.NewGuid().ToString(), transcript, "test");
        terminal.StartDrawing();
        session.ApplyTerminalActivityState(ActivityState.Working);
        return (session, terminal);
    }

    private static string Paragraph(string token) =>
        $"Token {token}. " + string.Concat(Enumerable.Range(1, 8).Select(i =>
            $"Sentence {i} of a spoken prompt sent while the agent is busy, long enough to go as a paste. "));

    [Fact]
    public async Task SendTextAsync_ClaudeCodeWorkingAndNeverQuiet_PasteIsTakenInWithoutWaitingOutTheLimit()
    {
        // Arrange: a working Claude Code whose spinner never stops, as at 09:05.
        var (session, terminal) = NewWorkingSession(AgentKind.ClaudeCode);
        var text = Paragraph("PASTE1");
        Assert.True(text.Length > TerminalSubmit.ClaudeTypingLimit);
        var sw = Stopwatch.StartNew();

        // Act
        await session.SendTextAsync(text, SessionTestDoors.TestDoor);

        // Assert: taken in on the composer's evidence, in seconds - not the two-minute limit - and in the records once.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"the send took {sw.Elapsed.TotalSeconds:F1}s");
        Assert.Equal(1, terminal.EntersAccepted);
        Assert.Equal(new[] { text }, terminal.Recorded);
    }

    [Fact]
    public async Task SendTextAsync_ClaudeCodeWorkingEnterSwallowed_ReportsNotDeliveredAndLeavesTheTextInPlace()
    {
        // Arrange: a working Claude Code that swallows the Enter, so the text stays in its composer.
        var (session, terminal) = NewWorkingSession(AgentKind.ClaudeCode);
        terminal.SwallowEnter = true;
        session.ArrivalWindow = TimeSpan.FromSeconds(3);
        var text = "Token SHORT1. Reply with exactly: ACK";

        // Act
        var ex = await Assert.ThrowsAsync<PromptNotSubmittedException>(
            () => session.SendTextAsync(text, SessionTestDoors.TestDoor));

        // Assert: reported NOT delivered, the text is still in the composer exactly as typed, it was typed once, and
        // nothing cleared it or typed it again.
        Assert.Contains("NOT delivered", ex.Message);
        Assert.Equal(text, terminal.Composer);
        Assert.Empty(terminal.Recorded);
        Assert.Equal(1, CountOf(terminal.TypedText, text));
        Assert.Equal(0, terminal.ClearKeysPressed);
    }

    [Fact]
    public async Task SendTextAsync_CodexWorkingEnterSwallowed_ReportsNotDeliveredAndLeavesTheTextInPlace()
    {
        // Arrange: a working Codex - whose records cannot prove a prompt sent mid-turn - that swallows the Enter. Its
        // spinner prints plenty, so counting output after the Enter would call the send submitted.
        var (session, terminal) = NewWorkingSession(AgentKind.Codex);
        terminal.SwallowEnter = true;
        session.ComposerReleaseWindowForTests = TimeSpan.FromSeconds(1);
        var text = "Token CODEX1. Reply with exactly: ACK";

        // Act
        var ex = await Assert.ThrowsAsync<PromptNotSubmittedException>(
            () => session.SendTextAsync(text, SessionTestDoors.TestDoor));

        // Assert
        Assert.Contains("still in", ex.Message);
        Assert.Equal(text, terminal.Composer);
        Assert.Equal(1, CountOf(terminal.TypedText, text));
        Assert.Equal(0, terminal.ClearKeysPressed);
    }

    [Fact]
    public async Task SendTextAsync_CodexWorkingEnterTaken_IsDeliveredOnceTheComposerEmpties()
    {
        // Arrange
        var (session, terminal) = NewWorkingSession(AgentKind.Codex);
        var text = "Token CODEX2. Reply with exactly: ACK";

        // Act
        await session.SendTextAsync(text, SessionTestDoors.TestDoor);

        // Assert
        Assert.Equal(1, terminal.EntersAccepted);
        Assert.Equal("", terminal.Composer);
    }

    [Fact]
    public async Task SendTextAsync_ComposerHeldTextBeforeThePaste_EnterWaitsForThePasteToBeDrawn()
    {
        // Arrange: the composer already holds text, and the agent draws the paste only after two seconds. The text that
        // was there before is not the paste, so it must not release the Enter.
        var (session, terminal) = NewWorkingSession(AgentKind.ClaudeCode, composer: "an old draft");
        terminal.PasteDrawDelay = TimeSpan.FromSeconds(2);
        var text = Paragraph("PASTE2");

        // Act
        await session.SendTextAsync(text, SessionTestDoors.TestDoor);

        // Assert
        Assert.NotNull(terminal.PasteDrawnAt);
        Assert.NotNull(terminal.FirstEnterAt);
        Assert.True(terminal.FirstEnterAt >= terminal.PasteDrawnAt,
            $"Enter at {terminal.FirstEnterAt:HH:mm:ss.fff} came before the paste was drawn at {terminal.PasteDrawnAt:HH:mm:ss.fff}");
    }

    [Fact]
    public async Task SendTextAsync_WorkingAgentSlowToDrawTypedText_WaitsForTheEchoAndNeverClearsAndRetypes()
    {
        // Arrange: a working Claude Code, quiet between ticks, that draws typed characters only six seconds after they
        // arrive - as measured on 25 September 2026 under full load while it ran a shell command. Four seconds of that
        // used to be called a missing echo, and the clear-and-retype that followed left a fragment in the composer.
        var (session, terminal) = NewWorkingSession(AgentKind.ClaudeCode);
        terminal.SpinnerInterval = TimeSpan.FromSeconds(30);
        terminal.TypedDrawDelay = TimeSpan.FromSeconds(6);
        var text = "Token SLOW1. Reply with exactly: ACK";

        // Act
        await session.SendTextAsync(text, SessionTestDoors.TestDoor);

        // Assert: delivered once, typed once, nothing cleared, nothing left behind.
        Assert.Equal(new[] { text }, terminal.Recorded);
        Assert.Equal(1, CountOf(terminal.TypedText, text));
        Assert.Equal(0, terminal.ClearKeysPressed);
        Assert.Equal("", terminal.Composer);
    }

    // ===== Round 2: the review's findings 1, 2 and 3 =====

    [Fact]
    public async Task SendTextAsync_WorkingAgentEnterSwallowedUnderAMenu_IsNeverReportedDelivered()
    {
        // Arrange (review finding 1): a working Codex swallows the Enter, and for the whole release window a menu is drawn
        // where the composer would be - its hint in the footer, the text still underneath. A menu over the composer says
        // nothing about whether the text left it, so it must never be read as "the text left".
        var (session, terminal) = NewWorkingSession(AgentKind.Codex);
        terminal.SwallowEnter = true;
        terminal.MenuAfterEnter = true;
        session.ComposerReleaseWindowForTests = TimeSpan.FromSeconds(1.5);
        var text = "Token MENU1. Reply with exactly: ACK";

        // Act
        var outcome = await session.SendTextAsync(text, SessionTestDoors.TestDoor);

        // Assert: not reported delivered; the text is where it was, typed once, nothing cleared.
        Assert.False(outcome.Confirmed, "a send whose composer was hidden under a menu the whole window was reported delivered");
        Assert.Contains("cannot be read", outcome.Reason);
        Assert.Equal(text, terminal.Composer);
        Assert.Equal(0, terminal.EntersAccepted);
        Assert.Equal(1, CountOf(terminal.TypedText, text));
        Assert.Equal(0, terminal.ClearKeysPressed);
    }

    [Fact]
    public async Task SendTextAsync_WorkingAgentEnterSwallowedAndFramesUnreadable_ReportsNotDeliveredAndLeavesTheTextInPlace()
    {
        // Arrange (review finding 2): a working Codex swallows the Enter; for the first four seconds after it every frame
        // is one the composer reader cannot read (the composer row drawn without its glyph, as mid-repaint), and after that
        // every frame shows the text still held. Unreadable frames must not end the check.
        var (session, terminal) = NewWorkingSession(AgentKind.Codex);
        terminal.SwallowEnter = true;
        terminal.UnreadableAfterEnter = TimeSpan.FromSeconds(4);
        session.ComposerReleaseWindowForTests = TimeSpan.FromSeconds(6);
        var text = "Token BLINK1. Reply with exactly: ACK";

        // Act
        var ex = await Assert.ThrowsAsync<PromptNotSubmittedException>(
            () => session.SendTextAsync(text, SessionTestDoors.TestDoor));

        // Assert: not delivered, the text still in the composer, typed once, never cleared.
        Assert.Contains("still in", ex.Message);
        Assert.Equal(text, terminal.Composer);
        Assert.Equal(1, CountOf(terminal.TypedText, text));
        Assert.Equal(0, terminal.ClearKeysPressed);
    }

    [Fact]
    public async Task SendTextAsync_WorkingClaudeCodeRecordsThePromptAfterTheWindow_IsDeliveringThenDelivered()
    {
        // Arrange (review finding 3, case a): a working Claude Code takes the Enter - the composer empties - but writes
        // the prompt to its records only five seconds later, after the two-second records window, as it does when its
        // running tool ends.
        var (session, terminal) = NewWorkingSession(AgentKind.ClaudeCode);
        terminal.RecordDelay = TimeSpan.FromSeconds(5);
        session.ArrivalWindow = TimeSpan.FromSeconds(2);
        session.LateArrivalLimitForTests = TimeSpan.FromSeconds(30);
        var text = "Token LATE1. Reply with exactly: ACK";

        // Act
        var outcome = await session.SendTextAsync(text, SessionTestDoors.TestDoor);
        var composerAtAnswer = terminal.Composer;
        var provenLater = await outcome.LateProof!;

        // Assert: still delivering when the send returned - never a failure - then proven delivered from the records;
        // typed once, one Enter, nothing cleared, and in the records once.
        Assert.False(outcome.Confirmed);
        Assert.Equal("", composerAtAnswer);
        Assert.True(provenLater);
        Assert.Equal(new[] { text }, terminal.Recorded);
        Assert.Equal(1, CountOf(terminal.TypedText, text));
        Assert.Equal(1, terminal.EntersAccepted);
        Assert.Equal(0, terminal.ClearKeysPressed);
    }

    [Fact]
    public async Task SendTextAsync_WorkingClaudeCodeNeverRecordsThePrompt_StaysDeliveringAndIsNeverTypedAgain()
    {
        // Arrange (review finding 3, case b): the composer empties on the Enter, and the records never show the prompt.
        var (session, terminal) = NewWorkingSession(AgentKind.ClaudeCode);
        terminal.NeverRecord = true;
        session.ArrivalWindow = TimeSpan.FromSeconds(2);
        session.LateArrivalLimitForTests = TimeSpan.FromSeconds(2);
        var text = "Token LOST1. Reply with exactly: ACK";

        // Act
        var outcome = await session.SendTextAsync(text, SessionTestDoors.TestDoor);
        var provenLater = await outcome.LateProof!;

        // Assert: still delivering, never not-delivered; the watch ends unproven; nothing typed a second time.
        Assert.False(outcome.Confirmed);
        Assert.False(provenLater);
        Assert.Equal(1, CountOf(terminal.TypedText, text));
        Assert.Equal(1, terminal.EntersAccepted);
        Assert.Equal(0, terminal.ClearKeysPressed);
    }

    private static int CountOf(string hay, string needle)
    {
        var count = 0;
        for (var at = hay.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = hay.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
