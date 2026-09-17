namespace CcDirector.Gateway.Util;

/// <summary>The verdict on one request from a session key, and the sentence explaining it. A refusal always
/// names its reason so an agent whose command breaks is debuggable from one log line.</summary>
public readonly record struct SessionKeyVerdict(bool Allowed, string Reason)
{
    public static readonly SessionKeyVerdict Allow = new(true, "");

    public static SessionKeyVerdict Refuse(string reason) => new(false, reason);
}

/// <summary>
/// The sentences an agent reads when it tries to type into a session (the Message Load mission, ruling 17).
/// Held once so the guard's refusal and the compaction route's refusal say the same thing.
/// </summary>
public static class AgentInputRefusal
{
    /// <summary>What to do instead, said once and appended to every refusal below.</summary>
    public const string Instead =
        "To reach a session you started, or the session that started you, send a queued message: " +
        "cc-devthrottle message send <session> \"<text>\" - it is read when that session is free.";

    /// <summary>A session key asked to type into, interrupt or escape a session, to fan a prompt out, or to
    /// answer a judged stop (which types the verdict's option into the session).</summary>
    public const string Typing =
        "An agent may not type into, interrupt or escape another session: only the owner does that, from his " +
        "own screens. " + Instead;

    /// <summary>A session key asked to compact a session and then send it a prompt.</summary>
    public const string CompactContinue =
        "An agent may compact a session but may not send it a prompt afterwards: typing into a session is the " +
        "owner's alone. Compact it with cc-devthrottle session compact, then " + Instead;
}

