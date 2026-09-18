namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /sessions on a Director's Control API.
/// </summary>
public sealed class NewSessionRequest
{
    /// <summary>Absolute path to the repository / working directory the session should open in.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>
    /// Optional explicit display name for the new session (issue #800). When provided it is used
    /// verbatim, EXCEPT that a blank name or one equal (case-insensitive) to the bare repository
    /// folder name is rejected with HTTP 400 - pass a meaningful name or a <see cref="Purpose"/>.
    /// When omitted, the Director auto-composes a meaningful name from the folder and a
    /// disambiguator so a session never displays as the bare folder name alone.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Optional short free-text description of what the session is FOR (issue #800), e.g.
    /// "implement #799". When no explicit <see cref="Name"/> is given, the Director builds the
    /// session name from the folder name plus this purpose (e.g. "devthrottle: implement #799").
    /// Trimmed and capped when building the name; not stored as a separate field on the session.
    /// </summary>
    public string? Purpose { get; set; }

    /// <summary>
    /// Which agent CLI to launch. Valid values: "ClaudeCode" (default), "Pi", "Codex",
    /// "Gemini", "OpenCode", "Grok", "RawCli". When "RawCli" is specified, <see cref="Command"/>
    /// must also be set to the executable to run.
    /// </summary>
    public string Agent { get; set; } = "ClaudeCode";

    /// <summary>Optional extra arguments to pass to the agent CLI.</summary>
    public string? Args { get; set; }

    /// <summary>
    /// Optional per-session permission-bypass choice, mirroring the desktop New Session dialog's
    /// "Bypass permission prompts" checkbox (issue #1497). Consulted ONLY when <see cref="Args"/> is
    /// null (no explicit command-line override): <c>true</c> (the desktop default) launches the agent's
    /// configured default model AND its permission-bypass flag; <c>false</c> launches the same configured
    /// model but WITHOUT the bypass flag, so the agent stops for each permission prompt. When
    /// <see cref="Args"/> is supplied, that explicit line wins and this is ignored. Null defaults to
    /// <c>true</c> (the desktop default), so callers that omit it are unaffected.
    /// </summary>
    public bool? BypassPermissions { get; set; }

    /// <summary>
    /// For <see cref="Agent"/> = "RawCli": the executable to run (e.g. "pwsh", "aider",
    /// or an absolute path). Resolved against PATH+PATHEXT before spawning; a path that
    /// cannot be resolved fails loudly at launch. Ignored for all other agent kinds.
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// For <see cref="Agent"/> = "RawCli": optional arguments appended to
    /// <see cref="Command"/> before any <see cref="Args"/>. Ignored for all other
    /// agent kinds.
    /// </summary>
    public string? CommandArgs { get; set; }

    /// <summary>
    /// Optional Claude session ID to resume. When set, the new session re-attaches to the
    /// given Claude Code conversation instead of starting fresh. Used by the Resume Session
    /// tab. Ignored by agents that don't support resume (e.g. Pi).
    /// </summary>
    public string? ResumeSessionId { get; set; }

    /// <summary>
    /// Optional first prompt to send into the new session as soon as the agent is up
    /// and Idle. Used by handovers and by phone clients that want to launch a session
    /// already loaded with context. The Director waits up to PrePromptWaitMs for the
    /// SessionStart hook before dispatching.
    /// </summary>
    public string? PrePrompt { get; set; }

    /// <summary>How long to wait for the new session to reach Idle before sending the
    /// PrePrompt (milliseconds). Default 30000.</summary>
    public int PrePromptWaitMs { get; set; } = 30_000;

    /// <summary>
    /// Whether the new session should boot with the Wingman experience on (auto-explain
    /// briefing on turn-end + Voice/Wingman tabs + Yellow "Wingman is reading" state).
    /// Defaults to false, matching <c>Session.WingmanEnabled</c>'s default; set to true to
    /// create a session with the Wingman experience on.
    /// </summary>
    public bool WingmanEnabled { get; set; } = false;

