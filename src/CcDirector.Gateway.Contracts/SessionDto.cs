namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Describes a single agent session (Claude/Pi/Codex/Gemini) running inside a Director.
/// Returned by /sessions on both the Director Control API and the Gateway.
/// </summary>
public sealed class SessionDto
{
    /// <summary>CC Director's internal session GUID. Stable for the life of the session.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Which Director owns this session. Empty in Director-local responses.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>Agent tool kind: ClaudeCode, Pi, Codex, Gemini, OpenCode, Cursor, Grok, Copilot, RawCli.</summary>
    public string Agent { get; set; } = "";

    /// <summary>
    /// Gateway-folded display name for the running agent tool, such as <c>Claude Code</c>, <c>Pi</c>, or
    /// <c>Codex</c>. Browser clients render this verbatim and never infer the tool from
    /// <see cref="CurrentModel"/>. Empty in Director-local responses; the Gateway stamps every served
    /// roster row through <see cref="AgentToolDisplayFold"/>.
    /// </summary>
    public string AgentToolDisplay { get; set; } = "";

    /// <summary>Group identity (issue #225) when this session belongs to a group; null for
    /// solo sessions. Lets a by-repo / fleet view keep group members adjacent.</summary>
    public string? GroupId { get; set; }

    /// <summary>The session's role within its group (issue #225); null for solo sessions.</summary>
    public string? GroupRole { get; set; }

    /// <summary>Repository / working directory.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>Process lifecycle status: Starting / Running / Exiting / Exited / Failed.</summary>
    public string Status { get; set; } = "";

    /// <summary>Cognitive activity state: Starting / Idle / Working / WaitingForInput / WaitingForPerm / Exited.
    /// This is the RAW state (issue #186 two-owner model): owned by the Director's dumb
    /// quiet-timer detector, purely mechanical, written by nothing else.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>
    /// The ASSESSED state (issue #186 two-owner model): owned by the GATEWAY, whose brain
    /// reads the transcript after a turn end and can refute the mechanical quiet signal
    /// (e.g. quiet but nothing needs you -> "Idle"). Null when no assessment stands - new
    /// PTY bytes auto-invalidate it. UIs display AssessedState ?? ActivityState. On a
    /// Director's own API this carries the display annotation the Gateway pushed down;
    /// it is NEVER fed into the detector and never re-pushed.
    /// </summary>
    public string? AssessedState { get; set; }

    /// <summary>UTC timestamp the session was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Total bytes the terminal buffer has accumulated since session start. Use as a cursor in /buffer?since=. </summary>
    public long TotalBufferBytes { get; set; }

    /// <summary>
    /// True when the agent currently has the terminal in the alternate screen buffer
    /// (full screen mode). While true, local scrollback is empty and terminal history
    /// capture does not work, so classify the session as full screen and use a transcript
    /// provider or screen reconstruction rather than the scrollback. Reflects the live
    /// terminal state at query time; it flips as the agent enters and leaves full screen.
    /// </summary>
    public bool IsAlternateScreen { get; set; }

    /// <summary>
    /// Claude's current session id, reported by the Claude SessionStart hook and updated across
    /// /clear and compaction (Claude mints a new id on each). Null for non-Claude sessions or
    /// before the first hook fires.
    /// </summary>
    public string? ClaudeSessionId { get; set; }

    /// <summary>
    /// Absolute path to Claude's current transcript .jsonl, reported by the SessionStart hook.
    /// Authoritative across /clear and compaction. Null for non-Claude sessions or before the
    /// first hook fires.
    /// </summary>
    public string? ClaudeTranscriptPath { get; set; }

    /// <summary>Optional friendly name for the session.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The Mission this session is ATTACHED to (see
    /// docs/new_architecture/mission-as-first-class-unit-of-work.md), or null when it is attached to no
    /// Mission. A Mission is its own persisted record (<see cref="MissionDto"/>); this is the attachment
    /// link, stamped at spawn (from <see cref="NewSessionRequest.MissionId"/>) or later via
    /// POST /sessions/{id}/mission. Mirrors <c>Session.MissionId</c> on the owning Director; passes through
    /// the Gateway aggregation unchanged. Null on Directors that predate this field.
    /// </summary>
    public Guid? MissionId { get; set; }

    /// <summary>
    /// The attached Mission's display name, CACHED on the session for at-a-glance rendering so a client
    /// does not have to resolve <see cref="MissionId"/> against the Mission store. Resolved and stamped at
    /// attach time; the Mission record (<see cref="MissionId"/>) remains the source of truth. Null when the
    /// session is attached to no Mission. Mirrors <c>Session.MissionName</c> on the owning Director.
    /// </summary>
    public string? MissionName { get; set; }

    /// <summary>The workflow RUN this session is seated on (Workflows mission, phase 5b - issue
    /// #1771), or null for an unseated session. Stamped at spawn from the Gateway-validated create
    /// request; mirrors <c>Session.WorkflowRunId</c> on the owning Director.</summary>
    public Guid? WorkflowRunId { get; set; }

    /// <summary>The seated run's workflow id (e.g. "mission"), cached beside the run link. Null when
    /// unseated.</summary>
    public string? WorkflowId { get; set; }

    /// <summary>The seated run's PINNED workflow version - the version the session's conduct is
    /// fetched at, never a moving head. Null when unseated.</summary>
    public int? WorkflowVersion { get; set; }

    /// <summary>
    /// The session's short, human-friendly three-digit number (100-999), or null when the session
    /// has no number (issue #820). Allocated by the owning Director at creation and stable for the
    /// life of the session - a separate field from <see cref="Name"/>, so a rename never changes it.
    /// Passes through the Gateway aggregation unchanged. Null on Directors that predate this field.
    /// </summary>
    public int? Number { get; set; }

    /// <summary>
    /// The session's position in its owning Director's list, mirroring
    /// <c>Session.SortOrder</c>. This is the user-controlled desktop order
    /// (set by drag-drop on the desktop and persisted), so a client that sorts
    /// by it keeps each session in a stable slot instead of reshuffling. 0 when
    /// the owning Director predates this field. Populated by the Director.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Aggregate at-a-glance status color, written by the Director's
    /// SessionStatusWingman. The UI renders this verbatim and never derives it
    /// from other fields.
    /// Values: "blue" (agent is working), "red" (needs the user - input/permission/idle),
    /// "yellow" (the Wingman is reading the screen and narrating), "purple" (parked on its
    /// own background task, will resume itself), "supporting" (a SUPERVISED session that has STOPPED -
    /// one that answers to another live session, or that a schedule started - so it parks instead of
    /// asking the owner; issue #815, rendered as a recessive slate #64748B), "unknown"
    /// (process exited, or source unreachable/unparseable - rendered gray). On-hold is separate:
    /// see <see cref="OnHold"/>. ("green" is legacy and no longer emitted.)
    /// </summary>
    public string StatusColor { get; set; } = "unknown";