/// <summary>
/// What a SESSION KEY may call on the Gateway (Remove-the-network-port mission, phase 1b).
///
/// This began as the Gateway twin of the Director's ControlApiGuard.CheckSessionChild (deleted with the Director's listener; this guard is the surviving one), and it is written the same way and
/// for the same reason: it is an ALLOW LIST. Anything the product grows later is DENIED to an agent until
/// somebody deliberately adds it here - the opposite of a deny list, where every new dangerous route is open
/// until someone remembers to close it. A pure function on the method and path, so there is exactly one place
/// this rule lives and it is unit-testable without a server.
///
/// THE LINE IT DRAWS: AN AGENT MAY CHANGE HOW THE PRODUCT BEHAVES. IT MAY NOT CHANGE WHO IS ALLOWED IN.
/// That is the owner's ruling of 2026-08-03, and it is the single sentence to reason from when a new route
/// has to be classified. The point of running agents is not to have to use the interface for most things:
/// once the agents are set up, the owner maintains and CONFIGURES through them. So configuration is an
/// agent's work, and admission is not.
///
/// BEHAVES - allowed. The FLEET WORK an agent's command line does: see the roster, find repositories and
/// worktrees and machines, read a session's terminal, queue a message for / hold / rename another session,
/// spawn one, take a mission or a role, mark itself done, and read and publish the fleet's shared skills and
/// workflows. It may also END a session outright - see the paragraph on stopping below. Plus CONFIGURATION,
/// in both directions: a Director's settings, the application's own settings
/// (the closed <c>/gateway</c> set in <see cref="IsApplicationSetting"/>), and handovers - which are content
/// agents produce, and which moving a session needs.
///
/// WHO IS ALLOWED IN - refused. Device registration, enrollment and revocation, because a credential that
/// can admit a NEW device is not configuring the product, it IS the boundary, and an agent holding one could
/// mint itself an account-wide credential and step straight out of this guard. Account-level identity:
/// sign-in and sign-out, ownership, billing, credits and subscription. Director registration and
/// de-registration. Force-killing a Director or shutting the Gateway down - ending ONE session is an agent's
/// work and is allowed (see the stopping paragraph below), but emptying a MACHINE of every session on it at
/// once is not the same act and stays refused. Also still refused, for the separate reason that they are not
/// configuration at all: the
/// diagnostics surface, and voice, dictation and transcription DATA - note the distinction from the voice
/// SETTINGS above, which say how the product should behave and are therefore allowed.
///
/// TYPING INTO A SESSION IS NOT BEHAVING, IT IS THE OWNER'S (the Message Load mission, 16 September 2026).
/// Prompt, interrupt, escape and the raw fan-out were on the allowed side until then. Every one of them put
/// keystrokes into a session mid-turn, and the owner ordered that stopped: an agent now reaches another
/// session only through a queued message, and only a session it started or the one that started it. These
/// four are refused by <see cref="IsAgentInput"/> with a sentence that says so, and so is the fifth found by the
/// first inspection of that mission: answering a judged stop, which types the verdict's chosen option into the
/// session exactly as a prompt would.
///
/// WHAT CHANGED, AND WHY THIS PARAGRAPH WAS REWRITTEN RATHER THAN AMENDED. Phase 1b refused the whole
/// <c>/directors</c> surface bar two sub-paths, and said so here in prose. The owner's ruling reverses that
/// for settings and handovers. The old sentence was not edited around, because an allow list whose stated
/// reasoning contradicts its contents is worse than a wrong entry: a wrong entry is visible in the code,
/// whereas prose that no longer describes the list is trusted by the next reader and quietly propagated.
///
/// STOPPING A SESSION, AND WHY THE LIST NOW SAYS TWO DIFFERENT THINGS ABOUT ONE ACT. This paragraph replaces
/// the sentence that used to say an agent's clean way to end a session was <c>request-deletion</c> - that was
/// true when the flag was the only thing an agent could reach, and it is no longer the whole truth. The
/// owner's ruling of 8 September 2026 (mission "Stop a session") is that any session may stop any other in
/// the same account, with no parent-child restriction, BECAUSE THE STOP CARRIES A REASON AND IS AUDITED. So:
///
///   POST /sessions/{sid}/stop            ALLOWED - the route requires a reason and records it.
///   DELETE /sessions/{sid}/request-deletion  ALLOWED - a flag comes off the way it went on (Ruling 6).
///   DELETE /sessions/{sid}               REFUSED - and this refusal is what makes the ruling exact.
///
/// The bare DELETE is the SAME stop behind a second door, kept because a phone client that does not deploy
/// with the Gateway calls it - but it carries no body, so it can carry no reason. Leaving it refused to a
/// session key is the whole of "an agent's key can only ever stop a session with a reason attached": the
/// door that cannot carry a reason is the door an agent cannot open. Do not add it here as a convenience.
///
/// A NOTE ON WHAT IS DELIBERATELY IN. Spawning a session and launching an application on a machine are both
/// CODE EXECUTION on a computer, and both are allowed here - because both are what the fleet's agents do all
/// day through the command line today, and phase 1b is a credential change, not a capability change.
/// Narrowing them is a product decision for the owner, not a decision to smuggle in behind a refactor. What
/// this guard DOES add over today is a boundary: the key is bound to one session and one tenant, so those
/// verbs can only ever act inside the account that issued it.
/// </summary>
public static class SessionKeyGuard
{
    /// <summary>
    /// Decide whether a session key may make this request. <paramref name="path"/> is the request path with
    /// no query string; the query is deliberately not consulted, because a rule that depends on a query
    /// parameter is a rule a caller can move.
    /// </summary>
    public static SessionKeyVerdict Check(string? method, string? path)
    {
        var verb = (method ?? "").ToUpperInvariant();
        var p = (path ?? "").TrimEnd('/');
        if (p.Length == 0) p = "/";

        // Matched lower-cased, because ASP.NET routing matches a path case-insensitively and a guard that
        // did not would be bypassed by /Sessions. Only the STRUCTURE is compared - the identifier segments
        // ({sid}, {id}) are never read here - so folding their case cannot change a decision.
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
            segments[i] = segments[i].ToLowerInvariant();

        // NO AGENT TYPES INTO A SESSION (the Message Load mission, ruling 17). Checked before the allow list and
        // refused with its own sentence, because an agent told only "may not call POST /sessions/x/prompt" does
        // not learn that a queued message is what it should send instead.
        if (IsAgentInput(verb, segments))
            return SessionKeyVerdict.Refuse(AgentInputRefusal.Typing);

        if (IsAllowed(verb, segments))
            return SessionKeyVerdict.Allow;

        return SessionKeyVerdict.Refuse(
            $"a session key may not call {verb} {p}; it may run the fleet's agent routes and configure the " +
            "product - settings and handovers - but never the admission surface: device enrollment, account " +
            "identity, Director registration, or force-killing a Director");
    }

    /// <summary>
    /// The routes that put keystrokes into a running session: a raw prompt, Ctrl+C, Escape, and the fan-out
    /// that sends one prompt to many sessions. All four were open to a session key until the Message Load
    /// mission, and each is a way round the inbox - a gate with a side door is not a gate. The owner's own
    /// screens reach them with a device key, which this guard never sees, so they are unchanged for him.
    ///
    /// ANSWERING A JUDGED STOP is the fifth (inspection 1 of the Message Load mission, ruling 1). The route sends
    /// the verdict's chosen option into the session through the same prompt channel, so an agent that could call
    /// it could type into another session whenever a verdict was live. It is matched as its one literal
    /// four-segment shape; the feedback route beside it writes only our own record and stays allowed.
    ///
    /// Compact-and-continue is the sixth such path; it shares its route with a plain compaction, so the route
    /// refuses it where it can read the body.
    /// </summary>
    private static bool IsAgentInput(string verb, string[] s)
    {
        if (verb != "POST") return false;
        if (s.Length == 3 && s[0] == "sessions" && s[2] is "prompt" or "interrupt" or "escape") return true;
        if (s.Length == 4 && s[0] == "sessions" && s[2] == "turn-verdict" && s[3] == "answer") return true;
        return s.Length == 1 && s[0] == "fanout";
    }