    /// <summary>
    /// Optional id of the session that is spawning and controlling this one (issue #815). When set,
    /// the new session is born as a controlled "Supporting" sub-agent of that session and shows the
    /// recessive Supporting status color while its controller is alive (a red "needs you" still
    /// breaks through). Set ONLY at birth - there is no way to mark/unmark control later. Must be a
    /// session id (GUID string); an unparseable value is ignored and the session is born normal.
    /// </summary>
    public string? ControllerSessionId { get; set; }

    /// <summary>
    /// Set only by a Director restoring a drained seat (the Message Load mission, inspection 7, ruling 3): the
    /// workspace seat this session is, and the token stored on it before this create was sent. The Gateway
    /// records the new session's id on that seat when the create succeeds. Refused from any caller that is not
    /// a Director. The Director's create verb ignores it.
    /// </summary>
    public WorkspaceRestoreClaim? RestoreClaim { get; set; }

    /// <summary>
    /// WHO is asking for this session (devthrottle_internal issue #982): one of the
    /// <c>SessionOriginKinds</c> tokens - "human", "agent", "schedule" - case-insensitive. An unknown
    /// value is REJECTED as a bad request, the same posture as <see cref="Role"/>: a mistyped origin
    /// must not silently become "unknown", because unknown is also what an honest older caller sends
    /// and the two would then be indistinguishable. Null records "unknown", which is the truth about a
    /// caller that did not say.
    ///
    /// STATED BY THE CALLER, AND THAT IS DELIBERATE - but it is not the last word on every path. The
    /// Gateway's spawn relay OVERWRITES this from the verified per-device key when the caller is a
    /// signed-in phone or browser (the same gateway-authoritative rule <c>PromptRequest.Surface</c>
    /// follows), so the one route a stranger could reach cannot forge its own origin. The Director's
    /// loopback floor is reachable only from this machine, so a caller there is already as trusted as
    /// the machine itself.
    /// </summary>
    public string? Origin { get; set; }

    /// <summary>
    /// WHERE this create call is coming from (issue #982): one of the <c>SessionOriginSurfaces</c>
    /// tokens - "desktop", "cockpit", "phone", "cli", "cron", "workflow", "api" - case-insensitive.
    /// An unknown value is REJECTED, for the same reason as <see cref="Origin"/>. Null records
    /// "unknown". Overwritten by the Gateway relay for a device-key caller.
    /// </summary>
    public string? OriginSurface { get; set; }

    /// <summary>
    /// The session MAKING this call (issue #982), when an agent session is: the lineage edge that turns
    /// a flat roster into the tree of operations it actually is. A session id (GUID string); an
    /// unparseable value is REJECTED rather than dropped, so a broken lineage edge is never mistaken for
    /// a root session.
    ///
    /// This is the issue's <c>originAgentSessionId</c> and its <c>parentSessionId</c>, which are one
    /// fact under two names - the session that made the create call IS the parent. Kept only alongside
    /// <see cref="Origin"/> = "agent"; a parent named on a human or scheduled origin is dropped, since
    /// one of the two statements must be wrong and the stated origin is the one the caller meant.
    ///
    /// DISTINCT from <see cref="ControllerSessionId"/>, which asks for a live supervision relationship
    /// and changes how the new session is painted. The two carry the same id on an ordinary CLI spawn
    /// and diverge on <c>--standalone</c>, where an agent starts a deliberate human-facing peer: no
    /// controller, but still an agent-started session.
    /// </summary>
    public string? ParentSessionId { get; set; }

    /// <summary>
    /// Optional EXPLICIT role for the new session (automatic session roles). One of the
    /// <see cref="SessionRoles"/> values (Standalone / Manager / Worker / Architect); case-insensitive. An
    /// unknown value is REJECTED as a bad request (so a mistyped --role never silently drops). When set it
    /// is sticky and WINS over auto-derivation - the way to make a session an Architect, which cannot be
    /// inferred from the spawn graph. Null leaves the role to auto-derivation.
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// Optional Mission to ATTACH the new session to at spawn (see
    /// docs/new_architecture/fleet.html). When set, the Director stamps the
    /// session's <see cref="SessionDto.MissionId"/> and caches the resolved
    /// <see cref="SessionDto.MissionName"/> at birth - the attachment that binds this session into a pod.
    /// The Mission must already exist (create it with POST /missions); an unknown Mission is REJECTED as a
    /// bad request BY THE GATEWAY, which is the only place that holds missions. Null leaves the session
    /// attached to no Mission.
    /// </summary>
    public Guid? MissionId { get; set; }

