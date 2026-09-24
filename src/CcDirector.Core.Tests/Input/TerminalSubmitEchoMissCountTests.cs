using CcDirector.Core.Drivers;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using CcDirector.Core.Tests.Drivers;
using Xunit;

namespace CcDirector.Core.Tests.Input;

/// <summary>
/// Composer echo misses are COUNTED against the session they happened on (issue internal#811).
///
/// This is the leading indicator nobody could see. On 2026-07-15 there were six echo-miss events and two
/// of them turned into lost prompts; every one of the six existed only as a line in a Director log file.
/// The miss itself raises no alarm - the retype usually works - but a session quietly racking them up is
/// the session about to eat somebody's words.
/// </summary>
[Collection("PromptDeliveryFailures")]
public sealed class TerminalSubmitEchoMissCountTests
{
    private static readonly TimeSpan FastVerifyBeat = TimeSpan.FromMilliseconds(20);

    public TerminalSubmitEchoMissCountTests() => PromptDeliveryFailures.ResetForTests();

    [Fact]
    public async Task ComposerThatNeverEchoes_CountsOneMissAgainstTheNamedSession()
    {
        // The text is typed ONCE (issue #3290): the clear-and-retype second attempt doubled prompts in real agents,
        // so a composer that never echoes is one typing and one miss, then the throw.
        //
        // The pin is what makes that true rather than merely intended: without it this test reads the
        // real machine, and on a laptop sitting near the threshold it flips behaviour mid-run.
        using var machine = PinnedMachineMemory.Healthy();

        var sessionId = Guid.NewGuid();
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Withheld());

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => TerminalSubmit.SharedSubmitAsync(
                backend,
                "a dictation the composer refuses to take",
                "ClaudeDriver",
                echoTimeout: TimeSpan.FromMilliseconds(20),
                pollInterval: TimeSpan.FromMilliseconds(5),
                enterSettleDelay: TimeSpan.FromMilliseconds(1),
                submitVerifyBeat: FastVerifyBeat,
                sessionId: sessionId));

        var tally = PromptDeliveryFailures.Tally(sessionId);
        Assert.Equal(1, tally.ComposerEchoMisses);
        // The THROW is what the session boundary counts as the lost delivery; this layer only counts
        // misses, so nothing here claims a failed delivery on its own.
        Assert.Equal(0, tally.FailedDeliveries);
        Assert.False(tally.Unresolved);
    }

    [Fact]
    public async Task ComposerThatEchoesFirstTime_CountsNothing()
    {
        var sessionId = Guid.NewGuid();
        using var machine = PinnedMachineMemory.Healthy();
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };

        await TerminalSubmit.SharedSubmitAsync(
            backend, "hello world", "ClaudeDriver", submitVerifyBeat: FastVerifyBeat, sessionId: sessionId);

        Assert.Equal(PromptDeliveryTally.Empty, PromptDeliveryFailures.Tally(sessionId));
    }

    [Fact]
    public async Task SubmitWithNoSessionId_CountsNothingRatherThanInventingAPhantomSession()
    {
        // The driver and backend call sites have no session to name. Their misses must not pile up under
        // one empty id and render as a "session" nobody can open.
        using var machine = PinnedMachineMemory.Healthy();
        var backend = new RecordingSessionBackend { Buffer = new CircularTerminalBuffer() };
        backend.EchoScript.UseDefault(RecordingEchoStep.Withheld());

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => TerminalSubmit.SharedSubmitAsync(
                backend,
                "an unattributed submit",
                "CodexDriver",
                echoTimeout: TimeSpan.FromMilliseconds(20),
                pollInterval: TimeSpan.FromMilliseconds(5),
                enterSettleDelay: TimeSpan.FromMilliseconds(1),
                submitVerifyBeat: FastVerifyBeat));

        Assert.Empty(PromptDeliveryFailures.Recent());
    }
}