    private static bool IsAllowed(string verb, string[] s)
    {
        // ---------- Reads ----------
        if (verb is "GET" or "HEAD")
        {
            switch (Join(s))
            {
                // Liveness, and the discovery set the fleet preamble and the read verbs need to orient an
                // agent: who is running, where, in which repositories and worktrees, on which machines.
                case "healthz":
                case "sessions":
                case "repositories":
                case "worktrees":
                case "directors":
                case "launchers":
                case "machines":
                case "missions":
                case "gateway/about":
                // What the session colours mean - the same words the Cockpit and the phone show. An agent that
                // can read a roster should be able to say what a row's colour means rather than guess it, and
                // the answer is the same for every account: it reads no tenant's data at all.
                case "gateway/session-colours":
                case "gateway/snooze-presets":
                case "gateway/skills":
                case "gateway/workflows":
                case "gateway/workflow-runs":
                case "cron/jobs":
                // Every restart request in the account, so an agent can see what is pending before it
                // asks. The same class of read as the machine-scoped list below, one level up.
                case "gateway/director-restart-requests":
                // The calling session's OWN message inbox (the Message Load mission). There is no session id in
                // the path: the route reads the inbox of the key that called it and no other.
                case "fleet/inbox":
                    return true;
            }

            // One session (the roster row) and its terminal scrollback. The buffer carries whatever any agent
            // typed, so it is account-scoped by the key's tenant - a session key can only ever read a session
            // inside its own account, which the tenant binding enforces before the handler runs.
            if (s.Length == 2 && s[0] == "sessions") return true;
            if (s.Length == 3 && s[0] == "sessions" && s[2] == "buffer") return true;

            // A session's parsed conversation history - what `cc-history` reads. Exactly the same class of
            // read as the buffer beside it: one session's own output, inside the caller's own account, and
            // bounded by the same tenant binding. It is listed separately rather than folded in because an
            // allow list that widens by pattern stops being an allow list.
            if (s.Length == 3 && s[0] == "sessions" && s[2] == "history") return true;

            // What the Wingman said this session's stops MEAN - the latest judged stop, and the history of
            // them. The same class of read again: one session's own record, inside the caller's own
            // account. Listed as two literals rather than one prefix, for the reason above.
            //
            // NOTE WHAT THIS DOES NOT DECIDE. Allowing the PATH here is not allowing the ANSWER: while an
            // account's colour switch is off its verdicts are a shadow record, and the route itself refuses
            // a session key and serves only a device key. A guard is a pure function on a method and a
            // path, so it cannot see a tenant's settings; the two halves add up at the route.
            if (s.Length == 3 && s[0] == "sessions"
                && (s[2] == "turn-verdict" || s[2] == "turn-verdicts")) return true;

            // A session's OWN dev reports (issue #2958): the list and one report, which the agent reads to see
            // the owner's notes and their state. The route itself refuses a session key naming any session but
            // its own, and a report of another session; the guard cannot read an identifier, so it cannot.
            // The OWNER's reads under /dev-reports are deliberately NOT here: a session key is never the owner.
            if (s.Length == 3 && s[0] == "sessions" && s[2] == "dev-reports") return true;
            if (s.Length == 4 && s[0] == "sessions" && s[2] == "dev-reports") return true;

            // One mission, one workflow run. Scheduled jobs are handled by IsScheduleRoute below, which
            // owns every /cron shape in one place rather than splitting the reads away from the writes.
            if (s.Length == 2 && s[0] == "missions") return true;
            if (s.Length == 3 && s[0] == "gateway" && s[1] == "workflow-runs") return true;
            if (IsScheduleRoute(verb, s)) return true;

            // What is installed on another machine, and which files it can see - the "start something over
            // there" discovery pair. Reads only; the start itself is a POST below.
            //
            // restart-capability joins them: CAN this machine complete a Director restart? Issue #2720.
            // It is the safest read on this surface - it sends no command, opens no connection and raises
            // no signal - and it is the one an agent must be able to ask, because the alternative is the
            // 2026-09-06 failure: drain seventeen sessions, then discover the answer was no. It is also
            // what makes the admission guard on POST .../director/restart affordable. That verb stays
            // refused to a session key; asking whether it COULD work is not asking to run it, and an
            // agent that can only act blindly is the argument for widening the guard itself.
            if (s.Length == 3 && s[0] == "machines"
                && (s[2] == "apps" || s[2] == "files" || s[2] == "restart-capability")) return true;

            // A session may read the restart REQUESTS on a machine, and one request by id - its own, or
            // the one already pending that refused it. Reads of a record the session could create.
            if (IsRestartRequestRead(s)) return true;

            // The fleet's shared skills and workflows: the catalogue entry, its body/instructions, and its
            // version history. This is how an agent reads a capability the fleet holds centrally.
            if (IsCatalogueRead(s)) return true;

            // One workspace. See IsWorkspaceRoute for why a session key writes these at all.
            if (IsWorkspaceRoute(verb, s)) return true;

            // The automation browsers on one Director's machine. See IsBrowserRoute for how the /directors
            // surface is split between configuration and admission.
            if (IsBrowserRoute(verb, s)) return true;

            // The Fleet Manager's stored news, preferences and digest. See IsFleetManagerRoute.
            if (IsFleetManagerRoute(verb, s)) return true;

            // Configuration, read side. A Director's settings, the application's own settings, and the
            // handovers on a Director - all three by the owner's ruling that an agent configures the product.
            if (IsDirectorSettings(s)) return true;
            if (IsApplicationSetting(s)) return true;
            if (IsHandoverRead(s)) return true;

            return false;
        }

        // ---------- Fleet actions ----------
        if (verb == "POST")
        {
            // Act on a session: queue it a message, park it, give it a role or a mission, ask it to compact,
            // or flag it finished. Every one of these names a session in the path, and the tenant binding keeps
            // it inside the calling account. Typing into it - prompt, interrupt, escape - is refused above.
            if (s.Length == 3 && s[0] == "sessions")
            {
                switch (s[2])
                {
                    // A queued message into another session's inbox. Nothing is typed; who may be written to,
                    // and how often, is the route's ruling (FleetMessagePolicy), because it needs the roster.
                    case "message":
                    case "hold":
                    case "role":
                    case "mission":
                    case "request-deletion":
                    case "compact-context":
                    // RAISE A HAND to your supervisor (`session raise`, issue #2662). It was missing from this list,
                    // so every agent's raise was refused, and it is the report channel of the Message Load mission
                    // (inspection 1, ruling 3). A key may raise only its OWN hand; the route checks that, because a
                    // guard never reads the id segment.
                    case "needs-manager":
                    // STOP a session, now (mission "Stop a session", Ruling 4: any session may stop any
                    // other in the same account, and there is no parent-child restriction). The reason is
                    // what makes this affordable, and the reason requirement lives on the ROUTE, not here:
                    // a guard is a pure function on a method and a path and cannot see a body. See the
                    // refusal of the bare DELETE /sessions/{sid} below for how the two halves add up.
                    case "stop":
                    // PUBLISH a dev report for this session (issue #2958). Its own session only - the route refuses
                    // any other id - and the owner's send, list and read under /dev-reports stay refused to every
                    // session key, because a session key is never the owner.
                    case "dev-reports":
                        return true;
                }
                return false;
            }

            // ANSWERING a judged stop is NOT here: it types the verdict's option into the session, so it is agent
            // input and IsAgentInput refuses it above (the Message Load mission, inspection 1). It was on this list
            // from the Wingman-on-every-turn mission, slice E, until 16 September 2026.

            // REPORT a judged stop wrong (slice G). Its own literal four-segment shape, rather than a
            // "turn-verdict/{anything}" prefix, for the reason the reads are listed as two literals: an allow
            // list that widens by pattern stops being an allow list, and the next word hung off this path has to
            // be classified here before anything can reach it.
            //
            // It writes one row of our own record and reaches nothing outside the Gateway - no bytes, no screen,
            // no session. Whether the route SERVES a session key is
            // still the route's decision and not this one: while an account's colours are off its verdicts are a
            // shadow record and the route refuses a session key, exactly as the reads do. A guard is a pure
            // function on a method and a path and cannot see a tenant's settings; the two halves add up there.
            if (s.Length == 4 && s[0] == "sessions" && s[2] == "turn-verdict" && s[3] == "feedback") return true;

            // REPLY on one of this session's own dev reports (issue #2958). One literal five-segment shape; the
            // route checks the session and the report are the caller's own.
            if (s.Length == 5 && s[0] == "sessions" && s[2] == "dev-reports" && s[4] == "replies") return true;

            // THE ADMINISTRATOR READ OF THOSE CORRECTIONS IS A DIFFERENT SURFACE AND IS NOT HERE:
            // GET /gateway/admin/turn-verdict-feedback serves the daily corpus pull, which is a server with no
            // device key, and it is gated on the administrator service token by the endpoint itself - the same
            // gate the administrator turn-log switch and account lookup carry. It is named here rather than left
            // to the default deny so this census says which administrator routes exist and that a session key
            // reaches none of them; SessionKeyGuardTests asserts the refusal by name. A session key that could
            // call it would read every account's corrections, which is the opposite of what a session credential
            // is for.

            // A queued message to each of the agent's own workers ("message send all"). Working out who they are
            // is the Gateway's ruling to make, not the caller's. The raw /fanout beside it types into sessions
            // and is refused above.
            if (Join(s) == "fleet/broadcast") return true;

            // Create a mission - the unit of work sessions attach to.
            if (Join(s) == "missions") return true;

            // Start a session, or an application, on a machine in this account.
            if (s.Length == 3 && s[0] == "machines" && (s[2] == "sessions" || s[2] == "launch")) return true;

            // ASK for a Director restart - issue #2725, and the line it draws. A session may CREATE a
            // restart request: a pending record that restarts nothing and grants nothing, which the
            // Gateway scrutinises and the owner then accepts or declines. The ACCEPT, the DECLINE and the
            // Director's REPORT on that record are NOT here, deliberately: the accept is the admission
            // decision, and it stays on the surface that already holds the direct restart, which this
            // guard refuses to a session key a few lines down and keeps refusing. Matched at length
            // exactly, so /accept and /decline hung off the same path never match by accident.
            if (IsRestartRequestCreate(s)) return true;

            // Start a session on ONE named Director. The same verb as the machine route above, addressed
            // precisely rather than to "some Director over there" - which is the only way to be specific on
            // a machine running several instances, and how a session spawns on its OWN Director now that
            // there is no loopback port meaning "here". No wider than what is already allowed: the machine
            // route would reach the same Director, it just would not let the caller say which.
            if (s.Length == 3 && s[0] == "directors" && s[2] == "sessions") return true;

            // Contribute to the fleet's shared skills and workflows: create one, publish it, clone one.
            if (IsCatalogueWrite(verb, s)) return true;

            // Capture a Director's live fleet into a workspace - the first act of a drain.
            if (IsWorkspaceRoute(verb, s)) return true;

            // File a Fleet Manager record, answer one, keep a standing preference.
            if (IsFleetManagerRoute(verb, s)) return true;

            // Create a scheduled job, or run one now.
            if (IsScheduleRoute(verb, s)) return true;

            // Create, start, stop, sign in to or rename an automation browser.
            if (IsBrowserRoute(verb, s)) return true;

            // Write a handover. This is the one that makes moving a session possible without the owner
            // opening the interface, which is the whole point of the ruling.
            if (IsHandoverWrite(s)) return true;

            return false;
        }

        // ---------- Configuration and update writes ----------
        // Every PUT is listed explicitly. Opening the verb, or opening /gateway by prefix, is what would stop
        // this being an allow list: a PUT arriving on a route added next year must be refused until somebody
        // classifies it, and a prefix rule would have handed over /gateway/skills/{id}/disable on day one.
        if (verb == "PUT")
        {
            if (IsDirectorSettings(s)) return true;
            if (IsApplicationSetting(s)) return true;

            // Update a skill or workflow draft, and update a scheduled job. Both were refused until the
            // inspection found that the shipped command line has always sent PUT here.
            if (IsCatalogueWrite(verb, s)) return true;
            if (IsScheduleRoute(verb, s)) return true;

            // Write a workspace - including writing the drain's judgments and, afterwards, what the
            // restart actually produced, back onto a captured one.
            if (IsWorkspaceRoute(verb, s)) return true;
            return false;
        }

        // Rename a session (PATCH /sessions/{sid}).
        if (verb == "PATCH" && s.Length == 2 && s[0] == "sessions") return true;

        // Change a mission a session key can already CREATE and READ: its name, its WHY, and whether it
        // is still live (complete / removed / reopened). PATCH /missions/{mid}.
        //
        // This sits exactly alongside the session rename above, and for the same reason: a mission is the
        // unit of work an agent is on, and an agent that can open one but can never rename it or END it
        // leaves the mission list growing forever - which is the state the fleet was found in, eleven
        // missions with several finished days earlier and no way out.
        //
        // No wider than what a session key already has. POST /missions (create) and GET /missions (read)
        // are both allowed above; this is the third verb on the same record, resolved inside the caller's
        // own tenant by the route itself, which answers 404 for another account's mission exactly as the
        // read does. It is nowhere near the admission surface this guard exists to protect - device
        // enrollment, account identity, Director registration.
        if (verb == "PATCH" && s.Length == 2 && s[0] == "missions") return true;

        if (verb == "DELETE")
        {
            // CLEAR a pending deletion flag - DELETE /sessions/{sid}/request-deletion (mission "Stop a
            // session", Ruling 6). A flag comes off the way it went on: POST /sessions/{sid}/request-deletion
            // is allowed above, and an agent that can set the flag but can never take it back leaves the only
            // remedy for a mis-typed session identifier as stopping that session outright, which is the most
            // destructive act the product has. No reason is required to clear a flag; this is the safe
            // direction.
            //
            // Matched by STRUCTURE and at EXACT LENGTH, the way the rest of this file matches: three segments,
            // /sessions/{sid}/request-deletion and nothing deeper. THE BARE DELETE /sessions/{sid} IS NOT
            // MATCHED HERE AND STAYS REFUSED - see the class note.
            if (s.Length == 3 && s[0] == "sessions" && s[2] == "request-deletion") return true;

            // Delete an automation browser.
            if (IsBrowserRoute(verb, s)) return true;

            // Delete a skill, a workflow, or a scheduled job - the same contribute-and-maintain surface as
            // the creates above.
            if (IsCatalogueWrite(verb, s)) return true;
            if (IsScheduleRoute(verb, s)) return true;

            // Delete a handover. Beyond the literal words of the owner's ruling, which named handovers as
            // content an agent produces without saying who may remove one - and recorded as a judgement call
            // in PHASE-2B-REPORT.md rather than slipped in. It is allowed because it is the same class as the
            // POST that created it: removing a handover changes neither who is admitted nor what identity the
            // account has, and an agent that can write handovers but must ask the owner to tidy them up is
            // exactly the trip back to the interface the ruling exists to remove.
            if (IsHandoverWrite(s)) return true;

            // Delete a workspace, on the same terms as the skills and workflows beside it.
            if (IsWorkspaceRoute(verb, s)) return true;

            // Forget a Fleet Manager standing preference.
            if (IsFleetManagerRoute(verb, s)) return true;

            return false;
        }

        return false;
    }

