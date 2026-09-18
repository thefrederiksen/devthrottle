using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end proof of the Snooze Length Phase 1 round-trip, entirely in-process (Snooze Length
/// mission, docs/architecture/snooze-length-mission-2026-07-11.md). It boots a REAL
/// <see cref="GatewayHost"/> on an ephemeral loopback port (no Tailscale front door -
/// CC_GATEWAY_NO_TAILSCALE=1), drives the REAL POST /sessions/{sid}/hold, and asserts the whole
/// state machine end to end:
///
///   1. Holding a session through the Gateway records a snooze-until at now + the per-user default.
///   2. Once that time passes, the /sessions fold returns the session to "needs you" on its own -
///      no client, no Director action.
///   3. The wired watchdog nudges the live Director off hold and clears the entry once the Director
///      confirms it is no longer held.
///   4. A pending snooze survives a full Gateway restart (re-armed from the on-disk registry).
///   5. A snooze survives the Director itself dying (dead-man's switch, served from the cached roster).
///
/// Gateway Cleanup mission (the cut): the roster is the PUSH store and every Director verb rides THE
/// TUNNEL. Each Director registers UNREACHABLE, connects the stream, PUSHES its sessions, and answers
/// the snooze watchdog's read/write verbs ("snapshot" for the raw OnHold read, "hold" for the nudge)
/// and the hold endpoint's "hold" forward over the tunnel. Because the advertised endpoint is dead, a
/// working result proves the tunnel. A DEAD Director drops its stream, so its sessions leave the push
/// store; the last-known roster (FleetRosterCache) still carries the session so the expiry overlay
/// returns it to "needs you" on its own.
///
/// CC_DIRECTOR_ROOT is redirected to a temp dir so the snooze-default setting and the registry file
/// live in an isolated store, never the real one. In the "DirectorRoot" collection so it never runs
/// alongside other root-redirecting tests.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class SnoozeEndToEndTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _prevNoTailscale;
    private readonly string _instancesDir;
    private readonly string _snoozePath;

    private GatewayHost _gw = null!;
    private HttpClient _http = null!;
    private readonly List<SnoozeFake> _fakes = new();

    public SnoozeEndToEndTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _prevNoTailscale = Environment.GetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE");
        _root = Path.Combine(Path.GetTempPath(), "cc-snooze-e2e-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        // Never touch the Tailscale Serve front door from a test (leftover-front-door hazard).
        Environment.SetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE", "1");
        _instancesDir = Path.Combine(_root, "instances");
        _snoozePath = Path.Combine(_root, "snooze", "snooze.json");
    }

    public async Task InitializeAsync()
    {
        (_gw, _http) = await StartGatewayAsync();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        foreach (var f in _fakes) await f.DisposeAsync();
        await _gw.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        Environment.SetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE", _prevNoTailscale);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    // Boot a real Gateway over the SAME isolated instances dir + snooze file, so a second call (after
    // disposing the first) is a genuine Gateway restart that must re-arm the persisted registry.
    private async Task<(GatewayHost, HttpClient)> StartGatewayAsync()
    {
        var gw = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_root, "worklists", "worklists.json"),
            snoozePath: _snoozePath,
            streamMode: true);
        await gw.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gw.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return (gw, http);
    }

    [Fact]
    public async Task Hold_records_the_default_snooze_and_the_fold_returns_it_to_needs_you_once_it_expires()
    {
        // The default snooze length is set through the REAL setting endpoint (one minute - the floor).
        var putResp = await _http.PutAsJsonAsync("gateway/snooze-default", new { minutes = 1 });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var fake = await StartFakeAsync("s1", onHold: false);

        // Drive the REAL Gateway hold endpoint - exactly what the phone/cockpit Snooze button calls.
        var holdResp = await _http.PostAsJsonAsync("sessions/s1/hold", new HoldRequest { OnHold = true });
        Assert.Equal(HttpStatusCode.OK, holdResp.StatusCode);

        // The Director now reports it held (the hold verb rode the tunnel), and the Gateway recorded a
        // snooze-until at ~now + 1 minute. The Director pushes its new held state up the stream.
        Assert.True(fake.CurrentOnHold("s1"));
        await fake.PushAsync();

        var entry = Assert.Single(_gw.SnoozeRegistry.Entries());
        Assert.Equal("s1", entry.SessionId);
        // The session was NOT working, so the hold landed at once and its clock is already running.
        Assert.False(entry.IsDeferred);
        var minutesOut = entry.SnoozeUntilUtc!.Value - DateTime.UtcNow;
        Assert.InRange(minutesOut.TotalSeconds, 45, 75); // one minute, generous tolerance

        // Still in the future -> the roster shows it parked (grey / onHold).
        var parked = await GetSession("s1");
        Assert.True(parked.OnHold);
        Assert.Equal("onHold", parked.TriageBucket);

        // Advance the clock deterministically by re-stamping the entry into the past (same as one minute
        // elapsing, without the wall-clock wait). Nothing else touches the session.
        _gw.SnoozeRegistry.Snooze("s1", DateTime.UtcNow.AddSeconds(-1), fake.DirectorId);

        // On its own, with no client and no Director action, the fold returns it to "needs you".
        var returned = await GetSession("s1");
        Assert.False(returned.OnHold);                  // overlay flipped it
        Assert.Equal("red", returned.EffectiveColor);
        Assert.Equal("needsYou", returned.TriageBucket);
    }

    [Fact]
    public async Task Hold_with_an_explicit_duration_records_that_duration_not_the_default()
    {
        // Issue #1500: the per-user default is short (1 minute), but the caller asks for a 12-hour snooze.
        // The Gateway must arm the timer for the REQUESTED length, not the default.
        await SetDefaultMinutes(1);
        var fake = await StartFakeAsync("s6", onHold: false);

        var holdResp = await _http.PostAsJsonAsync(
            "sessions/s6/hold", new HoldRequest { OnHold = true, SnoozeMinutes = 12 * 60 });
        Assert.Equal(HttpStatusCode.OK, holdResp.StatusCode);

        // The hold still rode the tunnel to the Director, and the recorded snooze-until is ~12 hours out -
        // clearly the requested length, not the 1-minute default.
        Assert.True(fake.CurrentOnHold("s6"));
        var entry = Assert.Single(_gw.SnoozeRegistry.Entries());
        Assert.Equal("s6", entry.SessionId);
        var until = entry.SnoozeUntilUtc!.Value - DateTime.UtcNow;
        Assert.InRange(until.TotalMinutes, 12 * 60 - 2, 12 * 60 + 1); // 12 hours, generous tolerance
    }

    [Fact]
    public async Task An_agent_requested_snooze_survives_the_deferral_and_its_clock_starts_when_the_turn_ends()
    {
        // DEFECT 20, END TO END, THROUGH THE REAL GATEWAY. This is THE case the feature exists to serve:
        // an agent snoozing its own session, which BY DEFINITION happens while it is working. It is not an
        // edge case - it is the headline case, and it was the one that failed.
        //
        // Before the fix: the Gateway armed a 12-hour timer at request time, the sweep ran 15 seconds
        // later, asked "is it held?", was told no (it was DEFERRED, which reports OnHold=false), concluded
        // the snooze was over and DELETED the timer. The turn then ended, the hold landed, and the session
        // was snoozed with NO CLOCK AT ALL - forever.
        await SetDefaultMinutes(1);
        var fake = await StartFakeAsync("s8", onHold: false, activityState: "Working");

        // The agent asks for a 12-hour snooze while it is working.
        var holdResp = await _http.PostAsJsonAsync(
            "sessions/s8/hold", new HoldRequest { OnHold = true, SnoozeMinutes = 12 * 60 });
        Assert.Equal(HttpStatusCode.OK, holdResp.StatusCode);

        // The Director deferred it, and the Gateway read that fact off HoldResponse.Pending: the entry is
        // recorded with its LENGTH and NO deadline, because the clock starts when the work ends.
        Assert.Equal(HoldStates.DeferredHold, fake.CurrentHoldState("s8"));
        var deferred = Assert.Single(_gw.SnoozeRegistry.Entries());
        Assert.True(deferred.IsDeferred);
        Assert.Null(deferred.SnoozeUntilUtc);
        Assert.Equal(12 * 60, deferred.PendingMinutes);

        // The session is still WORKING, so it is still blue and reads "Working" - the law.
        var working = await GetSession("s8");
        Assert.Equal("blue", working.EffectiveColor);
        Assert.Equal("Working", working.StateLabel);
        Assert.False(working.OnHold);

        // No background timer touches a deferral (there is no expiry sweep any more - it used to run every
        // 15 seconds and destroy the deferral here, which was defect 20). The deferral simply persists until
        // the work ends and it lands.
        Assert.True(_gw.SnoozeRegistry.Contains("s8"));           // THE REGRESSION: the deferral survives
        Assert.True(_gw.SnoozeRegistry.Entries().Single().IsDeferred);

        // The turn ends. The Director reports ONLY that it stopped working; the Gateway sees that on the
        // push seam and lands the deferral, which is what starts the clock (SnoozeLandingObserver).
        await fake.EndTurnAsync("s8");

        var armed = Assert.Single(_gw.SnoozeRegistry.Entries());
        Assert.False(armed.IsDeferred);                            // the clock is running at last
        Assert.NotNull(armed.SnoozeUntilUtc);
        var runsFor = armed.SnoozeUntilUtc!.Value - DateTime.UtcNow;
        Assert.InRange(runsFor.TotalMinutes, 12 * 60 - 2, 12 * 60 + 1); // 12 hours FROM THE TURN ENDING

        // And now it is a perfectly ordinary armed snooze: when it expires, the session comes back on its
        // own. Advance the clock by re-stamping the entry into the past.
        _gw.SnoozeRegistry.Snooze("s8", DateTime.UtcNow.AddSeconds(-1), fake.DirectorId);
        var returned = await GetSession("s8");
        Assert.False(returned.OnHold);
        Assert.Equal("needsYou", returned.TriageBucket);
    }

    [Fact]
    public async Task Snoozing_a_Starting_session_defers_and_survives_a_following_Starting_push()
    {
        // FINDING 2 (inspection), END TO END THROUGH THE REAL HOLD ENDPOINT. Session.IsWorking and the
        // working edge both treat Starting as active work, but the hold endpoint used to check only Working
        // when deciding whether to DEFER. So a snooze set while Starting was armed, not deferred - and the
        // very next Starting push deleted it through the working edge. The defer decision and the working edge
        // must agree on what "working" means.
        await SetDefaultMinutes(1);
        var fake = await StartFakeAsync("s9", onHold: false, activityState: "Starting");

        // Snooze it via the REAL hold endpoint while it is Starting.
        var holdResp = await _http.PostAsJsonAsync(
            "sessions/s9/hold", new HoldRequest { OnHold = true, SnoozeMinutes = 12 * 60 });
        Assert.Equal(HttpStatusCode.OK, holdResp.StatusCode);

        // It must DEFER (a length, no clock), not create an armed Held entry.
        Assert.Equal(HoldStates.DeferredHold, fake.CurrentHoldState("s9"));
        Assert.True(Assert.Single(_gw.SnoozeRegistry.Entries()).IsDeferred);

        // A following Starting push must NOT delete it - the working edge spares a deferred entry.
        fake.SetActivity("s9", "Starting");
        await fake.RePushAsync();

        Assert.True(_gw.SnoozeRegistry.Contains("s9"));
        Assert.True(Assert.Single(_gw.SnoozeRegistry.Entries()).IsDeferred);
    }

    [Fact]
    public async Task Hold_with_an_out_of_range_duration_is_rejected_and_arms_nothing()
    {
        // Issue #1500: a bad length fails loudly (400) and leaves NO side effect - the session is not held
        // and no timer is armed (no fallback / no silent clamp). MaxMinutes is 7 days (10080).
        await SetDefaultMinutes(1);
        var fake = await StartFakeAsync("s7", onHold: false);

        var tooLong = await _http.PostAsJsonAsync(
            "sessions/s7/hold", new HoldRequest { OnHold = true, SnoozeMinutes = 10081 });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var tooShort = await _http.PostAsJsonAsync(
            "sessions/s7/hold", new HoldRequest { OnHold = true, SnoozeMinutes = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        // Neither the Director nor the registry saw anything.
        Assert.False(fake.CurrentOnHold("s7"));
        Assert.Empty(_gw.SnoozeRegistry.Entries());
    }

    [Fact]
    public async Task An_expired_snooze_returns_the_session_immediately_with_a_durable_badge()
    {
        // This replaces "Watchdog_nudges_the_live_director_off_hold_and_clears_once_confirmed", which
        // asserted a two-round-trip handshake: sweep sees expired -> nudge the Director off hold -> keep
        // the entry -> next sweep sees the Director agree -> clear. That protocol is deleted. The Gateway
        // owns the hold and the clock, so an expired snooze IS returned, immediately, by the fold - no
        // nudge, no confirmation, no sweep at all.
        await SetDefaultMinutes(1);
        var fake = await StartFakeAsync("s2", onHold: true); // the Director's own claim, which counts for nothing
        _gw.SnoozeRegistry.Snooze("s2", DateTime.UtcNow.AddSeconds(-1), fake.DirectorId);

        var returned = await GetSession("s2");
        Assert.False(returned.OnHold);                  // already back, with no background timer having run
        Assert.Equal("needsYou", returned.TriageBucket);
        Assert.True(returned.SnoozeExpired);            // and it carries the "Snooze ended" badge
        Assert.DoesNotContain(false, fake.HoldCalls("s2")); // nobody was nudged

        // Round 2 finding 2: there is no expiry sweep to erase the badge's only source (the elapsed entry).
        // The entry lingers as the durable returned-by-timer tombstone, so a second read still shows the
        // badge - it is not a one-frame flicker that a background timer could delete out from under a
        // consumer.
        Assert.True(_gw.SnoozeRegistry.Contains("s2"));
        var again = await GetSession("s2");
        Assert.False(again.OnHold);                    // still needs-you, never re-held
        Assert.True(again.SnoozeExpired);               // badge still there
    }

    [Fact]
    public async Task A_director_cannot_make_a_session_look_snoozed_by_claiming_to_be_held()
    {
        // The inverse of the whole bug, and the property that makes the rest hold: hold is not something a
        // Director gets an opinion about. This fake Director reports OnHold=true and there is no entry in
        // the registry, so the session is NOT held - its claim is overwritten in the fold, unread.
        await StartFakeAsync("s6", onHold: true);

        var session = await GetSession("s6");

        Assert.False(session.OnHold);
        Assert.Equal(HoldStates.None, session.HoldState);
    }

    [Fact]
    public async Task The_owner_driving_a_turn_clears_the_snooze()
    {
        // This replaces "Early_return_before_expiry_clears_the_snooze_when_the_director_reports_not_held".
        // Same intent (issue #470: the session came back, so the snooze is over), correct trigger. The old
        // test believed a Director that said "not held" - and a Director said exactly that when a stray
        // terminal byte flipped it to Working, which is how a 12-hour snooze died in 90 seconds.
        //
        // The Director now reports only the FACT - the owner typed at this instant - and the Gateway rules.
        await SetDefaultMinutes(1);
        var fake = await StartFakeAsync("s3", onHold: true);
        _gw.SnoozeRegistry.Snooze("s3", DateTime.UtcNow.AddMinutes(30), fake.DirectorId); // NOT expired
        Assert.True((await GetSession("s3")).OnHold);

        // The owner comes back and drives a turn.
        fake.SetLastOwnerTurn("s3", DateTime.UtcNow.AddSeconds(1));
        await fake.RePushAsync();

        Assert.False(_gw.SnoozeRegistry.Contains("s3"));
        Assert.False((await GetSession("s3")).OnHold);
    }

    [Fact]
    public async Task Work_on_a_snoozed_session_clears_the_snooze_completely()
    {
        // THE MISSION, end to end. On 15 July 2026 session 8c17dc1c was snoozed, a reviewer session sent it
        // a fleet message 93 seconds later, it did the work - and it stayed snoozed, silently re-parked
        // where the owner would never look. The owner's law (17 July 2026): a snooze is a human "not now",
        // and the instant there is ANY work on that terminal the snooze is over, completely. It does not
        // matter that another agent, not the owner, woke it - a snooze exists to quiet a session with
        // nothing happening, and something is happening. The armed entry is DELETED (not merely outranked),
        // so when the work settles the session reads red "needs you", never grey "Snoozed".
        //
        // This deliberately reverses the earlier end-to-end test that asserted the message did NOT clear the
        // snooze; that test encoded the behaviour this mission exists to correct.
        await SetDefaultMinutes(1);
        var fake = await StartFakeAsync("s8", onHold: true);
        _gw.SnoozeRegistry.Snooze("s8", DateTime.UtcNow.AddHours(12), fake.DirectorId);
        Assert.True((await GetSession("s8")).OnHold); // snoozed to start

        fake.SetActivity("s8", "Working"); // woken by work the Director does not attribute to a send (no origin)
        await fake.RePushAsync();

        Assert.False(_gw.SnoozeRegistry.Contains("s8")); // the snooze is gone, deleted by work
        Assert.False((await GetSession("s8")).OnHold);   // no longer held

        // THE FULL MISSION PROMISE (round 4 finding 2): when the work SETTLES, the session reads red "needs
        // you" with NO "Snooze ended" badge - it came back by work, not by a timer. The whole terminal
        // branch: snooze -> work -> entry gone -> settle -> red / needsYou, no badge.
        fake.SetActivity("s8", "WaitingForInput");
        await fake.RePushAsync();

        var settled = await GetSession("s8");
        Assert.False(settled.OnHold);
        Assert.Equal("red", settled.EffectiveColor);
        Assert.Equal("needsYou", settled.TriageBucket);
        Assert.False(settled.SnoozeExpired); // came back by WORK, not by timer expiry - so no badge
    }

    [Fact]
    public async Task A_doorbell_on_a_snoozed_session_leaves_it_snoozed_and_the_owner_typing_ends_it()
    {
        // The Message Load mission, ruling 15 (inspection 4, ruling 8): the fleet doorbell is agent-origin, so
        // the work it starts does not end the owner's snooze. The owner's half of the law is unchanged: when he
        // types, the snooze is over.
        await SetDefaultMinutes(1);
        var fake = await StartFakeAsync("s9", onHold: true);
        var baseline = DateTime.UtcNow;
        _gw.SnoozeRegistry.Snooze("s9", DateTime.UtcNow.AddHours(12), fake.DirectorId, ownerTurnBaselineUtc: baseline);

        fake.SetActivity("s9", "Working");
        fake.SetWorkingOrigin("s9", WorkingOrigins.Agent); // the doorbell's line started this turn
        await fake.RePushAsync();
        Assert.True(_gw.SnoozeRegistry.Contains("s9"));

        fake.SetActivity("s9", "WaitingForInput");
        fake.SetWorkingOrigin("s9", null);
        await fake.RePushAsync();
        var settled = await GetSession("s9");
        Assert.True(settled.OnHold); // still snoozed after the doorbell turn

        fake.SetActivity("s9", "Working");
        fake.SetWorkingOrigin("s9", WorkingOrigins.Owner);
        fake.SetLastOwnerTurn("s9", baseline.AddSeconds(5)); // the owner typed
        await fake.RePushAsync();

        Assert.False(_gw.SnoozeRegistry.Contains("s9"));
        Assert.False((await GetSession("s9")).OnHold);
    }

    [Fact]
    public async Task A_pending_snooze_survives_a_full_gateway_restart()
    {
        await SetDefaultMinutes(1);
        var fake = await StartFakeAsync("s4", onHold: true);
        // An already-expired snooze written to disk. It must still fire after a restart.
        _gw.SnoozeRegistry.Snooze("s4", DateTime.UtcNow.AddSeconds(-1), fake.DirectorId);

        // Restart the Gateway: dispose the old host, boot a fresh one over the SAME on-disk registry.
        _http.Dispose();
        await _gw.StopAsync();
        (_gw, _http) = await StartGatewayAsync();

        // The pending snooze was re-armed from disk.
        Assert.True(_gw.SnoozeRegistry.Contains("s4"));

        // And it still drives the fold: the (still-running) Director reconnects its tunnel to the fresh
        // Gateway and re-pushes, and the expired snooze returns the session to "needs you" on its own.
        await fake.ReconnectAsync(_gw);
        var returned = await GetSession("s4");
        Assert.False(returned.OnHold);
        Assert.Equal("needsYou", returned.TriageBucket);
    }

    [Fact]
    public async Task A_dead_director_snooze_still_returns_to_needs_you_from_the_cached_roster()
    {
        // THE mission scenario: a session is snoozed, then its whole Director dies, and the snooze must
        // still bring it back - the dead-man's switch. The timer lives on the Gateway, and the last-known
        // roster (FleetRosterCache) keeps the session visible, so the fold's expiry overlay returns it to
        // "needs you" on its own even though the Director is gone.
        await SetDefaultMinutes(1);
        var fake = await StartFakeAsync("s5", onHold: true); // held on the Director

        // Prime the last-known-good roster while the Director is still alive, with a REAL hold - one this
        // Gateway owns. (The fake's own onHold claim is ignored now, so it cannot prime anything.)
        _gw.SnoozeRegistry.Snooze("s5", DateTime.UtcNow.AddHours(1), fake.DirectorId);
        var alive = await GetSession("s5");
        Assert.True(alive.OnHold);

        // Wind the snooze back so it is already up, then the Director DIES (its stream drops).
        _gw.SnoozeRegistry.Snooze("s5", DateTime.UtcNow.AddSeconds(-1), fake.DirectorId);
        await fake.DisconnectAsync();
        _fakes.Remove(fake);

        // The Gateway can no longer reach the Director (its sessions left the push store), but the cached
        // roster still carries s5 and the expiry overlay returns it to "needs you" - not lost by snoozing.
        var returned = await GetSession("s5");
        Assert.False(returned.OnHold);
        Assert.Equal("needsYou", returned.TriageBucket);
        Assert.True(returned.SnoozeExpired);
    }

    // ---- Round 2 finding 1: the reliable display-state channel is the SINGLE writer of hold ----
    // The one-shot hold mirror is deleted. Every edge this Gateway makes on its own initiative
    // (working-delete, deferral landing, exit, owner-turn) must now reach the Director's raw hold through
    // the change-gated display-state channel alone - and must send NO "hold" command, or the two writers
    // race again. The fake applies set-display-state exactly as the real Director does.

    [Fact]
    public async Task Reliable_channel_delivers_None_when_work_deletes_an_armed_snooze_and_sends_no_hold_command()
    {
        var fake = await StartFakeAsync("r1", onHold: false);
        _gw.SnoozeRegistry.Snooze("r1", DateTime.UtcNow.AddHours(12), fake.DirectorId); // armed
        await fake.RePushAsync();
        await WaitForHoldAsync(fake, "r1", HoldStates.Held); // the channel delivered Held (no one-shot in play)
        var holdCallsBaseline = fake.HoldCalls("r1").Count;

        fake.SetActivity("r1", "Working");
        await fake.RePushAsync();
        await WaitForHoldAsync(fake, "r1", HoldStates.None); // working edge -> None, via the reliable channel

        await Task.Delay(100); // give any (reverted) one-shot mirror time to fire, so the assert is real
        Assert.Equal(holdCallsBaseline, fake.HoldCalls("r1").Count); // SINGLE WRITER: the edge sent no hold command
    }

    [Fact]
    public async Task Reliable_channel_delivers_Held_when_a_deferral_lands_and_sends_no_hold_command()
    {
        var fake = await StartFakeAsync("r2", onHold: false, activityState: "Working");
        _gw.SnoozeRegistry.SnoozeDeferred("r2", 720, fake.DirectorId); // asked for while working
        await fake.RePushAsync();
        await WaitForHoldAsync(fake, "r2", HoldStates.DeferredHold);
        var holdCallsBaseline = fake.HoldCalls("r2").Count;

        await fake.EndTurnAsync("r2"); // settle -> the deferral lands
        await WaitForHoldAsync(fake, "r2", HoldStates.Held);

        await Task.Delay(100);
        Assert.Equal(holdCallsBaseline, fake.HoldCalls("r2").Count);
    }

    [Fact]
    public async Task Reliable_channel_delivers_None_when_a_snoozed_session_exits_and_sends_no_hold_command()
    {
        var fake = await StartFakeAsync("r3", onHold: false);
        _gw.SnoozeRegistry.Snooze("r3", DateTime.UtcNow.AddHours(12), fake.DirectorId);
        await fake.RePushAsync();
        await WaitForHoldAsync(fake, "r3", HoldStates.Held);
        var holdCallsBaseline = fake.HoldCalls("r3").Count;

        fake.SetActivity("r3", "Exited");
        await fake.RePushAsync();
        await WaitForHoldAsync(fake, "r3", HoldStates.None);

        await Task.Delay(100);
        Assert.Equal(holdCallsBaseline, fake.HoldCalls("r3").Count);
    }

    [Fact]
    public async Task Reliable_channel_delivers_None_when_the_owner_returns_and_sends_no_hold_command()
    {
        var fake = await StartFakeAsync("r4", onHold: false);
        var baseline = DateTime.UtcNow;
        _gw.SnoozeRegistry.Snooze("r4", DateTime.UtcNow.AddHours(12), fake.DirectorId, ownerTurnBaselineUtc: baseline);
        await fake.RePushAsync();
        await WaitForHoldAsync(fake, "r4", HoldStates.Held);
        var holdCallsBaseline = fake.HoldCalls("r4").Count;

        fake.SetLastOwnerTurn("r4", baseline.AddSeconds(5)); // a NEW owner turn
        await fake.RePushAsync();
        await WaitForHoldAsync(fake, "r4", HoldStates.None);

        await Task.Delay(100);
        Assert.Equal(holdCallsBaseline, fake.HoldCalls("r4").Count);
    }

    [Fact]
    public async Task No_stale_None_reaches_the_desktop_after_a_re_snooze_following_work()
    {
        // THE ADVERSE ORDER the one-shot mirror used to lose (finding 1): work clears the snooze (None), then
        // it is snoozed again (Held). With a single writer there is no delayed None to land after the fresh
        // Held, so the raw hold ends - and stays - Held.
        var fake = await StartFakeAsync("r5", onHold: false);
        _gw.SnoozeRegistry.Snooze("r5", DateTime.UtcNow.AddHours(12), fake.DirectorId);
        fake.SetActivity("r5", "Working");
        await fake.RePushAsync();
        await WaitForHoldAsync(fake, "r5", HoldStates.None); // work deleted it

        fake.SetActivity("r5", "WaitingForInput");
        await fake.RePushAsync(); // settle
        _gw.SnoozeRegistry.Snooze("r5", DateTime.UtcNow.AddHours(12), fake.DirectorId); // re-snooze, fresh clock
        await fake.RePushAsync();
        await WaitForHoldAsync(fake, "r5", HoldStates.Held);

        await Task.Delay(200); // no second writer can arrive late and overwrite it
        Assert.Equal(HoldStates.Held, fake.CurrentHoldState("r5"));
    }

    [Fact]
    public async Task The_real_hold_endpoint_is_not_a_second_writer_and_work_leaves_no_stale_hold()
    {
        // ROUND 4 FINDING 1. The POST /hold endpoint used to send its own hold command carrying the decided
        // HoldState - a SECOND writer that could land a stale Held after the reliable channel already sent
        // None, defeating the change gate forever. Now the endpoint records the registry and triggers the ONE
        // reliable display-state channel, which stamps the CURRENT folded hold down. It sends NO hold command.
        var fake = await StartFakeAsync("h1", onHold: false);

        // Snooze via the REAL endpoint. The reliable channel carried the hold down promptly (awaited), and no
        // hold command was sent.
        var resp = await _http.PostAsJsonAsync("sessions/h1/hold", new HoldRequest { OnHold = true, SnoozeMinutes = 12 * 60 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(HoldStates.Held, fake.CurrentHoldState("h1")); // delivered by the reliable channel
        Assert.Empty(fake.HoldCalls("h1"));                          // SINGLE WRITER: the endpoint sent no hold command

        // Work deletes the entry. With one writer there is no stale Held to resurrect: the raw hold ends None.
        fake.SetActivity("h1", "Working");
        await fake.RePushAsync();
        await WaitForHoldAsync(fake, "h1", HoldStates.None);
        await Task.Delay(150); // give any (reverted) second writer time to land, so the assert below is real
        Assert.Equal(HoldStates.None, fake.CurrentHoldState("h1")); // stays None - no second writer overwrote it
        Assert.Empty(fake.HoldCalls("h1"));                          // still no hold command from the endpoint
    }

    [Fact]
    public async Task Holding_a_session_that_has_already_exited_is_refused_and_records_nothing()
    {
        // ISSUE #824. Every edge that LIFTS a hold is driven by an ACTIVITY PUSH (SnoozeLandingObserver),
        // and an exited session never transitions again - so a hold recorded AFTER the exit is permanent.
        // The exit edge cannot save it either: dropping the hold when the session exits does nothing for a
        // hold that arrives afterwards. The only place that can refuse it is the entry point.
        var fake = await StartFakeAsync("x1", onHold: false, activityState: "Exited");

        var resp = await _http.PostAsJsonAsync("sessions/x1/hold", new HoldRequest { OnHold = true, SnoozeMinutes = 12 * 60 });

        // Refused, in plain English, naming the reason - not a silent 200 that parks it anyway.
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("exited", await resp.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // NOTHING was recorded. This is the assertion that matters: the old code took the not-working branch
        // and wrote an ARMED entry with a live clock, which no edge in the system could ever clear.
        Assert.Empty(_gw.SnoozeRegistry.Entries());

        // And the row still reads as what it is. Grey "Exited", never grey "Snoozed". Read from the
        // exited-inclusive roster: the default one drops exited rows, so it cannot show this either way.
        var session = await GetSessionIncludingExited("x1");
        Assert.False(session.OnHold);
        Assert.Equal("Exited", SessionOrdering.StateLabel(session));
    }

    [Fact]
    public async Task Holding_a_crashed_session_is_refused_so_the_crash_stays_visible()
    {
        // ISSUE #824, the expensive half. The fold reads OnHold BEFORE the base activity colour
        // (SessionOrdering.EffectiveColor: `s.OnHold ? "grey"`) and StateLabel returns "Snoozed" first of
        // all - so a hold on a CRASHED session paints deep-red "Crashed" over with grey "Snoozed" and keeps
        // it hidden for the whole snooze length. That is the signal the owner most needs to see, masked by
        // the one gesture a person triaging from a phone reaches for most easily.
        //
        // SCOPE, stated so this test is not read as more than it proves: the DEFAULT /sessions roster drops
        // exited rows, so the masking bites on the exited-inclusive surface, which is what is read here. The
        // permanent-registry-entry half (the other test) bites everywhere, roster or no roster.
        var fake = await StartFakeAsync("x2", onHold: false, activityState: "Exited");
        fake.SetCrashed("x2", true);
        await fake.RePushAsync();

        // Before: the crash is visible, and that is the state worth protecting.
        var before = await GetSessionIncludingExited("x2");
        Assert.Equal("error", SessionOrdering.EffectiveColor(before));
        Assert.Equal("Crashed", SessionOrdering.StateLabel(before));

        var resp = await _http.PostAsJsonAsync("sessions/x2/hold", new HoldRequest { OnHold = true });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        // After: unchanged. Asserted POSITIVELY on the crash reading, so this cannot pass by the session
        // having vanished from the roster or the fold having stopped ruling on it at all.
        var after = await GetSessionIncludingExited("x2");
        Assert.Equal("error", SessionOrdering.EffectiveColor(after));
        Assert.Equal("Crashed", SessionOrdering.StateLabel(after));
        Assert.Empty(_gw.SnoozeRegistry.Entries());
    }

    [Fact]
    public async Task Unsnoozing_an_exited_session_is_still_allowed_because_it_is_the_repair()
    {
        // The guard is one-directional ON PURPOSE. Refusing the CLEAR as well would strand any hold that is
        // already recorded - including every one written before this fix shipped - which is the exact state
        // issue #824 is about. A hold that outlives its session must always be removable.
        var fake = await StartFakeAsync("x3", onHold: false);

        // Park it while it is alive (the ordinary path), then let it die underneath the hold.
        var parked = await _http.PostAsJsonAsync("sessions/x3/hold", new HoldRequest { OnHold = true, SnoozeMinutes = 12 * 60 });
        Assert.Equal(HttpStatusCode.OK, parked.StatusCode);
        Assert.Single(_gw.SnoozeRegistry.Entries());
        fake.SetActivity("x3", "Exited");

        var released = await _http.PostAsJsonAsync("sessions/x3/hold", new HoldRequest { OnHold = false });

        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.Empty(_gw.SnoozeRegistry.Entries());
    }

    /// <summary>Poll the fake's raw hold until it reaches the expected value (the reliable channel is
    /// fire-and-forget, so delivery is asynchronous), then assert - giving a clear failure if it never does.</summary>
    private static async Task WaitForHoldAsync(SnoozeFake fake, string sid, string expected)
    {
        for (var i = 0; i < 200; i++) // up to ~4 seconds
        {
            if (string.Equals(fake.CurrentHoldState(sid), expected, StringComparison.Ordinal))
                return;
            await Task.Delay(20);
        }
        Assert.Equal(expected, fake.CurrentHoldState(sid));
    }

    // ---- helpers ----

    private async Task SetDefaultMinutes(int minutes)
    {
        var resp = await _http.PutAsJsonAsync("gateway/snooze-default", new { minutes });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private async Task<SessionDto> GetSession(string sid)
    {
        var sessions = await _http.GetFromJsonAsync<List<SessionDto>>("sessions", JsonOpts) ?? new();
        return Assert.Single(sessions, s => s.SessionId == sid);
    }

    /// <summary>
    /// Read a session from the EXITED-INCLUSIVE roster. The default /sessions read drops rows whose
    /// ActivityState is "Exited", so an exited session is invisible there and the fold's ruling on it cannot
    /// be observed at all; `?includeExited=true` is the supported query that keeps it in the set.
    /// </summary>
    private async Task<SessionDto> GetSessionIncludingExited(string sid)
    {
        var sessions = await _http.GetFromJsonAsync<List<SessionDto>>("sessions?includeExited=true", JsonOpts) ?? new();
        return Assert.Single(sessions, s => s.SessionId == sid);
    }

    private async Task<SnoozeFake> StartFakeAsync(string sid, bool onHold, string activityState = "WaitingForInput")
    {
        var fake = new SnoozeFake(sid, onHold, activityState);
        await fake.ConnectAsync(_gw, Token);
        _fakes.Add(fake);
        return fake;
    }

    /// <summary>
    /// A tunnel-connected stand-in Director for the snooze flow: it registers UNREACHABLE, pushes its
    /// sessions into the roster, and answers the two verbs the flow touches - "snapshot" (the raw
    /// per-session hold read the watchdog does) and "hold" (the park/un-park write) - with a MUTABLE
    /// per-session hold state the hold verb updates, so the Gateway's forward is observable. MachineName
    /// is this machine so the sweep treats it as reachable. It can reconnect to a fresh Gateway after a
    /// restart under the SAME director id, so the persisted snooze still addresses it.
    ///
    /// IT RUNS THE REAL HOLD RULE (upgraded 14 July 2026). Its <c>hold</c> verb mirrors
    /// <c>Session.RequestHold</c>: a hold asked for while the agent is WORKING is DEFERRED and answers
    /// <c>Pending=true</c>; otherwise it parks immediately. Until this change the fake always parked and
    /// never set <c>Pending</c> - so it could not produce a deferral at all, and defect 20 was invisible
    /// to every test that used it. A unit fake that is politer than the real Director does not prove the
    /// Gateway works; it proves the fake agrees with the Gateway.
    /// </summary>
    private sealed class SnoozeFake : IAsyncDisposable
    {
        public string DirectorId { get; } = Guid.NewGuid().ToString();
        public string MachineName { get; } = Environment.MachineName;

        private readonly object _gate = new();
        private readonly Dictionary<string, SessionDto> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<bool>> _holdCalls = new(StringComparer.Ordinal);
        private FakeTunnelDirector? _director;

        public SnoozeFake(string sid, bool onHold, string activityState = "WaitingForInput")
        {
            _sessions[sid] = new SessionDto
            {
                SessionId = sid,
                Agent = "ClaudeCode",
                RepoPath = "repo",
                ActivityState = activityState,
                Status = "Running",
                StatusColor = "red",
                HoldState = onHold ? HoldStates.Held : HoldStates.None,
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow,
            };
            _holdCalls[sid] = new List<bool>();
        }

        public bool CurrentOnHold(string sid) { lock (_gate) return _sessions[sid].OnHold; }
        // Nullable since 15 July 2026: an absent holdState now reads null ("the Director did not say")
        // rather than defaulting to None, so this fake Director's accessor has to admit that too.
        public string? CurrentHoldState(string sid) { lock (_gate) return _sessions[sid].HoldState; }
        public IReadOnlyList<bool> HoldCalls(string sid) { lock (_gate) return _holdCalls[sid].ToList(); }
        public void SetOnHold(string sid, bool value) { lock (_gate) _sessions[sid].OnHold = value; }

        /// <summary>The owner typed or spoke into this session at this instant - the FACT a real Director
        /// stamps at its input choke points and reports upward. The Gateway rules on what it means.</summary>
        public void SetLastOwnerTurn(string sid, DateTime atUtc) { lock (_gate) _sessions[sid].LastOwnerTurnAtUtc = atUtc; }

        /// <summary>The other fact a Director reports: what it is doing.</summary>
        public void SetActivity(string sid, string activityState) { lock (_gate) _sessions[sid].ActivityState = activityState; }
        public void SetWorkingOrigin(string sid, string? origin) { lock (_gate) _sessions[sid].WorkingOrigin = origin; }

        /// <summary>It died rather than finished (issue #959). A crash is NOT an ActivityState - a crashed
        /// session is "Exited" like any other - so it is a separate fact the Director reports, and the fold
        /// turns it into deep-red "Crashed" instead of grey "Exited".</summary>
        public void SetCrashed(string sid, bool value) { lock (_gate) _sessions[sid].Crashed = value; }

        /// <summary>Push the current state up the stream, as a real Director does on any change.</summary>
        public Task RePushAsync() => PushAsync();

        /// <summary>
        /// The turn ends: the agent stops working, so a deferred hold LANDS (Session.cs's settle edge).
        /// This is the moment the owner's ruling names - the snooze clock starts HERE, not when the
        /// snooze was asked for. Pushes the new state up the stream exactly as a real Director does on
        /// HoldStateChanged.
        /// </summary>
        public Task EndTurnAsync(string sid)
        {
            lock (_gate)
            {
                // Only the fact. A real Director no longer lands a deferral - it reports that the work
                // stopped, and the Gateway's SnoozeLandingObserver starts the clock.
                _sessions[sid].ActivityState = "WaitingForInput";
            }
            return PushAsync();
        }

        // Register UNREACHABLE + connect the stream + push the current snapshot.
        public async Task ConnectAsync(GatewayHost gw, string token)
        {
            _director = await FakeTunnelDirector.StartAsync(gw, token, DirectorId, MachineName, Dispatch);
            await PushAsync();
        }

        // After a Gateway restart the OLD stream is dead; reconnect the same director id to the fresh host.
        public async Task ReconnectAsync(GatewayHost gw)
        {
            await DisconnectAsync();
            await ConnectAsync(gw, Token);
        }

        public Task PushAsync()
        {
            SessionDto[] snap;
            lock (_gate) snap = _sessions.Values.Select(Clone).ToArray();
            return _director!.PushSnapshotAsync(snap);
        }

        public async Task DisconnectAsync()
        {
            if (_director is not null)
            {
                await _director.DisposeAsync();
                _director = null;
            }
        }

        private DirectorCommandResult Dispatch(DirectorCommand cmd)
        {
            switch (cmd.Verb)
            {
                case "snapshot":
                    lock (_gate)
                        return _sessions.TryGetValue(cmd.SessionId, out var s)
                            ? FakeTunnelDirector.Ok(Clone(s))
                            : DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "no such session");
                case "hold":
                {
                    var req = JsonSerializer.Deserialize<HoldRequest>(cmd.PayloadJson, FakeTunnelDirector.WebJson) ?? new HoldRequest();
                    lock (_gate)
                    {
                        if (!_sessions.TryGetValue(cmd.SessionId, out var s))
                            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "no such session");
                        _holdCalls[cmd.SessionId].Add(req.OnHold);

                        // Session.RequestHold's rule, faithfully: a hold asked for while the agent is
                        // WORKING defers ("snooze me when this finishes") and reports Pending; an
                        // un-hold clears everything; anything else parks now.
                        var working = string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(s.ActivityState, "Starting", StringComparison.OrdinalIgnoreCase);
                        if (!req.OnHold)
                            s.HoldState = HoldStates.None;
                        else
                            s.HoldState = working ? HoldStates.DeferredHold : HoldStates.Held;

                        return FakeTunnelDirector.Ok(new HoldResponse
                        {
                            OnHold = s.OnHold,
                            Pending = req.OnHold && working,
                        });
                    }
                }
                case "set-display-state":
                {
                    // The reliable display-state channel. Since round 2 finding 1 this is the SINGLE writer of
                    // hold down to the Director (the one-shot hold mirror is gone), so this fake must apply it
                    // exactly as the real FleetDisplayStateExecutor does, or CurrentHoldState/CurrentOnHold
                    // would never see an edge transition. A recognised HoldState reconciles the raw mirror; a
                    // blank/unknown value normalises to null and leaves it untouched.
                    var req = JsonSerializer.Deserialize<SetDisplayStateRequest>(cmd.PayloadJson, FakeTunnelDirector.WebJson) ?? new SetDisplayStateRequest();
                    lock (_gate)
                    {
                        if (!_sessions.TryGetValue(cmd.SessionId, out var s))
                            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "no such session");
                        var norm = HoldStates.Normalize(req.HoldState);
                        if (norm is not null) s.HoldState = norm;
                        s.SnoozeExpired = req.SnoozeExpired;
                    }
                    return DirectorCommandResult.Success();
                }
                default:
                    return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
            }
        }

        private static SessionDto Clone(SessionDto s) => new()
        {
            SessionId = s.SessionId,
            Agent = s.Agent,
            RepoPath = s.RepoPath,
            ActivityState = s.ActivityState,
            Status = s.Status,
            StatusColor = s.StatusColor,
            // Died-rather-than-finished (issue #959). Dropping it here would silently make the crash arm of
            // the fold untestable - a crashed session would arrive looking like a clean exit - which is
            // exactly the kind of gap this field list's own warning below is about.
            Crashed = s.Crashed,
            // The Director's hold claim still crosses the wire, and the Gateway still ignores it - it is
            // overwritten in the fold from the registry. Kept here only so these tests can prove that.
            HoldState = s.HoldState,
            // The owner-turn fact. A Director that does not report this cannot have its holds cleared by
            // the owner typing - so a Clone that drops it makes a real behaviour untestable while looking
            // fine. This hand-written field list has no compiler to catch that; add new fields here.
            LastOwnerTurnAtUtc = s.LastOwnerTurnAtUtc,
            // Who started the current work (the Message Load mission, ruling 15) - the same warning applies.
            WorkingOrigin = s.WorkingOrigin,
            CreatedAt = s.CreatedAt,
            LastActivityAt = s.LastActivityAt,
        };

        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }
}
