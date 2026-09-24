using System.Text;
using CcDirector.Core.Drivers;
using CcDirector.Core.Input;
using CcDirector.Core.Machine;
using CcDirector.Core.Memory;
using CcDirector.Core.Tests.Drivers;
using Xunit;

namespace CcDirector.Core.Tests.Input;

/// <summary>
/// A SLOW COMPOSER IS NOT A BROKEN ONE, AND THE DIFFERENCE IS THE OWNER'S WORDS (issue #2818).
///
/// The submit path used to press Escape over the composer whenever the echo deadline passed without
/// the text appearing. It did that on a boolean whose <c>false</c> meant two different things: "the
/// rendered screen does not show the text" and "there is no rendered screen to look at". On a machine
/// that was paging - where the agent's terminal interface simply had not repainted yet - the second
/// meaning is what deleted the owner's typed sentence, twice on 2026-07-15 and thirty times on one
/// Director in September.
///
/// These tests hold the line at the only place it can be held: a destructive step needs POSITIVE
/// evidence. They assert what was actually written to the terminal - the text, the Escape, the Enter -
/// rather than only the delivery outcome, because "did it clear the composer" is the whole question.
///
/// EVERY TEST INJECTS A MEMORY READING. The verdict now depends on measured pressure, so a suite that
/// read the real machine would pass or fail according to how much memory the build agent happened to
/// have free. The probe is replaced in the constructor and restored on dispose.
/// </summary>
[Collection("PromptDeliveryFailures")]
public sealed class TerminalSubmitComposerEvidenceTests : IDisposable
{
    private static readonly TimeSpan FastVerifyBeat = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Deliberately roomy against the delays these tests use. The unknown-evidence budget is a second
    /// whole deadline, so a submit that gives up takes about twice this, and an echo scheduled inside
    /// the budget needs clear daylight on both sides of it to stay deterministic on a busy machine.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan ShortPoll = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan ShortSettle = TimeSpan.FromMilliseconds(1);

    private const byte Escape = 0x1B;
    private const byte Enter = 0x0D;

    /// <summary>
    /// Healthy by default, so a test that says nothing is asserting the untouched path. Tests that mean
    /// a starved machine replace it, and the outermost pin is restored on dispose either way.
    /// </summary>
    private readonly PinnedMachineMemory _machine = PinnedMachineMemory.Healthy();

    private PinnedMachineMemory? _starved;

    public TerminalSubmitComposerEvidenceTests() => PromptDeliveryFailures.ResetForTests();

    public void Dispose()
    {
        _starved?.Dispose();
        _machine.Dispose();
    }

    /// <summary>Run the rest of this test as though the machine were measurably short of memory.</summary>
    private void OnAStarvedMachine() => _starved ??= PinnedMachineMemory.Starved();

    /// <summary>Return to a machine with room - for the second half of a two-send test.</summary>
    private void MemoryRecovers()
    {
        _starved?.Dispose();
        _starved = null;
    }