    /// <summary>
    /// The Fleet Manager shapes under <c>/gateway/fleet-manager</c> (the Fleet Manager mission, step 3): the
    /// stored news (Ready, Finding, Decision), the owner's standing preferences, and the digest.
    ///
    /// A session key reaches these because the Fleet Manager IS a session: it files a record the moment
    /// something is ready, answers it with the owner's words, and reads the digest at the start of every
    /// conversation, all with its own key. Each route resolves the caller's account itself, so a record of
    /// another account is not found. Nothing here admits anyone or changes an account's identity.
    ///
    /// Every shape is matched as a LITERAL at its exact length, verb by verb - including the answer verb, a
    /// literal fifth segment - so a new word hung off this prefix is refused until somebody classifies it here.
    /// </summary>
    private static bool IsFleetManagerRoute(string verb, string[] s)
    {
        if (s.Length < 3 || s[0] != "gateway" || s[1] != "fleet-manager") return false;
        var read = verb is "GET" or "HEAD";

        switch (s[2])
        {
            case "outcomes":
                // /gateway/fleet-manager/outcomes - list, or file one.
                if (s.Length == 3) return read || verb == "POST";
                // /gateway/fleet-manager/outcomes/{id} - read one.
                if (s.Length == 4) return read;
                // /gateway/fleet-manager/outcomes/{id}/answer - close one with the owner's words.
                return s.Length == 5 && s[4] == "answer" && verb == "POST";
            case "preferences":
                // /gateway/fleet-manager/preferences - list, or keep one.
                if (s.Length == 3) return read || verb == "POST";
                // /gateway/fleet-manager/preferences/{id} - forget one.
                return s.Length == 4 && verb == "DELETE";
            case "digest":
                return s.Length == 3 && read;
            default:
                return false;
        }
    }