    /// <summary>
    /// True when the agent process ended UNEXPECTEDLY - a crash (issue #959). Mirrors
    /// <c>Session.Crashed</c> on the owning Director, which decides it from the exit code AND whether the
    /// session dropped out while working (some crashes exit 0, so the code alone is not enough).
    ///
    /// This is a RAW fact and the fold reads it directly. It exists because a crash was NEVER modelled in
    /// <see cref="ActivityState"/> - a crashed session is <c>Exited</c> like any other - so without it the
    /// wire could not tell a crash from a clean exit and every client rendered both as the same grey. That
    /// is precisely what happened: the Director kept setting its deep-red Error colour, and the fold, which
    /// reads only raw facts, had no raw fact to read.
    ///
    /// Deliberately NOT inferred from <see cref="Status"/> == "Failed". The two coincide today only because
    /// the one other writer of Failed (a create-time failure) disposes the session before it ever reaches
    /// the roster. Reading the producer's own fact keeps that coincidence from becoming a dependency.
    /// </summary>
    public bool Crashed { get; set; }

    /// <summary>
    /// Short human-readable reason for the current <see cref="StatusColor"/>
    /// (e.g. "session created", "working", "waiting for input"). Surfaced as a
    /// tooltip on the dot so the user can see WHY the color is what it is.
    /// </summary>
    public string LastStatusReason { get; set; } = "";

    /// <summary>Wingman briefing-pipeline state: "None" | "Briefing" | "Briefed" | "Failed".
    /// Orthogonal to ActivityState (a session can ask AND keep working). "Briefing" renders
    /// the rail's yellow "wingman reading..." chip. Defaults to None on old Directors.</summary>
    public string BriefingState { get; set; } = "None";

    /// <summary>The latest turn brief's needs-you one-liner (&lt;=8 words) for the rail /
    /// voice. Null when nothing is needed or no brief exists.</summary>
    public string? RailLine { get; set; }

    /// <summary>
    /// Issue #218: UTC timestamp the session ENTERED the red / NEEDS-YOU effective state
    /// (<see cref="SessionOrdering.EffectiveColor"/> == "red"). Gateway-owned: stamped by the
    /// Gateway during the /sessions aggregation the first refresh the session presents red,
    /// held stable while it stays red, and cleared to null when it leaves red. Drives the
    /// Cockpit's "how long has this been waiting" triage label. Null whenever the session is
    /// not red (including while the wingman is still briefing/explaining - effective yellow/
    /// orange is not yet waiting time). In-memory only (like AssessedState): a Gateway restart
    /// re-derives it on the next red transition. Always null in Director-local responses.
    /// </summary>
    public DateTime? NeedsYouSince { get; set; }

    /// <summary>
    /// Issue #2576: UTC timestamp this session last found itself a VOICE session with no playable
    /// narration - the moment its wait for voice began. Gateway-owned, stamped during the same
    /// aggregation that folds <see cref="VoiceDisplay"/>, held stable for the whole episode, and
    /// cleared to null the moment audio arrives or the agent starts a new turn.
    ///
    /// It exists because <see cref="NeedsYouSince"/> could not answer this. That clock is stamped
    /// only when the folded colour is RED, and a session waiting for its voice is YELLOW - so the
    /// one clock the product had was, by construction, never running for exactly the sessions that
    /// were stuck. A session sat on "Preparing voice" for forty-eight minutes and no surface could
    /// say so, because no fact recorded when the wait started.
    ///
    /// Clients render the AGE of this, never a duration the Gateway computed: a number computed at
    /// stamp time is wrong by however long it sat in transit and on the screen. In-memory only - a
    /// Gateway restart re-derives it on the next waiting refresh. Always null in Director-local
    /// responses, which have no Gateway voice state at all.
    /// </summary>
    public DateTime? VoiceWaitingSince { get; set; }

    /// <summary>
    /// The absolute UTC time an ARMED snooze returns this session to "needs you" - the snooze clock's
    /// deadline, so a client can show "wakes in 3h 48m". Gateway-owned: the snooze registry
    /// (<c>SnoozeRegistry.SnoozeEntry.SnoozeUntilUtc</c>) is the sole source of the timer, stamped by the
    /// Gateway during the /sessions aggregation and pushed DOWN to the owning Director's display mirror so
    /// the desktop rail can render the hold time without owning the clock. Null when the session carries no
    /// armed snooze: no hold at all, OR a DEFERRED hold that has not landed yet (there is no deadline until
    /// the work ends - defect 20). Always null in Director-local responses that predate the display-state
    /// push. Pairs with <see cref="HoldState"/> = "Held": the state says it is parked, this says until when.
    /// </summary>
    public DateTime? SnoozeUntil { get; set; }

    /// <summary>
    /// When the OWNER last drove a turn on this session - a person typing or speaking - or null if they
    /// never have. Reported by the owning Director; null from a Director too old to send it.
    ///
    /// This is one of exactly TWO facts a Director contributes to the hold machine, the other being
    /// <see cref="ActivityState"/>. The Gateway owns hold and makes every ruling about it, but it cannot
    /// see this one for itself: desktop typing never leaves the Director's machine, and whether a human or
    /// an agent drove a turn is only knowable at the Director's input choke points.
    ///
    /// The Gateway clears a hold when this moves past the moment the hold was asked for. That ruling is
    /// the Gateway's; this field is only the observation it rules on.
    /// </summary>
    public DateTime? LastOwnerTurnAtUtc { get; set; }

    /// <summary>
    /// How many prompts to this session have FAILED to be delivered - the send threw, so the user's words
    /// never reached the agent (issue internal#811). Reported by the owning Director from
    /// <c>PromptDeliveryFailures</c>; 0 from a Director too old to count them. Counts survive a recovery,
    /// so this is the honest "how often does this happen here" number, not a live alarm - that is
    /// <see cref="PromptDeliveryUnresolved"/>.
    /// </summary>
    public int FailedPromptDeliveries { get; set; }

    /// <summary>
    /// How many times this session's composer failed to echo typed text and had to be cleared and retyped.
    /// A miss that recovers costs the user nothing, which is why it is a count and never an alarm - but it
    /// is the leading indicator of the outright failures, and it was invisible outside a log file until
    /// issue internal#811. Reported by the owning Director; 0 from a Director too old to count them.
    /// </summary>
    public int ComposerEchoMisses { get; set; }

    /// <summary>
    /// When the most recent prompt delivery to this session failed, or null if none ever has. Reported by
    /// the owning Director.
    /// </summary>
    public DateTime? LastPromptDeliveryFailureAtUtc { get; set; }

    /// <summary>
    /// Why the most recent delivery failed, in the words the submit protocol used ("the composer never
    /// echoed the typed text after 2 attempts ..."), or null if none ever has. Reported by the owning
    /// Director, one line, length-capped. A client never renders this as the headline - the Gateway's
    /// <see cref="PromptDeliveryNotice"/> is the headline - but it is the detail behind it.
    /// </summary>
    public string? LastPromptDeliveryFailureReason { get; set; }