    private static RecordingSessionBackend SilentComposer()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Withheld());
        return backend;
    }

    /// <summary>
    /// A composer that ACCEPTS a submit but whose byte-stream echo never matches what was typed - the
    /// shape that forces the screen to be the witness. <c>Withheld</c> cannot be used for that: it also
    /// leaves the composer refusing to submit, so an Enter pressed on screen evidence is swallowed and
    /// the submit verifier throws for an unrelated reason.
    /// </summary>
    private static bool TypedYet(RecordingSessionBackend backend, string text) =>
        backend.SentTexts.Any(t => t.Contains(text)) || backend.WrittenBytes.Any(b => System.Text.Encoding.UTF8.GetString(b).Contains(text));

    private static RecordingSessionBackend ComposerThatEchoesSomethingElse()
    {
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.CustomEcho("a spinner and some unrelated repaint"));
        return backend;
    }

    private static bool Wrote(RecordingSessionBackend backend, byte b) =>
        backend.WrittenBytes.Any(x => x.Length == 1 && x[0] == b);

    private static Task Submit(
        RecordingSessionBackend backend, string text, Func<string[]>? screen = null, Guid sessionId = default) =>
        TerminalSubmit.SharedSubmitAsync(
            backend,
            text,
            "ClaudeDriver",
            echoTimeout: Deadline,
            pollInterval: ShortPoll,
            enterSettleDelay: ShortSettle,
            screenSnapshot: screen,
            submitVerifyBeat: FastVerifyBeat,
            sessionId: sessionId);

    // ---------------------------------------------------------------------------------------------
    // Evidence is Unknown - we cannot see, so we must not cut
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoScreenToLookAt_DoesNotClearTheComposer_AndLeavesTheTextThere()
    {
        OnAStarvedMachine();
        // THE DEFECT, STATED. No screen provider means no evidence, and no evidence must not license an
        // Escape. Before this change the same call cleared the composer twice and retyped.
        var backend = SilentComposer();

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => Submit(backend, "the sentence the owner spoke"));

        Assert.False(Wrote(backend, Escape));
        Assert.Equal("the sentence the owner spoke", backend.EchoScript.ComposerText);
    }

    [Fact]
    public async Task ScreenThatRendersNothing_IsUnknown_NotProofTheComposerIsEmpty()
    {
        OnAStarvedMachine();
        // An empty capture is a broken instrument, not a clean reading. Treating it as proof of absence
        // is the same mistake wearing a different hat.
        var backend = SilentComposer();

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => Submit(backend, "words that must survive", screen: () => []));

        Assert.False(Wrote(backend, Escape));
    }

    [Fact]
    public async Task ScreenOfBlankRows_IsUnknown_NotProofTheComposerIsEmpty()
    {
        OnAStarvedMachine();
        var backend = SilentComposer();

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => Submit(backend, "words that must survive", screen: () => ["   ", "", "  "]));

        Assert.False(Wrote(backend, Escape));
    }

    [Fact]
    public async Task ScreenThatThrows_IsUnknown_AndDoesNotReplaceTheRealFailure()
    {
        OnAStarvedMachine();
        // A renderer that throws must not escape through the diagnostics and become the reported fault.
        // This test caught exactly that: the submit failed with InvalidOperationException from inside a
        // log line, losing the real report.
        var backend = SilentComposer();

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => Submit(backend, "words that must survive",
                screen: () => throw new InvalidOperationException("the renderer is busy")));

        Assert.False(Wrote(backend, Escape));
    }

    [Fact]
    public async Task UnknownEvidence_TypesTheTextExactlyOnce_RatherThanRetyping()
    {
        OnAStarvedMachine();
        // Watching for longer must be OBSERVATION, not another write. A retype on an unknown composer is
        // how one prompt becomes two.
        var backend = SilentComposer();

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(() => Submit(backend, "typed once"));

        var typedTimes = backend.WrittenBytes.Count(b => Encoding.UTF8.GetString(b) == "typed once");
        Assert.Equal(1, typedTimes);
    }

    [Fact]
    public async Task UnknownEvidence_StillFinishes_RatherThanWaitingForever()
    {
        OnAStarvedMachine();
        // The budget is finite by design: moving the harm from "your words were deleted" to "your send
        // never returns" would not be a fix. This test completing at all is the assertion.
        var backend = SilentComposer();

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(() => Submit(backend, "bounded"));
    }

    [Fact]
    public async Task UnknownEvidence_SaysTheTextWasNotCleared()
    {
        OnAStarvedMachine();
        // The failure notice the owner reads has to distinguish "your words are gone" from "your words
        // are still on screen, press Enter".
        var backend = SilentComposer();

        var ex = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => Submit(backend, "still on screen"));

        Assert.Contains("NOT cleared", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // A starved machine never reaches the destructive verdict at all
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnAStarvedMachine_ASilentScreenIsNeverTreatedAsProofOfAbsence()
    {
        // THE DESIGN REVIEW'S SECOND CONCERN, kept as a test. Two screen samples 120 milliseconds apart
        // prove nothing when the machine is paging: the renderer can be stalled long enough that both
        // captures are the same stale frame from before the text was typed. Under measured pressure the
        // Absent verdict is refused outright, so the Escape below can never fire.
        OnAStarvedMachine();
        var backend = SilentComposer();

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => Submit(backend, "must survive a stalled renderer",
                screen: () => ["a frame that does not show the text", "and never changes"]));

        Assert.False(Wrote(backend, Escape));
        Assert.Equal("must survive a stalled renderer", backend.EchoScript.ComposerText);
    }

    // ---------------------------------------------------------------------------------------------
    // The text arrives late - the case memory pressure actually produces
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task EchoThatArrivesDuringTheExtraWatch_IsSubmitted_NotDeleted()
    {
        OnAStarvedMachine();
        // The repaint is LATE, not absent - it lands after the deadline but inside the extra watch.
        // Under the old code the deadline expired, Escape wiped the text, and the owner's sentence was
        // gone. It must now be submitted.
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Delayed(TimeSpan.FromMilliseconds(350)));

        await Submit(backend, "a late repaint");

        Assert.False(Wrote(backend, Escape));
        Assert.True(Wrote(backend, Enter));
        Assert.Contains("a late repaint", backend.SubmittedTexts);
    }

    [Fact]
    public async Task ScreenThatShowsTheTextAfterTheDeadline_IsSubmitted()
    {
        var backend = ComposerThatEchoesSomethingElse();

        // The copy appears once the text has been typed; a copy already there before typing is not an echo.
        await Submit(backend, "on the screen all along", screen: () => TypedYet(backend, "on the screen all along")
            ? ["> on the screen all along", "footer"]
            : ["> ", "footer"]);

        Assert.False(Wrote(backend, Escape));
        Assert.Contains("on the screen all along", backend.SubmittedTexts);
    }

    // ---------------------------------------------------------------------------------------------
    // TYPED ONCE, NEVER CLEARED AND RETYPED (issue #3290) - on a healthy machine and a starved one,
    // with the screen unreadable and with the screen proving the text absent
    // ---------------------------------------------------------------------------------------------

    private static int TimesTyped(RecordingSessionBackend backend, string text) =>
        backend.WrittenBytes.Count(x => Encoding.UTF8.GetString(x) == text);

    [Fact]
    public async Task OnAHealthyMachineWithNoScreen_TheTextIsTypedOnce_AndNeverClearedOrRetyped()
    {
        // This used to be the regression guard FOR the clear-and-retype recovery. Measured on 24 September 2026
        // with real agents, that recovery is what doubled prompts: a single Escape empties neither Claude Code's nor
        // Codex's composer, so the retype appended a second copy. The text is now typed once and left where it is.
        var backend = SilentComposer();

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(() => Submit(backend, "no screen here"));

        Assert.False(Wrote(backend, Escape), "the composer was cleared");
        Assert.Equal(1, TimesTyped(backend, "no screen here"));
        Assert.False(Wrote(backend, Enter));
    }

    [Fact]
    public async Task OnAHealthyMachineWithNoScreen_CountsOneMissForTheOneTyping()
    {
        var sessionId = Guid.NewGuid();
        var backend = SilentComposer();

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => Submit(backend, "no screen here", sessionId: sessionId));

        Assert.Equal(1, PromptDeliveryFailures.Tally(sessionId).ComposerEchoMisses);
    }

    [Fact]
    public async Task ScreenThatProvesTheTextIsNotThere_IsReported_NotClearedAndRetyped()
    {
        // A modal covering the composer: the send is reported as not accepted, and the text is marked as possibly
        // retained, so the NEXT send clears with the agent's measured keys before it types.
        var backend = SilentComposer();

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => Submit(backend, "never lands", screen: () => ["a modal is covering the composer", "[ OK ]"]));

        Assert.False(Wrote(backend, Escape));
        Assert.Equal(1, TimesTyped(backend, "never lands"));
    }

    [Fact]
    public async Task ProvenAbsent_CountsOneMissForTheOneTyping()
    {
        var sessionId = Guid.NewGuid();
        var backend = SilentComposer();

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => Submit(backend, "never lands", screen: () => ["a modal", "[ OK ]"], sessionId: sessionId));

        Assert.Equal(1, PromptDeliveryFailures.Tally(sessionId).ComposerEchoMisses);
    }

    // ---------------------------------------------------------------------------------------------
    // The send AFTER a retained composer - the half that stops two prompts running as one
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheNextSendClearsARetainedComposerBeforeTyping()
    {
        OnAStarvedMachine();
        // Leaving text in the composer is only safe if the NEXT send cannot weld itself onto it - the
        // defect pull request #1513 was opened for. With no screen to consult the evidence is Unknown,
        // and Unknown deliberately clears: one prompt the owner was already told did not arrive is a
        // smaller harm than two prompts run as one instruction they never gave.
        var backend = SilentComposer();

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(() => Submit(backend, "first prompt"));
        Assert.False(Wrote(backend, Escape));

        backend.WrittenBytes.Clear();
        backend.EchoScript.UseDefault(RecordingEchoStep.Immediate());

        await Submit(backend, "second prompt");

        var escapeAt = backend.WrittenBytes.FindIndex(b => b.Length == 1 && b[0] == Escape);
        var textAt = backend.WrittenBytes.FindIndex(b => Encoding.UTF8.GetString(b) == "second prompt");

        Assert.True(escapeAt >= 0, "the retained composer was never cleared");
        Assert.True(escapeAt < textAt, "the new text was typed before the retained composer was cleared");
        Assert.Contains("second prompt", backend.SubmittedTexts);
        Assert.DoesNotContain(backend.SubmittedTexts, t => t.Contains("first prompt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheNextSendDoesNotClearWhenTheOrphanIsProvablyGone()
    {
        OnAStarvedMachine();
        // The owner pressed Enter themselves, so the composer now holds whatever they are typing next.
        // An Escape here would fall on THAT, which is the harm this whole change exists to stop.
        var backend = SilentComposer();

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(() => Submit(backend, "first prompt"));

        // The owner saw their words on screen and pressed Enter themselves, so the orphan has left the
        // composer. The screen below and the composer state have to agree about that, or the fixture is
        // describing a situation that cannot happen.
        backend.EchoScript.ClearComposer();
        backend.WrittenBytes.Clear();
        backend.EchoScript.UseDefault(RecordingEchoStep.Immediate());

        // Memory has recovered by the time the next send arrives - which it must have, because a screen
        // can only be trusted to PROVE absence on a machine that is not paging.
        MemoryRecovers();

        await Submit(backend, "second prompt", screen: () => ["the orphan is gone from this screen", "> "]);

        Assert.False(Wrote(backend, Escape));
        Assert.Contains("second prompt", backend.SubmittedTexts);
    }

    [Fact]
    public async Task ASuccessfulSendDoesNotClearTheComposerOfTheNextOne()
    {
        // The mark is only set when a submit gives up without proving the composer clear. A normal send
        // must not leave one behind, or every second send would start with a needless Escape.
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };

        await Submit(backend, "first");
        backend.WrittenBytes.Clear();
        await Submit(backend, "second");

        Assert.False(Wrote(backend, Escape));
    }

    // ---------------------------------------------------------------------------------------------
    // Every route is covered, not just the inline one
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARetainedComposerIsClearedEvenWhenTheNextSendTakesTheBracketedPasteRoute()
    {
        // THE CODE REVIEW'S FIRST FINDING, kept as a permanent guard.
        //
        // The retained-composer guard originally lived inside the inline echo route. A multiline send
        // with bracketed paste enabled - which Session.SendTextAsync reaches using the session's own
        // setting - leaves SharedSubmitAsync for BracketedPasteSubmitAsync and never passed that guard,
        // so it typed and pressed Enter over a composer still holding an earlier unsent prompt and
        // submitted BOTH as one instruction. That is the pull request #1513 corruption arriving through
        // a route the guard did not cover.
        OnAStarvedMachine();
        var backend = SilentComposer();

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(() => Submit(backend, "first prompt"));
        Assert.False(Wrote(backend, Escape));
        Assert.Equal("first prompt", backend.EchoScript.ComposerText);

        backend.WrittenBytes.Clear();
        backend.EchoScript.UseDefault(RecordingEchoStep.Immediate());

        await TerminalSubmit.SharedSubmitAsync(
            backend,
            "second message\nwith a second line",
            "ClaudeDriver",
            bracketedPasteEnabled: true,
            enterSettleDelay: ShortSettle,
            submitVerifyBeat: FastVerifyBeat);

        Assert.True(Wrote(backend, Escape),
            "the paste route wrote new text without clearing the retained composer");
        Assert.DoesNotContain(backend.SubmittedTexts,
            t => t.Contains("first prompt", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // Pressure that ARRIVES during the wait
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PressureThatBeginsDuringTheEchoWait_StillPreservesThePrompt()
    {
        // THE CODE REVIEW'S SECOND FINDING, kept as a permanent guard.
        //
        // The probe was read ONCE before typing, and that one reading governed the destructive decision
        // taken four seconds later. A machine with room when the send began and short of memory by the
        // time the deadline passed therefore used the stale "Normal" verdict, pressed Escape twice,
        // deleted the prompt, and reported that the machine had memory to spare. The transition INTO
        // pressure is the ordinary case on a loading Director, not an exotic one.
        var probe = new ShiftingProbe(MachineMemoryReading.Read(
            16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, DateTime.UtcNow));
        TerminalSubmit.MemoryProbe = probe;

        var backend = SilentComposer();
        backend.OnFirstWrite = () => probe.Now = MachineMemoryReading.Read(
            16UL * 1024 * 1024 * 1024, 300UL * 1024 * 1024, DateTime.UtcNow);

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => Submit(backend, "typed just as the machine ran out"));

        Assert.False(Wrote(backend, Escape), "the stale reading licensed an Escape");
        Assert.Equal("typed just as the machine ran out", backend.EchoScript.ComposerText);
        Assert.True(probe.Reads >= 2, "the machine was read only once, before typing");
    }

    [Fact]
    public async Task PressureThatBeginsDuringTheEchoWait_AlsoWidensTheObservationWindow()
    {
        // THE SECOND REVIEW'S FINDING 1, kept as a permanent guard.
        //
        // The fix above was half applied and this test is what would have caught it. The DECISION to
        // preserve the text used the fresh reading; the BUDGET for the extra watch did not - it came
        // from the deadline computed before the first keystroke. So a machine that had room at send
        // start and was Critical by the deadline correctly refused the Escape, correctly said it was
        // nearly out of memory, and then watched for the HEALTHY machine's four seconds instead of the
        // sixteen the same rationale asks for.
        //
        // Asserting the decision alone could not see that. This asserts the WINDOW: with no explicit
        // timeout, a submit that transitions into Critical must spend materially longer watching than
        // one that stays healthy.
        static async Task<TimeSpan> TimeASilentSubmit(bool goesCritical)
        {
            var probe = new ShiftingProbe(MachineMemoryReading.Read(
                16UL * 1024 * 1024 * 1024, 12UL * 1024 * 1024 * 1024, DateTime.UtcNow));
            TerminalSubmit.MemoryProbe = probe;

            var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
            backend.EchoScript.UseDefault(RecordingEchoStep.Withheld());
            if (goesCritical)
                backend.OnFirstWrite = () => probe.Now = MachineMemoryReading.Read(
                    16UL * 1024 * 1024 * 1024, 300UL * 1024 * 1024, DateTime.UtcNow);

            var started = DateTime.UtcNow;
            // NO explicit echoTimeout - the whole point is that the deadline is chosen from the reading.
            // A short poll keeps the wait responsive; the deadline itself is what is under test.
            await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
                () => TerminalSubmit.SharedSubmitAsync(
                    backend, "x", "ClaudeDriver",
                    pollInterval: ShortPoll, enterSettleDelay: ShortSettle, submitVerifyBeat: FastVerifyBeat));
            return DateTime.UtcNow - started;
        }

        var healthy = await TimeASilentSubmit(goesCritical: false);
        var transitioned = await TimeASilentSubmit(goesCritical: true);

        // Healthy: two attempts at the four second base deadline. Transitioned: one attempt at four
        // seconds, then an extra watch sized from Critical. If the budget were still taken from the
        // pre-typing reading the two would be indistinguishable.
        Assert.True(transitioned > healthy + TimeSpan.FromSeconds(3),
            $"the extra watch was not widened for a machine that went Critical mid-wait: " +
            $"healthy {healthy.TotalSeconds:F1}s vs transitioned {transitioned.TotalSeconds:F1}s");
    }

    private sealed class ShiftingProbe(MachineMemoryReading first) : IMachineMemoryProbe
    {
        public MachineMemoryReading Now { get; set; } = first;
        public int Reads { get; private set; }

        public MachineMemoryReading Read()
        {
            Reads++;
            return Now with { TakenAtUtc = DateTime.UtcNow };
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The deadline itself
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ScaledEchoTimeout_HealthyOrUnreadableMachine_IsTheOriginalFourSeconds()
    {
        Assert.Equal(TerminalSubmit.BaseEchoTimeout, TerminalSubmit.ScaledEchoTimeout(MemoryPressureLevel.Normal));
        Assert.Equal(TerminalSubmit.BaseEchoTimeout, TerminalSubmit.ScaledEchoTimeout(MemoryPressureLevel.Unknown));
    }

    [Fact]
    public void ScaledEchoTimeout_StarvedMachine_IsLonger()
    {
        Assert.True(TerminalSubmit.ScaledEchoTimeout(MemoryPressureLevel.Tight) > TerminalSubmit.BaseEchoTimeout);
        Assert.True(TerminalSubmit.ScaledEchoTimeout(MemoryPressureLevel.Critical)
                    > TerminalSubmit.ScaledEchoTimeout(MemoryPressureLevel.Tight));
    }

    [Fact]
    public async Task AnExplicitTimeoutFromTheCallerStillWins()
    {
        // Drivers and tests that chose a deadline keep it, whatever the machine looks like. Without this
        // the change would silently retune every caller in the code base - the scaled deadline on a
        // starved machine is sixteen seconds, so this would not finish quickly if the override leaked.
        OnAStarvedMachine();
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        var started = DateTime.UtcNow;

        await Submit(backend, "quick");

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3),
            "the caller's explicit deadline was overridden by the pressure scaling");
    }
}