    /// <summary>
    /// The workspace shapes under <c>/gateway/workspaces</c> (issue #2722): list, read one, CAPTURE one
    /// from a running Director (POST), store one (PUT), delete one.
    ///
    /// A session key may write these because a SESSION drives a drain. The whole point of the object is
    /// that a session, on another machine, empties a Director and writes down what was running so the
    /// fleet can be brought back; a guard that let an agent read workspaces but never write one would
    /// leave the record to a human at the keyboard of the machine being restarted, which is the one
    /// person the exercise exists to spare.
    ///
    /// It is nowhere near the admission surface this guard protects. A workspace decides nothing about
    /// who is admitted and nothing about account identity: it is a document, resolved inside the
    /// caller's own tenant, describing seats. Restarting the Director it describes is a different route
    /// entirely, and that one stays refused.
    ///
    /// Matched by STRUCTURE, and here is exactly what that does and does not buy. A DEEPER shape -
    /// /gateway/workspaces/{id}/anything - is refused until somebody classifies it. A new THREE-segment
    /// LITERAL, say POST /gateway/workspaces/purge, would be authorized on the day it is mapped, because
    /// this cannot tell a literal segment from an id. So do not add one: put a new verb one level deeper,
    /// or extend this method deliberately. (The skills and workflows families beside it have the same
    /// property; it is written down here rather than left to be discovered.)
    /// </summary>
    private static bool IsWorkspaceRoute(string verb, string[] s)
    {
        if (s.Length < 2 || s[0] != "gateway" || s[1] != "workspaces") return false;

        // /gateway/workspaces - list, or capture a running Director's fleet into a new one.
        if (s.Length == 2) return verb is "GET" or "HEAD" or "POST";

        // /gateway/workspaces/{id} - read, store, delete.
        if (s.Length == 3) return verb is "GET" or "HEAD" or "PUT" or "DELETE";

        return false;
    }

