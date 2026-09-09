using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The stop route, driven end to end through the production <see cref="GatewayEndpoints.Map"/> (mission
/// "Stop a session", Seat 2). The fold's sentences are proved without a server in
/// <c>SessionStopFoldTests</c>; what these prove is the part only a real route can: which status code each
/// case answers, that BOTH DOORS reach the same handler, and that the audit row is actually written.
///
/// THE STATUS CODES ARE THE POINT. Ruling 3 says a stop never fails because there is nothing left to stop,
/// so "no session in the account carries that identifier" answers 200 with a verdict - not the 404 the
/// session-unavailable helper would have produced, which would make the SECOND stop an error and teach an
/// operator that a stopped session is still alive. That single decision is what
/// <see cref="A_session_that_is_not_on_this_fleet_is_a_success_not_a_404"/> holds down.
///
/// The Director is a stub here. That is deliberate and it is stated rather than left to be discovered:
/// these tests prove what the GATEWAY does with an answer, and prove nothing whatever about whether the
/// Director's answer is true. Seat 1's own tests carry that half.
/// </summary>
public sealed class SessionStopEndpointTests : IDisposable
{
    private const string DirectorId = "dir-north";
    private const string Machine = "SOREN_NORTH";
    private const string Sid = "9c41e7a2-1111-2222-3333-444455556666";

    private readonly GatewayDbTestHarness _db = new();

    public void Dispose() => _db.Dispose();

    private static SessionDto Row(string id = Sid) => new()
    {
        SessionId = id,
        Name = "throwaway",
        ActivityState = "Waiting",
        StatusColor = "green",
        LastActivityAt = DateTime.UtcNow,
    };

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static DirectorCommandResult DirectorAnswers(
        string verdict, int? processId, bool processEnded, bool rowRemoved,
        string? worktree = null, bool? dirty = null)
        => DirectorCommandResult.Success(Json(new DirectorStopResult
        {
            Killed = true,
            Removed = rowRemoved,
            ProcessId = processId,
            ProcessEnded = processEnded,
            RowRemoved = rowRemoved,
            WorktreePath = worktree,
            WorktreeHadUncommittedChanges = dirty,
            Verdict = verdict,
        }));

    private static DirectorCommandResult Stopped(string? worktree = null, bool? dirty = null)
        => DirectorAnswers(SessionStopVerdict.Stopped, 51884, processEnded: true, rowRemoved: true,
            worktree: worktree, dirty: dirty);

    private static DirectorCommandResult AlreadyStoppedRowCleared()
        => DirectorAnswers(SessionStopVerdict.AlreadyStopped, null, processEnded: false, rowRemoved: true);

    // ---------------------------------------------------------------------------------------------------
    // The three verdicts.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_live_session_is_stopped_and_the_answer_says_what_happened_to_it()
    {
        await WithGateway(Row(), Stopped(), async (http, sent, _) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                new SessionStopRequest { Reason = "spawned into the wrong mode" });

            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            var body = await reply.Content.ReadFromJsonAsync<SessionStopResponse>();
            Assert.NotNull(body);
            Assert.Equal(SessionStopVerdict.Stopped, body!.Verdict);
            Assert.Equal("stopped 9c41e7a2 - process 51884 ended, row removed", body.Headline);
            Assert.Equal(new[] { "reason: spawned into the wrong mode" }, body.Details.ToArray());
            Assert.Equal(51884, body.ProcessId);

            // It reached the Director, as the kill verb, for THIS session.
            var command = Assert.Single(sent);
            Assert.Equal("kill", command.Verb);
            Assert.Equal(Sid, command.SessionId);
        });
    }

    [Fact]
    public async Task A_row_with_no_process_is_already_stopped_and_the_row_is_still_cleared()
    {
        await WithGateway(Row(), AlreadyStoppedRowCleared(), async (http, _, _) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                new SessionStopRequest { Reason = "tidying up" });

            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            var body = await reply.Content.ReadFromJsonAsync<SessionStopResponse>();
            Assert.Equal(SessionStopVerdict.AlreadyStopped, body!.Verdict);
            Assert.Equal(
                "already stopped 9c41e7a2 - no process was running; the row it left behind has been cleared",
                body.Headline);
        });
    }

    /// <summary>
    /// Ruling 3, and the reason this route does not use the session-unavailable helper every other session
    /// route uses: that helper answers 404, and a 404 here would make the SECOND stop an error - which an
    /// operator reads as "it is still alive". The mission exists to remove exactly that.
    /// </summary>
    [Fact]
    public async Task A_session_that_is_not_on_this_fleet_is_a_success_not_a_404()
    {
        await WithGateway(session: null, killAnswer: null, async (http, sent, _) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                new SessionStopRequest { Reason = "already gone, I think" });

            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            var body = await reply.Content.ReadFromJsonAsync<SessionStopResponse>();
            Assert.Equal(SessionStopVerdict.NotOnFleet, body!.Verdict);
            Assert.Equal(
                "not on this fleet - nothing in this account carries the id 9c41e7a2, so no machine was "
                + "asked and no machine's processes were searched",
                body.Headline);

            // AND NO MACHINE WAS ASKED - the headline's claim, proved rather than asserted.
            Assert.Empty(sent);
        });
    }

    /// <summary>
    /// The second stop, run straight after the first, exactly as an operator would. It succeeds quietly.
    /// This is the case Ruling 3 was written for, and the one a 404 would have broken.
    /// </summary>
    [Fact]
    public async Task A_second_stop_straight_after_the_first_succeeds_quietly()
    {
        var store = StoreWith(Row());
        await WithGateway(store, Stopped(), async (http, _, _) =>
        {
            var first = await http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                new SessionStopRequest { Reason = "wrong mode" });
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(SessionStopVerdict.Stopped,
                (await first.Content.ReadFromJsonAsync<SessionStopResponse>())!.Verdict);

            // The Director has since pushed a roster without that session - the session is gone.
            Assert.True(store.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 2, Array.Empty<SessionDto>()));

            var second = await http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                new SessionStopRequest { Reason = "wrong mode" });
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(SessionStopVerdict.NotOnFleet,
                (await second.Content.ReadFromJsonAsync<SessionStopResponse>())!.Verdict);
        });
    }

    // ---------------------------------------------------------------------------------------------------
    // The refusal - Ruling 4.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_stop_with_no_reason_is_refused_and_the_refusal_names_the_reason(string? reason)
    {
        await WithGateway(Row(), Stopped(), async (http, sent, audit) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                new SessionStopRequest { Reason = reason });

            Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
            var text = await reply.Content.ReadAsStringAsync();
            Assert.Contains("reason", text, StringComparison.OrdinalIgnoreCase);
            // The Gateway does not know what a client's options are called, so it never names one.
            Assert.DoesNotContain("--", text);

            // And nothing happened: no machine was asked and nothing was recorded.
            Assert.Empty(sent);
            Assert.Empty(audit!.List(sessionId: Sid));
        });
    }

    // ---------------------------------------------------------------------------------------------------
    // The worktree line - Ruling 2.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_session_holding_a_dirty_worktree_is_stopped_and_the_worktree_is_named()
    {
        await WithGateway(Row(), Stopped(worktree: @"C:\Repos\thing", dirty: true), async (http, _, _) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                new SessionStopRequest { Reason = "doing the wrong work" });

            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            var body = await reply.Content.ReadFromJsonAsync<SessionStopResponse>();
            Assert.Equal(
                new[]
                {
                    @"the worktree C:\Repos\thing was left untouched - it has uncommitted changes in it",
                    "reason: doing the wrong work",
                },
                body!.Details.ToArray());
            Assert.True(body.WorktreeHadUncommittedChanges);
        });
    }

    [Fact]
    public async Task A_worktree_whose_state_could_not_be_determined_never_reads_as_clean()
    {
        await WithGateway(Row(), Stopped(worktree: @"C:\Repos\thing", dirty: null), async (http, _, _) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                new SessionStopRequest { Reason = "why" });

            var body = await reply.Content.ReadFromJsonAsync<SessionStopResponse>();
            Assert.Contains(body!.Details,
                d => d.Contains("whether it has uncommitted changes could not be determined"));
            Assert.DoesNotContain(body.Details, d => d.Contains("had no uncommitted changes"));
            Assert.Null(body.WorktreeHadUncommittedChanges);
        });
    }

    // ---------------------------------------------------------------------------------------------------
    // The audit trail - the ground the owner allowed this on.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stop_appends_one_intervention_row_carrying_who_asked_and_why()
    {
        await WithGateway(Row(), Stopped(), async (http, _, audit) =>
        {
            await http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                new SessionStopRequest { Reason = "spawned into the wrong mode" });

            var row = Assert.Single(audit!.List(sessionId: Sid));
            Assert.Equal(GovernanceAuditCategory.Intervention, row.Category);
            Assert.Equal(GovernanceAuditEventType.Stopped, row.EventType);
            Assert.Equal("spawned into the wrong mode", row.Detail);
            // The event type REQUIRES an actor, so a row existing at all proves one was supplied. This
            // harness authenticates nothing, so the honest answer is "unknown" - never a guess.
            Assert.Equal("unknown", row.Actor);
        });
    }

    [Fact]
    public async Task An_already_stopped_session_is_recorded_too()
    {
        await WithGateway(Row(), AlreadyStoppedRowCleared(), async (http, _, audit) =>
        {
            await http.PostAsJsonAsync($"/sessions/{Sid}/stop", new SessionStopRequest { Reason = "tidying" });

            var row = Assert.Single(audit!.List(sessionId: Sid));
            Assert.Equal(GovernanceAuditEventType.Stopped, row.EventType);
        });
    }

    /// <summary>
    /// Nothing was stopped and there is no session in this account for a row to be about, so none is
    /// written. A DELIBERATE GAP, pinned so that a later reader can see it was decided rather than missed.
    /// </summary>
    [Fact]
    public async Task Not_on_this_fleet_writes_no_audit_row()
    {
        await WithGateway(session: null, killAnswer: null, async (http, _, audit) =>
        {
            await http.PostAsJsonAsync($"/sessions/{Sid}/stop", new SessionStopRequest { Reason = "why" });

            Assert.Empty(audit!.List(sessionId: Sid));
        });
    }

    /// <summary>
    /// A stop still serves when no audit log is wired - a test host that has no database must not 500 - but
    /// it is not silent about it. The log line is the only thing standing between "unaudited" and
    /// "unnoticed"; this pins that the route survives the case rather than that it is acceptable.
    /// </summary>
    [Fact]
    public async Task A_stop_with_no_audit_log_wired_still_serves()
    {
        await WithGateway(StoreWith(Row()), Stopped(), async (http, _, _) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop", new SessionStopRequest { Reason = "why" });
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        }, audit: null, auditSupplied: false);
    }

    // ---------------------------------------------------------------------------------------------------
    // The second door.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// DELETE /sessions/{sid} is a THIN FORWARD into the same handler: the same fold and the same audit
    /// trail. It carries no body, so the trail says in words that no reason was given - it never invents
    /// one. The original killed/removed pair is still on the answer for the callers that read only those.
    /// </summary>
    [Fact]
    public async Task The_delete_door_reaches_the_same_stop_and_records_that_no_reason_was_given()
    {
        await WithGateway(Row(), Stopped(), async (http, sent, audit) =>
        {
            var reply = await http.DeleteAsync($"/sessions/{Sid}");

            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            var body = await reply.Content.ReadFromJsonAsync<SessionStopResponse>();
            Assert.Equal(SessionStopVerdict.Stopped, body!.Verdict);
            Assert.Equal("stopped 9c41e7a2 - process 51884 ended, row removed", body.Headline);
            Assert.True(body.Killed);
            Assert.True(body.Removed);
            Assert.Null(body.Reason);
            Assert.DoesNotContain(body.Details, d => d.StartsWith("reason:", StringComparison.Ordinal));

            Assert.Equal("kill", Assert.Single(sent).Verb);

            var row = Assert.Single(audit!.List(sessionId: Sid));
            Assert.Equal(GovernanceAuditEventType.Stopped, row.EventType);
            Assert.Contains("no reason was given", row.Detail);
        });
    }

    /// <summary>
    /// THE ONE PLACE THE TWO DOORS DIFFER, AND IT IS A REGRESSION TEST, NOT A DESIGN PREFERENCE.
    ///
    /// Making DELETE a thin forward silently changed its answer for an unknown session from the locator's
    /// 404 to the stop's 200 notOnFleet. That broke two EXISTING tests, and only the PARKED suite runs
    /// either of them: StreamCommandTests.StreamModeOff_KillEndpoint_StaysOnHttp, and - the one that
    /// matters - HostedSessionCommandRouteTenancyTests.Another_tenant_cannot_reach_it(DELETE), which pins
    /// that one account naming ANOTHER account's session id gets exactly the locator's not-found answer.
    ///
    /// Ruling 3's "notOnFleet is a success" is about THE STOP - the verb this mission adds. DELETE is a
    /// legacy door kept for exactly one reason, a shipped native phone client that does not deploy with the
    /// Gateway, and keeping a door for compatibility means keeping what it answers. The available
    /// alternative was to edit a cross-tenant isolation test until it agreed, which is the move to distrust.
    /// </summary>
    [Fact]
    public async Task An_unknown_session_is_notOnFleet_through_the_stop_but_still_not_found_through_the_old_door()
    {
        await WithGateway(session: null, killAnswer: null, async (http, sent, audit) =>
        {
            // The stop - the mission's verb. A success, so a second stop is never an error.
            var stop = await http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                new SessionStopRequest { Reason = "already gone, I think" });
            Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
            Assert.Equal(SessionStopVerdict.NotOnFleet,
                (await stop.Content.ReadFromJsonAsync<SessionStopResponse>())!.Verdict);

            // The legacy door - unchanged, because its callers and its isolation contract are unchanged.
            var legacy = await http.DeleteAsync($"/sessions/{Sid}");
            Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);
            // The machine-readable code the tenancy test anchors on, not the prose beside it.
            Assert.Contains("session_not_found", await legacy.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // Neither door asked a machine, and neither wrote a row: nothing was stopped on either path.
            Assert.Empty(sent);
            Assert.Empty(audit!.List(sessionId: Sid));
        });
    }

    // ---------------------------------------------------------------------------------------------------
    // The failures Ruling 3 keeps.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A Director that is LOCATED but not reachable is a failure and stays one - it is not folded into
    /// notOnFleet, because a machine that cannot be asked is not the same as a session that does not exist.
    /// </summary>
    [Fact]
    public async Task A_director_that_cannot_be_reached_is_a_failure()
    {
        await WithGateway(Row(), killAnswer: null, async (http, _, audit) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop", new SessionStopRequest { Reason = "why" });

            Assert.Equal(HttpStatusCode.BadGateway, reply.StatusCode);
            Assert.Contains("not connected", await reply.Content.ReadAsStringAsync());
            Assert.Empty(audit!.List(sessionId: Sid));
        });
    }

    /// <summary>
    /// A Director older than this Gateway answers only the original killed/removed pair. THE SESSION WAS
    /// STILL STOPPED, so this is not a failure: an earlier version of this route answered 502, and
    /// answering a failure for an operation that succeeded is the same false report this mission exists to
    /// remove. Nor is it "stopped" - an old Director's killed:true says the verb RAN, never that a process
    /// was found. It is the fourth verdict, and the row IS written, because a stop happened.
    /// </summary>
    [Fact]
    public async Task An_answer_this_gateway_cannot_describe_is_still_a_stop_and_never_a_failure()
    {
        var legacy = DirectorCommandResult.Success(Json(new { killed = true, removed = true }));
        await WithGateway(Row(), legacy, async (http, _, audit) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop", new SessionStopRequest { Reason = "why" });

            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            var body = await reply.Content.ReadFromJsonAsync<SessionStopResponse>();
            Assert.Equal(SessionStopVerdict.StoppedNotDescribed, body!.Verdict);
            Assert.StartsWith("stopped ", body.Headline);
            Assert.Contains("older version", body.Headline);
            // Nothing is invented: no process named, no worktree sentence.
            Assert.Null(body.ProcessId);
            Assert.DoesNotContain(body.Details, d => d.Contains("worktree", StringComparison.Ordinal));

            // A stop happened, so the trail holds it - this is the half the 502 was silently losing.
            var row = Assert.Single(audit!.List(sessionId: Sid));
            Assert.Equal(GovernanceAuditEventType.Stopped, row.EventType);
        });
    }

    /// <summary>
    /// And it reaches the LEGACY door too. DELETE must not start failing against an older Director - it
    /// succeeds there today, and a shipped phone client calls it.
    /// </summary>
    [Fact]
    public async Task The_old_door_also_gets_the_stop_it_could_not_describe()
    {
        var legacy = DirectorCommandResult.Success(Json(new { killed = true, removed = true }));
        await WithGateway(Row(), legacy, async (http, _, _) =>
        {
            var reply = await http.DeleteAsync($"/sessions/{Sid}");

            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            Assert.Equal(SessionStopVerdict.StoppedNotDescribed,
                (await reply.Content.ReadFromJsonAsync<SessionStopResponse>())!.Verdict);
        });
    }

    // ---------------------------------------------------------------------------------------------------
    // Harness.
    // ---------------------------------------------------------------------------------------------------

    private static PushedSessionStore StoreWith(params SessionDto[] sessions)
    {
        var store = new PushedSessionStore(() => DateTime.UtcNow);
        store.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(store.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, sessions));
        return store;
    }

    private Task WithGateway(
        SessionDto? session,
        DirectorCommandResult? killAnswer,
        Func<HttpClient, List<DirectorCommand>, GovernanceAuditLog?, Task> assertion)
        => WithGateway(
            session is null ? StoreWith() : StoreWith(session),
            killAnswer,
            assertion,
            audit: new GovernanceAuditLog(_db.Open()));

    private async Task WithGateway(
        PushedSessionStore store,
        DirectorCommandResult? killAnswer,
        Func<HttpClient, List<DirectorCommand>, GovernanceAuditLog?, Task> assertion,
        GovernanceAuditLog? audit = null,
        bool auditSupplied = true)
    {
        audit ??= auditSupplied ? new GovernanceAuditLog(_db.Open()) : null;

        var instancesDirectory = Path.Combine(Path.GetTempPath(), "cc-session-stop-" + Guid.NewGuid().ToString("N"));
        var sent = new List<DirectorCommand>();
        WebApplication? app = null;
        DirectorRegistry? registry = null;
        var started = false;
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}");
            app = builder.Build();
            registry = new DirectorRegistry(instancesDirectory);
            registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", 4242, DateTime.UtcNow, TenantId.Local);

            GatewayEndpoints.Map(
                app,
                registry,
                version: "test",
                token: "test-token",
                tenantBoundary: new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                    new SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry()),
                pushedSessions: store,
                streamStaleAfter: TimeSpan.FromSeconds(20),
                sendCommand: (_, command, _) =>
                {
                    sent.Add(command);
                    return Task.FromResult(killAnswer);
                },
                governanceAudit: audit);

            await app.StartAsync();
            started = true;
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{BoundPort.Of(app)}/") };
            await assertion(http, sent, audit);
        }
        finally
        {
            if (app is not null)
            {
                if (started) await app.StopAsync();
                await app.DisposeAsync();
            }
            registry?.Dispose();
            try { if (Directory.Exists(instancesDirectory)) Directory.Delete(instancesDirectory, true); }
            catch { /* best effort */ }
        }
    }
}