    /// <summary>
    /// True when the LAST thing that happened on this session was a failed delivery and nothing has landed
    /// since: the user's words are gone RIGHT NOW. Cleared by the next successful delivery. Reported by the
    /// owning Director, because only the Director can see whether a later send got through.
    ///
    /// This is the fact; the Gateway turns it into <see cref="PromptDeliveryNotice"/>, the words a client
    /// renders.
    /// </summary>
    public bool PromptDeliveryUnresolved { get; set; }

    /// <summary>
    /// Gateway-owned plain-English notice that this session lost a prompt, or null when there is nothing to
    /// say. Folded once by <c>SessionOrdering.PromptDeliveryNotice</c> from the Director-reported facts
    /// above and rendered VERBATIM by every client (CLAUDE.md rule 7). Always null in Director-local
    /// responses, which carry the raw facts and no verdict.
    /// </summary>
    public string? PromptDeliveryNotice { get; set; }

    /// <summary>
    /// Gateway-owned plain-English notice that the session which was DRIVING this one is gone, or null when
    /// there is nothing to say. Folded once by <c>SessionOrdering.SupervisorLostNotice</c> and rendered
    /// VERBATIM by every client (CLAUDE.md rule 7).
    ///
    /// WHY IT EXISTS. Suppressing a supervised session's attention is only safe while it HAS a supervisor.
    /// When that supervisor dies the suppression correctly stops - the role resolves to Standalone, so the
    /// red surfaces and the owner is involved, which is his 2026-07-09 orphan ruling ("ALWAYS involve the
    /// operator" on an exception). But the ruling asked for more than a red dot: "the Manager &lt;name&gt; you
    /// were working for is gone; here is the task; here is where you are stuck". Without that, an orphan is
    /// indistinguishable from an ordinary session wanting attention, and the owner has to open it to find
    /// out it is wreckage rather than work.
    ///
    /// Only the Gateway can produce it: naming the supervisor means looking it up across the WHOLE fleet,
    /// which no single Director can do - the supervisor may be on another machine. Always null in
    /// Director-local responses, which carry raw facts and no verdict.
    /// </summary>
    public string? SupervisorLostNotice { get; set; }

    /// <summary>
    /// Gateway-owned presentation color after all overlays are folded in
    /// (<see cref="SessionOrdering.EffectiveColor"/>): on-hold, transcribing, explaining,
    /// briefing, and voice-generation state. Stamped by the Gateway aggregator so browser
    /// clients do not reimplement the session state machine. Required on Gateway /sessions
    /// responses; Director-local responses may leave it null.
    /// </summary>
    public string? EffectiveColor { get; set; }

    /// <summary>
    /// The pixel HEX for <see cref="EffectiveColor"/>, resolved through the ONE canonical map
    /// (<see cref="SessionColorPalette.HexFor"/>) and stamped beside the name at the same fold point.
    /// This is the whole of the "Dumb Clients" palette slice: a client paints THIS verbatim instead of
    /// carrying its own name-&gt;hex table that can drift from the others. A missing/unparseable value on
    /// a client (an old Gateway that stamps the name but not the hex) renders the loud magenta sentinel
    /// (<see cref="SessionColorPalette.Broken"/>) plus a log - never a guessed colour. Required on Gateway
    /// /sessions responses; Director-local responses may leave it null.
    /// </summary>
    public string? EffectiveColorHex { get; set; }

    /// <summary>
    /// Gateway-owned triage bucket after the same effective-color fold:
    /// "needsYou" | "active" | "onHold". Stamped by the Gateway aggregator as the
    /// authoritative grouping value for mobile/Cockpit rosters. Required on Gateway /sessions
    /// responses; Director-local responses may leave it null.
    /// </summary>
    public string? TriageBucket { get; set; }

    /// <summary>
    /// Gateway-owned human-readable state label after the same fold (issue #1177, Phase 2):
    /// e.g. "Needs you" | "Working" | "Ready" | "Wingman reading" | "Preparing voice" |
    /// "Transcribing" | "Background" | "Snoozed" | "Exited".
    /// ("Explaining" was in this list and was never once emitted - see the tombstone in
    /// <see cref="SessionOrdering.EffectiveColor"/>.)
    /// Computed by <see cref="SessionOrdering.StateLabel"/> from the same raw facts + overlays as
    /// <see cref="EffectiveColor"/>, so every client renders ONE consistent label instead of each
    /// hand-rolling its own. Stamped by the Gateway aggregator; Director-local responses may leave it null.
    /// </summary>
    public string? StateLabel { get; set; }

    /// <summary>Backend type: ConPty / UnixPty / Pipe / Studio / Embedded.</summary>
    public string BackendType { get; set; } = "";

    /// <summary>
    /// The session's agent-driver capability names (e.g. "Cancel", "Interrupt",
    /// "ClearContext", "History", "TranscriptRead"). UIs render action buttons from
    /// this list verbatim - a verb a tool lacks is simply absent, never guessed.
    /// Empty on Directors that predate the driver layer.
    /// </summary>
    public List<string> DriverCapabilities { get; set; } = new();

    /// <summary>
    /// UTC timestamp of the most recent terminal-buffer write. Falls back to
    /// <see cref="CreatedAt"/> if the session has produced no output yet.
    /// Drives the "Idle Xm" freshness column in the Gateway directory view.
    /// </summary>
    public DateTime? LastActivityAt { get; set; }

    /// <summary>
    /// Seconds since the most recent terminal-buffer write -- the raw "time since the last
    /// ConPTY character" idle clock, computed server-side so external callers (e.g. the test
    /// harness) need no clock math. 0 when the session has produced no output yet.
    /// </summary>
    public double IdleSeconds { get; set; }

    /// <summary>
    /// The terminal-state detector's quiet threshold in seconds: how long the terminal must be
    /// COMPLETELY byte-silent before the turn is even a candidate for being over. Paired with
    /// <see cref="IdleSeconds"/> so a test can assert against the live threshold directly.
    /// </summary>
    public double QuietThresholdSeconds { get; set; }

    /// <summary>
    /// Machine name of the Director that owns this session. Populated by the
    /// Gateway aggregator. Empty in Director-local responses.
    /// </summary>
    public string MachineName { get; set; } = "";

    /// <summary>
    /// Epic #1159 step A: whether the machine that owns this session is reachable RIGHT NOW - meaning its
    /// tunnel is up. Stamped by the Gateway's roster read; null in Director-local responses, where the
    /// question is meaningless because the answering Director is the machine.
    ///
    /// It is the TUNNEL, not staleness. A machine whose push is merely late is still reachable: a command
    /// sent to it lands. Keying this to staleness instead would make the badge blink off whenever a push ran
    /// a few seconds behind - the same transient-staleness-destroys-information defect this step exists to
    /// end, just wearing a third disguise. Whether data is current enough to be ACTED on is a different and
    /// stricter question, answered separately inside the roster read and never exposed here.
    ///
    /// This is the Gateway's answer to "may this session nag the human", and it is a Gateway answer on
    /// purpose. The roster now serves the sessions of a machine nobody can reach - dimmed and dated instead
    /// of deleted - and the moment it does, "needs you" splits into two different questions that used to be
    /// one: SHOW it (yes, the work is real and the human should see it) and NAG about it (no, there is
    /// nothing they can do about a laptop that is asleep). A badge lit all night over three red sessions on
    /// a machine nobody can act on is not information, it is noise the owner cannot switch off.
    ///
    /// Every client reads this field rather than deriving it. The reachability list in the roster envelope
    /// carries the same fact per MACHINE, and a client could join the two itself - one of them already did,
    /// for the voice queue - but a join is a rule, and a rule computed in two clients is two rules that
    /// drift. The desktop shell has no such join at all, which is precisely how its badge and the phone's
    /// came to disagree. One field, stamped once, read verbatim.
    /// </summary>
    public bool? MachineReachable { get; set; }