    /// <summary>
    /// The automation-browser shapes under <c>/directors/{id}/browsers</c>.
    ///
    /// HOW <c>/directors</c> IS SPLIT. Four shapes are open to a session key - browsers (here), <c>POST
    /// /directors/{id}/sessions</c>, <c>/directors/{id}/settings</c>, and <c>/directors/{id}/handovers</c> -
    /// and they are open because each is the product BEHAVING, which the owner's ruling puts in an agent's
    /// hands. What stays refused on this surface is admission and lifecycle: <c>POST /directors/register</c>,
    /// <c>DELETE /directors/{id}/registration</c>, and <c>DELETE /directors/{id}</c>, which force-kills the
    /// Director. Registering or de-registering a Director decides who is in the account; force-killing one
    /// takes down EVERY session on that machine at once, and an agent that needs one session to stop has
    /// <c>POST /sessions/{sid}/stop</c>, which names the session and carries a reason.
    /// This sub-path is not Director administration at all: an automation browser is a tool an agent uses,
    /// it was reachable by every agent on the machine before this mission (over the Director's loopback
    /// port, with no credential narrower than the machine secret), and routing it through the Gateway
    /// NARROWS it, because the key is bound to one session inside one account.
    ///
    /// Matched by structure so a new sibling under /directors cannot be reached by accident: the id segment
    /// is never read here, only counted.
    /// </summary>
    private static bool IsBrowserRoute(string verb, string[] s)
    {
        if (s.Length < 3 || s[0] != "directors" || s[2] != "browsers") return false;

        // /directors/{id}/browsers - list, or create.
        if (s.Length == 3) return verb is "GET" or "HEAD" or "POST";

        // /directors/{id}/browsers/{browserId} - DELETE only. There is no GET of a single browser and no
        // POST to the item itself, so neither is authorized.
        if (s.Length == 4) return verb == "DELETE";

        // /directors/{id}/browsers/{browserId}/{verb}. `attach` is a GET that prints the connection lines;
        // the four actions are POSTs.
        if (s.Length == 5)
        {
            if (s[4] == "attach") return verb is "GET" or "HEAD";
            if (s[4] is "start" or "stop" or "signin" or "rename") return verb == "POST";
        }

        return false;
    }