    /// <summary>
    /// The resolved display name of the Mission named by <see cref="MissionId"/>, and it travels WITH the
    /// id rather than instead of it. Set by the Gateway on every spawn into a mission: the Gateway resolves
    /// and validates the mission against its OWN store inside the caller's tenant - it is the source of
    /// truth - and forwards BOTH values, so the Director stamps the attachment directly with no lookup of
    /// its own. The Director holds no mission store, so an id arriving here with a blank name was resolved
    /// by nobody and the create is REFUSED (issue #2629; there is no local fallback and there must not be
    /// one - the transitional bridge that used to sit in SessionCommandExecutor consulted a stale
    /// per-machine file and reported live missions as unknown).
    /// </summary>
    public string? MissionName { get; set; }

    /// <summary>
    /// Optional workflow RUN to SEAT the new session on (Workflows mission, phase 5b - the
    /// governance outcome spine, issue #1771). Set explicitly by a caller seating a session onto a
    /// known run, or resolved by the Gateway spawn relay from <see cref="MissionId"/> (a mission's
    /// sessions auto-seat onto the mission's run). The Gateway validates the run and stamps
    /// <see cref="WorkflowId"/> + <see cref="WorkflowVersion"/> beside it; after the spawn succeeds
    /// the Gateway records the new session as a run PARTICIPANT. Null seats the session on nothing.
    /// </summary>
    public Guid? WorkflowRunId { get; set; }

    /// <summary>
    /// The seated run's workflow id (e.g. "mission"), stamped by the GATEWAY after validating
    /// <see cref="WorkflowRunId"/> - the same resolved-by-the-source-of-truth pattern as
    /// <see cref="MissionName"/>. The Director stamps the seat only when the run id, this, and
    /// <see cref="WorkflowVersion"/> all arrive together.
    /// </summary>
    public string? WorkflowId { get; set; }

    /// <summary>The seated run's PINNED workflow version, stamped by the Gateway beside
    /// <see cref="WorkflowId"/>. A seated session fetches its conduct at exactly this version -
    /// never a moving head, even if a newer version publishes mid-run.</summary>
    public int? WorkflowVersion { get; set; }

    /// <summary>
    /// Optional target machine for spawn routing ("start a session on another computer"). Null,
    /// empty, or the local machine name spawns on the LOCAL machine (the default, unchanged); a
    /// remote machine name routes the spawn via the Gateway to a Director on that machine (first
    /// available, auto-launched if none is running). This field is ADVISORY on a Director's own
    /// POST /sessions - the Director always creates locally and its identity comes from
    /// <c>Environment.MachineName</c>; the routing decision is made before the request reaches the
    /// target Director.
    /// </summary>
    public string? Machine { get; set; }

    /// <summary>
    /// Optional target DIRECTOR for spawn routing - "start a session on THAT Director, not merely on
    /// that computer". One machine runs several named Director instances, so <see cref="Machine"/>
    /// alone cannot say which one: it resolves to the first available Director on the machine. This
    /// field names ONE - by its Director id or its display name, matched case-insensitively, with the
    /// ID WINNING outright when it matches - and the resolve is pinned to it.
    ///
    /// Those two handles and no others. The instance SLUG (the <c>--instance</c> value) is deliberately
    /// not accepted: it is not carried on <see cref="DirectorDto"/>, so the Gateway could not resolve
    /// it, and a handle that worked only when the target happened to be the local Director would fail
    /// on the far side of the fleet for reasons the caller could not see.
    ///
    /// FAIL LOUD, NEVER SUBSTITUTE. A named Director that is not registered is an error naming it; a
    /// name that matches two Directors is an error listing both. Neither falls back to another
    /// Director, and neither auto-launches one: the caller asked for a specific Director, and quietly
    /// starting a session somewhere else is exactly the wrong answer.
    ///
    /// <see cref="Machine"/>, when both are given, NARROWS the match rather than being overridden by
    /// it. That is how a display name two machines happen to share is disambiguated - and it means a
    /// Director named alongside a machine it does not run on is a contradiction that fails, rather
    /// than one half of the caller's instruction being honored silently.
    ///
    /// Null or empty leaves machine routing exactly as it was.
    /// </summary>
    public string? Director { get; set; }