    /// <summary>
    /// User the owning Director is running as. Populated by the Gateway aggregator.
    /// Empty in Director-local responses.
    /// </summary>
    public string User { get; set; } = "";

    /// <summary>
    /// Tailnet-reachable base URL of the owning Director (no trailing slash).
    /// Clients use this to talk directly to the session (prompts, renames, etc.)
    /// without going through the Gateway. Populated by the Gateway aggregator.
    /// Empty in Director-local responses.
    /// </summary>
    public string TailnetEndpoint { get; set; } = "";

    /// <summary>
    /// Full URL of this session's view page on its owning Director.
    /// Format: <c>{TailnetEndpoint}/sessions/{SessionId}/view</c>.
    /// Populated by the Gateway aggregator. Empty in Director-local responses.
    /// </summary>
    public string ViewUrl { get; set; } = "";

    /// <summary>
    /// True when this session is currently in walkie-talkie voice mode (a client is
    /// driving it by voice). The authoritative flag every client reads so the desktop
    /// tile, the HTML view, and the Android roster all agree the session is spoken-to
    /// rather than typed-at. Mirrors <c>Session.VoiceMode</c> on the owning Director.
    /// </summary>
    public bool VoiceMode { get; set; }

    /// <summary>
    /// Where this session sits in the hold ("Snooze") machine - one of the three
    /// <see cref="HoldStates"/> values: <c>None</c>, <c>Held</c>, or <c>DeferredHold</c>. Mirrors
    /// <c>Session.HoldState</c> on the owning Director, which is the only writer.
    ///
    /// THIS IS THE AUTHORITATIVE HOLD FIELD (defect 12, fixed 14 July 2026). It replaced the boolean
    /// <see cref="OnHold"/>, which could not distinguish <c>None</c> from <c>DeferredHold</c> - both
    /// reported false - and so lost the one fact the Gateway's expiry sweep needed. Anything deciding
    /// what to DO about a hold reads this; see <see cref="OnHold"/> for the render-side boolean.
    ///
    /// NULL MEANS "THE DIRECTOR DID NOT SAY", AND THAT IS NOT THE SAME AS <c>None</c>. It defaulted to
    /// <c>None</c> until 15 July 2026, which reintroduced defect 12 across a version skew: an old
    /// Director does not send this field at all, so an absent value deserialized to <c>None</c> - and a
    /// deferred snooze on that Director (which reports only <c>onHold=false</c>) was read by the sweep as
    /// "genuinely not held" and cleared, fifteen seconds after it was asked for. The defect the tri-state
    /// exists to kill, resurrected by a default. A Gateway is routinely newer than the Directors it
    /// serves, so this is an ordinary running state, not a corner case.
    ///
    /// Absent now stays null, <see cref="HoldStates.Normalize"/> maps it to null, and the sweep treats
    /// null as a missed read and changes nothing - which keeps the snooze. Never default this to a real
    /// state: guessing <c>None</c> is what deletes the user's twelve-hour snooze.
    /// </summary>
    public string? HoldState { get; set; }

    /// <summary>
    /// True when the user has parked this session ("deal with this later") -
    /// i.e. it is parked RIGHT NOW. A user override orthogonal to <see cref="ActivityState"/> and
    /// <see cref="StatusColor"/>: the underlying state is still reported truthfully, but the
    /// conductor skips held sessions. The UI renders this verbatim and never derives it.
    ///
    /// DERIVED from <see cref="HoldState"/>, never stored (defect 12): it is exactly
    /// <c>HoldState == Held</c>, so the boolean and the tri-state cannot disagree - which is what went
    /// wrong when they were two independent fields. A <c>DeferredHold</c> reads FALSE here, correctly:
    /// it is not parked yet. That is precisely why nothing that must tell "not held" from "about to be
    /// held" may read this field - the snooze sweep reads <see cref="HoldState"/>.
    ///
    /// It stays on the wire, settable, for backward compatibility: a client that predates
    /// <see cref="HoldState"/> still reads the boolean it always did, and a Director that predates it
    /// sends only the boolean, which the setter maps onto the tri-state (true -&gt; Held, false -&gt;
    /// None). The setter is deliberately idempotent, so a payload carrying BOTH fields lands on the same
    /// state whichever order the deserializer assigns them in.
    /// </summary>
    public bool OnHold
    {
        get => HoldStates.IsHeld(HoldState);
        set
        {
            if (value)
            {
                if (!OnHold) HoldState = HoldStates.Held;
            }
            else
            {
                // Only a LANDED hold is released here. A DeferredHold already reads OnHold=false, so
                // "set false" is a no-op against it - it must not be silently destroyed by a writer
                // that only knows about the boolean (the snooze expiry overlay is one such writer).
                if (OnHold) HoldState = HoldStates.None;
            }
        }
    }

    /// <summary>
    /// Snooze Length mission: display-only marker that this session has just RETURNED from an expired
    /// snooze - its Gateway-owned snooze timer elapsed and the fold put it back into "needs you" on its
    /// own (the dead-man's switch fired). Stamped by the Gateway aggregation overlay while the snooze
    /// registry still holds an expired entry for the session; it is NOT a Director fact and nothing is
    /// injected into the session's conversation. Clients render it as a distinct "Snooze ended" badge so
    /// the owner knows this is a "go investigate why it went quiet" item rather than a fresh turn-end, and
    /// the phone push reads "Snooze ended - still waiting on you" the one time it first appears.
    /// </summary>
    public bool SnoozeExpired { get; set; }