    /// <summary>
    /// <c>POST /machines/{machine}/director/restart-requests</c> exactly - five segments, and the last is
    /// the collection, never an id and never an action. A restart request is a record a session asks the
    /// owner to decide on (issue #2725).
    /// </summary>
    private static bool IsRestartRequestCreate(string[] s)
        => s.Length == 4 && s[0] == "machines" && s[2] == "director" && s[3] == "restart-requests";

    /// <summary>
    /// The read shapes of the restart-request record: the machine's list at
    /// <c>/machines/{machine}/director/restart-requests</c> and one request at <c>.../{id}</c>. Length is
    /// matched exactly on both, so <c>.../{id}/accept</c> - a POST anyway - is not a read.
    /// </summary>
    private static bool IsRestartRequestRead(string[] s)
    {
        if (s.Length < 4 || s[0] != "machines" || s[2] != "director" || s[3] != "restart-requests") return false;
        return s.Length == 4 || s.Length == 5;
    }

    /// <summary>
    /// One Director's settings: <c>/directors/{id}/settings</c>, read with GET and written with PUT.
    ///
    /// Matched by structure, and by length exactly 3, so that a sub-path somebody hangs off settings later -
    /// <c>/directors/{id}/settings/credentials</c>, say - is refused until it is classified here on purpose.
    /// </summary>
    private static bool IsDirectorSettings(string[] s)
        => s.Length == 3 && s[0] == "directors" && s[2] == "settings";

    /// <summary>
    /// The application's own settings - the closed <c>/gateway</c> set the Settings page writes.
    ///
    /// Every one of these says how the product should BEHAVE: when it reports, how long a snooze lasts, which
    /// time zone and which model, which voice and which language it speaks, what text it injects, how it
    /// transcribes. None of them says who may sign in or which devices are admitted, which is why the whole
    /// set moves together under the owner's ruling.
    ///
    /// NOTE THE DISTINCTION THAT MATTERS: the voice, language and transcription entries here are SETTINGS,
    /// not DATA. Recordings, dictation audio and transcripts remain refused - a knob saying how to transcribe
    /// is configuration, whereas what was actually said into the microphone is the owner's.
    ///
    /// Written as a literal list rather than a <c>/gateway</c> prefix on purpose. A prefix would have opened
    /// the catalogue enable/disable routes that sit under the same segment and are deliberately refused, and
    /// it would silently swallow every future <c>/gateway</c> route on the day it was added.
    /// </summary>
    private static bool IsApplicationSetting(string[] s) => Join(s) switch
    {
        "gateway/settings" => true,
        "gateway/daily-report" => true,
        "gateway/snooze-default" => true,
        "gateway/snooze-presets" => true,
        "gateway/time-zone" => true,
        "gateway/ai-provider" => true,
        "gateway/tts-voice" => true,
        "gateway/spoken-language" => true,
        "gateway/spoken-language/voice" => true,
        "gateway/injected-text" => true,
        "gateway/transcription-mode" => true,
        // The Wingman's turn judging: whether this account's stops are judged at all, and whether the
        // verdicts reach its screens. Both say how the product should BEHAVE - what it looks at and what
        // colour it paints a row - and neither says who may sign in or which devices are admitted, which
        // is the line this whole set is drawn on.
        "gateway/turn-verdict-judge" => true,
        "gateway/turn-verdict-colour" => true,
        // Which session is the account's Fleet Manager. It says which session's Workers the Wingman judges -
        // how the product behaves - and admits nobody: the mark names a session already inside the account.
        // The Fleet Manager sets it through the command line, so a session key must reach it.
        "gateway/fleet-manager" => true,
        _ => false,
    };

