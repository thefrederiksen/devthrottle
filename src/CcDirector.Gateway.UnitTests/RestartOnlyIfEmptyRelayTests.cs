using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway half of "restart only if the Director is empty": the flag has to REACH the launcher, and
/// the launcher's refusal has to reach the caller with the session count still in it.
///
/// BOTH HALVES CAN FAIL SILENTLY, which is why they are pinned here. A flag that is parsed from the
/// request body and then never copied onto the command produces a perfectly successful restart of a busy
/// Director, and every other test in the suite stays green. A refusal that is folded into the generic 502
/// tells the reader their machine is broken and sends them to look for a fault instead of finishing their
/// drain.
/// </summary>
public sealed class RestartOnlyIfEmptyRelayTests
{
    private const string Machine = "RESTART-GUARD-MACHINE";

    /// <summary>The refusal a launcher sends back, in the shape the launcher really writes it: naming the
    /// count, because the count is what tells the reader what to do next.</summary>
    private const string LauncherRefusal =
        "refusing to restart the Director 1111-2222 (pid 4242) on SOME-MACHINE: it is holding 3 live "
        + "sessions. Drain it first - every session writes a handover and is closed - then ask again.";

    [Fact]
    public async Task SendDirectorVerbAsync_Restart_CarriesOnlyIfEmptyDownTheStream()
    {
        LauncherCommand? sent = null;
        LauncherCommandRouter.SendLauncherCommandAsync capture = (_, _, cmd, _) =>
        {
            sent = cmd;
            return Task.FromResult<LauncherCommandResult?>(LauncherCommandResult.Ok());
        };

        await LauncherLifecycleRelay.SendDirectorVerbAsync(TenantId.Local, Machine, "restart", exePath: null,
            confirmProtected: false, new LauncherRegistry(), capture, CancellationToken.None, onlyIfEmpty: true);

        Assert.NotNull(sent);
        Assert.Equal("director/restart", sent!.Verb);
        Assert.True(sent.OnlyIfEmpty,
            "the caller asked for a restart only if the Director is empty and the launcher was sent an "
            + "ordinary restart - which would restart a Director in the middle of its drain.");
    }

    [Fact]
    public async Task SendDirectorVerbAsync_WithoutTheFlag_SendsAnOrdinaryRestart()
    {
        LauncherCommand? sent = null;
        LauncherCommandRouter.SendLauncherCommandAsync capture = (_, _, cmd, _) =>
        {
            sent = cmd;
            return Task.FromResult<LauncherCommandResult?>(LauncherCommandResult.Ok());
        };

        await LauncherLifecycleRelay.SendDirectorVerbAsync(TenantId.Local, Machine, "restart", exePath: null,
            confirmProtected: false, new LauncherRegistry(), capture, CancellationToken.None);

        Assert.NotNull(sent);
        Assert.False(sent!.OnlyIfEmpty);
    }

    [Fact]
    public async Task SendDirectorVerbAsync_ALauncherRefusalBecomes409_AndCarriesTheCount()
    {
        LauncherCommandRouter.SendLauncherCommandAsync refuse = (_, _, _, _) =>
            Task.FromResult<LauncherCommandResult?>(LauncherCommandResult.Refuse(LauncherRefusal));

        var outcome = await LauncherLifecycleRelay.SendDirectorVerbAsync(TenantId.Local, Machine, "restart",
            exePath: null, confirmProtected: false, new LauncherRegistry(), refuse, CancellationToken.None,
            onlyIfEmpty: true);

        // The launcher answered, so this is a relayed answer rather than an undeliverable command.
        Assert.Equal(LauncherLifecycleRelay.RelayOutcomeKind.Relayed, outcome.Kind);

        // 409, not 502: the machine is not broken, it is busy. A 502 would send the reader hunting a fault.
        Assert.Equal(409, outcome.RelayStatus);
        Assert.False(outcome.Accepted);

        // And the count survives the hop. This is the whole point of the refusal.
        Assert.Contains("3 live sessions", outcome.Payload);
    }

    [Fact]
    public async Task SendDirectorVerbAsync_AGuardedRestartsOwnAnswerIsPassedThroughIntact()
    {
        // What the launcher writes when it HONOURED the flag. A caller reads it to tell this apart from an
        // older launcher that ignored the flag and answered a bare OK.
        const string guarded = """{"ok":true,"restarted":true,"onlyIfEmpty":true,"sessions":0}""";
        LauncherCommandRouter.SendLauncherCommandAsync ok = (_, _, _, _) =>
            Task.FromResult<LauncherCommandResult?>(LauncherCommandResult.OkWithPayload(guarded));

        var outcome = await LauncherLifecycleRelay.SendDirectorVerbAsync(TenantId.Local, Machine, "restart",
            exePath: null, confirmProtected: false, new LauncherRegistry(), ok, CancellationToken.None,
            onlyIfEmpty: true);

        Assert.Equal(200, outcome.RelayStatus);
        Assert.Equal(guarded, outcome.Payload);
    }