    /// <summary>
    /// True when this session has asked (or been asked) to be deleted and is awaiting the owning
    /// Director's deletion reaper. Set through POST /sessions/{id}/request-deletion; cleared by
    /// DELETE /sessions/{id}/request-deletion during the grace window. Mirrors
    /// <c>Session.PendingDeletion</c>. The common case is an unattended run flagging ITSELF once it has
    /// nothing left for the user, so the session tears itself down instead of lingering as a dead tab.
    ///
    /// THIS IS A BADGE, NEVER A COLOUR (owner's ruling, 14 July 2026, defect 23). Pending deletion says
    /// nothing about what the agent is DOING, and a flagged session may still be working - the reaper
    /// explicitly waits out a running final turn (<c>SessionManager.ReapPendingDeletions</c>). So it is
    /// NOT folded into <see cref="SessionOrdering.EffectiveColor"/>: a flagged session that is working is
    /// BLUE, with a badge beside the dot; a flagged session that is waiting is still red "Needs you".
    /// Do NOT add a PendingDeletion branch to the fold - that would make it a colour.
    ///
    /// This comment used to claim the row "paints as a winding-down grey". IT DID NOT, on any
    /// Gateway-backed screen. The fold has never read this field, so the row kept its normal colour;
    /// the only implementation was the Director calling <c>SetStatusColor(Unknown, ...)</c> on itself,
    /// which law 2 forbids. That call is now deleted, and the promise is gone with it - the fact travels
    /// here, as a fact.
    ///
    /// This comment ALSO used to say that call was read by "nothing that paints". That was too strong:
    /// <see cref="StatusColor"/> did gate a painted colour - the Gateway's voice-mode window stamped
    /// <see cref="BriefingState"/> (which the fold paints yellow) only for a session whose COOKED colour
    /// was red, so writing Unknown there would have suppressed that yellow. That Gateway gate now reads
    /// the raw activity fact instead (<see cref="SessionOrdering.IsRawRed"/>), so no Gateway-decided
    /// colour depends on a Director-decided colour any more. The cooked field still has desktop-side
    /// consumers and is NOT dead - see <see cref="StatusColor"/>.
    ///
    /// THE REMAINDER, stated exactly: the fact crosses the wire, the DESKTOP RAIL RENDERS THE BADGE (see
    /// MainWindow.axaml), and the PHONE AND COCKPIT DO NOT YET. Rendering it there is outstanding - see
    /// "Still to do" in docs/new_architecture/session-state.html.
    ///
    /// This paragraph said "no client renders a badge yet" in the same change that added the desktop
    /// badge - false the moment it was written, and false in the direction that makes a reader stop
    /// looking. Keep it exact: name the surfaces, not "no client".
    /// </summary>
    public bool PendingDeletion { get; set; }

    /// <summary>Short human reason captured when <see cref="PendingDeletion"/> was set
    /// (e.g. "jobs-auto: nothing to report"). Null when the session is not flagged.
    /// Mirrors <c>Session.DeletionReason</c>.</summary>
    public string? DeletionReason { get; set; }

    /// <summary>
    /// Issue #553: true while the Gateway's wingman is actively producing this session's spoken
    /// summary (<c>WingmanVoiceService.IsGenerating</c>). Stamped by the Gateway aggregator only;
    /// always false in Director-local responses. Drives the voice-mode "yellow until ready" color
    /// rule (see <see cref="SessionOrdering.EffectiveColor"/>): a voice-mode session waiting for the
    /// user stays yellow while this is true.
    /// </summary>
    public bool VoiceGenerating { get; set; }

    /// <summary>
    /// Issue #553: true when the Gateway has fetchable, playable cached audio for this session
    /// (<c>WingmanVoiceService.HasVoice</c>) - the SINGLE truthful "there is voice you can play right
    /// now" signal. Stamped by the Gateway aggregator only; always false in Director-local responses.
    /// This controls whether a play affordance is available; the "preparing voice" color is driven
    /// by <see cref="VoiceGenerating"/>, not by missing audio, so a TTS failure cannot wedge a
    /// session outside Needs You forever.
    /// </summary>
    public bool VoiceAudioReady { get; set; }

    /// <summary>
    /// Issue #939 (epic #937): set when the Gateway could NOT keep this session's voice because hosted
    /// AI is unavailable (out of credits, monthly cap reached, or - in bring-your-own mode - no key).
    /// Carries the ONE shared message + call-to-action (built from the single-source
    /// <c>HostedAiMessages</c>), so the owning UI shows the consistent "add credit / add a key" state
    /// instead of a silently missing play triangle. Null when voice is fine. Stamped by the Gateway
    /// aggregator only; cleared on the next successful generation (dismissible).
    /// </summary>
    public HostedAiMessageDto? VoiceUnavailable { get; set; }

    /// <summary>
    /// The FOLDED voice-mode display verdict (what the Voice screen shows and offers), computed by the
    /// Gateway aggregator from the facts above plus the "nothing to narrate" marker, and rendered
    /// VERBATIM by every client. This is the field the Voice screen reads; the individual
    /// <see cref="VoiceMode"/> / <see cref="VoiceGenerating"/> / <see cref="VoiceAudioReady"/> /
    /// <see cref="VoiceUnavailable"/> facts remain for the roster and other surfaces, but the screen's
    /// badge, message, and whether a Generate button appears all come from here - never re-derived on the
    /// client (see <see cref="VoiceDisplay"/> for why). Null in Director-local responses.
    /// </summary>
    public VoiceDisplay? VoiceDisplay { get; set; }

    /// <summary>
    /// True while a client is transcribing a dictated utterance into this session: the phone has
    /// released the Speak dialog and the Gateway is uploading + transcribing the recorded audio in
    /// the background, which will then be submitted into the session. Stamped by the Gateway
    /// aggregator only (from the in-memory <c>TranscribingSessions</c> store); always false in
    /// Director-local responses. Drives the orange "Transcribing..." roster color so every client
    /// sees the session is busy and nobody else starts using it (see
    /// <see cref="SessionOrdering.EffectiveColor"/>).
    /// </summary>
    public bool Transcribing { get; set; }

    /// <summary>
    /// Issue #1181, Task 4: the phone-dictation presentation sub-state - which PHASE an inbound
    /// dictation is in, so a client can show the honest label instead of one blanket "Transcribing...".
    /// One of: <c>"Uploading from phone"</c> (the durable PENDING delivery marker stands AND the phone is
    /// still making progress); <c>"Transcribing"</c> (the audio is up and the server is turning it into
    /// text - computed from an active transcription run, NOT the 90-second idle mark); or <c>null</c> when
    /// no dictation is inbound. Stamped by the Gateway aggregator only (always null in Director-local
    /// responses). Both non-null values drive the orange roster color; the clients render this string as
    /// the label.
    ///
    /// This comment used to say "Uploading from phone" was computed from the durable PENDING marker "so it
    /// never wedges". That was HALF-TRUE, and the missing half was defect 19: the marker does clear on
    /// delivery, so the normal path never wedges - but a dictation that reaches no terminal state at all
    /// never clears it, and the session then read "Uploading from phone" indefinitely about an upload that
    /// had stopped (observed: 1h30m, upload f13cb4b6d9d0, 12 July 2026). A reader who trusted this comment
    /// concluded there was no bug, which is exactly why nobody believed the report. The colour is now
    /// bounded by the progress-idle rule at the Gateway fold; the durable record still never expires.
    /// </summary>
    public string? DictationStatus { get; set; }