    /// <summary>
    /// Reading handovers on a Director: the list at <c>/directors/{id}/handovers</c>, and one document's text
    /// at <c>/directors/{id}/handovers/content</c>. The document is named by query string, which this guard
    /// deliberately never consults - see <see cref="Check"/>.
    /// </summary>
    private static bool IsHandoverRead(string[] s)
    {
        if (s.Length < 3 || s[0] != "directors" || s[2] != "handovers") return false;
        return s.Length == 3 || (s.Length == 4 && s[3] == "content");
    }

    /// <summary>Creating or removing a handover, both at <c>/directors/{id}/handovers</c> exactly.</summary>
    private static bool IsHandoverWrite(string[] s)
        => s.Length == 3 && s[0] == "directors" && s[2] == "handovers";

    /// <summary>The read shapes of the shared skill/workflow catalogue.</summary>
    private static bool IsCatalogueRead(string[] s)
    {
        if (s.Length < 3 || s[0] != "gateway") return false;
        if (s[1] != "skills" && s[1] != "workflows") return false;

        // /gateway/{skills|workflows}/{id}
        if (s.Length == 3) return true;

        // /gateway/{skills|workflows}/{id}/{body|instructions|versions}
        if (s.Length == 4)
            return s[3] is "body" or "instructions" or "versions";

        // /gateway/{skills|workflows}/{id}/versions/{version}
        return s.Length == 5 && s[3] == "versions";
    }

    /// <summary>
    /// The write shapes of the shared skill/workflow catalogue, matched on METHOD AND PATH TOGETHER
    /// against the routes the Gateway actually maps in <c>SkillEndpoints</c> and <c>WorkflowEndpoints</c>.
    ///
    /// THIS METHOD IS WHY AN ALLOW LIST MUST BE BUILT FROM THE ROUTE TABLE, NOT FROM MEMORY. Its previous
    /// form allowed only four-segment <c>POST /gateway/{kind}/{id}/{draft|publish|clone}</c>, so the two
    /// verbs the shipped command line uses most - <c>POST /gateway/skills</c> to create and
    /// <c>PUT /gateway/skills/{id}/draft</c> to update - were refused with 403 on every call. The guard's
    /// own test did not catch it because it pinned <c>POST .../draft</c>, a route that does not exist: the
    /// test agreed with the guard about a shape neither the client nor the server ever uses, so both were
    /// consistently wrong and green. A test written from the guard tests the guard against itself.
    ///
    /// STILL REFUSED: <c>enable</c> and <c>disable</c>. Turning a fleet-wide capability off for everyone is
    /// an owner's decision, and it is the one catalogue verb that is not an agent contributing work.
    /// </summary>
    private static bool IsCatalogueWrite(string verb, string[] s)
    {
        if (s.Length < 2 || s[0] != "gateway") return false;
        if (s[1] != "skills" && s[1] != "workflows") return false;

        // POST /gateway/{skills|workflows} - create.
        if (s.Length == 2) return verb == "POST";

        // DELETE /gateway/{skills|workflows}/{id} - remove one the agent (or the fleet) no longer wants.
        if (s.Length == 3) return verb == "DELETE";

        if (s.Length == 4)
        {
            // PUT .../{id}/draft - save a draft. The client's update verb, and previously refused.
            if (s[3] == "draft") return verb == "PUT";

            // POST .../{id}/{publish|clone}.
            if (s[3] is "publish" or "clone") return verb == "POST";
        }

        return false;
    }

    /// <summary>
    /// The scheduled-job surface, matched on method and path together against <c>CronJobEndpoints</c> and
    /// <c>CronRunEndpoints</c>.
    ///
    /// Same defect as the catalogue above and found by the same inspection: the guard allowed the two GETs
    /// and nothing else, so every schedule command except <c>schedule list</c> returned 403. A schedule is
    /// configuration - it says what the product should do and when - so it falls on the allowed side of the
    /// owner's line, and running one now is no more than doing by hand what the schedule does anyway.
    /// </summary>
    private static bool IsScheduleRoute(string verb, string[] s)
    {
        if (s.Length < 2 || s[0] != "cron" || s[1] != "jobs") return false;

        // /cron/jobs - list, or create.
        if (s.Length == 2) return verb is "GET" or "HEAD" or "POST";

        // /cron/jobs/{id} - read, update, delete.
        if (s.Length == 3) return verb is "GET" or "HEAD" or "PUT" or "DELETE";

        // /cron/jobs/{id}/run - run it now. /cron/jobs/{id}/runs - its history.
        if (s.Length == 4)
        {
            if (s[3] == "run") return verb == "POST";
            if (s[3] == "runs") return verb is "GET" or "HEAD";
        }

        return false;
    }

    private static string Join(string[] segments) => string.Join('/', segments);
}