    [Fact]
    public async Task SendDirectorVerbAsync_AGuardedRestartAnsweredWithABareOk_IsReportedAsAFailure()
    {
        // A launcher older than the flag: it cannot see the condition, restarts the Director whatever it
        // was holding, and answers exactly as a healthy launcher answers an ordinary restart. Reporting
        // that as a guarded restart would be the fail-open the whole feature exists to close.
        LauncherCommandRouter.SendLauncherCommandAsync oldLauncher = (_, _, _, _) =>
            Task.FromResult<LauncherCommandResult?>(LauncherCommandResult.Ok());

        var outcome = await LauncherLifecycleRelay.SendDirectorVerbAsync(TenantId.Local, Machine, "restart",
            exePath: null, confirmProtected: false, new LauncherRegistry(), oldLauncher, CancellationToken.None,
            onlyIfEmpty: true);

        Assert.Equal(502, outcome.RelayStatus);
        Assert.False(outcome.Accepted);
        Assert.Contains("launcher-did-not-honour-only-if-empty", outcome.Payload);
    }

    /// <summary>
    /// An acknowledgement is READ, not counted. Each of these answers contradicts itself - the condition
    /// was applied and yet three sessions were live, or it was applied and nothing was restarted - and
    /// each is the shape a half-finished implementation on the other side produces. An earlier version of
    /// this check asked only whether the flag had been echoed back, and passed every one of them.
    /// </summary>
    [Theory]
    [InlineData("""{"ok":true,"restarted":true,"onlyIfEmpty":true,"sessions":3}""")]
    [InlineData("""{"ok":true,"restarted":false,"onlyIfEmpty":true,"sessions":0}""")]
    [InlineData("""{"onlyIfEmpty":true}""")]
    [InlineData("""{"ok":true,"restarted":true,"onlyIfEmpty":true,"sessions":"none"}""")]
    [InlineData("not json at all")]
    public async Task SendDirectorVerbAsync_AnAcknowledgementThatDoesNotHoldTogether_IsAFailure(string payload)
    {
        LauncherCommandRouter.SendLauncherCommandAsync half = (_, _, _, _) =>
            Task.FromResult<LauncherCommandResult?>(LauncherCommandResult.OkWithPayload(payload));

        var outcome = await LauncherLifecycleRelay.SendDirectorVerbAsync(TenantId.Local, Machine, "restart",
            exePath: null, confirmProtected: false, new LauncherRegistry(), half, CancellationToken.None,
            onlyIfEmpty: true);

        Assert.Equal(502, outcome.RelayStatus);
        Assert.Contains("launcher-did-not-honour-only-if-empty", outcome.Payload);
    }

    /// <summary>A launcher that adds fields of its own is still understood - the check requires what it
    /// needs and tolerates what it does not, so a newer launcher is not refused for being newer.</summary>
    [Fact]
    public async Task SendDirectorVerbAsync_AnAcknowledgementWithExtraFields_IsStillAccepted()
    {
        const string richer =
            """{"ok":true,"restarted":true,"onlyIfEmpty":true,"sessions":0,"startedPid":4242,"tookMs":812}""";
        LauncherCommandRouter.SendLauncherCommandAsync newer = (_, _, _, _) =>
            Task.FromResult<LauncherCommandResult?>(LauncherCommandResult.OkWithPayload(richer));

        var outcome = await LauncherLifecycleRelay.SendDirectorVerbAsync(TenantId.Local, Machine, "restart",
            exePath: null, confirmProtected: false, new LauncherRegistry(), newer, CancellationToken.None,
            onlyIfEmpty: true);

        Assert.Equal(200, outcome.RelayStatus);
        Assert.Equal(richer, outcome.Payload);
    }

    [Fact]
    public async Task SendDirectorVerbAsync_AnUnguardedVerbsPayload_DoesNotReplaceTheEnvelope()
    {
        // Nothing asked a conditional question here, so the answer stays the envelope every existing
        // caller of start/stop/restart reads - even if some future launcher starts writing a payload of
        // its own. A response shape that changes because the other side got chattier is a change nobody
        // asked for.
        LauncherCommandRouter.SendLauncherCommandAsync chatty = (_, _, _, _) =>
            Task.FromResult<LauncherCommandResult?>(LauncherCommandResult.OkWithPayload(
                """{"somethingNew":true}"""));

        var outcome = await LauncherLifecycleRelay.SendDirectorVerbAsync(TenantId.Local, Machine, "restart",
            exePath: null, confirmProtected: false, new LauncherRegistry(), chatty, CancellationToken.None);

        Assert.Equal(200, outcome.RelayStatus);
        Assert.Contains("\"via\":\"stream\"", outcome.Payload);
        Assert.DoesNotContain("somethingNew", outcome.Payload);
    }

    [Fact]
    public async Task SendDirectorVerbAsync_AnActionVerbThatSaysNothing_StillAnswersOkViaStream()
    {
        // The unchanged shape, pinned: every action verb that writes no payload of its own still gets the
        // synthesised one, so passing a launcher's payload through did not quietly rewrite start and stop.
        LauncherCommandRouter.SendLauncherCommandAsync ok = (_, _, _, _) =>
            Task.FromResult<LauncherCommandResult?>(LauncherCommandResult.Ok());

        var outcome = await LauncherLifecycleRelay.SendDirectorVerbAsync(TenantId.Local, Machine, "stop",
            exePath: null, confirmProtected: false, new LauncherRegistry(), ok, CancellationToken.None);

        Assert.Equal(200, outcome.RelayStatus);
        Assert.Contains("\"via\":\"stream\"", outcome.Payload);
    }
}
