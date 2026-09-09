using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// What a SESSION KEY may and may not call (Remove-the-network-port mission, phase 1b).
///
/// The guard is the reason a session credential is worth having at all. Without it a session key is just
/// a differently-shaped account key: it would authenticate, and then reach the account surface exactly as
/// the Director's own key does, which is the widening this phase exists to avoid. So the tests that matter
/// most here are the REFUSALS - and they are written route by route rather than as one "denies something"
/// case, because an allow list that has quietly grown an entry fails by ALLOWING, and only a test that
/// names the thing it must refuse can see that.
/// </summary>
public sealed class SessionKeyGuardTests
{
    // ---------- The agent route set: what the fleet's command line needs ----------

    [Theory]
    [InlineData("GET", "/healthz")]
    [InlineData("GET", "/sessions")]
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111")]
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/buffer")]
    [InlineData("GET", "/repositories")]
    [InlineData("GET", "/worktrees")]
    [InlineData("GET", "/directors")]
    [InlineData("GET", "/launchers")]
    [InlineData("GET", "/machines")]
    [InlineData("GET", "/machines/SOREN_NORTH/apps")]
    [InlineData("GET", "/machines/SOREN_NORTH/files")]
    // Issue #2720: CAN this machine complete a Director restart? The safest read on this surface - it
    // sends no command, opens no connection and raises no signal - and the one an agent must be able to
    // ask, because the alternative is the 2026-09-06 failure: drain seventeen sessions, then discover
    // the answer was no. It is also what makes the refusal on POST .../director/restart affordable:
    // asking whether a verb COULD work is not asking to run it, and an agent that can only act blindly
    // is the argument for widening the admission guard itself.
    [InlineData("GET", "/machines/SOREN_NORTH/restart-capability")]
    [InlineData("GET", "/missions")]
    [InlineData("GET", "/missions/m-123")]
    [InlineData("GET", "/cron/jobs")]
    [InlineData("GET", "/cron/jobs/cj_abc")]
    [InlineData("GET", "/gateway/snooze-presets")]
    [InlineData("GET", "/gateway/skills")]
    [InlineData("GET", "/gateway/skills/move-session")]
    [InlineData("GET", "/gateway/skills/move-session/body")]
    [InlineData("GET", "/gateway/skills/move-session/versions")]
    [InlineData("GET", "/gateway/skills/move-session/versions/2")]
    [InlineData("GET", "/gateway/workflows/mission/instructions")]
    [InlineData("GET", "/gateway/workflow-runs")]
    [InlineData("GET", "/gateway/workflow-runs/run-9")]
    // Workspaces (issue #2722): the named set of seats a drain captures and a restore reads.
    [InlineData("GET", "/gateway/workspaces")]
    [InlineData("GET", "/gateway/workspaces/director-restart-2026-09-06")]
    public void The_read_side_of_the_agent_route_set_is_allowed(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} should be allowed");