    /// <summary>
    /// Whether the Wingman experience is enabled for this session: auto-explain briefing on
    /// turn-end, Voice + Wingman tabs visible, Yellow "Wingman is reading" state available.
    /// Default OFF. When false the session behaves as a plain terminal -- clients hide the
    /// Voice + Wingman tabs and the dot goes straight Blue->Red on turn-end (no Yellow).
    /// Mirrors <c>Session.WingmanEnabled</c> on the owning Director.
    /// </summary>
    public bool WingmanEnabled { get; set; } = false;

    /// <summary>
    /// Scheduled-run auto-dismiss (issue #1200): true when this session is an AUTOMATED run that should
    /// close itself when it finishes with nothing needing a human (mirrors <c>Session.AutoDismiss</c>).
    /// Only sessions carrying this flag are ever auto-closed; human-started sessions leave it false and are
    /// never touched. Rides the existing snapshot/delta path; defaults false on Directors that predate it.
    /// </summary>
    public bool AutoDismiss { get; set; } = false;

    /// <summary>
    /// Scheduled-run auto-dismiss (issue #1200): the agent's explicit end-of-run verdict parsed from its
    /// final message (mirrors <c>Session.DismissVerdict</c>). <c>"done"</c> = nothing needs the human, safe
    /// to auto-close; <c>"needs-human"</c> = keep the session open. Null when the run has not yet emitted a
    /// verdict (the conservative default - a session is never auto-closed without an explicit <c>done</c>).
    /// Only meaningful when <see cref="AutoDismiss"/> is true.
    /// </summary>
    public string? DismissVerdict { get; set; }

    // ===== Raw local facts for the Gateway color fold (issue #1177, Phase 2) =====
    // These are RAW FACTS the Director reports straight from the Session - it does NOT fold them into a
    // color. The Gateway is the single fold: it computes EffectiveColor / TriageBucket / StateLabel from
    // these plus its own overlays (Transcribing/Briefing/Explaining/VoicePreparing). Additive: they ride
    // the existing snapshot/delta path, and default to false/null on Directors that predate them.

    /// <summary>
    /// RAW FACT: a brand-new session that has not yet taken its first turn (mirrors
    /// <c>Session.IsBrandNew</c>). At a turn-end this is what the fold paints green ("ready") - the
    /// session is sitting at its prompt, available, and must not read as red "needs you".
    /// </summary>
    public bool IsBrandNew { get; set; }

    /// <summary>
    /// RAW FACT: this session is a controlled "Supporting" sub-agent that another session is driving
    /// (mirrors <c>Session.IsControlled</c>, i.e. <see cref="ControllerSessionId"/> is set). The fold
    /// recedes it to slate while its controller is alive, except red "needs you" breaks through.
    /// </summary>
    public bool IsControlled { get; set; }

    /// <summary>
    /// RAW FACT: the id of the session controlling this one (mirrors <c>Session.ControllerSessionId</c>),
    /// or null when this is a normal (uncontrolled) session. The fold uses its presence for the slate
    /// overlay; the roster can confirm the controller is still alive.
    /// </summary>
    public string? ControllerSessionId { get; set; }

    /// <summary>
    /// Automatic session role, COMPUTED by the Gateway aggregation from the whole fleet (not stored):
    /// <c>Worker</c> = this session is controlled AND its controller is still alive; <c>Manager</c> = a
    /// non-worker that controls at least one LIVE worker; <c>Standalone</c> = neither. Dynamic - a
    /// Standalone becomes a Manager when it gains a live worker and reverts when the last dies. The fold
    /// (<see cref="SessionOrdering.EffectiveColor"/>) SUPPRESSES a live Worker's red (it recedes and never
    /// nags the human); Manager/Standalone stay human-facing. Empty on a Director-local response (the role
    /// needs the fleet view).
    ///
    /// One of "Standalone" / "Worker" / "Manager" / "Architect" - FOUR, not the three this comment used to
    /// list. Architect is never inferred from the spawn graph; it arrives only via <see cref="ExplicitRole"/>,
    /// which wins over auto-derivation in the aggregation's role resolution (see <see cref="ExplicitRole"/>
    /// eleven lines below, which said so all along, and the "A" glyph the desktop rail renders for it).
    /// </summary>
    public string? SessionRole { get; set; }

    /// <summary>
    /// RAW FACT: WHO asked for this session to exist (devthrottle_internal issue #982) - one of the
    /// <c>SessionOriginKinds</c> tokens ("human" / "agent" / "schedule" / "unknown"). Mirrors
    /// <c>Session.OriginKind</c>; stamped at birth by the create path and never changed, so it keeps
    /// describing the create call however the session later behaves. Passes through the Gateway
    /// aggregation unchanged and is folded into the durable work-history record.
    ///
    /// "unknown" is a real answer here and must be rendered as one, never as "human". It is what a
    /// Director that predates the field reports, and what an honest create path with nothing to say
    /// records. The whole point of the field is the share of sessions AGENTS start; quietly resolving
    /// unknown to either real value corrupts that number where no later reader could see it.
    /// </summary>
    public string? OriginKind { get; set; }

    /// <summary>
    /// RAW FACT: WHERE the create call came from (issue #982) - one of the
    /// <c>SessionOriginSurfaces</c> tokens ("desktop" / "cockpit" / "phone" / "cli" / "cron" /
    /// "workflow" / "api" / "unknown"). Mirrors <c>Session.OriginSurface</c>, on the same terms as
    /// <see cref="OriginKind"/>.
    /// </summary>
    public string? OriginSurface { get; set; }

    /// <summary>
    /// RAW FACT: the id of the session that ASKED for this one (issue #982), or null when nothing did.
    /// Mirrors <c>Session.ParentSessionId</c>. This is the lineage edge: with it, a roster of
    /// twenty-two sessions resolves into the handful of operations it actually is, and delegation depth
    /// becomes answerable at all.
    ///
    /// DISTINCT from <see cref="ControllerSessionId"/>, which is a live supervision relationship the
    /// fold reads to recede a sub-agent to slate. This one is history and paints nothing. They carry
    /// the same id on an ordinary CLI spawn and diverge whenever a session spawns a deliberate peer
    /// (<c>--standalone</c>) - no controller, but an agent still started it.
    /// </summary>
    public string? ParentSessionId { get; set; }

    /// <summary>
    /// RAW FACT: the sticky EXPLICIT role a human/session declared for this session (mirrors
    /// <c>Session.ExplicitRole</c>), or null when none was set. When present it WINS over auto-derivation in
    /// the aggregation's role resolution (so an explicit <see cref="SessionRoles.Architect"/> - which can
    /// never be inferred from the spawn graph - stays Architect and stays human-facing). One of the
    /// <see cref="SessionRoles"/> values, or null.
    /// </summary>
    public string? ExplicitRole { get; set; }

