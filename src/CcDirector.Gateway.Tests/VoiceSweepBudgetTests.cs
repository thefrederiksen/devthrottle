using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE VOICE SWEEP'S PER-CYCLE BUDGET IS SPENT ON WORK, NOT ON ATTEMPTS (issue #2675).
///
/// What happened. The background narration sweep starts at most three generations per 45-second cycle,
/// and the cap is global across accounts on purpose - the wingman brain is one serialized resource. It
/// counted every attempt it DISPATCHED. One account's four sessions were owned by a computer running a
/// build that cannot send its conversation, so each attempt reached the Gateway's own store, found it
/// empty, recorded the honest "update that computer" fact and returned - having called neither the model
/// nor the speech provider. Each still took a slot. On 2026-09-04 that account consumed 4,356 slots and
/// every other account on the hosted Gateway got 5, so the one loop that recovers a session whose
/// narration failed never reached the sessions that needed it.
///
/// This test is the fleet in miniature: an account whose every voice session takes a no-op arm, beside a
/// second account with one session that genuinely has words to narrate, and ONE cycle of the REAL sweep.
///
/// IT DOES NOT DEPEND ON WHICH ACCOUNT IS SWEPT FIRST, which matters because nothing promises an order.
/// It asserts that FIVE sessions were all attempted in the one cycle; the old code could attempt three,
/// so it fails whichever account the pass happens to reach first.
///
/// REVERT-PROOF (against the real production lines in GatewayHost.SweepVoiceSessionsAsync): replace the
/// callback-counted budget with the old unconditional `generated++` after the dispatch and
/// <see cref="One_accounts_no_op_sessions_cannot_starve_another_accounts_session_out_of_the_same_cycle"/>
/// goes RED - three of the five sessions are attempted and the rest of the cycle never happens. Confirmed
/// by running it that way before the fix was written.
/// </summary>
public sealed class VoiceSweepBudgetTests : IAsyncLifetime
{
    private const string Token = "test-token";

    /// <summary>
    /// A suffix unique to THIS test instance, so no two tests in this class ever name the same session.
    ///
    /// It is not tidiness, it is the isolation this class depends on, and it was missing: the tests here
    /// share a process, xunit gives each one a fresh GatewayHost but the Gateway's turn store resolves under
    /// the process-wide CcStorage root, so a conversation SEEDED for a session id by one test is still there
    /// when another test uses the same id. The recovery test seeds one; with the ids shared, whichever order
    /// put it first left the starvation test looking at a session that already had words - which reads
    /// exactly like the sweep having skipped it. Caught by the full suite after both tests passed together
    /// in isolation, because the two orders are not the same experiment.
    ///
    /// The rule to carry: a test's isolation is only as deep as the deepest thing the production code
    /// derives from, and a Gateway per test is NOT a store per test.
    /// </summary>
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Four, because that is how many the account on the hosted Gateway had, and because it is more
    /// than the three-generation cap - so the starvation is reproduced rather than merely described.</summary>
    private string[] NoOpSessions => new[]
        { $"sweep-noop-1-{_run}", $"sweep-noop-2-{_run}", $"sweep-noop-3-{_run}", $"sweep-noop-4-{_run}" };

    private string NarratableSession => $"sweep-narratable-{_run}";

    private TenantId TenantNoOp { get; set; }
    private TenantId TenantNarratable { get; set; }

    private GatewayHost _gateway = null!;
    private FakeTunnelDirector _dirNoOp = null!;
    private FakeTunnelDirector _dirNarratable = null!;

    private readonly ConcurrentQueue<DirectorCommand> _seenByNarratable = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-voice-budget-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var deviceNoOp = HostedTestEnrollment.Enroll(_gateway, "sub-noop", "noop@example.com", "dev-noop", "MN");
        var deviceNarratable = HostedTestEnrollment.Enroll(_gateway, "sub-real", "real@example.com", "dev-real", "MR");
        TenantNoOp = deviceNoOp.Tenant;
        TenantNarratable = deviceNarratable.Tenant;

