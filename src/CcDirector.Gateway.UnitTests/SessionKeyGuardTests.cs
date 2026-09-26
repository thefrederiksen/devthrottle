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
    // What the Wingman said this session's stops mean, and the history of them. Whether the ANSWER is
    // served is the route's decision - while an account's colours are off these serve a device key only -
    // because a guard is a pure function on a method and a path and cannot see a tenant's settings.
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdict")]
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/dev-reports")]
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/dev-reports/22222222-2222-2222-2222-222222222222")]
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdicts")]
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
    [InlineData("GET", "/gateway/session-colours")]
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
    // The calling session's own message inbox (the Message Load mission).
    [InlineData("GET", "/fleet/inbox")]
    public void The_read_side_of_the_agent_route_set_is_allowed(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} should be allowed");

    [Theory]
    // A queued message into another session's inbox (the Message Load mission). Who may be written to is the
    // route's ruling, because it needs the roster; the guard only lets the request reach it.
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/message")]
    [InlineData("POST", "/fleet/broadcast")]
    // An answer to a message that asked for one (slice 3). Who may answer, and to whom, is the route's ruling.
    [InlineData("POST", "/fleet/reply")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/hold")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/role")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/mission")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/request-deletion")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/compact-context")]
    // `session raise` (issue #2662). Refused to every agent until the Message Load mission's inspection 1, ruling 3;
    // whose hand may be raised is the route's check.
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/needs-manager")]
    // Mission "Stop a session", Ruling 4: any session may stop any other in the same account, because the
    // stop carries a reason and is audited. Ruling 6: and the polite flag comes off the way it went on.
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/stop")]
    // Report a judged stop WRONG (the Wingman-on-every-turn mission, slice G). It writes one row of our own
    // record and reaches nothing outside the Gateway. (Answering one is refused - see the agent input set below.) Whether the route SERVES
    // a session key is still the route's own decision - while the account's colours are off its verdicts are a
    // shadow record and the route refuses one, exactly as the reads do.
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdict/feedback")]
    // Dev reports (issue #2958): a session publishes and replies on its OWN reports. The route refuses any other
    // session id; the guard cannot read one.
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/dev-reports")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/dev-reports/22222222-2222-2222-2222-222222222222/replies")]
    [InlineData("DELETE", "/sessions/11111111-1111-1111-1111-111111111111/request-deletion")]
    [InlineData("PATCH", "/sessions/11111111-1111-1111-1111-111111111111")]
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
    // Ask a Director to restore a drained fleet (the Message Load mission, slice 6). A session drives the
    // restore as it drives the drain; the Director names the owners, from the capture.
    [InlineData("POST", "/gateway/workspaces/director-restart-2026-09-06/restore")]
    public void The_action_side_of_the_agent_route_set_is_allowed(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} should be allowed");

    // ---------- No agent types into a session (the Message Load mission, ruling 17) ----------
    //
    // These four were on the ALLOWED list above until 16 September 2026. Each put keystrokes into a running
    // session, and each was a way round the inbox. They are refused with a sentence that names the queued
    // message, because an agent told only "may not call POST /sessions/x/prompt" does not learn what to do.

    [Theory]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/interrupt")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/escape")]
    [InlineData("POST", "/fanout")]
    // Answering a judged stop types the verdict's option into the session (inspection 1, ruling 1). It was on
    // the allowed list above until the slice 1 fix round.
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdict/answer")]
    [InlineData("POST", "/Sessions/11111111-1111-1111-1111-111111111111/Turn-Verdict/ANSWER/")]
    // Case is folded before matching, as ASP.NET routing folds it; an upper-cased path is the same route.
    [InlineData("POST", "/Sessions/11111111-1111-1111-1111-111111111111/INTERRUPT")]
    [InlineData("post", "/fanout/")]
    public void Typing_into_a_session_is_refused_with_the_queued_message_named(string method, string path)
    {
        var verdict = SessionKeyGuard.Check(method, path);
        Assert.False(verdict.Allowed, $"{method} {path} must be refused to a session key");
        Assert.Equal(AgentInputRefusal.Typing, verdict.Reason);
        Assert.Contains("cc-devthrottle message send", verdict.Reason);
    }

    // ---------- A session may type into a session it owns (Parent Control, fix 1) ----------
    //
    // The guard cannot read an id, so it lets the one prompt shape through for every session key and the ROUTE
    // decides ownership (OwnedSessionInput, and the route tests). Only this exact shape: nothing deeper, no other verb.

    [Theory]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/prompt")]
    [InlineData("POST", "/Sessions/11111111-1111-1111-1111-111111111111/PROMPT/")]
    public void The_prompt_route_is_let_through_for_its_route_to_decide_ownership(string method, string path)
    {
        var verdict = SessionKeyGuard.Check(method, path);
        Assert.True(verdict.Allowed, $"{method} {path} should reach its route, which decides ownership");
        Assert.Equal(RaisedGrant.None, verdict.RaisedGrant);
    }

    [Theory]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/prompt/extra")]
    [InlineData("PUT", "/sessions/11111111-1111-1111-1111-111111111111/prompt")]
    [InlineData("POST", "/sessions/prompt")]
    public void Only_the_exact_prompt_shape_is_let_through(string method, string path)
        => Assert.False(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} must stay refused");

    [Fact]
    public void The_typing_refusal_names_what_a_session_may_do_instead()
    {
        Assert.Contains("session it owns", AgentInputRefusal.Typing);
        Assert.Contains("cc-devthrottle session prompt", AgentInputRefusal.Typing);
        Assert.Contains("cc-devthrottle message send", AgentInputRefusal.Typing);
    }

    [Theory]
    // The shapes beside the refused ones stay as they were: a plain compaction (its continue prompt is refused
    // by the route, which can read the body), a queued message, and a GET of the buffer.
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/compact-context")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/message")]
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/buffer")]
    // Reporting a judged stop wrong writes only our own record, and reading a verdict types nothing.
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdict/feedback")]
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdict")]
    public void The_neighbours_of_the_refused_input_routes_are_unchanged(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} should still be allowed");

    [Fact]
    public void Reading_another_sessions_inbox_by_id_is_not_a_route_a_session_key_reaches()
    {
        // The inbox has no session id in its path on purpose. A shape that named one would let a key read - and
        // so acknowledge - someone else's messages; if such a route is ever added it is refused until classified.
        Assert.False(SessionKeyGuard.Check("GET", "/fleet/inbox/11111111-1111-1111-1111-111111111111").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/fleet/inbox").Allowed);
    }

    [Fact]
    public void The_reply_route_is_allowed_only_in_its_one_shape()
    {
        // Slice 3: the id is in the body, so a shape carrying one in the path, or a read of the route, is not a route
        // a session key reaches.
        Assert.True(SessionKeyGuard.Check("POST", "/fleet/reply").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", "/fleet/reply").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/fleet/reply/0123456789abcdef0123456789abcdef").Allowed);
    }

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
    // Factory triggers: `cc-devthrottle trigger add|list|show|pause|resume|runs`.
    [InlineData("GET", "/triggers")]
    [InlineData("POST", "/triggers")]
    [InlineData("GET", "/triggers/website-new-mail")]
    [InlineData("PUT", "/triggers/website-new-mail")]
    [InlineData("DELETE", "/triggers/website-new-mail")]
    [InlineData("POST", "/triggers/website-new-mail/pause")]
    [InlineData("POST", "/triggers/website-new-mail/resume")]
    [InlineData("GET", "/triggers/website-new-mail/runs")]
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
    [InlineData("GET", "/gateway/workspaces/director-restart-2026-09-06/restore")]
    [InlineData("PUT", "/gateway/workspaces/director-restart-2026-09-06/restore")]
    [InlineData("POST", "/gateway/workspaces/director-restart-2026-09-06/restore/now")]
    // What a restore did is written only by the Director running it (inspection 7, ruling 1). A session never.
    [InlineData("POST", "/gateway/workspaces/director-restart-2026-09-06/restore/marks")]
    // A restored seat's dev reports pass to the new session only when the Director running the restore asks. A
    // session that could reach this could ask for another session's reports (Smart Director Restart, item 13).
    [InlineData("POST", "/gateway/workspaces/director-restart-2026-09-06/restore/dev-reports")]
    public void Workspace_shapes_the_Gateway_does_not_route_stay_refused(string method, string path)
        => Assert.False(SessionKeyGuard.Check(method, path).Allowed,
            $"{method} {path} is not a routed workspace shape and must not be authorized");

    [Theory]
    // A check report decides whether a session starts, so a session that could forge one could start sessions at
    // will. Only a Director's own key reports; the guard refuses both of the Director's trigger routes to a session.
    [InlineData("GET", "/directors/dir-north-1/triggers")]
    [InlineData("POST", "/directors/dir-north-1/triggers/website-new-mail/checks")]
    // And the trigger shapes that are not routed.
    [InlineData("POST", "/triggers/website-new-mail/runs")]
    [InlineData("GET", "/triggers/website-new-mail/pause")]
    [InlineData("POST", "/triggers/website-new-mail/checks")]
    public void Trigger_shapes_a_session_may_not_call_are_refused(string method, string path)
        => Assert.False(SessionKeyGuard.Check(method, path).Allowed,
            $"{method} {path} must not be open to a session key");

    // The Fleet Manager mission, step 3: the Fleet Manager is a session, and files, answers and reads its
    // records with its own key.
    [Theory]
    [InlineData("GET", "/gateway/fleet-manager/outcomes")]
    [InlineData("POST", "/gateway/fleet-manager/outcomes")]
    [InlineData("GET", "/gateway/fleet-manager/outcomes/5b1c2d3e-0000-4000-8000-000000000001")]
    [InlineData("POST", "/gateway/fleet-manager/outcomes/5b1c2d3e-0000-4000-8000-000000000001/answer")]
    [InlineData("GET", "/gateway/fleet-manager/preferences")]
    [InlineData("POST", "/gateway/fleet-manager/preferences")]
    [InlineData("DELETE", "/gateway/fleet-manager/preferences/5b1c2d3e-0000-4000-8000-000000000002")]
    [InlineData("GET", "/gateway/fleet-manager/digest")]
    [InlineData("GET", "/gateway/fleet-manager/events")]
    [InlineData("POST", "/gateway/fleet-manager/events/ack")]
    [InlineData("PUT", "/gateway/fleet-manager/outcomes/5b1c2d3e-0000-4000-8000-000000000001/advice")]
    public void The_fleet_manager_routes_are_allowed(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} should be allowed");

    // Only the shapes the Gateway maps, each with its own verb. A word hung off the prefix later is refused
    // until somebody classifies it. (GET and PUT on the bare prefix are the account's mark, allowed above.)
    [Theory]
    [InlineData("DELETE", "/gateway/fleet-manager/outcomes/5b1c2d3e-0000-4000-8000-000000000001")]
    [InlineData("PUT", "/gateway/fleet-manager/outcomes/5b1c2d3e-0000-4000-8000-000000000001")]
    [InlineData("GET", "/gateway/fleet-manager/outcomes/5b1c2d3e-0000-4000-8000-000000000001/answer")]
    [InlineData("POST", "/gateway/fleet-manager/outcomes/5b1c2d3e-0000-4000-8000-000000000001/reopen")]
    [InlineData("DELETE", "/gateway/fleet-manager/preferences")]
    [InlineData("POST", "/gateway/fleet-manager/digest")]
    [InlineData("GET", "/gateway/fleet-manager/purge")]
    [InlineData("POST", "/gateway/fleet-manager/events")]
    [InlineData("DELETE", "/gateway/fleet-manager/events")]
    [InlineData("GET", "/gateway/fleet-manager/events/ack")]
    [InlineData("POST", "/gateway/fleet-manager/events/5b1c2d3e-0000-4000-8000-000000000001")]
    [InlineData("POST", "/gateway/fleet-manager/events/ack/all")]
    [InlineData("GET", "/gateway/fleet-manager/outcomes/5b1c2d3e-0000-4000-8000-000000000001/advice")]
    [InlineData("POST", "/gateway/fleet-manager/outcomes/5b1c2d3e-0000-4000-8000-000000000001/advice")]
    [InlineData("PUT", "/gateway/fleet-manager/outcomes/5b1c2d3e-0000-4000-8000-000000000001/advice/again")]
    public void Fleet_manager_shapes_the_gateway_does_not_route_stay_refused(string method, string path)
        => Assert.False(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} should be refused");

    // The Fleet Manager mission, step 5: where the Fleet Manager runs, and starting, restarting and moving it, are
    // the OWNER's. A Fleet Manager that could move or restart itself would answer to nobody. Every verb is listed,
    // including the ones the Gateway does not route, so no future verb on these words slips through.
    [Theory]
    [InlineData("GET", "/gateway/fleet-manager/placement")]
    [InlineData("PUT", "/gateway/fleet-manager/placement")]
    [InlineData("POST", "/gateway/fleet-manager/placement")]
    [InlineData("POST", "/gateway/fleet-manager/start")]
    [InlineData("POST", "/gateway/fleet-manager/restart")]
    [InlineData("POST", "/gateway/fleet-manager/move")]
    [InlineData("POST", "/Gateway/Fleet-Manager/Move")]
    [InlineData("GET", "/gateway/fleet-manager/start")]
    [InlineData("GET", "/gateway/fleet-manager/page")]
    [InlineData("HEAD", "/gateway/fleet-manager/page")]
    // Step 7: the walkthrough and its answered, snoozed and close verbs are the owner's too.
    [InlineData("GET", "/gateway/fleet-manager/walkthrough")]
    [InlineData("POST", "/gateway/fleet-manager/walkthrough/5b1c2d3e-0000-4000-8000-000000000001/answered")]
    [InlineData("POST", "/gateway/fleet-manager/walkthrough/5b1c2d3e-0000-4000-8000-000000000001/snoozed")]
    [InlineData("POST", "/gateway/fleet-manager/walkthrough/5b1c2d3e-0000-4000-8000-000000000001/close")]
    public void The_fleet_manager_placement_routes_are_the_owners(string method, string path)
        => Assert.False(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} must be refused to a session key");

    // The Fleet Manager mission, step 8: the guard lets a session key POST a hand over - the route then refuses every
    // session key but the account's live Fleet Manager (FleetManagerHandOverService). Any other verb or shape is refused.
    [Theory]
    [InlineData("POST", "/gateway/fleet-manager/hand-over")]
    [InlineData("POST", "/Gateway/Fleet-Manager/Hand-Over")]
    public void Check_HandOverPost_ReachesTheRouteWithASessionKey(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} must reach the route, which decides");

    [Theory]
    [InlineData("GET", "/gateway/fleet-manager/hand-over")]
    [InlineData("PUT", "/gateway/fleet-manager/hand-over")]
    [InlineData("POST", "/gateway/fleet-manager/hand-over/5b1c2d3e-0000-4000-8000-000000000001")]
    public void Check_HandOverOtherShapes_AreRefusedToASessionKey(string method, string path)
        => Assert.False(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} must be refused to a session key");

    [Fact]
    public void Restarting_a_Director_is_still_refused_even_though_capturing_its_fleet_is_not()
    {
        // The whole point of the split. An agent may write down what was running; asking the machine
        // to restart is the admission surface and stays with a key that has that scope.
        Assert.True(SessionKeyGuard.Check("POST", "/gateway/workspaces").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/machines/SOREN_NORTH/director/restart").Allowed);
    }

    // THE COMMAND LINE DOOR onto the smart shutdown (mission "Smart Director Restart", 5.3 item 12).
    // Emptying a Director of every session is the same act as restarting its machine, so it draws the same
    // line: the start is the owner's, and the refusal NAMES the request command a session may run instead -
    // an agent told only "no" goes looking for a way round.
    [Fact]
    public void Starting_a_smart_restart_is_refused_to_a_session_key_and_names_what_to_do_instead()
    {
        var verdict = SessionKeyGuard.Check("POST", "/directors/11112222-3333-4444-5555-666677778888/smart-restart");

        Assert.False(verdict.Allowed);
        Assert.Contains("may not empty and restart a Director", verdict.Reason);
        Assert.Contains("cc-devthrottle machine restart-request", verdict.Reason);
    }

    // The other half of that line, and the same one restart-capability already draws: ASKING how an
    // emptying is going, or what was emptied before, is not asking to empty anything. Both read records
    // this key may already read at /gateway/workspaces.
    [Theory]
    [InlineData("GET", "/directors/11112222-3333-4444-5555-666677778888/smart-restart")]
    [InlineData("GET", "/directors/11112222-3333-4444-5555-666677778888/restart-history")]
    public void Reading_a_smart_restart_and_the_restart_history_are_allowed_to_a_session_key(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} must be allowed to a session key");

    // An allow list that widens by pattern stops being an allow list. Only the two literal read shapes are
    // in, and only the one literal start shape carries the named refusal.
    [Theory]
    [InlineData("POST", "/directors/11112222-3333-4444-5555-666677778888/smart-restart/now")]
    [InlineData("DELETE", "/directors/11112222-3333-4444-5555-666677778888/smart-restart")]
    [InlineData("POST", "/directors/11112222-3333-4444-5555-666677778888/restart-history")]
    [InlineData("GET", "/directors/11112222-3333-4444-5555-666677778888/restart-history/restart-2026-09-20")]
    public void Other_shapes_on_the_smart_restart_surface_are_refused_to_a_session_key(string method, string path)
        => Assert.False(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} must be refused to a session key");

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
    // The Wingman's turn judging (the Wingman-on-every-turn mission): whether this account's stops are
    // judged, and whether the verdicts reach its screens. Both say how the product BEHAVES.
    [InlineData("PUT", "/gateway/turn-verdict-judge")]
    [InlineData("PUT", "/gateway/turn-verdict-colour")]
    // The account's Fleet Manager mark: read it, and set or clear it (both are the one PUT).
    [InlineData("GET", "/gateway/fleet-manager")]
    [InlineData("PUT", "/gateway/fleet-manager")]
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

    [Fact]
    public void The_dictation_decision_log_is_dictation_data_and_is_refused_to_a_session_key()
    {
        // Proves an agent's session key cannot read what the Gateway decided about the owner's recordings
        // (Voice Delivery mission). It is refused by the allow list's default, not by a new rule: this pins
        // that nobody adds the route to the allowed side as "only diagnostics".
        Assert.False(SessionKeyGuard.Check("GET", "/dictation/11111111111111111111111111111111/decisions").Allowed);
    }

    [Fact]
    public void The_dictation_outcome_read_is_dictation_data_and_is_refused_to_a_session_key()
    {
        // Proves an agent's session key cannot read how the owner's recordings came out (Voice Delivery phase 5), by
        // the allow list's default, exactly as the decision log.
        Assert.False(SessionKeyGuard.Check("GET", "/dictation/11111111111111111111111111111111/outcome").Allowed);
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
    // The turn-verdict reads are READS. A write verb on either path is refused: nothing routes there,
    // and the day something does it has to be classified here before an agent can reach it. The two
    // switches are likewise PUT-only, so a POST to one is not a settings write by another name.
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdict")]
    [InlineData("DELETE", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdicts")]
    [InlineData("POST", "/gateway/turn-verdict-judge")]
    [InlineData("DELETE", "/gateway/turn-verdict-colour")]
    // Clearing the Fleet Manager mark is a PUT with a null id, so no other verb is a route there.
    [InlineData("POST", "/gateway/fleet-manager")]
    [InlineData("DELETE", "/gateway/fleet-manager")]
    [InlineData("PUT", "/gateway/fleet-manager/anything")]
    // And nothing hung off a verdict path later is reachable by accident - the allow matches a length
    // of exactly three segments.
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdict/options")]
    // The answer and feedback routes are TWO literal shapes. Their verb is POST; their neighbours are not them;
    // and a fifth segment, the plural path, or a third last word is refused until somebody classifies it here.
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdict/answer")]
    [InlineData("PUT", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdict/answer")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdict/answer/again")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdicts/answer")]
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdict/feedback")]
    [InlineData("PUT", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdict/feedback")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdict/feedback/again")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/turn-verdicts/feedback")]
    [InlineData("POST", "/machines/SOREN_NORTH/turn-verdict/answer")]
    // The ADMINISTRATOR read of the corrections (the Wingman-on-every-turn mission, slice G). It is gated on
    // the administrator service token by the endpoint itself, and it serves ONE named account's corrections to
    // the daily corpus pull - so a session key reaching it would be a session credential reading an operator
    // surface. Named here rather than left to the default deny, so the census says the route exists and that
    // this guard refuses it.
    // Dev reports (issue #2958): the OWNER routes. A session key is never the owner - it may not list the
    // account's reports, read one through the owner surface, fetch its bytes, or send notes and answers into a
    // session as if the owner had. And nothing hung off the session routes is reachable by accident.
    [InlineData("GET", "/dev-reports")]
    [InlineData("GET", "/dev-reports/22222222-2222-2222-2222-222222222222")]
    [InlineData("GET", "/dev-reports/22222222-2222-2222-2222-222222222222/html")]
    [InlineData("POST", "/dev-reports/22222222-2222-2222-2222-222222222222/send")]
    [InlineData("PUT", "/sessions/11111111-1111-1111-1111-111111111111/dev-reports")]
    [InlineData("DELETE", "/sessions/11111111-1111-1111-1111-111111111111/dev-reports/22222222-2222-2222-2222-222222222222")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/dev-reports/22222222-2222-2222-2222-222222222222/send")]
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/dev-reports/22222222-2222-2222-2222-222222222222/replies")]
    [InlineData("POST", "/sessions/11111111-1111-1111-1111-111111111111/dev-reports/22222222-2222-2222-2222-222222222222/replies/again")]
    [InlineData("GET", "/gateway/admin/turn-verdict-feedback")]
    [InlineData("POST", "/gateway/admin/turn-verdict-feedback")]
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
    // The Wingman inspector's read (phase 2). It serves raw terminal screens, conversations, the whole prompt and the
    // judge's raw answer, so it is the account's devices' only - never a session key's, whatever the colour switch
    // says. It is deliberately NOT on the allow list, and its handler refuses a session key on its own as well.
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/wingman-stops")]
    // The Wingman tab's live stop (version 3, item 1). Same reasoning, same refusal: it carries the agent's decisive
    // sentence and its whole last reply, so it is the account's devices' only. Deliberately NOT on the allow list,
    // and its handler refuses a session key on its own as well.
    [InlineData("GET", "/sessions/11111111-1111-1111-1111-111111111111/wingman-now")]
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