    /// <summary>
    /// Scheduled-run auto-dismiss (issue #1200). When true, this session is an AUTOMATED run that should
    /// close ITSELF once it finishes with nothing that needs a human: the agent ends its run by emitting a
    /// <c>CC-DISMISS</c> verdict block (see <c>Session.DismissVerdict</c>), and on <c>done</c> the Gateway
    /// closes the session over the Director stream so it never lingers in the rail. On <c>needs-human</c>
    /// (or no verdict) it stays open exactly like a normal session. Default false: a human-started session
    /// is NEVER auto-closed. Set true only by the cron starter for scheduled seed runs.
    /// </summary>
    public bool AutoDismiss { get; set; } = false;
}

/// <summary>
/// Body of the set-role command / POST /sessions/{sid}/role: (re)declare a session's explicit role after
/// birth. <see cref="Role"/> is one of the <see cref="SessionRoles"/> values (case-insensitive); an empty
/// or null value CLEARS the explicit role (reverting the session to auto-derivation).
/// </summary>
public sealed class SetRoleRequest
{
    public string? Role { get; set; }
}

/// <summary>
/// Describes a registered repository (returned by GET /repos).
/// </summary>
public sealed class RepositoryDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime? LastUsed { get; set; }
}

/// <summary>Body of POST /repos: register a repository explicitly (no session needed).</summary>
public sealed class RepoAddRequest
{
    /// <summary>Required. Absolute path of the repository directory. Must exist on the Director.</summary>
    public string Path { get; set; } = "";

    /// <summary>Optional display name; defaults to the folder name.</summary>
    public string? Name { get; set; }
}

/// <summary>Body of PATCH /repos: rename a registered repository (path is the identity).</summary>
public sealed class RepoRenameRequest
{
    /// <summary>Required. Path of the registered repository to rename.</summary>
    public string Path { get; set; } = "";

    /// <summary>Required. New display name.</summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// One repository with everything the repositories page needs, aggregated by the Director
/// from the repo registry, live sessions, session history, Claude Code session metadata,
/// and handover documents (returned by GET /repos/overview).
/// </summary>
public sealed class RepoOverviewDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>When a session was last started in this repo (UTC, from the registry).</summary>
    public DateTime? LastUsed { get; set; }

    /// <summary>False when the registered directory no longer exists on disk.</summary>
    public bool PathExists { get; set; }

    /// <summary>Live (non-exited) Director sessions currently open in this repo.</summary>
    public int LiveSessionCount { get; set; }

    /// <summary>Display names of the live sessions (custom name or folder name).</summary>
    public List<string> LiveSessionNames { get; set; } = new();

    /// <summary>Resumable Claude Code sessions recorded for this repo (~/.claude/projects).</summary>
    public int ResumableSessionCount { get; set; }

    /// <summary>CC Director workspace-history entries for this repo.</summary>
    public int HistorySessionCount { get; set; }

    /// <summary>When the most recent session (history or Claude metadata) was active (UTC).</summary>
    public DateTime? LastSessionAtUtc { get; set; }

    /// <summary>One-line summary of the most recent session, when available.</summary>
    public string? LastSessionSummary { get; set; }

    /// <summary>Git branch recorded by the most recent Claude session in this repo.</summary>
    public string? GitBranch { get; set; }

    /// <summary>Handover documents referencing this repo.</summary>
    public int HandoverCount { get; set; }

    /// <summary>Date of the newest handover referencing this repo (UTC).</summary>
    public DateTime? LastHandoverUtc { get; set; }
}