        _dirNoOp = await FakeTunnelDirector.StartAsync(_gateway, deviceNoOp.DeviceKey, "dir-noop", "MN",
            dispatch: _ => FakeTunnelDirector.Ok(new { ok = true }));
        _dirNarratable = await FakeTunnelDirector.StartAsync(_gateway, deviceNarratable.DeviceKey, "dir-real", "MR",
            dispatch: cmd =>
            {
                _seenByNarratable.Enqueue(cmd);
                return cmd.Verb == "screen-grid"
                    ? FakeTunnelDirector.Ok(new ScreenGridResponse
                    {
                        SessionId = cmd.SessionId,
                        HasGrid = true,
                        Rows = new List<string>
                        {
                            "continue with the work",
                            "Error: API key auth failed for the configured provider",
                            ">",
                        },
                    })
                    : FakeTunnelDirector.Ok(new { ok = true });
            });

        // Neither fake Director's Hello claims it sends conversations, which is exactly the state of the
        // real computer in the incident - so a session with nothing in the store takes the
        // "that computer cannot send its conversation" arm, the no-op arm this test is about.
        await _dirNoOp.PushSnapshotAsync(NoOpSessions.Select(Sample).ToArray());
        await _dirNarratable.PushSnapshotAsync(Sample(NarratableSession));