    [Theory]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/prompt")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/interrupt")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/hold")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/role")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/mission")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/request-deletion")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/compact-context")]
    // Mission "Stop a session", Ruling 4: any session may stop any other in the same account, because the
    // stop carries a reason and is audited. Ruling 6: and the polite flag comes off the way it went on.
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/stop")]
    [InlineData("DELETE", "/sessions/11111111-1111-1111-1111-111111111111/request-deletion")]
    [InlineData("PATCH", "/sessions/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/fanout")]
    [InlineData("POST", "/missions")]
    // The third verb on a record a session key can already create and read: rename it, set its WHY,
    // and end it (complete / removed) or reopen it. An agent that can open a mission but can never
    // end one is how the mission list grows forever.
    [InlineData("PATCH", "/missions/m-123")]
    [InlineData("POST", "/machines/SOREN_NORTH/sessions")]
    [InlineData("POST", "/machines/SOREN_NORTH/launch")]
    [InlineData("POST", "/gateway/skills/move-session/publish")]
    [InlineData("POST", "/gateway/workflows/mission/clone")]
    // Capture a Director's live fleet into a workspace, write the drain's judgments onto it, and
    // delete one. A SESSION drives a drain, so a session key that could only read these would leave
    // the record to a human at the keyboard of the machine being restarted.
    [InlineData("POST", "/gateway/workspaces")]
    [InlineData("PUT", "/gateway/workspaces/director-restart-2026-09-06")]
    [InlineData("DELETE", "/gateway/workspaces/director-restart-2026-09-06")]
    public void The_action_side_of_the_agent_route_set_is_allowed(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} should be allowed");

    // ---------- The routes the SHIPPED CLIENTS actually call ----------
    //
    // Every case below is copied from the Gateway's route table and from the command line that calls it -
    // SkillEndpoints/WorkflowEndpoints/CronJobEndpoints/CronRunEndpoints, and skill_ops.py/workflow_ops.py/
    // schedule_ops.py - NOT from reading the guard and writing down what it does.
    //
    // That distinction is the whole finding. The guard previously allowed only four-segment
    // POST /gateway/{kind}/{id}/{draft|publish|clone}, so `skill push` (POST /gateway/skills) and every
    // draft update (PUT .../draft) returned 403 to every agent, and every schedule command except `list`
    // did too. The suite stayed green because it asserted POST .../draft - a route that exists in neither
    // the client nor the server. A test written from the implementation agrees with the implementation's
    // mistakes; only a test written from the other side can disagree with it.

    [Theory]
    // Catalogue: create with POST on the collection, update with PUT on the draft.
    [InlineData("POST", "/gateway/skills")]
    [InlineData("POST", "/gateway/workflows")]
    [InlineData("PUT", "/gateway/skills/move-session/draft")]
    [InlineData("PUT", "/gateway/workflows/mission/draft")]
    [InlineData("POST", "/gateway/skills/move-session/publish")]
    [InlineData("POST", "/gateway/workflows/mission/publish")]
    [InlineData("POST", "/gateway/skills/move-session/clone")]
    [InlineData("DELETE", "/gateway/skills/move-session")]
    [InlineData("DELETE", "/gateway/workflows/mission")]
    // Schedules: the client needs create, read, update, delete, run-now and run-history.
    [InlineData("GET", "/cron/jobs")]
    [InlineData("POST", "/cron/jobs")]
    [InlineData("GET", "/cron/jobs/cj_abc")]
    [InlineData("PUT", "/cron/jobs/cj_abc")]
    [InlineData("DELETE", "/cron/jobs/cj_abc")]
    [InlineData("POST", "/cron/jobs/cj_abc/run")]
    [InlineData("GET", "/cron/jobs/cj_abc/runs")]
    public void The_methods_and_paths_the_shipped_clients_send_are_allowed(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed,
            $"{method} {path} is what the shipped client sends; refusing it returns 403 to every agent");

    [Theory]
    // The workspace surface is an exact method+path allow list, not a method/path cross-product.
    // Each of these is a shape the Gateway does not route: today they 404, and the day somebody adds
    // a route at one of them it must be classified before a session key can reach it.
    [InlineData("PUT", "/gateway/workspaces")]
    [InlineData("DELETE", "/gateway/workspaces")]
    [InlineData("POST", "/gateway/workspaces/director-restart-2026-09-06")]
    [InlineData("POST", "/gateway/workspaces/director-restart-2026-09-06/restart")]
    [InlineData("GET", "/gateway/workspaces/director-restart-2026-09-06/seats")]
    public void Workspace_shapes_the_Gateway_does_not_route_stay_refused(string method, string path)
        => Assert.False(SessionKeyGuard.Check(method, path).Allowed,
            $"{method} {path} is not a routed workspace shape and must not be authorized");

    [Fact]
    public void Restarting_a_Director_is_still_refused_even_though_capturing_its_fleet_is_not()
    {
        // The whole point of the split. An agent may write down what was running; asking the machine
        // to restart is the admission surface and stays with a key that has that scope.
        Assert.True(SessionKeyGuard.Check("POST", "/gateway/workspaces").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/machines/SOREN_NORTH/director/restart").Allowed);
    }

    [Fact]
    public void Turning_a_fleet_wide_capability_off_is_still_the_owners_call()
    {
        // The one catalogue verb that is not an agent contributing work. Widening create/update/delete
        // must not drag these along with them.
        Assert.False(SessionKeyGuard.Check("POST", "/gateway/skills/move-session/disable").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/gateway/skills/move-session/enable").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/gateway/workflows/mission/disable").Allowed);
    }

    [Theory]
    // The browser surface is an exact method+path allow list, not a method/path cross-product. Each of
    // these is a shape the guard used to authorize and the Gateway does not route: today they 404, and the
    // day somebody adds a route at one of them it would have been silently open to every session key.
    [InlineData("DELETE", "/directors/d-1/browsers")]
    [InlineData("POST", "/directors/d-1/browsers/b-1/attach")]
    [InlineData("GET", "/directors/d-1/browsers/b-1/start")]
    [InlineData("DELETE", "/directors/d-1/browsers/b-1/signin")]
    [InlineData("GET", "/directors/d-1/browsers/b-1")]
    [InlineData("POST", "/directors/d-1/browsers/b-1")]
    public void A_browser_shape_the_gateway_does_not_route_is_not_authorized(string method, string path)
        => Assert.False(SessionKeyGuard.Check(method, path).Allowed,
            $"{method} {path} is not a mapped route; authorizing it is latent widening");

    [Theory]
    // ...and the ones it DOES route stay allowed, so the tightening above did not overshoot.
    [InlineData("GET", "/directors/d-1/browsers")]
    [InlineData("POST", "/directors/d-1/browsers")]
    [InlineData("GET", "/directors/d-1/browsers/b-1/attach")]
    [InlineData("POST", "/directors/d-1/browsers/b-1/start")]
    [InlineData("POST", "/directors/d-1/browsers/b-1/stop")]
    [InlineData("POST", "/directors/d-1/browsers/b-1/signin")]
    [InlineData("POST", "/directors/d-1/browsers/b-1/rename")]
    [InlineData("DELETE", "/directors/d-1/browsers/b-1")]
    public void The_browser_routes_the_gateway_does_map_are_allowed(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} is a mapped route");

    // ---------- Configuration: the owner's ruling of 2026-08-03 ----------
    //
    // "An agent may change how the product BEHAVES. It may not change WHO IS ALLOWED IN." Phase 1b refused
    // the whole /directors surface bar two sub-paths; the ruling reverses that for settings and handovers,
    // because the point of running agents is not to have to use the interface to configure the product.
    //
    // These are written as a matched PAIR with the refusals below. An allow list is only as good as the
    // refusals sitting next to it: "settings are allowed" is a safe sentence only while "enrolment is not"
    // is still true and still tested, and the two are one decision, not two.

    [Theory]
    // A Director's own settings, both directions.
    [InlineData("GET", "/directors/d-1/settings")]
    [InlineData("PUT", "/directors/d-1/settings")]
    // The application's settings - how it reports, snoozes, speaks, and which model it uses.
    [InlineData("GET", "/gateway/settings")]
    [InlineData("GET", "/gateway/daily-report")]
    [InlineData("PUT", "/gateway/daily-report")]
    [InlineData("GET", "/gateway/snooze-default")]
    [InlineData("PUT", "/gateway/snooze-default")]
    [InlineData("PUT", "/gateway/snooze-presets")]
    [InlineData("GET", "/gateway/time-zone")]
    [InlineData("PUT", "/gateway/time-zone")]
    [InlineData("GET", "/gateway/ai-provider")]
    [InlineData("PUT", "/gateway/ai-provider")]
    [InlineData("GET", "/gateway/tts-voice")]
    [InlineData("PUT", "/gateway/tts-voice")]
    [InlineData("GET", "/gateway/spoken-language")]
    [InlineData("PUT", "/gateway/spoken-language")]
    [InlineData("PUT", "/gateway/spoken-language/voice")]
    [InlineData("GET", "/gateway/injected-text")]
    [InlineData("PUT", "/gateway/injected-text")]
    [InlineData("GET", "/gateway/transcription-mode")]
    [InlineData("PUT", "/gateway/transcription-mode")]
    // Handovers: list, read one, write one, remove one. Moving a session needs the first three.
    [InlineData("GET", "/directors/d-1/handovers")]
    [InlineData("GET", "/directors/d-1/handovers/content")]
    [InlineData("POST", "/directors/d-1/handovers")]
    [InlineData("DELETE", "/directors/d-1/handovers")]
    public void Configuring_the_product_is_allowed(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} should be allowed");

    [Fact]
    public void Settings_are_allowed_but_the_gateway_prefix_they_share_is_not()
    {
        // The application settings live under /gateway, and so do the shared catalogue's enable/disable
        // routes, which are deliberately refused because turning a fleet-wide capability off for everyone is
        // the owner's call. A guard written as "PUT under /gateway is configuration" would have read as
        // correct and handed those over. This is the test that would catch that rewrite.
        Assert.True(SessionKeyGuard.Check("PUT", "/gateway/time-zone").Allowed);
        Assert.False(SessionKeyGuard.Check("PUT", "/gateway/skills/move-session/disable").Allowed);
        Assert.False(SessionKeyGuard.Check("PUT", "/gateway/some-setting-invented-next-year").Allowed);
    }

    [Fact]
    public void A_sub_path_hung_off_settings_later_is_refused()
    {
        // Settings are matched at exactly /directors/{id}/settings. If the match were a prefix, anything a
        // future release parked underneath - credentials, tokens, enrolment state - would be open on the day
        // it shipped, which is precisely the deny-list failure this guard is shaped to avoid.
        Assert.True(SessionKeyGuard.Check("PUT", "/directors/d-1/settings").Allowed);
        Assert.False(SessionKeyGuard.Check("PUT", "/directors/d-1/settings/credentials").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", "/directors/d-1/settings/credentials").Allowed);
    }

    [Fact]
    public void Voice_settings_are_configuration_but_voice_data_is_not()
    {
        // The line the ruling draws is behaviour versus admission, but there is a second distinction inside
        // the voice surface that is easy to lose: a knob saying HOW to transcribe is configuration, while
        // what was actually said into the microphone is the owner's and stays refused.
        Assert.True(SessionKeyGuard.Check("PUT", "/gateway/transcription-mode").Allowed);
        Assert.True(SessionKeyGuard.Check("PUT", "/gateway/tts-voice").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", "/gateway/recordings").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", "/sessions/11111111-1111-1111-1111-111111111111/transcript").Allowed);
    }

    [Theory]
    // Device registration and enrolment. The owner named this one himself: a credential that can enrol a
    // device can admit a NEW device, which is not configuring the product - it is the boundary itself.
    // These are the routes the Gateway actually maps, not plausible-looking spellings of them, because a
    // refusal test aimed at a path that does not exist passes on the default-deny and proves nothing about
    // the route that does.
    [InlineData("GET", "/devices")]
    [InlineData("POST", "/devices/enroll-hosted")]
    [InlineData("POST", "/mobile/enroll")]
    [InlineData("POST", "/m/enroll")]
    [InlineData("GET", "/account/devices")]
    [InlineData("DELETE", "/account/devices/dev-1")]
    // Account-level identity: who the account belongs to, what it is worth, and signing in or out of it.
    [InlineData("GET", "/account/status")]
    [InlineData("GET", "/account/credits")]
    [InlineData("GET", "/account/trial")]
    [InlineData("POST", "/account/email")]
    [InlineData("POST", "/account/logout")]
    [InlineData("GET", "/account/sign-in-start")]
    [InlineData("POST", "/account/sign-in-start")]
    // Force-killing a Director takes down EVERY session on that machine at once. An agent that needs one
    // session to stop has POST /sessions/{sid}/stop, which names the session and carries a reason; this is
    // refused as the blunt instrument rather than as something an agent has no business doing.
    [InlineData("DELETE", "/directors/d-1")]
    // Which Directors are in the account at all.
    [InlineData("POST", "/directors/register")]
    [InlineData("DELETE", "/directors/d-1/registration")]
    public void The_admission_boundary_stays_refused_even_though_configuration_opened(string method, string path)
        => Assert.False(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} must NOT be allowed");

    // ---------- The account surface: what a session key must never reach ----------

    [Theory]
    // Signing in and out, and the account's own data - the owner's identity, not an agent's.
    [InlineData("GET", "/account/status")]
    [InlineData("POST", "/account/sign-in")]
    [InlineData("POST", "/account/logout")]
    [InlineData("GET", "/account/credits")]
    [InlineData("GET", "/account/devices")]
    // Device enrollment and revocation: the credential system itself. A session key that could enroll a
    // device could mint itself an account-wide credential and step straight out of this guard.
    [InlineData("POST", "/devices/enroll")]
    [InlineData("POST", "/devices/enroll-signed-in")]
    [InlineData("DELETE", "/account/devices/some-device")]
    // Turning the Gateway off, force-killing a Director, or changing which Directors are in the account.
    // Settings USED to sit in this list and no longer do - see the configuration tests above.
    [InlineData("POST", "/shutdown")]
    [InlineData("DELETE", "/directors/d-1")]
    [InlineData("DELETE", "/directors/d-1/registration")]
    [InlineData("POST", "/directors/register")]
    // The verb still decides: settings are read with GET and written with PUT, and nothing else is a route.
    // Keeping this here is what stops "settings are allowed" from becoming "the settings path is allowed".
    [InlineData("POST", "/directors/d-1/settings")]
    [InlineData("DELETE", "/directors/d-1/settings")]
    // Somebody else's Director process lifecycle on another machine.
    [InlineData("POST", "/machines/SOREN_NORTH/director/stop")]
    [InlineData("POST", "/machines/SOREN_NORTH/director/restart")]
    // Issue #2720, and the line it is drawing: ASKING whether a machine can be restarted is allowed
    // above, and the restart itself stays refused - which is the whole reason the query was worth
    // adding rather than widening the guard. The query is a READ, so a POST to the same path is refused
    // too: a rule that let a verb through on the strength of a path is a rule a caller can move.
    [InlineData("POST", "/machines/SOREN_NORTH/restart-capability")]
    [InlineData("DELETE", "/machines/SOREN_NORTH/restart-capability")]
    // And nothing hung off it later is reachable by accident - the allow matches a length exactly.
    [InlineData("GET", "/machines/SOREN_NORTH/restart-capability/history")]
    // Issue #2725: a session may ASK for a restart and READ the request. It may not ACCEPT one, DECLINE
    // one, or REPORT on one - those are the owner's and the Director's, on the admission-scoped surface
    // where the direct restart already sits. The accept is the decision; the ask is a record.
    [InlineData("POST", "/machines/SOREN_NORTH/director/restart-requests/abc123/accept")]
    [InlineData("POST", "/machines/SOREN_NORTH/director/restart-requests/abc123/decline")]
    [InlineData("POST", "/machines/SOREN_NORTH/director/restart-requests/abc123/report")]
    // The verb still decides: the record is created with POST and read with GET, and nothing else.
    [InlineData("PUT", "/machines/SOREN_NORTH/director/restart-requests")]
    [InlineData("DELETE", "/machines/SOREN_NORTH/director/restart-requests")]
    [InlineData("DELETE", "/machines/SOREN_NORTH/director/restart-requests/abc123")]
    [InlineData("POST", "/machines/SOREN_NORTH/director/restart-requests/abc123")]
    [InlineData("POST", "/gateway/director-restart-requests")]
    // Turning a fleet-wide capability off for everyone.
    [InlineData("POST", "/gateway/skills/move-session/disable")]
    [InlineData("POST", "/gateway/workflows/mission/enable")]
    // Deleting rather than reading. NOTE what is no longer here: DELETE /cron/jobs/{id} used to be
    // asserted as refused, which was this test agreeing with the guard's mistaken idea of the schedule
    // surface rather than with the client, whose `schedule delete` has always sent exactly that. It is
    // allowed above now. Deleting a SESSION is different and stays refused - `request-deletion` is the
    // verb an agent has for that, and it is a request rather than an execution.
    [InlineData("DELETE", "/sessions/11111111-1111-1111-1111-111111111111")]
    // The diagnostics and reporting surfaces.
    [InlineData("GET", "/diag/loadmetrics")]
    [InlineData("GET", "/gateway/reports/morning")]
    // A route nobody has classified. THE DEFAULT IS DENY - this is the whole point of an allow list.
    [InlineData("GET", "/some/route/invented/next/year")]
    [InlineData("POST", "/some/route/invented/next/year")]
    public void The_account_surface_is_refused(string method, string path)
        => Assert.False(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} must NOT be allowed");

    // ---------- Issue #2725: asking for a Director restart is allowed, deciding it is not ----------

    [Theory]
    [InlineData("POST", "/machines/SOREN_NORTH/director/restart-requests")]
    [InlineData("GET", "/machines/SOREN_NORTH/director/restart-requests")]
    [InlineData("GET", "/machines/SOREN_NORTH/director/restart-requests/abc123")]
    [InlineData("GET", "/gateway/director-restart-requests")]
    public void A_session_may_ask_for_a_restart_and_read_the_request(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} must be allowed");

    /// <summary>
    /// THE PAIR THE WHOLE DESIGN TURNS ON, asserted together so neither half can drift alone: the same
    /// session key that may ASK for a restart still may NOT perform one. If the second line ever goes
    /// green the admission surface was widened, which is exactly what the request route exists to avoid.
    /// </summary>
    [Fact]
    public void The_key_that_may_ask_for_a_restart_still_may_not_perform_one()
    {
        Assert.True(SessionKeyGuard.Check("POST", "/machines/SOREN_NORTH/director/restart-requests").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/machines/SOREN_NORTH/director/restart").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/machines/SOREN_NORTH/director/restart-requests/abc123/accept").Allowed);
    }

    // ---------- The refusal itself ----------

    [Fact]
    public void A_refusal_names_the_method_and_the_path_it_refused()
    {
        var verdict = SessionKeyGuard.Check("POST", "/account/sign-in");

        Assert.False(verdict.Allowed);
        Assert.Contains("POST", verdict.Reason);
        Assert.Contains("/account/sign-in", verdict.Reason);
    }

    [Fact]
    public void An_allowed_request_carries_no_reason_to_read()
        => Assert.Equal("", SessionKeyGuard.Check("GET", "/sessions").Reason);

    // ---------- Shapes that could walk around it ----------

    [Fact]
    public void Case_does_not_open_a_route()
    {
        // ASP.NET matches a path case-insensitively, so a guard that compared ordinally would refuse
        // /Sessions here and then the router would serve it - a bypass consisting of one capital letter.
        Assert.True(SessionKeyGuard.Check("GET", "/Sessions").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/Account/Sign-In").Allowed);
    }

    [Fact]
    public void A_trailing_slash_does_not_open_a_route()
    {
        Assert.True(SessionKeyGuard.Check("GET", "/sessions/").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/account/sign-in/").Allowed);
    }

    [Fact]
    public void The_method_decides_as_much_as_the_path()
    {
        // /sessions is a read an agent needs. The same path as a DELETE is not, and a guard that keyed on
        // the path alone would hand an agent the ability to wipe the roster.
        Assert.True(SessionKeyGuard.Check("GET", "/sessions").Allowed);
        Assert.False(SessionKeyGuard.Check("DELETE", "/sessions").Allowed);
        Assert.False(SessionKeyGuard.Check("PUT", "/sessions").Allowed);
    }

    [Fact]
    public void An_unknown_session_sub_route_is_refused_even_though_its_prefix_is_allowed()
    {
        // The sessions branch is an explicit list of verbs, NOT "anything under /sessions/{id}". If it were
        // the latter, every future per-session route - an upload, a settings write, a credential read -
        // would be open to every agent on the day it was added.
        Assert.False(SessionKeyGuard.Check("POST", "/sessions/11111111-1111-1111-1111-111111111111/upload-image").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/sessions/11111111-1111-1111-1111-111111111111/wingman").Allowed);
    }

    // ---------- Stopping a session (mission "Stop a session") ----------
    //
    // An allow list is only worth what its refusals are worth, and this pair is the whole of the owner's
    // ruling. Both doors reach the SAME stop handler, the same fold and the same audit trail - so what makes
    // "an agent's key can only ever stop a session with a reason attached" true is not anything in the
    // handler, it is this refusal. The bare DELETE carries no body and therefore can carry no reason; if it
    // were ever added to the allow list "for symmetry", an agent would have a silent, unrecorded way to end
    // any session in the account and nothing in the route would notice.

    [Fact]
    public void The_stop_that_carries_a_reason_is_allowed_and_the_door_that_cannot_carry_one_is_refused()
    {
        const string sid = "11111111-1111-1111-1111-111111111111";

        Assert.True(SessionKeyGuard.Check("POST", $"/sessions/{sid}/stop").Allowed);
        Assert.False(SessionKeyGuard.Check("DELETE", $"/sessions/{sid}").Allowed);
    }

    [Fact]
    public void The_bare_delete_stays_refused_however_it_is_written()
    {
        const string sid = "11111111-1111-1111-1111-111111111111";

        // Case folding and a trailing slash are the two shapes this guard normalises, so the refusal is
        // pinned against both rather than against one spelling of the path.
        Assert.False(SessionKeyGuard.Check("DELETE", $"/sessions/{sid}").Allowed);
        Assert.False(SessionKeyGuard.Check("DELETE", $"/Sessions/{sid}").Allowed);
        Assert.False(SessionKeyGuard.Check("DELETE", $"/sessions/{sid}/").Allowed);
    }

    /// <summary>
    /// Ruling 6: a flag comes off the way it went on. The DELETE is matched at EXACTLY three segments, so
    /// opening it does not open anything deeper hung off the same path.
    /// </summary>
    [Fact]
    public void Clearing_a_pending_deletion_is_allowed_and_does_not_open_anything_deeper()
    {
        const string sid = "11111111-1111-1111-1111-111111111111";

        Assert.True(SessionKeyGuard.Check("DELETE", $"/sessions/{sid}/request-deletion").Allowed);
        Assert.False(SessionKeyGuard.Check("DELETE", $"/sessions/{sid}/request-deletion/all").Allowed);
        Assert.False(SessionKeyGuard.Check("DELETE", $"/sessions/{sid}/stop").Allowed);
    }

    [Fact]
    public void A_missing_or_empty_path_is_refused()
    {
        Assert.False(SessionKeyGuard.Check("GET", null).Allowed);
        Assert.False(SessionKeyGuard.Check("GET", "").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", "/").Allowed);
        Assert.False(SessionKeyGuard.Check(null, "/sessions").Allowed);
    }
}
