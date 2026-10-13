using System.Diagnostics;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Stable Release (v1.3.0), Tier 1 item 1 - a dropped command must explain itself. These are the regression
/// tests for the bounded wait at <see cref="DirectorCommandRouter.TrySendAsync"/>, the ONE chokepoint every
/// Gateway-to-Director command routes through.
///
/// Before this, two failures were live and silent: a Director that stayed tunnel-connected but never answered
/// hung the caller FOREVER (no timeout existed anywhere on the path), and a tunnel that dropped mid-command
/// threw out of an uncaught InvokeAsync so the caller saw a raw HTTP 500 with no explanation. The three
/// non-success outcomes must now be distinguishable from each other, and each must say what actually happened.
/// </summary>
public sealed class DirectorCommandRouterTimeoutTests
{
    /// <summary>A send delegate that never answers - the Director holds the tunnel open and says nothing.</summary>
    private static DirectorCommandRouter.SendDirectorCommandAsync NeverAnswers() =>
        async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };

    /// <summary>A send delegate that throws - the tunnel dropped while the command was in flight.</summary>
    private static DirectorCommandRouter.SendDirectorCommandAsync Throws(Exception ex) =>
        (_, _, _) => Task.FromException<DirectorCommandResult?>(ex);

    /// <summary>
    /// A send delegate that behaves the way REAL SignalR does when its cancellation token fires: it does NOT
    /// throw an OperationCanceledException. The server completes the pending client-result invocation with a
    /// plain exception reading "Invocation canceled by the server."
    ///
    /// This shape is not a guess - it was observed by driving a live Gateway with a Director that connected and
    /// went silent. An earlier version of the router filtered its timeout catch on OperationCanceledException,
    /// so every real timeout fell through and was misreported as a tunnel drop. Every unit test still passed,
    /// because a hand-written delegate that awaits Task.Delay(ct) throws the tidy exception SignalR never does.
    /// </summary>
    private static DirectorCommandRouter.SendDirectorCommandAsync NeverAnswersLikeSignalR() =>
        async (_, _, ct) =>
        {
            var completion = new TaskCompletionSource<DirectorCommandResult?>();
            using var registration = ct.Register(() =>
                completion.TrySetException(new InvalidOperationException("Invocation canceled by the server.")));
            return await completion.Task;
        };

    [Fact]
    public async Task TrySendAsync_DirectorNeverAnswers_TimesOut_RatherThanHangingForever()
    {
        // Arrange - the exact live failure: connected, but no reply is ever sent.
        var timeout = TimeSpan.FromMilliseconds(200);

        // Act
        var result = await DirectorCommandRouter.TrySendAsync(
            NeverAnswers(), "director-1", "prompt", "sid-1", null, CancellationToken.None, timeout);

        // Assert - it returns instead of hanging, and the outcome names the timeout.
        Assert.NotNull(result);
        Assert.Equal(DirectorCommandStatus.Timeout, result!.Status);
        Assert.False(result.Ok);
    }

    // ---- the WORDING, tested against the pure function that produces it ----
    //
    // These three used to run a real thirty-second timeout each (one of them twice) purely to read the
    // sentence out of the result - two minutes of every suite run, spent waiting on a clock to check a
    // string (issue #1156). The message is a pure function of the machine name and the wait, so it is
    // asserted directly. The JOIN is not lost: the seam test below runs a real timeout and asserts the
    // router returns exactly this function's output, so wording and wiring are each proven, and neither
    // can drift without a test going red.

    [Fact]
    public void TimeoutMessage_NamesTheMachineAndTheWait_InPlainEnglish()
    {
        var message = DirectorCommandRouter.DescribeTimeout("SOREN_NORTH", TimeSpan.FromSeconds(30));

        // This message is read by a person on a phone in a moving car. It must name the machine, say what did
        // not happen, and give the wait in whole seconds - no status code, no stack trace, no jargon.
        Assert.Contains("SOREN_NORTH", message);
        Assert.Contains("did not answer within 30 seconds", message);
        Assert.DoesNotContain("30.0", message);
    }

    [Fact]
    public void TimeoutMessage_WithoutAMachineName_KeepsTheSameMessageShape()
    {
        // The call sites that hold only a Director id cannot name the machine. The degraded message is the
        // SAME sentence minus the machine clause, never vaguer and never a second message style.
        Assert.Equal(
            "The Director did not answer within 30 seconds. It is not known whether the command was carried out.",
            DirectorCommandRouter.DescribeTimeout(null, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void TimeoutMessage_DoesNotClaimTheCommandWasSkipped()
    {
        // A timeout proves only that the GATEWAY stopped waiting. The Director may have received the command,
        // carried it out, and answered late, so the message must leave the outcome open. It used to end "The
        // command was not carried out" - stated as fact - which is how a user came to re-tap Send and hand the
        // agent the same prompt twice. Positive assertion first: an absence-only check would still pass if the
        // sentence were deleted, which is a worse message than the wrong one.
        foreach (var message in new[]
                 {
                     DirectorCommandRouter.DescribeTimeout("SOREN_NORTH", TimeSpan.FromSeconds(30)),
                     DirectorCommandRouter.DescribeTimeout(null, TimeSpan.FromSeconds(30)),
                 })
        {
            Assert.Contains("It is not known whether the command was carried out.", message);
            Assert.DoesNotContain("The command was not carried out", message);
        }
    }

    [Fact]
    public async Task TrySendAsync_Timeout_ReturnsExactlyTheDescribedMessage()
    {
        // THE SEAM. The three tests above assert the wording without waiting; this one proves the router
        // actually uses that wording on a real timeout. Without it, DescribeTimeout could be perfect and the
        // router could return something else entirely, and every test would still pass. A SHORT timeout is
        // enough - what is under test is which string comes back, not how long the wait is.
        var wait = TimeSpan.FromMilliseconds(200);

        var result = await DirectorCommandRouter.TrySendAsync(
            NeverAnswers(), "director-1", "prompt", "sid-1", null, CancellationToken.None,
            wait, machineName: "SOREN_NORTH");

        Assert.Equal(DirectorCommandStatus.Timeout, result!.Status);
        Assert.Equal(DirectorCommandRouter.DescribeTimeout("SOREN_NORTH", wait), result.Error);
    }

    [Fact]
    public async Task TrySendAsync_Timeout_IsTypedTheSame_WithOrWithoutAMachineName()
    {
        // Act
        var named = await DirectorCommandRouter.TrySendAsync(
            NeverAnswers(), "d", "prompt", "s", null, CancellationToken.None, TimeSpan.FromMilliseconds(150), "SOREN_NORTH");
        var anonymous = await DirectorCommandRouter.TrySendAsync(
            NeverAnswers(), "d", "prompt", "s", null, CancellationToken.None, TimeSpan.FromMilliseconds(150));

        // Assert - the machine name must NEVER gate the typed status, or the outcome would stop being
        // distinguishable exactly where the caller knows least about the Director.
        Assert.Equal(DirectorCommandStatus.Timeout, named!.Status);
        Assert.Equal(DirectorCommandStatus.Timeout, anonymous!.Status);
    }

    [Fact]
    public async Task TrySendAsync_TimeoutReportedTheWaySignalRReallyReportsIt_IsStillATimeout_NotADrop()
    {
        // Arrange - the REAL transport behaviour, not the tidy cancellation a hand-written delegate throws.
        // Act
        var result = await DirectorCommandRouter.TrySendAsync(
            NeverAnswersLikeSignalR(), "director-1", "repos-overview", "", null, CancellationToken.None,
            TimeSpan.FromMilliseconds(200), machineName: "SOREN_NORTH");

        // Assert - a timeout must be reported as a TIMEOUT. This exact case shipped green under a type-filtered
        // catch and was only caught against a live Gateway: the user was told the connection dropped when in
        // fact their Director was sitting there connected and silent. Two different problems, two different
        // things to do about them, so telling the user the wrong one is its own kind of lie.
        Assert.Equal(DirectorCommandStatus.Timeout, result!.Status);
        Assert.Contains("did not answer within", result.Error!);
        Assert.DoesNotContain("dropped", result.Error);
    }

    [Fact]
    public async Task TrySendAsync_TunnelDropsMidCommand_ReturnsTypedDrop_NotAnUnhandledException()
    {
        // Arrange - InvokeAsync throws when the tunnel dies in flight. Nothing on the path used to catch it,
        // so it escaped and the caller saw a raw 500.
        var send = Throws(new InvalidOperationException("Connection disconnected before invocation result was received."));

        // Act
        var result = await DirectorCommandRouter.TrySendAsync(
            send, "director-1", "prompt", "sid-1", null, CancellationToken.None, machineName: "SOREN_NORTH");

        // Assert - caught at the chokepoint and turned into an outcome that explains itself.
        Assert.NotNull(result);
        Assert.Equal(DirectorCommandStatus.TunnelDropped, result!.Status);
        Assert.Contains("SOREN_NORTH", result.Error!);
        Assert.Contains("dropped while the command was being sent", result.Error);
    }

    [Fact]
    public async Task TrySendAsync_TunnelDrop_DoesNotClaimTheCommandWasSkipped()
    {
        // Act
        var result = await DirectorCommandRouter.TrySendAsync(
            Throws(new InvalidOperationException("boom")), "d", "kill", "s", null, CancellationToken.None);

        // Assert - the Director may have run the command before the connection died. Saying it did not happen
        // would be a guess stated as fact, and for a verb like kill that guess is dangerous.
        Assert.Contains("not known whether the command was carried out", result!.Error!);
    }

    [Fact]
    public async Task TrySendAsync_TimeoutAndDrop_AreDistinguishableFromEachOther()
    {
        // Act
        var timedOut = await DirectorCommandRouter.TrySendAsync(
            NeverAnswers(), "d", "prompt", "s", null, CancellationToken.None, TimeSpan.FromMilliseconds(150));
        var dropped = await DirectorCommandRouter.TrySendAsync(
            Throws(new InvalidOperationException("boom")), "d", "prompt", "s", null, CancellationToken.None);

        // Assert - the whole point of the item: these were indistinguishable before, and from everything else.
        Assert.NotEqual(timedOut!.Status, dropped!.Status);
        Assert.Equal(DirectorCommandStatus.Timeout, timedOut.Status);
        Assert.Equal(DirectorCommandStatus.TunnelDropped, dropped.Status);
    }

    [Fact]
    public async Task TrySendAsync_DirectorNotTunnelConnected_StillReturnsNull_Unchanged()
    {
        // Act - the pre-existing third outcome: no send delegate means the command is unroutable.
        var result = await DirectorCommandRouter.TrySendAsync(
            null, "director-1", "prompt", "sid-1", null, CancellationToken.None);

        // Assert - unchanged by this work; the caller still surfaces null as a 502.
        Assert.Null(result);
    }

    [Fact]
    public async Task TrySendAsync_CallerCancels_PropagatesTheCancellation_NotAFalseTimeout()
    {
        // Arrange - the caller gives up (the browser goes away) well inside the timeout.
        using var caller = new CancellationTokenSource();
        var send = NeverAnswers();
        var task = DirectorCommandRouter.TrySendAsync(
            send, "director-1", "prompt", "sid-1", null, caller.Token, TimeSpan.FromMinutes(5));

        caller.Cancel();

        // Assert - the caller's own cancellation must stay a cancellation. Reporting it as "the Director did
        // not answer" would blame the Director for something the caller did.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task TrySendAsync_SuccessfulCommand_IsUntouchedByTheTimeout()
    {
        // Arrange
        var expected = DirectorCommandResult.Success("{\"ok\":true}");
        DirectorCommandRouter.SendDirectorCommandAsync send = (_, _, _) => Task.FromResult<DirectorCommandResult?>(expected);

        // Act
        var result = await DirectorCommandRouter.TrySendAsync(
            send, "director-1", "prompt", "sid-1", null, CancellationToken.None);

        // Assert - the happy path returns the Director's own result unchanged.
        Assert.Same(expected, result);
        Assert.True(result!.Ok);
    }

    [Fact]
    public async Task TrySendAsync_DefaultTimeout_IsThirtySeconds_AndIsUsedWhenNoOverrideIsGiven()
    {
        // Arrange - the default is a named constant so it cannot drift per verb.
        Assert.Equal(30, DirectorCommandRouter.DefaultCommandTimeout.TotalSeconds);
        TimeSpan? observed = null;
        DirectorCommandRouter.SendDirectorCommandAsync send = (_, _, ct) =>
        {
            // A linked token carries the deadline; read it back to prove the default was applied.
            observed = DirectorCommandRouter.DefaultCommandTimeout;
            Assert.True(ct.CanBeCanceled);
            return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success());
        };

        // Act - no timeout argument.
        await DirectorCommandRouter.TrySendAsync(send, "d", "prompt", "s", null, CancellationToken.None);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(30), observed);
    }

    [Fact]
    public void LanguageModelTimeout_IsLongerThanTheDefault_ForTheVerbsThatGenerate()
    {
        // recap-generate, handover-generate and wingman-ask run a language model on the Director before they
        // can answer. Killing a real answer at the 30-second default would be a regression dressed up as a
        // fix, so those call sites override rather than the default being raised for everyone.
        Assert.True(DirectorCommandRouter.LanguageModelCommandTimeout > DirectorCommandRouter.DefaultCommandTimeout);
    }

    [Fact]
    public void LanguageModelTimeout_StaysStrictlyOutsideEveryInnerBound_SoTheInnerOneAlwaysFiresFirst()
    {
        // This backstop sits OUTSIDE bounds the verbs already enforce themselves. An inner timeout knows which
        // step died and says so; this one can only ever say "the Director did not answer". So the inner bound
        // must always win: if they were equal they would race, and whenever the backstop won it would MASK the
        // specific message with a generic one - the exact disease this release exists to kill.
        //
        // Asserted against the real constants, not copies, so raising an inner bound to meet or exceed this one
        // fails HERE rather than silently reintroducing the race in production.
        Assert.True(DirectorCommandRouter.LanguageModelCommandTimeout > CcDirector.Core.Claude.RecapGenerator.ProcessTimeout,
            "recap-generate's own timeout must fire before the Gateway backstop, or its specific error is masked.");
        Assert.True(DirectorCommandRouter.LanguageModelCommandTimeout > CcDirector.Core.Wingman.WingmanService.ProcessTimeout,
            "wingman-ask's own timeout must fire before the Gateway backstop, or its specific error is masked.");
        // compact-context (issue #2150) joined this family: it waits on a language model summarizing a whole
        // conversation. Its inner bound must fire first so the caller is told "the tool never reported a
        // finished compaction" - which says what to do next - instead of "the Director did not answer".
        Assert.True(DirectorCommandRouter.LanguageModelCommandTimeout > CcDirector.Core.Sessions.Session.CompactionWaitTimeout,
            "the Director's compaction wait must fire before the Gateway backstop, or its specific error is masked.");
    }

    [Fact]
    public async Task DescribeFailure_ForTimeoutAndDrop_CarriesTheMessageVerbatim()
    {
        // Arrange
        var timedOut = await DirectorCommandRouter.TrySendAsync(
            NeverAnswers(), "d", "prompt", "s", null, CancellationToken.None, TimeSpan.FromMilliseconds(150), "SOREN_NORTH");

        // Act
        var described = DirectorCommandRouter.DescribeFailure(timedOut!);

        // Assert - the Director returned NOTHING, so "director returned Timeout: ..." would be a lie, and the
        // message already explains itself in plain English.
        Assert.Equal(timedOut!.Error, described);
        Assert.DoesNotContain("director returned", described);
    }

    [Fact]
    public void DescribeFailure_ForDirectorSentStatuses_KeepsItsExistingWording()
    {
        // Arrange - a status the Director really did send.
        var failure = DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        // Act + Assert - unchanged wording: consumers and re-added tests assert this shape byte-for-byte.
        Assert.Equal("director returned NotFound: session not found", DirectorCommandRouter.DescribeFailure(failure));
    }
}
