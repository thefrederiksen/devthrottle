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
    // A STOP THAT WAS NOT RECORDED SAYS SO IN ITS OWN ANSWER - second inspection, finding I1 NARROWED.
    //
    // The first inspection's I1 was about the row never being ATTEMPTED on roads the reply did not reach.
    // That was fixed. The second inspection found what was left: when the append is attempted and FAILS,
    // the handler logged it and returned an ordinary success, so the operator was told the session had
    // stopped and told nothing at all about the trail being empty. Ruling 4 - any session may stop any
    // other - was granted by the owner ON THE CONDITION THAT STOPS ARE AUDITED, so the one case where that
    // condition silently fails is exactly the case he would want to hear about.
    //
    // The fix does NOT fail the response, and these tests pin that too: the session really did stop, and
    // answering an error would be a lie about the one fact this verb exists to report. What happened to the
    // SESSION and what happened to the RECORD of it are two different facts, and the answer carries both.
    // It arrives as one more line of Details, which every surface already renders verbatim, so no client
    // learns a new rule and the verdict word does not move.

    /// <summary>
    /// The trail is wired and demonstrably working, then its write fails. The stop still succeeds, the
    /// verdict is unchanged - and the answer says the stop is not in the trail.
    /// </summary>
    [Fact]
    public async Task A_stop_whose_audit_write_fails_says_so_in_its_own_answer()
    {
        var db = _db.Open();
        var audit = new GovernanceAuditLog(db);

        // Establish that this trail CAN write before breaking it. Without this the test would pass just as
        // well against a rig whose audit never worked at all, which would make it a check that cannot fail
        // for the reason it claims to.
        audit.Append(new AppendGovernanceAuditEventRequest
        {
            SessionId = "sentinel",
            Category = GovernanceAuditCategory.Intervention,
            EventType = GovernanceAuditEventType.Stopped,
            Actor = "test",
            Detail = "the trail accepts writes",
        });
        Assert.Single(audit.List(sessionId: "sentinel"));

        // Now every append throws, the way a real database outage would.
        db.Dispose();

        await WithGateway(StoreWith(Row()), Stopped(), async (http, _, _) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop", new SessionStopRequest { Reason = "why" });

            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            var body = await reply.Content.ReadFromJsonAsync<SessionStopResponse>();

            // The stop is reported exactly as it happened. The failure is about the record, not the session.
            Assert.Equal(SessionStopVerdict.Stopped, body!.Verdict);

            Assert.Contains(
                body.Details,
                line => line.Contains("NOT recorded in the governance trail", StringComparison.Ordinal));
        }, audit: audit);
    }

    /// <summary>
    /// The same sentence when there is no trail at all to write to. A Gateway with no governance store must
    /// not pretend a stop was audited just because nothing threw.
    /// </summary>
    [Fact]
    public async Task A_stop_with_no_audit_log_wired_says_so_in_its_own_answer()
    {
        await WithGateway(StoreWith(Row()), Stopped(), async (http, _, _) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop", new SessionStopRequest { Reason = "why" });

            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            var body = await reply.Content.ReadFromJsonAsync<SessionStopResponse>();

            Assert.Equal(SessionStopVerdict.Stopped, body!.Verdict);
            Assert.Contains(
                body.Details,
                line => line.Contains("NOT recorded in the governance trail", StringComparison.Ordinal));
        }, audit: null, auditSupplied: false);
    }

    /// <summary>
    /// The control, and the reason the two tests above are not vacuous: an ordinary stop against a working
    /// trail carries NO such line. A test that only ever asserts a line's presence cannot tell you the line
    /// is conditional.
    /// </summary>
    [Fact]
    public async Task An_ordinary_stop_does_not_claim_anything_about_the_trail()
    {
        await WithGateway(StoreWith(Row()), Stopped(), async (http, _, audit) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop", new SessionStopRequest { Reason = "why" });

            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            var body = await reply.Content.ReadFromJsonAsync<SessionStopResponse>();

            Assert.Single(audit!.List(sessionId: Sid));
            Assert.DoesNotContain(
                body!.Details,
                line => line.Contains("governance trail", StringComparison.Ordinal));
        });
    }

    // ---------------------------------------------------------------------------------------------------
    // THE ROW BELONGS TO THE DISPATCH, NOT TO THE REPLY - inspection 1, finding I1.
    //
    // The handler used to hand the request's cancellation token to the tunnel and then append the row only
    // after a successful reply. A Director that carried the stop out and answered a moment after the caller
    // gave up therefore left ZERO rows behind, and a tunnel timeout lost the row the same way. No database
    // fault was needed for either, and both are reachable from the shipped desktop dialog: closing it
    // cancels its request, and its client gives up after ten seconds.
    //
    // Ruling 4 - any session may stop any other - was accepted by the owner ON THE EXPLICIT GROUND THAT
    // STOPS ARE AUDITED. An audit that only lands while the caller is still listening is not an audit.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The Director in both halves of this pair: it carries the stop out, says so, and only then tries to
    /// answer. <paramref name="theAnswerIsReleased"/> is what holds the answer back until the test decides.
    /// </summary>
    private static DirectorCommandRouter.SendDirectorCommandAsync ADirectorThatStopsThenAnswersLate(
        TaskCompletionSource theStopHasHappened, Task theAnswerIsReleased, DirectorCommandResult answer)
        => async (_, _, token) =>
        {
            // Past this point the session is GONE on the Director. Everything after it is only the answer
            // trying to get home, and whether it gets there changes nothing about what was destroyed.
            theStopHasHappened.SetResult();
            await theAnswerIsReleased.WaitAsync(token);
            return answer;
        };

    /// <summary>
    /// The defect itself. The caller hangs up after the Director has carried the stop out and before the
    /// answer is released - which is a person closing the stop dialog, not an exotic fault. The session is
    /// destroyed either way, so the trail must hold a row for it.
    ///
    /// Read this against <see cref="A_caller_that_waits_for_the_same_stop_gets_the_same_row_and_an_answer"/>
    /// below: the two set up an identical Director and differ in ONE line, whether the caller cancels.
    /// </summary>
    [Fact]
    public async Task A_stop_the_caller_stopped_waiting_for_is_still_recorded_with_its_reason_and_actor()
    {
        var theStopHasHappened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Never completed: the only way out of the wait is the caller's cancellation.
        var theAnswerIsReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await WithGateway(
            StoreWith(Row()),
            killAnswer: null,
            sendOverride: ADirectorThatStopsThenAnswersLate(theStopHasHappened, theAnswerIsReleased.Task, Stopped()),
            assertion: async (http, sent, audit) =>
            {
                using var theCaller = new CancellationTokenSource();
                var request = http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                    new SessionStopRequest { Reason = "it was rewriting the wrong branch" }, theCaller.Token);

                await theStopHasHappened.Task.WaitAsync(TimeSpan.FromSeconds(30));
                theCaller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

                var row = await WaitForTheStopToBeRecorded(audit!);
                Assert.Equal(GovernanceAuditCategory.Intervention, row.Category);
                Assert.Equal(GovernanceAuditEventType.Stopped, row.EventType);
                // The caller's own words survive their leaving.
                Assert.Contains("it was rewriting the wrong branch", row.Detail);
                // And the row does not pretend to know what it does not know.
                Assert.Contains("what came of it is not known", row.Detail);
                Assert.Contains("the caller went away", row.Detail);
                // The trail refuses a stop with no actor, so a row existing proves one was supplied. This
                // harness authenticates nothing, so the honest answer is "unknown" - never a guess.
                Assert.Equal("unknown", row.Actor);

                Assert.Equal("kill", Assert.Single(sent).Verb);
            });
    }

    /// <summary>
    /// The control. Same Director, same held-back answer, same reason - the caller simply waits. It differs
    /// from the test above in exactly one thing: nobody cancels. So the row it produces is the ORDINARY
    /// one, with the reason alone and no talk of an unknown outcome, and the caller is answered.
    /// </summary>
    [Fact]
    public async Task A_caller_that_waits_for_the_same_stop_gets_the_same_row_and_an_answer()
    {
        var theStopHasHappened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var theAnswerIsReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await WithGateway(
            StoreWith(Row()),
            killAnswer: null,
            sendOverride: ADirectorThatStopsThenAnswersLate(theStopHasHappened, theAnswerIsReleased.Task, Stopped()),
            assertion: async (http, sent, audit) =>
            {
                using var theCaller = new CancellationTokenSource();
                var request = http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                    new SessionStopRequest { Reason = "it was rewriting the wrong branch" }, theCaller.Token);

                await theStopHasHappened.Task.WaitAsync(TimeSpan.FromSeconds(30));
                theAnswerIsReleased.SetResult();
                var reply = await request;

                Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
                Assert.Equal(SessionStopVerdict.Stopped,
                    (await reply.Content.ReadFromJsonAsync<SessionStopResponse>())!.Verdict);

                var row = Assert.Single(audit!.List(sessionId: Sid));
                Assert.Equal(GovernanceAuditEventType.Stopped, row.EventType);
                Assert.Equal("it was rewriting the wrong branch", row.Detail);
                Assert.Equal("unknown", row.Actor);

                Assert.Equal("kill", Assert.Single(sent).Verb);
            });
    }

    /// <summary>
    /// The other road to the same place. The Director never answers, the Gateway gives up waiting, and the
    /// stop may perfectly well have been carried out - the timeout sentence the caller gets says exactly
    /// that. The row records the stop AND records that its outcome is not known.
    ///
    /// HONEST SCOPE: the timeout here is the router's own synthesized result handed to the handler, not a
    /// thirty-second wait slept through. What the router does with a real expiring deadline is proved in
    /// <c>DirectorCommandRouterTimeoutTests</c>; what this proves is what the STOP HANDLER does when it
    /// receives that result, which is where the row was being lost.
    /// </summary>
    [Fact]
    public async Task A_stop_the_director_never_answered_is_recorded_as_a_stop_whose_outcome_is_unknown()
    {
        var timedOut = DirectorCommandResult.Fail(
            DirectorCommandStatus.Timeout, DirectorCommandRouter.DescribeTimeout(Machine, TimeSpan.FromSeconds(30)));

        await WithGateway(Row(), timedOut, async (http, sent, audit) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                new SessionStopRequest { Reason = "the run had already finished" });

            // The caller is told the truth: it is not known whether the command was carried out.
            Assert.Equal(HttpStatusCode.GatewayTimeout, reply.StatusCode);
            Assert.Contains("not known whether the command was carried out", await reply.Content.ReadAsStringAsync());

            var row = Assert.Single(audit!.List(sessionId: Sid));
            Assert.Equal(GovernanceAuditEventType.Stopped, row.EventType);
            Assert.Contains("the run had already finished", row.Detail);
            Assert.Contains("what came of it is not known", row.Detail);
            Assert.Contains("the Director did not answer in time", row.Detail);
            Assert.Equal("unknown", row.Actor);

            Assert.Equal("kill", Assert.Single(sent).Verb);
        });
    }

    /// <summary>
    /// And the third: the tunnel drops with the stop in flight. The Gateway cannot tell whether the command
    /// left before the connection died, and says so rather than choosing one. That is the whole ruling -
    /// an "attempted, outcome unknown" row is a fact, and silence is not.
    /// </summary>
    [Fact]
    public async Task A_stop_whose_tunnel_dropped_mid_flight_is_recorded_as_a_stop_whose_outcome_is_unknown()
    {
        var dropped = DirectorCommandResult.Fail(
            DirectorCommandStatus.TunnelDropped, "The tunnel to the Director dropped while the command was in flight.");

        await WithGateway(Row(), dropped, async (http, _, audit) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                new SessionStopRequest { Reason = "wrong repository" });

            Assert.Equal(HttpStatusCode.BadGateway, reply.StatusCode);

            var row = Assert.Single(audit!.List(sessionId: Sid));
            Assert.Contains("wrong repository", row.Detail);
            Assert.Contains("the tunnel dropped while the stop was in flight", row.Detail);
        });
    }

    /// <summary>
    /// THE OTHER HALF OF THE RULING, AND IT MATTERS AS MUCH: a row is written only where something was
    /// actually sent. A Director that was never connected had no command delivered to it, so writing a row
    /// would attach a stop to something that did not happen. (The reachability failure itself is pinned by
    /// <see cref="A_director_that_cannot_be_reached_is_a_failure"/>; this names the row as the point.)
    /// </summary>
    [Fact]
    public async Task A_director_that_was_never_connected_writes_no_row_because_nothing_was_sent()
    {
        await WithGateway(Row(), killAnswer: null, async (http, _, audit) =>
        {
            await http.PostAsJsonAsync($"/sessions/{Sid}/stop", new SessionStopRequest { Reason = "why" });

            Assert.Empty(audit!.List(sessionId: Sid));
        });
    }

    /// <summary>
    /// Nor where the DIRECTOR ITSELF answered a failure. "The process would not die" is the live example:
    /// the Director looked, the process is still running, and it deliberately leaves the row in place so the
    /// operator can see the session and try again. Recording a stop against that would be false, and the
    /// Director is the one party that actually knows - this is not an unknown outcome, it is a known one.
    /// </summary>
    [Fact]
    public async Task A_failure_the_director_itself_reported_writes_no_row_because_nothing_was_stopped()
    {
        var wouldNotDie = DirectorCommandResult.Fail(
            DirectorCommandStatus.Error, "the process would not die: process 51884 is still running after the stop");

        await WithGateway(Row(), wouldNotDie, async (http, sent, audit) =>
        {
            var reply = await http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                new SessionStopRequest { Reason = "stuck in a loop" });

            Assert.Equal(HttpStatusCode.BadGateway, reply.StatusCode);
            Assert.Contains("would not die", await reply.Content.ReadAsStringAsync());

            // A machine WAS asked - so this is not the notOnFleet case - and it told us what it found.
            Assert.Equal("kill", Assert.Single(sent).Verb);
            Assert.Empty(audit!.List(sessionId: Sid));
        });
    }

    /// <summary>
    /// The reason travels with the command instead of the null payload the tunnel used to carry, so the
    /// machine doing the destroying is given the caller's own words for it.
    ///
    /// STATED SO NOBODY READS MORE INTO THIS THAN IT SAYS: the Director IGNORES this payload today -
    /// <c>SessionCommandExecutor.KillAsync</c> never reads <c>PayloadJson</c> - and no consumer is claimed
    /// here. What is pinned is that the Gateway SENDS it, so that a Director half can read it without the
    /// Gateway needing to change again.
    /// </summary>
    [Fact]
    public async Task The_reason_travels_down_the_tunnel_with_the_stop()
    {
        await WithGateway(Row(), Stopped(), async (http, sent, _) =>
        {
            await http.PostAsJsonAsync($"/sessions/{Sid}/stop",
                new SessionStopRequest { Reason = "spawned into the wrong mode" });

            var command = Assert.Single(sent);
            Assert.Equal("kill", command.Verb);
            Assert.Contains("spawned into the wrong mode", command.PayloadJson);
        });
    }

    /// <summary>
    /// The door that carries no reason sends no payload either - it does not invent one, exactly as the
    /// audit row does not.
    /// </summary>
    [Fact]
    public async Task The_delete_door_sends_no_payload_because_it_has_no_reason_to_send()
    {
        await WithGateway(Row(), Stopped(), async (http, sent, _) =>
        {
            await http.DeleteAsync($"/sessions/{Sid}");

            Assert.Equal("", Assert.Single(sent).PayloadJson);
        });
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

    /// <summary>
    /// Wait for the audit trail to hold a row for this session, or fail saying it never did.
    ///
    /// A CANCELLED REQUEST IS NOT SYNCHRONOUS WITH THE CLIENT GIVING UP: the client's task completes when
    /// it abandons the connection, and the server learns of the abort a moment later. Reading the trail
    /// once, immediately, would therefore be a race that passes or fails on timing rather than on
    /// behaviour. This is a WAIT WITH A DEADLINE, not a sleep - it returns the instant the row lands, and
    /// the deadline exists only so a broken build fails with a sentence instead of hanging.
    /// </summary>
    private static async Task<GovernanceAuditEventDto> WaitForTheStopToBeRecorded(GovernanceAuditLog audit)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var rows = audit.List(sessionId: Sid);
            if (rows.Count > 0)
                return Assert.Single(rows);
            await Task.Delay(25);
        }

        Assert.Fail("The stop was dispatched to the Director and no audit row was ever written for it. "
            + "Ruling 4 was accepted on the ground that stops are audited (inspection 1, finding I1).");
        throw new InvalidOperationException("unreachable");
    }

    private async Task WithGateway(
        PushedSessionStore store,
        DirectorCommandResult? killAnswer,
        Func<HttpClient, List<DirectorCommand>, GovernanceAuditLog?, Task> assertion,
        GovernanceAuditLog? audit = null,
        bool auditSupplied = true,
        DirectorCommandRouter.SendDirectorCommandAsync? sendOverride = null)
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
                // A Director that answers at once with a fixed result, unless the test supplies its own
                // delegate - which the cancellation and timeout cases do, because what they are about is
                // WHEN the answer arrives rather than what it says.
                sendCommand: async (directorId, command, token) =>
                {
                    sent.Add(command);
                    return sendOverride is null ? killAnswer : await sendOverride(directorId, command, token);
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