        foreach (var sid in NoOpSessions)
            _gateway.VoiceService!.Mark(TenantNoOp, sid);
        _gateway.VoiceService!.Mark(TenantNarratable, NarratableSession);
    }

    public async Task DisposeAsync()
    {
        await _dirNoOp.DisposeAsync();
        await _dirNarratable.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task One_accounts_no_op_sessions_cannot_starve_another_accounts_session_out_of_the_same_cycle()
    {
        // Only the second account has words to narrate. The first account's four sessions have nothing in
        // the store and a computer that cannot send one - they cost a dictionary lookup and a store read,
        // and nothing else.
        _gateway.SeedStoredConversationForTest(TenantNarratable, "dir-real", NarratableSession,
            ("User", "do the thing"), ("Assistant", "it is done"));

        // ONE cycle of the REAL production sweep - the timer callback itself, not a re-implementation.
        await _gateway.SweepVoiceSessionsAsync();

        // THE NO-OP ACCOUNT WAS FULLY SWEPT. The marker is recorded only by an ACTUAL attempt that read the
        // store and found it empty, so its presence on all four is the evidence that all four were tried -
        // and, under the old budget, at most three of these five facts could hold.
        foreach (var sid in NoOpSessions)
            Assert.True(_gateway.VoiceService!.DirectorCannotSendConversationFor(TenantNoOp, sid),
                $"session {sid} was never attempted, so the cycle ran out of budget on sessions that spend nothing");

        // AND THE OTHER ACCOUNT'S SESSION WAS REACHED IN THE SAME CYCLE. Its narration is the only attempt
        // here that costs the shared model and speech legs, and it is the one the budget exists to ration -
        // so it is the one that must survive a cycle full of attempts that cost neither. The live-screen
        // read arriving at ITS OWN Director is the proof the generation actually started.
        Assert.NotNull(await WaitForVerb(_seenByNarratable, "screen-grid", NarratableSession));
    }

    [Fact]
    public async Task A_no_op_session_recovers_on_the_next_pass_with_nobody_touching_the_gateway()
    {
        // The fix must NOT be "drop these sessions from the sweep". Keeping them is what makes the recovery
        // free: the moment that computer is updated it starts pushing, the next pass finds words, and
        // narration resumes with nobody touching the Gateway.
        await _gateway.SweepVoiceSessionsAsync();
        foreach (var sid in NoOpSessions)
            Assert.True(_gateway.VoiceService!.DirectorCannotSendConversationFor(TenantNoOp, sid),
                $"session {sid} was never attempted on the first pass");

        // That computer is updated and pushes its conversation for one of them.
        _gateway.SeedStoredConversationForTest(TenantNoOp, "dir-noop", NoOpSessions[0],
            ("User", "ask"), ("Assistant", "answered"));

        await _gateway.SweepVoiceSessionsAsync();

        // The marker comes off by itself on the very next pass - no restart, nothing to clear by hand. A
        // CHANGE from true to false, which is the one observation here that only the second pass can have
        // produced.
        Assert.False(_gateway.VoiceService!.DirectorCannotSendConversationFor(TenantNoOp, NoOpSessions[0]));

        // NOTHING IS ASSERTED HERE ABOUT THE OTHER THREE. Their markers are still set - but they were set by
        // the FIRST pass, so their presence is a leftover and would hold just as well if the second pass had
        // skipped them entirely (found in review). A check a stale value satisfies is not a check. The claim
        // that every one of them is visited in a single cycle is made, as a presence, by
        // One_accounts_no_op_sessions_cannot_starve_another_accounts_session_out_of_the_same_cycle.
    }

    [Fact]
    public async Task Cached_audio_cannot_hide_a_terminal_failure_after_a_later_user_message()
    {
        // Exact production shape from the failed inspection: the old clip survived because the sampled
        // Working transition was missed, while the stored conversation now ends with a later user message
        // whose only answer is the failure on the live terminal.
        _gateway.SeedStoredConversationForTest(TenantNarratable, "dir-real", NarratableSession,
            ("Assistant", "The older successful answer."),
            ("User", "continue with the work"));
        _gateway.VoiceService!.StoreReadyAudioForTest(TenantNarratable, NarratableSession,
            "old spoken answer", "The older successful answer.", new byte[] { 1, 2, 3 });
        _gateway.VoiceService.SetNothingToNarrate(TenantNarratable, NarratableSession, true);
        Assert.True(_gateway.VoiceService.HasVoice(TenantNarratable, NarratableSession));
        Assert.True(_gateway.VoiceService.NothingToNarrateFor(TenantNarratable, NarratableSession));

        // One invocation of the real timer callback, including its cached-audio guard and provider budget.
        await _gateway.SweepVoiceSessionsAsync();

        // The sweep itself awaits only the live-screen preparation, so the command must already be present
        // when it returns. This positive presence proves the callback path reached the owning Director.
        Assert.Contains(_seenByNarratable,
            command => command.Verb == "screen-grid" && command.SessionId == NarratableSession);

        // Selecting the terminal source clears the deliberately planted opposite verdict. If the callback
        // path skipped the live screen, it would leave this true after finding no current reply.
        Assert.False(_gateway.VoiceService.NothingToNarrateFor(TenantNarratable, NarratableSession));

        // The old clip cannot remain playable. A fast provider may already have replaced it; otherwise the
        // positively selected newer source leaves the card with no audio until its own clip arrives.
        var ready = _gateway.VoiceService.Get(TenantNarratable, NarratableSession);
        if (ready is null)
        {
            Assert.False(_gateway.VoiceService.HasVoice(TenantNarratable, NarratableSession));
        }
        else
        {
            Assert.Contains("API key auth failed", ready.Reply);
            Assert.NotEqual("The older successful answer.", ready.Reply);
        }
    }

    /// <summary>Poll for a verb+session rather than sleeping a fixed time: the sweep fires generation onto
    /// the thread pool and the tunnel round-trip is asynchronous.</summary>
    private static async Task<DirectorCommand?> WaitForVerb(ConcurrentQueue<DirectorCommand> seen, string verb, string sessionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var hit = seen.FirstOrDefault(c => c.Verb == verb && c.SessionId == sessionId);
            if (hit is not null) return hit;
            await Task.Delay(50);
        }
        return null;
    }

    private static SessionDto Sample(string sid) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        // Settled at a turn end - the idle sweep only pre-builds Idle / WaitingForInput / WaitingForPerm.
        ActivityState = "WaitingForInput",
        Status = "Running",
        StatusColor = "red",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };
}