    /// <summary>
    /// A supervised session has its hand up: it is still WORKING and has hit something it cannot decide
    /// inside its mandate (issue #2662). Gateway-owned and Gateway-stamped, from the hand-raise registry.
    ///
    /// THIS USED TO BE A DECLARATION AND NOTHING ELSE. Its old comment said "reserved for the manager-facing
    /// rail; the global fold does not consume it yet", and that stayed true from July until it was built -
    /// zero writers, zero readers, on a field the whole worker-suppression design depends on. Suppressing a
    /// worker's attention is only safe if something else is listening, and for that whole period nothing was.
    ///
    /// IT LOWERS ITSELF. The fold stamps it as "raised AND still working", so a worker that stops - finished
    /// or blocked - has its hand lowered with no sweep, no timer and nothing to remember. Stopping already
    /// carries its own cue to the supervisor, so the flag would be redundant the moment it went quiet, and a
    /// latch that had to be cleared by someone is exactly the thing that would still be up next week.
    ///
    /// THE OWNER NEVER SEES IT. It is not read by <c>SessionOrdering</c>'s colour, label or triage arms and
    /// must never be: it is a signal between two sessions. Adding a branch for it would put a worker's
    /// problem back in his queue, which is the whole thing the supervised rule exists to stop.
    /// </summary>
    public bool NeedsManager { get; set; }

    /// <summary>
    /// The worker's OWN WORDS for what it is blocked on, or null when its hand is down. Rides beside
    /// <see cref="NeedsManager"/> so a supervisor learns WHAT is wanted from the roster it already reads,
    /// rather than having to open the session to find out - the same reason the delivery notice exists.
    ///
    /// Never composed by us. A worker that raises its hand without saying why has told its supervisor almost
    /// nothing, so the command that sets this requires the words.
    /// </summary>
    public string? NeedsManagerReason { get; set; }

    /// <summary>
    /// RAW FACT: the display name was AUTO-composed at birth (mirrors <c>Session.IsAutoNamed</c>); false once
    /// a human/self explicitly renamed it. Automatic session roles (chunk 3): the marker any future
    /// auto-rename gates on so a self/human name is never re-auto-named.
    /// </summary>
    public bool IsAutoNamed { get; set; }

    /// <summary>
    /// RAW FACT: the Wingman determined this session is parked on its OWN background task (a build, a
    /// running shell) rather than on the user (mirrors <c>Session.IsBackgroundRunning</c>). At a turn-end
    /// the fold paints this purple instead of red.
    /// </summary>
    public bool IsBackgroundRunning { get; set; }

    /// <summary>
    /// RAW FACT: a dictated utterance is being transcribed and submitted into this session in the
    /// background on the DIRECTOR (the Avalonia desktop <c>BackgroundDictationSend</c> flow; mirrors
    /// <c>Session.IsTranscribing</c>). Distinct from <see cref="Transcribing"/>, which is the Gateway's
    /// OWN transcribing flag (the mobile Speak flow). The fold paints EITHER source orange, so a
    /// desktop-dictating session shows "Transcribing..." the same as a phone-dictating one.
    /// </summary>
    public bool IsTranscribing { get; set; }

    /// <summary>
    /// RAW FACT: the legacy auto-explain deep dive (<c>ProactiveExplainService</c>) is running for this
    /// session at a turn-end (mirrors <c>Session.IsExplaining</c>). While it runs, and the session is
    /// WingmanEnabled and at a turn-end, the fold paints yellow ("wingman is reading"). THIS FEATURE
    /// WORKS and this field is the one that carries it.
    ///
    /// It used to be described as "DISTINCT from the Gateway's user-initiated deep dive
    /// (<c>BriefingState == "Explaining"</c>, issue #217), which the fold paints ORANGE". The distinction
    /// was real but the other half was not: that orange never once fired, and the rule is gone - see the
    /// tombstone in <see cref="SessionOrdering.EffectiveColor"/>. This yellow is what survived, because it
    /// is what was actually reachable.
    /// </summary>
    public bool IsAutoExplaining { get; set; }

    /// <summary>
    /// For GitHub Actions remote sessions: the "owner/repo" the session runs against.
    /// Empty for local sessions. Mirrors the backend's repo slug.
    /// </summary>
    public string RemoteRepo { get; set; } = "";

    /// <summary>
    /// For GitHub Actions remote sessions: web URL of the issue/PR thread driving
    /// the session, or empty until the thread is established / for local sessions.
    /// </summary>
    public string RemoteThreadUrl { get; set; } = "";

    /// <summary>
    /// For GitHub Actions remote sessions: web URL of the most recent workflow run,
    /// or empty when none yet / for local sessions.
    /// </summary>
    public string RemoteRunUrl { get; set; } = "";

    /// <summary>
    /// For GitHub Actions remote sessions: last observed run status
    /// (queued / in_progress / completed / none). Empty for local sessions.
    /// </summary>
    public string RemoteRunStatus { get; set; } = "";

    /// <summary>
    /// DevThrottle Stats: the "owner/repo" repo name of this session's LOCAL checkout (GitHub or Azure
    /// DevOps), resolved by the owning Director from the checkout's origin remote (the git repo lives on the
    /// Director's machine, so only it can resolve this). Empty when the checkout is on no host we recognize.
    /// This is the grouping key the Repos page uses so every worktree and every per-machine clone of one
    /// repository rolls up into a single row - <see cref="RepoPath"/> alone splits them, because each checkout
    /// is a different path.
    ///
    /// DISTINCT from <see cref="RemoteRepo"/>: that is populated only for GitHub Actions CLOUD sessions and
    /// names the repo the cloud run executes against; this is populated for ordinary LOCAL sessions and names
    /// the repo their working directory is a checkout of. A session has one or the other, not both.
    /// Passes through the Gateway aggregation unchanged; the aggregator folds it as the repository identity
    /// and retains <see cref="RepoPath"/> alongside as the checkout identity.
    /// </summary>
    public string RepoName { get; set; } = "";

    /// <summary>
    /// DevThrottle Stats: this session's input tally (submitted turns + character volume by modality and
    /// surface), taken at the Director's SendInput/SendTextAsync choke point so desktop-local input is
    /// counted too. Rides the existing snapshot/delta path up to the Gateway, which aggregates every
    /// session's tally into the always-available stats page. Null when the session has taken no counted
    /// input yet, or on Directors that predate this field. Only counts ever travel - never the text of
    /// anything typed or said (mission decision 5).
    /// </summary>
    public InputStatsDto? InputStats { get; set; }

    /// <summary>
    /// The model this session's agent is CURRENTLY using (issue #1637), reported by the owning
    /// Director from the agent driver's own records (transcript / rollout / event log / session
    /// store) - e.g. <c>claude-fable-5</c>, <c>gpt-5.5</c>, <c>grok-4.5</c>. Refreshed at every
    /// turn-end, so a mid-session model switch (Claude's /model) is reflected; the launch flag alone
    /// would go stale. RECORDS-ONLY: this never carries the launch ALIAS (<c>opus[1m]</c>), only
    /// what the tool recorded producing - so a fold can never attribute a turn to an alias the
    /// records will later contradict (see SessionRecordsWatcher.RefreshModel). Null when the
    /// tool has recorded no model yet (fresh session before its first turn-end), the agent's driver
    /// does not declare the ModelReport capability, or the Director predates this field. Free text
    /// with unbounded cardinality, casing by convention only - consumers key it like RepoPath/Agent
    /// (identity table, ordinal-ignore-case in C#), never by database collation. Passes through the
    /// Gateway aggregation unchanged. This is the PRODUCER the Gateway statistics' model dimension
    /// folds from (the gateway-sqlite mission brief names this field as its prerequisite).
    /// </summary>
    public string? CurrentModel { get; set; }

