using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission (the cut): the <c>GET /sessions</c> aggregation. Post-cut the Gateway NEVER dials a
/// Director over HTTP - the fleet ROSTER is read from the PUSH store (Directors push their sessions up the
/// tunnel). So each Director here is registered UNREACHABLE (its advertised endpoint is dead and never dialed),
/// opens a real tunnel connection, and delivers its sessions with <c>PushSnapshot</c>. Because the endpoint is
/// dead, any session that appears could ONLY have come from the push store (tunnel-by-construction).
///
/// The aggregation still stamps the fleet-only fields (machine / user / tailnet endpoint / view url), computes
/// automatic session roles from the whole fleet, folds the authoritative color / triage bucket / NeedsYouSince
/// clock and the snooze-expiry overlay, applies the filters, and surfaces a Director that is NOT tunnel-connected
/// (no fresh push) as a <c>machineErrors</c> entry instead of dropping it silently. Those behaviors now operate
/// over pushed <see cref="SessionDto"/> rows; this exercises them by pushing the rows each case needs.
///
/// (The old "a Director whose HTTP dial returned 500 surfaces in machineErrors" trigger is gone - there is no
/// HTTP dial. The replacement failure mode, "a registered Director that never connected to the tunnel", still
/// surfaces in machineErrors, so that coverage is preserved with the new trigger.)
/// </summary>
public sealed class SessionsAggregationTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private readonly List<Fake> _fakes = new();

    // Isolated discovery dir so a real Director running on the dev machine never leaks
    // its sessions into these aggregation assertions.
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        foreach (var f in _fakes)
            if (f.Tunnel is not null) await f.Tunnel.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    // ---------- field stamping ----------

    [Fact]
    public async Task Aggregator_stamps_machine_user_tailnet_view_url()
    {
        var fake = await StartFake("MACHINE_A", "alice", new[]
        {
            Sample("s1", "ClaudeCode", "repo-a", "Idle", "green"),
        });
        await Register(fake);

        var sessions = await GetSessions();
        var s = Assert.Single(sessions);
        Assert.Equal("s1", s.SessionId);
        Assert.Equal("MACHINE_A", s.MachineName);
        Assert.Equal("alice", s.User);
        Assert.Equal(fake.BaseUrl, s.TailnetEndpoint);
        // THE LINK IS THE GATEWAY'S OWN, not the Director's. It used to be rooted on the Director's
        // endpoint with the Gateway carried along as a ?gw= parameter; the Director has no endpoint any
        // more, so the roots are swapped and the parameter is gone. Asserted against the address THIS
        // request arrived on, which is what the Gateway mints from.
        Assert.Equal($"{GatewayOrigin()}/sessions/s1", s.ViewUrl);
    }

    [Fact]
    public async Task Aggregator_returns_sessions_from_multiple_directors()
    {
        var a = await StartFake("MACHINE_A", "alice", new[]
        {
            Sample("a1", "ClaudeCode", "repo-a", "Idle", "green"),
            Sample("a2", "Pi", "repo-a", "Working", "yellow"),
        });
        var b = await StartFake("MACHINE_B", "bob", new[]
        {
            Sample("b1", "Codex", "repo-b", "WaitingForInput", "red"),
        });
        await Register(a);
        await Register(b);

        var sessions = await GetSessions();
        Assert.Equal(3, sessions.Count);
        Assert.Contains(sessions, s => s.SessionId == "a1" && s.MachineName == "MACHINE_A");
        Assert.Contains(sessions, s => s.SessionId == "a2" && s.MachineName == "MACHINE_A");
        Assert.Contains(sessions, s => s.SessionId == "b1" && s.MachineName == "MACHINE_B");
    }

    [Fact]
    public async Task Aggregator_stamps_authoritative_effective_color_and_triage_bucket()
    {
        var red = Sample("red1", "ClaudeCode", "repo", "WaitingForInput", "red");
        var briefing = Sample("brief1", "ClaudeCode", "repo", "WaitingForInput", "red");
        briefing.BriefingState = "Briefing";
        var parked = Sample("hold1", "ClaudeCode", "repo", "WaitingForInput", "red");
        var fake = await StartFake("M", "u", new[] { red, briefing, parked });
        await Register(fake);

        // The hold is the GATEWAY'S, so it is armed here rather than by the Director asserting OnHold on
        // its own DTO. That used to be how this test set it up, and it no longer means anything: the fold
        // overwrites whatever a Director claims about hold from this registry, unread. A Director cannot
        // make a session look snoozed by saying so.
        _gateway.SnoozeRegistry.Snooze("hold1", DateTime.UtcNow.AddMinutes(30), fake.DirectorId);

        var sessions = await GetSessions();

        var redOut = Assert.Single(sessions, s => s.SessionId == "red1");
        Assert.Equal("red", redOut.EffectiveColor);
        Assert.Equal("needsYou", redOut.TriageBucket);

        var briefingOut = Assert.Single(sessions, s => s.SessionId == "brief1");
        Assert.Equal("yellow", briefingOut.EffectiveColor);
        Assert.Equal("active", briefingOut.TriageBucket);

        var parkedOut = Assert.Single(sessions, s => s.SessionId == "hold1");
        Assert.Equal("grey", parkedOut.EffectiveColor);
        Assert.Equal("onHold", parkedOut.TriageBucket);
    }

    // ---------- snooze expiry overlay (Snooze Length mission) ----------

    [Fact]
    public async Task Snooze_that_has_not_expired_still_reads_as_snoozed()
    {
        // A held session with a snooze that is still in the future stays parked (grey / onHold).
        var held = Sample("snz1", "ClaudeCode", "repo", "WaitingForInput", "red");
        held.OnHold = true;
        var fake = await StartFake("M", "u", new[] { held });
        await Register(fake);
        _gateway.SnoozeRegistry.Snooze("snz1", DateTime.UtcNow.AddMinutes(30), fake.DirectorId);

        var s = Assert.Single(await GetSessions());
        Assert.True(s.OnHold);
        Assert.Equal("grey", s.EffectiveColor);
        Assert.Equal("onHold", s.TriageBucket);
    }

    [Fact]
    public async Task Expired_snooze_overlays_the_session_back_into_needs_you()
    {
        // The Director still reports the session held, but its snooze has elapsed. The /sessions fold
        // must override OnHold=false so the session reads as "needs you" again on its own - the whole
        // point of the mission. This holds even though the Director itself has not yet cleared the hold.
        var held = Sample("snz2", "ClaudeCode", "repo", "WaitingForInput", "red");
        held.OnHold = true;
        var fake = await StartFake("M", "u", new[] { held });
        await Register(fake);
        _gateway.SnoozeRegistry.Snooze("snz2", DateTime.UtcNow.AddMinutes(-1), fake.DirectorId); // already due

        var s = Assert.Single(await GetSessions());
        Assert.False(s.OnHold);                    // overlay flipped it
        Assert.Equal("red", s.EffectiveColor);     // back to needs-you red
        Assert.Equal("needsYou", s.TriageBucket);
        // Phase 2: the returned-from-snooze marker is stamped so clients show a distinct "Snooze ended"
        // badge and the phone push announces it once.
        Assert.True(s.SnoozeExpired);
    }

    // ---------- automatic session roles (chunk 1) ----------

    [Fact]
    public async Task Aggregator_computes_worker_manager_standalone_roles_and_suppresses_worker_red()
    {
        var mgr = Sample("mgr", "ClaudeCode", "repo", "Working", "blue");
        var worker = Sample("wrk", "ClaudeCode", "repo", "WaitingForInput", "red");
        worker.IsControlled = true;
        worker.ControllerSessionId = "mgr"; // controlled by the live manager
        var lone = Sample("lone", "ClaudeCode", "repo", "Working", "blue");
        var fake = await StartFake("M", "u", new[] { mgr, worker, lone });
        await Register(fake);

        var sessions = await GetSessions();

        // Roles computed from the whole fleet.
        Assert.Equal("Manager", Assert.Single(sessions, s => s.SessionId == "mgr").SessionRole);
        Assert.Equal("Standalone", Assert.Single(sessions, s => s.SessionId == "lone").SessionRole);
        var w = Assert.Single(sessions, s => s.SessionId == "wrk");
        Assert.Equal("Worker", w.SessionRole);

        // Fold: the LIVE worker's red is SUPPRESSED - it recedes and never enters the needs-you bucket.
        Assert.Equal("supporting", w.EffectiveColor);
        Assert.NotEqual("needsYou", w.TriageBucket);

        // The manager stays human-facing (its own working shows blue).
        Assert.Equal("blue", Assert.Single(sessions, s => s.SessionId == "mgr").EffectiveColor);
    }

    // ---------- defect 13: the role universe is the UNFILTERED fleet ----------

    [Fact]
    public async Task Filtered_read_resolves_roles_from_the_unfiltered_fleet_soAWorkersRedStaysSuppressed()
    {
        // DEFECT 13. The per-session filters run BEFORE the fleet pass, so a filter that drops a Worker's
        // CONTROLLER used to drop it out of the liveness set too - the Worker then resolved Standalone, the
        // red-suppression could not fire, and a worker nagged the human because of a query parameter.
        //
        // ?statusColor=red is the sharp case, and this is the same fleet as the role test above: the manager
        // is blue (filtered OUT), the worker is red (kept). The worker's controller is alive the whole time;
        // the caller simply asked a narrower question.
        var mgr = Sample("mgr", "ClaudeCode", "repo", "Working", "blue");
        var worker = Sample("wrk", "ClaudeCode", "repo", "WaitingForInput", "red");
        worker.IsControlled = true;
        worker.ControllerSessionId = "mgr";
        var fake = await StartFake("M", "u", new[] { mgr, worker });
        await Register(fake);

        var sessions = await GetSessions("statusColor=red");

        // The filter still narrows the RESPONSE - that contract is untouched.
        var w = Assert.Single(sessions);
        Assert.Equal("wrk", w.SessionId);

        // ...but the ROLE was resolved from the whole fleet, so the suppression still fires.
        // The SYMPTOM first - this is what the human saw: a worker's red breaking through, and the worker
        // sitting in NEEDS YOU, purely because the caller passed ?statusColor=red.
        Assert.Equal("supporting", w.EffectiveColor);
        Assert.NotEqual("needsYou", w.TriageBucket);
        // ...then the MECHANISM that produced it.
        Assert.Equal("Worker", w.SessionRole);
    }

    [Fact]
    public async Task Filtered_read_and_unfiltered_read_agree_about_the_same_session()
    {
        // The property the fix really buys: a session's presentation does not depend on the query that
        // happened to surface it. Same fleet, two reads, one answer.
        var mgr = Sample("mgr", "ClaudeCode", "repo", "Working", "blue");
        var worker = Sample("wrk", "ClaudeCode", "repo", "WaitingForInput", "red");
        worker.IsControlled = true;
        worker.ControllerSessionId = "mgr";
        var fake = await StartFake("M", "u", new[] { mgr, worker });
        await Register(fake);

        var unfiltered = Assert.Single(await GetSessions(), s => s.SessionId == "wrk");
        var filtered = Assert.Single(await GetSessions("statusColor=red"));

        Assert.Equal(unfiltered.SessionRole, filtered.SessionRole);
        Assert.Equal(unfiltered.EffectiveColor, filtered.EffectiveColor);
        Assert.Equal(unfiltered.StateLabel, filtered.StateLabel);
        Assert.Equal(unfiltered.TriageBucket, filtered.TriageBucket);
    }

    // ---------- defect 15: GET /sessions/{sid} runs the same fold as the roster ----------

    [Fact]
    public async Task SessionById_stamps_the_same_fold_as_the_roster()
    {
        // DEFECT 15. This route never ran the fold - it serialized the raw cached DTO, so EffectiveColor,
        // StateLabel and TriageBucket came back NULL while SessionDto documents all three as "Required on
        // Gateway /sessions responses".
        //
        // Scope note, deliberately honest: no shipped client fetches this route today, so this pins a
        // documented CONTRACT, not an observed user-visible bug. None is claimed.
        var mgr = Sample("mgr", "ClaudeCode", "repo", "Working", "blue");
        var worker = Sample("wrk", "ClaudeCode", "repo", "WaitingForInput", "red");
        worker.IsControlled = true;
        worker.ControllerSessionId = "mgr";
        var fake = await StartFake("M", "u", new[] { mgr, worker });
        await Register(fake);

        var roster = Assert.Single(await GetSessions(), s => s.SessionId == "wrk");
        var byId = await _http.GetFromJsonAsync<SessionDto>("sessions/wrk", JsonOpts);
        Assert.NotNull(byId);

        // Before the fix all three were null here.
        Assert.NotNull(byId!.EffectiveColor);
        Assert.NotNull(byId.StateLabel);
        Assert.NotNull(byId.TriageBucket);

        // And they agree with the roster - including the role, which needs the WHOLE fleet (this route only
        // ever located ONE session, so a naive per-session fold would have resolved Standalone and answered
        // "red" where the roster says "supporting").
        Assert.Equal(roster.SessionRole, byId.SessionRole);
        Assert.Equal(roster.EffectiveColor, byId.EffectiveColor);
        Assert.Equal(roster.StateLabel, byId.StateLabel);
        Assert.Equal(roster.TriageBucket, byId.TriageBucket);
    }

    // ---------- the voice-mode window no longer reads the Director's cooked colour ----------

    [Fact]
    public async Task VoiceWindow_yellow_survives_a_stale_or_absent_cooked_color()
    {
        // The Gateway's voice-mode window stamps BriefingState="Briefing" (which the fold paints yellow) for
        // a session whose wingman is generating its spoken summary. That stamp used to gate on s.StatusColor
        // - the DIRECTOR's cooked colour - which made a Gateway-rendered colour depend on a Director-made
        // decision, the one thing law 2 forbids, and it was the last Gateway consumer of the field. It now
        // gates on SessionOrdering.IsRawRed.
        //
        // READ THE SCOPE OF THIS TEST HONESTLY. It exercises the CONSUMER (IsBriefing) end-to-end with a
        // stale/absent cooked colour, which is worth pinning. It does NOT exercise the converted STAMP, and
        // it therefore does NOT fail if the conversion is reverted - IsBriefing already required raw red
        // before this change. See the note below for why no test covers the stamp itself.
        var s = Sample("v1", "ClaudeCode", "repo", "WaitingForInput", "");
        s.BriefingState = "Briefing";
        var fake = await StartFake("M", "u", new[] { s });
        await Register(fake);

        var outp = Assert.Single(await GetSessions());
        Assert.Equal("yellow", outp.EffectiveColor);
        Assert.Equal("active", outp.TriageBucket);
    }

    // NO TEST COVERS THE CONVERTED STAMP ITSELF, AND HERE IS WHY - stated rather than faked.
    //
    // The stamp's condition includes voiceGeneratingFor(sid), which GatewayHost wires to
    // `_voiceService?.IsGenerating(sid)`. _voiceService is private, constructed lazily, and its _generating
    // set is private with no test seam; no existing test drives it at the HTTP level. Making the stamp
    // reachable from a test means adding a seam to production for a test's benefit, which is a product change
    // and not this defect's business.
    //
    // What the conversion rests on instead is an argument from construction, not an equivalence claim: the
    // fold that CONSUMES the stamp (SessionOrdering.IsBriefing) is `BriefingState == "Briefing" &&
    // IsRawRed(s)` - it ALREADY requires raw red. So the rendered yellow's condition went from (cooked red
    // AND raw red) to (raw red AND raw red); the cooked gate was redundant with respect to every painted
    // pixel. The two halves of that argument ARE pinned: IsBriefing's raw-red requirement by the briefing
    // tests in SessionOrderingTests, and IsRawRed's indifference to the cooked colour by
    // IsRawRed_IsCaseInsensitive_AndIgnoresTheCookedColor.
    //
    // Residual risk, named: in the window where cooked-red is FALSE while raw-red is TRUE (a lagging or
    // empty StatusColor), the stamp did not fire before and does now - so a voice session gains the yellow
    // it should always have had. That is the intended repair, and it is the one behaviour no test here
    // observes.

    [Fact]
    public async Task Aggregator_deadControllerWorker_isStandalone_andItsRedSurfaces()
    {
        // A controlled session whose controller is NOT in the fleet (dead/gone) is role Standalone, so its
        // red is NOT suppressed - the escape hatch, so a stranded worker is never lost to the human.
        var orphan = Sample("orphan", "ClaudeCode", "repo", "WaitingForInput", "red");
        orphan.IsControlled = true;
        orphan.ControllerSessionId = "ghost-controller-not-in-fleet";
        var fake = await StartFake("M", "u", new[] { orphan });
        await Register(fake);

        var sessions = await GetSessions();

        var o = Assert.Single(sessions, s => s.SessionId == "orphan");
        Assert.Equal("Standalone", o.SessionRole);
        Assert.Equal("red", o.EffectiveColor);   // surfaces
        Assert.Equal("needsYou", o.TriageBucket);
    }

    [Fact]
    public async Task Aggregator_manager_reverts_to_standalone_when_its_last_worker_dies()
    {
        // Dynamic role: a manager whose only worker has EXITED (filtered out of the live roster) controls no
        // live worker anymore, so it reverts from Manager to Standalone.
        var mgr = Sample("mgr", "ClaudeCode", "repo", "Working", "blue");
        var deadWorker = Sample("dw", "ClaudeCode", "repo", "Exited", "grey");
        deadWorker.IsControlled = true;
        deadWorker.ControllerSessionId = "mgr";
        var fake = await StartFake("M", "u", new[] { mgr, deadWorker });
        await Register(fake);

        var sessions = await GetSessions(); // default excludes Exited, so the dead worker is not in the roster

        Assert.DoesNotContain(sessions, s => s.SessionId == "dw");
        Assert.Equal("Standalone", Assert.Single(sessions, s => s.SessionId == "mgr").SessionRole);
    }

    [Fact]
    public async Task Aggregator_explicit_role_wins_over_derivation_and_a_held_architect_reports_to_its_supervisor()
    {
        // A session that WOULD auto-derive Worker (controlled + live controller) but carries an EXPLICIT
        // Architect role resolves to Architect - explicit wins, sticky, and it is the only way to be an
        // Architect since it cannot be inferred from the spawn graph. THAT HALF HAS NEVER CHANGED and is
        // still what this test is mainly here to prove.
        //
        // THE SECOND HALF HAS NOW BEEN WRITTEN THREE WAYS, AND THIS ONE IS NOT A REVERSAL OF THE LAST.
        // It asserted red + needsYou until 2026-09-03, became slate + "Snoozed" + onHold (the owner's
        // 2026-07-09 amendment: "the Architect does NOT push needs-you or status to the human"), and went
        // back to red + needsYou on 2026-09-06 when he overturned that: "parking the architect seat is
        // wrong. the architect is always the session i talk to."
        //
        // WHAT CHANGED ON 2026-09-13 IS NOT THE ARCHITECT RULING - IT IS WHAT THE QUESTION IS. The seat no
        // longer decides who a stopped session may ask; the only thing that decides is whether a supervisor
        // is alive right now. "It should only go red if it doesn't have a parent. If it does have a parent,
        // the parent should be notified."
        //
        // READ THIS SESSION'S SHAPE BEFORE READING A CONTRADICTION INTO IT. The Architect built below is
        // CONTROLLED, by a manager that is alive on the same roster. It is not the seat the owner opens and
        // talks to - that one has no supervisor, and it still goes red, which is what
        // Architect_Stopped_IsRed_NeedsYou_AndNotParked asserts in the unit suite. This one was spawned by a
        // live manager and answers to it, so its report goes there. Both of his rulings hold at once: the
        // architect he talks to reaches him, and a session somebody alive is holding reports to them.
        //
        // THE HISTORY IS THE REASON THIS FILE SAYS SO MUCH. The July amendment sat in the design document,
        // unimplemented, for two months, because the tests of the day asserted the SHIPPED behaviour in the
        // present tense - there was nothing anywhere that could go red when the written rule and the code
        // disagreed. A behaviour test cannot close that whichever way it points; the guard that does is
        // SupervisionRuleMatchesTheDesignDocumentTests in CcDirector.Gateway.UnitTests, which reads the
        // attention table out of docs/new_architecture/sessions.html and fails when the
        // document and SessionOrdering.IsSupervised disagree about any case.
        var mgr = Sample("mgr", "ClaudeCode", "repo", "Working", "blue");
        var arch = Sample("arch", "ClaudeCode", "repo", "WaitingForInput", "red");
        arch.IsControlled = true;
        arch.ControllerSessionId = "mgr"; // would derive Worker...
        arch.ExplicitRole = "Architect";   // ...but the explicit role wins
        var fake = await StartFake("M", "u", new[] { mgr, arch });
        await Register(fake);

        var sessions = await GetSessions();

        var a = Assert.Single(sessions, s => s.SessionId == "arch");
        Assert.Equal("Architect", a.SessionRole);     // the explicit seat still wins, unchanged
        Assert.True(a.HasLiveSupervisor);             // and its manager is alive on the same roster
        Assert.Equal("supporting", a.EffectiveColor); // so it is held, and reports to the manager
        Assert.Equal("Snoozed", a.StateLabel);
        // THE BUCKET, OVER REAL HTTP, and this is the right place to prove it: the whole pass runs here
        // (push, fleet assembly, role resolution, fold, serialisation), so an answer the wire dropped would
        // show up. Note HasLiveSupervisor is asserted from the SERVED row - it is resolved fleet-wide and
        // serialised, not something this test set.
        Assert.Equal("onHold", a.TriageBucket);
    }

    [Fact]
    public async Task Aggregator_an_architect_with_no_supervisor_still_reaches_the_owner()
    {
        // THE OTHER HALF, AND THE ONE THE OWNER'S 2026-09-06 RULING IS ACTUALLY ABOUT: the Architect he
        // opens himself. Nobody is holding it, so it goes red and lands in his queue - exactly as it did
        // before the attention rule changed.
        //
        // Without this beside the test above, that one reads as "the Architect was silenced again", which is
        // the ruling reversed rather than the question replaced. Together they say what is actually true: the
        // seat is not what decides, and the architect he talks to still reaches him.
        var arch = Sample("arch", "ClaudeCode", "repo", "WaitingForInput", "red");
        arch.ExplicitRole = "Architect";   // his own design seat - no controller, nobody above it
        var fake = await StartFake("M", "u", new[] { arch });
        await Register(fake);

        var sessions = await GetSessions();

        var a = Assert.Single(sessions, s => s.SessionId == "arch");
        Assert.Equal("Architect", a.SessionRole);
        Assert.False(a.HasLiveSupervisor);
        Assert.Equal("red", a.EffectiveColor);
        Assert.Equal("Needs you", a.StateLabel);
        Assert.Equal("needsYou", a.TriageBucket);
    }

    [Fact]
    public async Task Aggregator_manager_derivation_excludes_architect()
    {
        // An explicit Architect that controls a live worker stays Architect (NOT re-derived to Manager),
        // because explicit wins over the Manager derivation. The worker it controls is still a Worker.
        var arch = Sample("arch", "ClaudeCode", "repo", "Working", "blue");
        arch.ExplicitRole = "Architect";
        var worker = Sample("wrk", "ClaudeCode", "repo", "Working", "blue");
        worker.IsControlled = true;
        worker.ControllerSessionId = "arch"; // controlled by the (alive) architect
        var fake = await StartFake("M", "u", new[] { arch, worker });
        await Register(fake);

        var sessions = await GetSessions();

        Assert.Equal("Architect", Assert.Single(sessions, s => s.SessionId == "arch").SessionRole);
        Assert.Equal("Worker", Assert.Single(sessions, s => s.SessionId == "wrk").SessionRole);
    }

    // ---------- error surfacing ----------

    [Fact]
    public async Task Failed_director_surfaces_in_machine_errors_envelope()
    {
        // GOOD is tunnel-connected and pushes a session; BAD is registered but never connects to the tunnel
        // (no fresh push). Post-cut that unconnected Director is the failure mode - it must surface in
        // machineErrors, never be dropped silently.
        var good = await StartFake("GOOD", "alice", new[]
        {
            Sample("g1", "ClaudeCode", "repo", "Idle", "green"),
        });
        var bad = await StartFake("BAD", "alice", sessions: null, connected: false);
        await Register(good);
        await Register(bad);

        var env = await GetEnvelope();
        var s = Assert.Single(env.Sessions);
        Assert.Equal("g1", s.SessionId);

        var err = Assert.Single(env.MachineErrors);
        Assert.Equal("BAD", err.MachineName);
        Assert.False(string.IsNullOrEmpty(err.Error), "machineError.Error should be populated");
    }

    /// <summary>
    /// A DIRECTOR THAT SAID GOODBYE IS NOT AN UNREACHABLE MACHINE, and the roster read has to say so.
    ///
    /// This is the end-to-end shape of the defect that started this: a shut-down Director's registration
    /// survives for a day (it is deliberately not deleted), so every roster read for that day reported it
    /// offline, put it in machineErrors, and the Fleet Map counted that row and called it a machine. Here the
    /// machine also carries a LIVE Director pushing a session - the whole point, because that is the fleet the
    /// map was lying about.
    ///
    /// Revert-prove: remove the <c>d.StoppedAtUtc is not null</c> branch from the roster read in
    /// GatewayEndpoints and this goes red on the machineErrors assertion, after the positive controls above
    /// it have passed.
    /// </summary>
    [Fact]
    public async Task A_director_that_said_goodbye_is_not_an_error_and_not_a_dead_machine()
    {
        var live = await StartFake("SOREN_NORTH", "soren", new[]
        {
            Sample("s1", "ClaudeCode", "repo", "Working", "blue"),
        });
        var slot5 = await StartFake("SOREN_NORTH", "soren", sessions: null, connected: false);
        await Register(live);
        await Register(slot5);

        // Positive control: BEFORE the farewell this is exactly the reported-unreachable case - one offline
        // Director, in machineErrors, with a banner. Without this the assertions below would also pass if the
        // Director had simply never been registered.
        var before = await GetEnvelope();
        Assert.Contains(before.MachineErrors, m => m.DirectorId == slot5.DirectorId);
        Assert.Equal(DirectorReachabilityDto.StateOffline,
            Assert.Single(before.Directors, d => d.DirectorId == slot5.DirectorId).State);
        Assert.NotNull(before.UnreachableBanner);
        // ...and even THEN it is reported as a director, never as the machine - the live Director is on it.
        Assert.Contains("director could not be reached", before.UnreachableBanner);
        Assert.DoesNotContain("machine could not be reached", before.UnreachableBanner);

        // The goodbye.
        _gateway.Registry.MarkStopped(CcDirector.Core.Tenancy.TenantId.Local, slot5.DirectorId);

        var after = await GetEnvelope();
        Assert.DoesNotContain(after.MachineErrors, m => m.DirectorId == slot5.DirectorId);
        Assert.Equal(DirectorReachabilityDto.StateStopped,
            Assert.Single(after.Directors, d => d.DirectorId == slot5.DirectorId).State);
        Assert.Null(after.UnreachableBanner);

        // The live Director on the same machine is untouched throughout - its session is still served.
        Assert.Equal("s1", Assert.Single(after.Sessions).SessionId);
        Assert.Equal(DirectorReachabilityDto.StateOnline,
            Assert.Single(after.Directors, d => d.DirectorId == live.DirectorId).State);
    }

    /// <summary>
    /// The Gateway ships the finished presentation with every reachability row, so no client re-derives what
    /// a state means. A stopped Director is "Not running" and is NOT offered as free capacity: its tunnel
    /// went with the process, so a start could not be delivered.
    /// </summary>
    [Fact]
    public async Task The_envelope_carries_the_gateways_finished_presentation()
    {
        var live = await StartFake("SOREN_NORTH", "soren", new[] { Sample("s1", "ClaudeCode", "repo", "Working", "blue") });
        var slot5 = await StartFake("SOREN_NORTH", "soren", sessions: null, connected: false);
        await Register(live);
        await Register(slot5);
        _gateway.Registry.MarkStopped(CcDirector.Core.Tenancy.TenantId.Local, slot5.DirectorId);

        var env = await GetEnvelope();

        var stopped = Assert.Single(env.Directors, d => d.DirectorId == slot5.DirectorId);
        Assert.Equal("Not running", stopped.StateLabel);
        Assert.False(stopped.CanStartSession);
        Assert.Equal("No sessions - this director is not running", stopped.EmptySlotText);

        var online = Assert.Single(env.Directors, d => d.DirectorId == live.DirectorId);
        Assert.Equal("", online.StateLabel);
        Assert.True(online.CanStartSession);
        Assert.False(online.DataIsStale);
    }

    [Fact]
    public async Task Flat_response_drops_failed_directors_silently()
    {
        // Backward-compat path. DirectorView still consumes the flat shape. BAD is registered but never
        // tunnel-connected, so it contributes no sessions and does not appear in the flat array.
        var good = await StartFake("GOOD", "alice", new[] { Sample("g1", "ClaudeCode", "repo", "Idle", "green") });
        var bad = await StartFake("BAD", "alice", sessions: null, connected: false);
        await Register(good);
        await Register(bad);

        var sessions = await GetSessions();
        var s = Assert.Single(sessions);
        Assert.Equal("g1", s.SessionId);
    }

    // ---------- filtering ----------

    [Fact]
    public async Task Default_response_hides_exited_sessions()
    {
        var fake = await StartFake("M", "u", new[]
        {
            Sample("live", "ClaudeCode", "r", "Idle", "green"),
            Sample("dead", "ClaudeCode", "r", "Exited", "unknown"),
        });
        await Register(fake);

        var sessions = await GetSessions();
        var s = Assert.Single(sessions);
        Assert.Equal("live", s.SessionId);
    }

    [Fact]
    public async Task IncludeExited_true_returns_exited_sessions()
    {
        var fake = await StartFake("M", "u", new[]
        {
            Sample("live", "ClaudeCode", "r", "Idle", "green"),
            Sample("dead", "ClaudeCode", "r", "Exited", "unknown"),
        });
        await Register(fake);

        var sessions = await GetSessions("includeExited=true");
        Assert.Equal(2, sessions.Count);
    }

    [Fact]
    public async Task StatusColor_filter_narrows_results()
    {
        var fake = await StartFake("M", "u", new[]
        {
            Sample("r1", "ClaudeCode", "r", "WaitingForInput", "red"),
            Sample("y1", "ClaudeCode", "r", "Working", "yellow"),
            Sample("g1", "ClaudeCode", "r", "Idle", "green"),
        });
        await Register(fake);

        var sessions = await GetSessions("statusColor=red");
        var s = Assert.Single(sessions);
        Assert.Equal("r1", s.SessionId);
    }

    [Fact]
    public async Task Machine_filter_narrows_results()
    {
        var a = await StartFake("MACHINE_A", "u", new[] { Sample("a1", "ClaudeCode", "r", "Idle", "green") });
        var b = await StartFake("MACHINE_B", "u", new[] { Sample("b1", "ClaudeCode", "r", "Idle", "green") });
        await Register(a);
        await Register(b);

        var sessions = await GetSessions("machine=MACHINE_A");
        var s = Assert.Single(sessions);
        Assert.Equal("a1", s.SessionId);
    }

    [Fact]
    public async Task Q_search_matches_name_and_repo_path()
    {
        var fake = await StartFake("M", "u", new[]
        {
            new SessionDto { SessionId = "x1", Agent = "ClaudeCode", RepoPath = "/repos/auth-middleware", Name = "", ActivityState = "Idle", StatusColor = "green" },
            new SessionDto { SessionId = "x2", Agent = "ClaudeCode", RepoPath = "/repos/other", Name = "fix auth flow", ActivityState = "Idle", StatusColor = "green" },
            new SessionDto { SessionId = "x3", Agent = "ClaudeCode", RepoPath = "/repos/other", Name = "rename hotkey", ActivityState = "Idle", StatusColor = "green" },
        });
        await Register(fake);

        var sessions = await GetSessions("q=auth");
        Assert.Equal(2, sessions.Count);
        Assert.Contains(sessions, s => s.SessionId == "x1");
        Assert.Contains(sessions, s => s.SessionId == "x2");
    }

    [Fact]
    public async Task Agent_filter_narrows_results()
    {
        var fake = await StartFake("M", "u", new[]
        {
            Sample("a", "ClaudeCode", "r", "Idle", "green"),
            Sample("b", "Pi", "r", "Idle", "green"),
            Sample("c", "Codex", "r", "Idle", "green"),
        });
        await Register(fake);

        var sessions = await GetSessions("agent=Pi");
        var s = Assert.Single(sessions);
        Assert.Equal("b", s.SessionId);
    }

    // ---------- NeedsYouSince stamping (issue #218) ----------

    [Fact]
    public async Task NeedsYouSince_is_nonNull_for_red_and_null_for_nonRed()
    {
        var fake = await StartFake("M", "u", new[]
        {
            Sample("red1", "ClaudeCode", "r", "WaitingForInput", "red"),
            Sample("blue1", "ClaudeCode", "r", "Working", "blue"),
        });
        await Register(fake);

        var sessions = await GetSessions();
        var red = Assert.Single(sessions, s => s.SessionId == "red1");
        var blue = Assert.Single(sessions, s => s.SessionId == "blue1");
        Assert.NotNull(red.NeedsYouSince);
        Assert.Null(blue.NeedsYouSince);
    }

    [Fact]
    public async Task NeedsYouSince_is_within_5s_of_entry()
    {
        var fake = await StartFake("M", "u", new[]
        {
            Sample("red1", "ClaudeCode", "r", "WaitingForInput", "red"),
        });
        await Register(fake);

        var before = DateTime.UtcNow;
        var sessions = await GetSessions();
        var after = DateTime.UtcNow;

        var red = Assert.Single(sessions);
        Assert.NotNull(red.NeedsYouSince);
        // Stamped at the moment the aggregation observed it red: within the poll window.
        Assert.InRange(red.NeedsYouSince!.Value, before.AddSeconds(-5), after.AddSeconds(5));
    }

    [Fact]
    public async Task NeedsYouSince_is_stable_across_polls_while_red()
    {
        var fake = await StartFake("M", "u", new[]
        {
            Sample("red1", "ClaudeCode", "r", "WaitingForInput", "red"),
        });
        await Register(fake);

        var first = Assert.Single(await GetSessions()).NeedsYouSince;
        await Task.Delay(50);
        var second = Assert.Single(await GetSessions()).NeedsYouSince;

        Assert.NotNull(first);
        Assert.NotNull(second);
        // Must not advance while the session stays red (AC: byte-identical).
        Assert.Equal(first!.Value, second!.Value);
    }

    [Fact]
    public async Task NeedsYouSince_resets_strictly_later_after_leaving_and_re_entering_red()
    {
        var session = Sample("flip", "ClaudeCode", "r", "WaitingForInput", "red");
        var fake = await StartFake("M", "u", new[] { session });
        await Register(fake);

        // Episode 1: red.
        var first = Assert.Single(await GetSessions()).NeedsYouSince;
        Assert.NotNull(first);

        // A new turn starts: leaves red -> NeedsYouSince must go null. The Director re-pushes the changed row.
        session.StatusColor = "blue";
        session.ActivityState = "Working";
        await fake.Tunnel!.PushSnapshotAsync(session);
        var between = Assert.Single(await GetSessions()).NeedsYouSince;
        Assert.Null(between);

        await Task.Delay(50);

        // Episode 2: returns to red -> a strictly-later stamp than episode 1. Re-push the red row.
        session.StatusColor = "red";
        session.ActivityState = "WaitingForInput";
        await fake.Tunnel!.PushSnapshotAsync(session);
        var second = Assert.Single(await GetSessions()).NeedsYouSince;
        Assert.NotNull(second);
        Assert.True(second!.Value > first!.Value,
            $"second episode stamp {second.Value:o} must be strictly later than first {first.Value:o}");
    }

    [Fact]
    public async Task NeedsYouSince_is_null_while_briefing_overlay_keeps_effective_color_off_red()
    {
        // A raw-red session that is still being briefed presents as effective YELLOW (not red),
        // so the waiting clock must not start - briefing time is not waiting time. We assert the
        // contract directly via SessionOrdering (the same fold the Gateway uses): with
        // BriefingState="Briefing" + raw red, EffectiveColor is "yellow", so isRed is false and
        // NeedsYouSince stays null. (The Gateway's briefStampFor only runs with briefing enabled;
        // here we prove the EffectiveColor gate the stamp keys off.)
        var briefing = Sample("briefing1", "ClaudeCode", "r", "WaitingForInput", "red");
        briefing.BriefingState = "Briefing";
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(briefing));
        Assert.NotEqual("red", SessionOrdering.EffectiveColor(briefing));
    }

    // ---------- owner-cache pruning on observed exit (issue #291) ----------

    // Gateway Cleanup (the cut) DELETED test:
    // Aggregator_prunes_owner_cache_for_session_no_longer_live_on_reachable_director_then_ws_proxy_is_404.
    // Its premise - the owner-cache prune flips the per-session WS proxy from #288's 503 (owner offline) to a
    // 404 (session gone) - is deleted machinery. The WS proxy now resolves ONLY from the push store
    // (PushedSessionStore.TryLocate): a session not in a fresh push is 503 "not connected", never 404, and the
    // SessionOwnerCache is no longer consulted for that decision (nothing reads OwnerOf in production). So the
    // 404 outcome the test asserted no longer exists by design.

    [Fact]
    public async Task Aggregator_prune_does_not_touch_a_session_owned_by_a_different_offline_director()
    {
        // #288 must not regress: a session cached against an OFFLINE Director (id never registered, so
        // unreachable) must survive a reconcile triggered by a DIFFERENT reachable Director, and the
        // WS proxy must still answer 503 for it.
        var reachable = await StartFake("M", "u", new[] { Sample("live", "ClaudeCode", "r", "Idle", "green") });
        await Register(reachable);
        _gateway.SessionOwners.Remember(TenantId.Local, "offline-owned", "dead-director-id");

        await GetSessions();

        Assert.Equal("dead-director-id", _gateway.SessionOwners.OwnerOf(TenantId.Local, "offline-owned"));
        var resp = await _http.GetAsync("sessions/offline-owned/stream");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    // ---------- issue #335: Director-supplied identity fields win over Gateway-derived ----------

    [Fact]
    public async Task Aggregator_preserves_director_supplied_identity_fields_and_does_not_overwrite_them()
    {
        // A new-version Director (issue #335+) that populates the four identity fields itself.
        // The Gateway aggregation must NOT overwrite them with its own derived values.
        const string directorMachine = "REAL_DIRECTOR_MACHINE";
        const string directorUser = "real_user";
        const string directorEndpoint = "https://real-machine.tailnet.ts.net:7879";
        const string directorViewUrl = "https://real-machine.tailnet.ts.net:7879/sessions/s1/view";

        var fake = await StartFakeWithPrePopulatedIdentity(
            directorMachine, directorUser, directorEndpoint, directorViewUrl,
            new[] { Sample("s1", "ClaudeCode", "repo-a", "Idle", "green") });
        await Register(fake);

        var sessions = await GetSessions();
        var s = Assert.Single(sessions);
        Assert.Equal("s1", s.SessionId);
        // Director-supplied identity survives the aggregation pass unchanged...
        Assert.Equal(directorMachine, s.MachineName);
        Assert.Equal(directorUser, s.User);
        Assert.Equal(directorEndpoint, s.TailnetEndpoint);

        // ...but the LINK does not, and that is deliberate. Where a session can be opened is a verdict,
        // and the standing law is that the Gateway owns every verdict. A Director old enough to supply
        // one supplies a link to its own port, which is a dead door on a current fleet - so preferring
        // it "when present" would keep exactly the case that breaks.
        Assert.Equal($"{GatewayOrigin()}/sessions/s1", s.ViewUrl);
        Assert.NotEqual(directorViewUrl, s.ViewUrl);
    }

    [Fact]
    public async Task Aggregator_back_compat_enriches_old_director_empty_identity_fields()
    {
        // An OLD Director (pre-issue #335) that returns empty identity fields must still
        // have them enriched by the Gateway aggregation pass (back-compat for mixed fleets).
        var fake = await StartFake("OLD_MACHINE", "old_user", new[]
        {
            Sample("s2", "ClaudeCode", "repo-b", "Idle", "green"),
        });
        await Register(fake);

        var sessions = await GetSessions();
        var s = Assert.Single(sessions);
        Assert.Equal("s2", s.SessionId);
        // Fields were empty from the fake Director; the Gateway must have enriched them.
        Assert.Equal("OLD_MACHINE", s.MachineName);
        Assert.Equal("old_user", s.User);
        Assert.False(string.IsNullOrEmpty(s.TailnetEndpoint), "Gateway must set TailnetEndpoint for old Directors");
        Assert.False(string.IsNullOrEmpty(s.ViewUrl), "Gateway must set ViewUrl for old Directors");
    }

    // ---------- view-url shape ----------

    [Fact]
    public async Task ViewUrl_has_no_double_slashes_when_endpoint_has_trailing_slash()
    {
        var fake = await StartFake("M", "u", new[] { Sample("only", "ClaudeCode", "r", "Idle", "green") });
        // Register with a trailing slash on the tailnet endpoint to exercise the TrimEnd path.
        await Register(fake, tailnetOverride: fake.BaseUrl + "/");

        var sessions = await GetSessions();
        var s = Assert.Single(sessions);
        Assert.DoesNotContain("//sessions", s.ViewUrl);
        Assert.Equal($"{GatewayOrigin()}/sessions/only", s.ViewUrl);
    }

    // ---------- single-session lookup ----------

    [Fact]
    public async Task Single_session_lookup_stamps_fleet_fields()
    {
        var fake = await StartFake("MACHINE_X", "carol", new[] { Sample("only", "Gemini", "r", "Idle", "green") });
        await Register(fake);

        var resp = await _http.GetAsync("/sessions/only");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var s = await resp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.NotNull(s);
        Assert.Equal("MACHINE_X", s!.MachineName);
        Assert.Equal("carol", s.User);
        Assert.Equal(fake.BaseUrl, s.TailnetEndpoint);
        // The single-session read mints the same Gateway-rooted link as the roster does. Both paths are
        // asserted because they build it in different places, and only one of them was fixed the first
        // time this kind of change was made.
        Assert.Equal($"{GatewayOrigin()}/sessions/only", s.ViewUrl);
    }

    [Fact]
    public async Task Single_session_lookup_returns_404_when_not_found()
    {
        var fake = await StartFake("M", "u", new[] { Sample("a", "ClaudeCode", "r", "Idle", "green") });
        await Register(fake);

        var resp = await _http.GetAsync("/sessions/nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // The /api reference page was removed with every other Gateway-served UI page
    // (docs/plans/one-url-cockpit.md): unmatched paths fall through the Cockpit proxy,
    // covered by GatewayDirectoryRegistrationTests.Root_falls_through_to_the_cockpit_proxy.

    // ---------- helpers ----------

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed record EnvelopeResponse(
        [property: JsonPropertyName("sessions")] List<SessionDto> Sessions,
        [property: JsonPropertyName("machineErrors")] List<MachineErrorDto> MachineErrors,
        [property: JsonPropertyName("directors")] List<DirectorReachabilityDto> Directors,
        [property: JsonPropertyName("unreachableBanner")] string? UnreachableBanner
    );

    /// <summary>
    /// Fetches /sessions and filters to ONLY the sessions belonging to fakes registered
    /// by this test class. The DirectorRegistry's filesystem watch path picks up real
    /// cc-director.exe instances running on the developer's machine; those entries also
    /// appear in the aggregator response and would otherwise pollute test assertions.
    /// </summary>
    private async Task<List<SessionDto>> GetSessions(string query = "")
    {
        var url = string.IsNullOrEmpty(query) ? "sessions" : $"sessions?{query}";
        var sessions = await _http.GetFromJsonAsync<List<SessionDto>>(url, JsonOpts);
        return FilterToFakes(sessions ?? new());
    }

    private async Task<EnvelopeResponse> GetEnvelope(string query = "")
    {
        var url = string.IsNullOrEmpty(query) ? "sessions?envelope=true" : $"sessions?envelope=true&{query}";
        var body = await _http.GetFromJsonAsync<EnvelopeResponse>(url, JsonOpts);
        Assert.NotNull(body);
        var fakeIds = _fakes.Select(f => f.DirectorId).ToHashSet();
        return new EnvelopeResponse(
            body!.Sessions.Where(s => fakeIds.Contains(s.DirectorId)).ToList(),
            body.MachineErrors.Where(m => fakeIds.Contains(m.DirectorId)).ToList(),
            (body.Directors ?? new()).Where(d => fakeIds.Contains(d.DirectorId)).ToList(),
            // The banner is one fleet-wide sentence and cannot be filtered to this class's fakes. It is safe
            // to assert on because the discovery directory is isolated per test class, so nothing but these
            // fakes is ever in the registry.
            body.UnreachableBanner
        );
    }

    private List<SessionDto> FilterToFakes(List<SessionDto> sessions)
    {
        var fakeIds = _fakes.Select(f => f.DirectorId).ToHashSet();
        return sessions.Where(s => fakeIds.Contains(s.DirectorId)).ToList();
    }

    /// <summary>
    /// Register the fake's identity in the Gateway registry - the fields the aggregation enriches from
    /// (machine / user / tailnet endpoint) - then deliver its sessions over the tunnel (the ONLY roster source
    /// post-cut). The endpoint is never dialed, so it only has to be a well-formed URL for the
    /// TailnetEndpoint / ViewUrl derivation; <paramref name="tailnetOverride"/> exercises the trailing-slash path.
    /// A not-connected fake pushes nothing, so it surfaces as a machineError.
    /// </summary>
    private async Task Register(Fake fake, string? tailnetOverride = null)
    {
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = fake.DirectorId,
            TailnetEndpoint = tailnetOverride ?? fake.BaseUrl,
            Pid = 1234,
            MachineName = fake.MachineName,
            User = fake.User,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        if (fake.Connected && fake.Tunnel is not null && fake.Sessions is not null)
            await fake.Tunnel.PushSnapshotAsync(fake.Sessions);
    }

    /// <summary>
    /// The origin these tests reach the Gateway on - which is exactly what the Gateway mints session
    /// links from, because it roots them on the address the CALLER used. Derived from the live client
    /// rather than written out, so a link asserted here is one a caller could actually follow.
    /// </summary>
    private string GatewayOrigin() => $"http://127.0.0.1:{_gateway.Port}";

    private async Task<Fake> StartFake(string machine, string user, SessionDto[]? sessions, bool connected = true)
    {
        var fake = new Fake
        {
            DirectorId = Guid.NewGuid().ToString(),
            MachineName = machine,
            User = user,
            // A plausible advertised endpoint that is NEVER dialed - the roster comes from the push store, so a
            // working result proves the tunnel (tunnel-by-construction).
            BaseUrl = $"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}",
            Sessions = sessions,
            Connected = connected,
        };
        if (connected)
            fake.Tunnel = await FakeTunnelDirector.StartAsync(_gateway, Token, fake.DirectorId, machine);
        _fakes.Add(fake);
        return fake;
    }

    /// <summary>
    /// Issue #335: start a tunnel-connected fake whose sessions already carry the four identity fields
    /// (machineName, user, tailnetEndpoint, viewUrl) pre-populated - simulating a new-version Director that
    /// populated them itself. The Gateway aggregation pass must NOT overwrite these Director-supplied values.
    /// </summary>
    private async Task<Fake> StartFakeWithPrePopulatedIdentity(
        string machine, string user, string tailnetEndpoint, string viewUrl, SessionDto[]? sessions)
    {
        // Stamp the identity fields onto every session before the fake pushes them.
        if (sessions is not null)
        {
            foreach (var s in sessions)
            {
                s.MachineName = machine;
                s.User = user;
                s.TailnetEndpoint = tailnetEndpoint;
                s.ViewUrl = viewUrl;
            }
        }
        return await StartFake(machine, user, sessions);
    }

    private static SessionDto Sample(string sid, string agent, string repo, string state, string color) => new()
    {
        SessionId = sid,
        Agent = agent,
        RepoPath = repo,
        ActivityState = state,
        Status = state == "Exited" ? "Exited" : "Running",
        StatusColor = color,
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

    /// <summary>
    /// A registered Director stand-in for the aggregation. Post-cut it delivers its roster ONLY over the tunnel
    /// (via <see cref="FakeTunnelDirector"/>); its advertised endpoint (<see cref="BaseUrl"/>) is never dialed.
    /// A not-<see cref="Connected"/> fake never opens the tunnel and never pushes, so the Gateway surfaces it as
    /// a machineError (not tunnel-connected).
    /// </summary>
    private sealed class Fake
    {
        public string DirectorId { get; init; } = "";
        public string MachineName { get; init; } = "";
        public string User { get; init; } = "";
        public string BaseUrl { get; init; } = "";
        public SessionDto[]? Sessions { get; init; }
        public bool Connected { get; init; }
        public FakeTunnelDirector? Tunnel { get; set; }
    }
}