    /// <summary>
    /// The FOLDED display verdict for <see cref="CurrentModel"/> (issue devthrottle_internal#1340): the
    /// finished badge text, the full id for the tooltip, and WHICH of the two absences this is when there
    /// is no model - computed once by <see cref="ModelDisplayFold"/> and rendered verbatim. Stamped by the
    /// Gateway aggregator beside <see cref="EffectiveColor"/> and <see cref="StateLabel"/>; null in
    /// Director-local responses, where the desktop rail folds the same function over the same two local
    /// facts (see <see cref="ModelDisplay"/> for why that is one rule and not two).
    /// </summary>
    public ModelDisplay? ModelDisplay { get; set; }

    /// <summary>
    /// This session's CUMULATIVE token spend (issue #1637), reported by the owning Director from the
    /// agent driver's own records (<c>ReadUsage</c>) and refreshed at every turn-end - the same moment
    /// and the same records read as <see cref="CurrentModel"/>, so the model that produced a turn and the
    /// tokens it cost arrive together. Null when the tool has recorded no usage yet (fresh session before
    /// its first turn-end), the agent's driver does not declare the TokenUsage capability (only Claude
    /// does today - Codex reports context occupancy, which is a gauge, not spend), or the Director
    /// predates this field.
    ///
    /// LEAN ON PURPOSE: the totals only, not the per-turn breakdown the on-demand usage view shows. This
    /// rides the roster snapshot for every session on every poll, so it carries what a governance page
    /// sums and nothing heavier. Passes through the Gateway aggregation unchanged; this is the PRODUCER
    /// the Gateway statistics' token dimension will fold from (the wire the mission brief names as the
    /// prerequisite for the token column).
    /// </summary>
    public TokenTotalsDto? TokenTotals { get; set; }

    /// <summary>
    /// How many files are changed in this session's working tree - staged plus unstaged plus untracked -
    /// as the owning Director's <c>SessionGitStatusMonitor</c> last measured it. This is the number behind
    /// the desktop rail's amber "N chg" badge, and it is on the wire so the Cockpit roster and the phone
    /// show the SAME number rather than each polling git for themselves (or, as before this field existed,
    /// showing nothing at all).
    ///
    /// A RAW FACT, not a verdict. Only the machine holding the checkout can measure it, so the Gateway
    /// passes it through untouched; what a client DOES with it (the badge, its wording) is one shared
    /// formatter, not a per-client rule.
    ///
    /// NULL MEANS UNKNOWN AND MUST NEVER BE RENDERED AS ZERO (issue 516). A git probe can fail - no git on
    /// the path, a permissions problem, a repository mid-rebase - and "we could not tell" is a different
    /// answer from "this tree is clean". Null is also what an older Director reports, since it does not
    /// know the field. Clients show no badge for null AND for 0; only a positive count renders.
    ///
    /// Scoped to the session's OWN working tree: a session running in a worktree reports that worktree's
    /// count, not its parent repository's. Two sessions sharing one checkout therefore report the same
    /// number, which is correct - it is one tree.
    /// </summary>
    public int? UncommittedCount { get; set; }

    /// <summary>
    /// How many turns the agent has completed in this run of the session, counted by the owning
    /// Director at the activity flip: one flip to WaitingForInput equals one turn - the same rule
    /// <c>TurnReviewLogger</c> writes its records by - so it works for every agent kind and no
    /// reader ever re-parses a transcript. A RAW FACT the Gateway passes through untouched.
    ///
    /// NULL MEANS UNKNOWN, NEVER ZERO: null is what an older Director reports, since it does not
    /// know the field. A live Director always reports a number, and 0 from one genuinely means
    /// "no turn has completed yet". Distinct from <see cref="InputStats"/>, which counts turns
    /// the user SUBMITTED, not turns the agent finished.
    /// </summary>
    public int? TurnCount { get; set; }

    /// <summary>
    /// UTC moment the session last entered a waiting-on-the-user state (WaitingForInput or
    /// WaitingForPerm), or null while it is not waiting. An ABSOLUTE ANCHOR, not a duration:
    /// clients derive the ticking "sitting on you for X" clock from it locally, the same way the
    /// waiting label already ticks between roster polls (the durationLabel precedent, issue #844).
    /// Distinct from <see cref="IdleSeconds"/>, which is byte-silence and resets on any output,
    /// and from <see cref="NeedsYouSince"/>, which is the Gateway's red-state verdict clock -
    /// this one is the Director's raw fact about the terminal's turn state.
    /// </summary>
    public DateTime? WaitingSince { get; set; }

    /// <summary>
    /// Total seconds this session has spent waiting on the user, summed over CLOSED waiting
    /// stretches, as measured by the owning Director. While <see cref="WaitingSince"/> is set the
    /// current open stretch is NOT yet included - a reader adds (now - WaitingSince) for the live
    /// total, so the number keeps moving between pushes without the Director re-pushing. Null when
    /// an older Director does not report it; unknown, never zero.
    /// </summary>
    public double? CumulativeIdleSeconds { get; set; }

    /// <summary>
    /// How many times this session has STARTED waiting on the user (devthrottle_internal issue #982),
    /// as counted by the owning Director at the same activity flip that opens the stretch
    /// <see cref="CumulativeIdleSeconds"/> later closes. Mirrors <c>Session.WaitingStretchCount</c>.
    ///
    /// The matched pair to the idle clock, and not derivable from it: one session that needed you once
    /// for an hour and one that needed you twelve times for five minutes read the same on the clock and
    /// are nothing alike to live with. Null when an older Director does not report it - unknown, never
    /// zero, and zero from a current Director genuinely means it has not needed you yet.
    /// </summary>
    public int? WaitingStretchCount { get; set; }

    /// <summary>
    /// Issue #1176 (Phase 1a): a copy safe to hand out from the Gateway's pushed-session cache. The
    /// <c>/sessions</c> aggregation stamps scalar fields (EffectiveColor, DirectorId, MachineName,
    /// voice/transcription overlays, etc.) on the object it serves, so callers must never receive the
    /// cached instance itself or one request would contaminate the cache for later ones. Reference-type
    /// members the aggregator could mutate in place are re-created here; <see cref="VoiceUnavailable"/>,
    /// <see cref="VoiceDisplay"/> and <see cref="ModelDisplay"/> are only ever replaced wholesale by the
    /// aggregator (never mutated), so sharing their references is safe.
    /// </summary>
    public SessionDto Clone()
    {
        var copy = (SessionDto)MemberwiseClone();
        copy.DriverCapabilities = new List<string>(DriverCapabilities);
        return copy;
    }
}
