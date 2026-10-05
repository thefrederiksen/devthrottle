namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One session's durable work-history record, in the <c>session_history</c> table (issue #2194,
/// standing on the #1862 ledger design). One row per fleet session, keyed on the Director-minted
/// session GUID, written the FIRST time the Gateway sees the session on the push stream and kept
/// fresh WHILE IT RUNS - never produced at the end. A session killed by a power cut therefore still
/// leaves a usable record; exactly the sessions that end badly are the ones this table must not lose.
///
/// The ENDING is always a Gateway ruling (the dumb-client rule): a farewell path stamps
/// closed/finished/director-stopped, and the history sweep CONCLUDES "interrupted" for an open row
/// whose Director has been silent past the threshold - the absence of a goodbye is the evidence.
/// The ruling is revisable: a session that reappears on the stream reopens its row.
///
/// HONESTY (issue #2157): <see cref="StartedAtUtc"/> is the Director-measured creation time riding
/// every push - a real measurement, persisted so a Gateway restart does not erase when a session
/// began. <see cref="LastSeenUtc"/> is refreshed by the throttled recorder (at most once per
/// freshness interval per session, plus material changes), so for an interrupted session the end is
/// honestly "last seen", never an invented instant.
///
/// The summary fields hold the session's own sealed account (clean close) or the Gateway's
/// prompt-log-derived account (dirty end, MARKED partial). The list fields are JSON arrays of
/// strings in TEXT columns - structured enough to query through the API, portable across SQLite and
/// Postgres. Prompt-derived text is customer content: rows are tenant-scoped by the global query
/// filter exactly like the dictation transcripts that already store text in this database.
///
/// Retention: 90 days, pruned by the history sweep. The consumable range (the 30-day API) sits well
/// inside it.
/// </summary>
public sealed class SessionHistoryEntity : TenantScopedEntity
{
    /// <summary>The Director-minted session GUID - natural key, composite with the tenant. The id is
    /// CALLER-SUPPLIED (it arrives on the push stream), so a SessionId-only key would let one tenant
    /// squat an id for every other tenant - the session_spend reasoning exactly.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The Gateway-issued human session number (100-999), when known.</summary>
    public int? SessionNumber { get; set; }

    public string? SessionName { get; set; }
    /// <summary>
    /// The machine the session ran on. STAMPED BY THE GATEWAY from the Director's connection
    /// record, not taken from the pushed session.
    ///
    /// It has to be, because the pushed field is empty and always has been:
    /// <c>ControlEndpoints.Map</c> takes <c>string machineName = ""</c> and no production caller
    /// passes it - not the snapshot the stream sends, not any of the delta pushes. The column was
    /// null on every row of this table, for every account, since it was created. Sourcing it from
    /// <see cref="Discovery.DirectorRegistry"/> instead fixes it for every client ALREADY IN THE
    /// FIELD, including versions that will never be upgraded, because the machine name arrives on
    /// the connection hello rather than in the session payload.
    ///
    /// A non-blank pushed value still wins, so a future client that fills it in is believed.
    /// </summary>
    public string? MachineName { get; set; }

    /// <summary>
    /// The Director version this session ran on, from the connection hello. Null when the Gateway
    /// has no live record for the Director (a row written from a reconnect race, or an old build
    /// that did not report one).
    ///
    /// WHY IT IS WORTH A COLUMN. Without it there is no way to ask what version anybody is running
    /// from the history: no upgrade curve, no way to tie a defect to a release, no way to see who
    /// is stranded on an old build. The only other record of a version is the website's
    /// devices.app_version, which is a registry of GATEWAY installs - a member on the hosted
    /// Gateway never appears in it at all, so for hosted users it answers nothing.
    /// </summary>
    public string? DirectorVersion { get; set; }

    public string DirectorId { get; set; } = "";
    public string? RepoPath { get; set; }

    /// <summary>owner/repo resolved from the origin remote, when known. The roll-up grouping key.</summary>
    public string? RepoName { get; set; }

    public string? AgentKind { get; set; }
    public string? Model { get; set; }
    public string? MissionName { get; set; }

    /// <summary>
    /// The mission this session was attached to (devthrottle_internal issue #982), or null. The NAME
    /// was already stored beside it and reads well, but a name is not a key: missions get renamed, two
    /// can share a name, and reporting a mission as a unit of work - sessions taken, elapsed time, cost
    /// - needs the id the mission store is keyed by. Written once, like the other attachment facts.
    /// </summary>
    public Guid? MissionId { get; set; }

    /// <summary>The declared role (Architect/Manager/Worker...), when the session has one.</summary>
    public string? SessionRole { get; set; }

    /// <summary>
    /// WHO asked for this session (devthrottle_internal issue #982) - one of the
    /// <c>SessionOriginKinds</c> tokens. A BIRTH FACT: written on first sight and never revised, unlike
    /// the running facts above, which are refreshed on every observed push. It cannot change, and a
    /// later push that lost it (an old Director mid-upgrade) must not be allowed to erase it.
    ///
    /// Null on rows written before the field existed, and "unknown" on rows whose create path did not
    /// say. Those are different states and are kept different: the first means the Gateway was not
    /// asking, the second means it asked and the answer was nothing.
    /// </summary>
    public string? OriginKind { get; set; }

    /// <summary>WHERE the create call came from (issue #982) - one of the <c>SessionOriginSurfaces</c>
    /// tokens. A birth fact on the same terms as <see cref="OriginKind"/>.</summary>
    public string? OriginSurface { get; set; }

    /// <summary>
    /// The session that asked for this one (issue #982), or null. THE LINEAGE EDGE, and the reason this
    /// table can answer questions the live roster never could: a fleet of twenty-two rows resolves into
    /// the handful of operations it actually was, long after every one of those sessions has exited.
    ///
    /// Stored as the session GUID string, matching <see cref="SessionId"/>, so a parent joins to its
    /// own row in this table. NOT a foreign key: a parent's row can be pruned by the 90-day retention
    /// while a child's remains, and a dangling id is a truthful record of a parent we no longer keep -
    /// a constraint here would force the retention sweep to either lie or cascade.
    /// </summary>
    public string? ParentSessionId { get; set; }

    /// <summary>
    /// The FACTORY this session belongs to (Factory Memory mission, phase 1), or null when it is in no
    /// factory. Lower-case factory id, as a factory is spelled everywhere else (e.g. <c>website-factory</c>).
    ///
    /// THIS ROW IS THE SOURCE OF TRUTH FOR MEMBERSHIP, and deliberately so (review finding 4). The Gateway
    /// holds a session's facts in two places: this table, and the pushed roster held in memory. The roster
    /// is EMPTY for a moment after a Gateway restart, so a membership check reading it would refuse every
    /// factory session's write in that window and - worse - copy an empty factory onto any child spawned in
    /// it, putting that child outside the factory for life. This row survives the restart, so the check and
    /// the child-copy both read it here.
    ///
    /// An ABSENT row is its own answer, not "in no factory": a session younger than its first push has no
    /// row yet, and that is a "try again in a moment", never a refusal that reads as "you do not belong".
    ///
    /// WRITE-ONCE, like <see cref="ParentSessionId"/> and the origin pair beside it: it describes the create
    /// call, so the first push carrying it is as good as the last, and a push from a Director that predates
    /// the field must not be able to blank it.
    /// </summary>
    public string? Factory { get; set; }

    /// <summary>
    /// In a TEAM's tenant, the account subject of the person whose Director ran this session
    /// (devthrottle_internal#2305), stamped by the Gateway from the Director's key through the team caller resolver
    /// (devthrottle_internal#2311) - never taken from the pushed session. Null in a personal tenant, where the tenant
    /// already names the one person, and null when the resolver could not name anyone. Write-once. The Mentor's weekly
    /// run reads "did this person run sessions this week" from this column, because by then the Director may be gone.
    /// Personally identifying: never logged.
    /// </summary>
    public string? PersonSubject { get; set; }

    /// <summary>Director-measured session creation time (SessionDto.CreatedAt) - a real measurement.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>Last terminal activity as reported by the Director, when known.</summary>
    public DateTime? LastActivityUtc { get; set; }

    /// <summary>The last push in which the Gateway observed this session (throttled - see class doc).</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>The session's last known activity state (Working/Idle/...), refreshed on the same
    /// throttle as <see cref="LastSeenUtc"/>. Display context for ended rows; open rows read live state
    /// from the roster, not from here.</summary>
    public string? LastActivityState { get; set; }

    /// <summary>Total input turns observed (operator plus agent-driven), from the pushed input stats.</summary>
    public long? TurnCount { get; set; }

    /// <summary>Completed AGENT turns (SessionDto.TurnCount, internal#625 phase 4): one flip to
    /// waiting-for-input equals one turn, counted incrementally on the Director. Distinct from
    /// <see cref="TurnCount"/>, which counts turns SUBMITTED to the session. Null when the owning
    /// Director predates the counter; a known value is never overwritten by null.</summary>
    public long? AgentTurnCount { get; set; }

    /// <summary>Total seconds the session spent waiting on the user, summed over CLOSED waiting
    /// stretches (SessionDto.CumulativeIdleSeconds, internal#625 phase 4), as last pushed. Null when
    /// the owning Director predates the clock.</summary>
    public double? CumulativeIdleSeconds { get; set; }

    /// <summary>
    /// How many times the session started waiting on the user (devthrottle_internal issue #982) - the
    /// matched pair to <see cref="CumulativeIdleSeconds"/>. Seconds waited is the total; this is the
    /// number of times, and the two together are what make either readable. High-water mark, like the
    /// counters above: a Director restart resets its own counter, and the run's record must not follow
    /// it down. Null when the owning Director predates the counter.
    /// </summary>
    public long? WaitingStretchCount { get; set; }

    /// <summary>
    /// Character volume of input submitted into this session (issue #982), operator plus agent-driven,
    /// summed from the pushed input stats. Turn counts alone flatten a one-word "yes" and a pasted
    /// design document into the same number. High-water mark; null when never reported.
    /// </summary>
    public long? InputCharacterCount { get; set; }

    /// <summary>
    /// This session's cumulative token spend (issue #982), from the pushed
    /// <c>SessionDto.TokenTotals</c>. Spend existed globally, by hour and by model, but not per
    /// session - which is what "cost per merged change" needs, joined against the commits and pull
    /// requests already on this row (the session-to-forge join product strategy decision 27 calls ours).
    ///
    /// ADDITIVE tokens only, and all four kept apart rather than pre-summed: cache reads and cache
    /// creation are priced differently from plain input, so a single total could not be turned back
    /// into money. High-water marks; null when the agent's driver reports no usage (only Claude's does
    /// today).
    /// </summary>
    public long? InputTokens { get; set; }

    /// <summary>Cumulative output tokens. See <see cref="InputTokens"/>.</summary>
    public long? OutputTokens { get; set; }

    /// <summary>Cumulative cache-read input tokens. See <see cref="InputTokens"/>.</summary>
    public long? CacheReadTokens { get; set; }

    /// <summary>Cumulative cache-creation input tokens. See <see cref="InputTokens"/>.</summary>
    public long? CacheCreationTokens { get; set; }

    /// <summary>
    /// The FULLEST the session's context window was observed to be (issue #982), in tokens.
    ///
    /// A GAUGE, not spend, and that is why it is a peak rather than a sum: context occupancy rises
    /// through a turn and DROPS when the agent compacts, so adding the readings together would produce
    /// a number that means nothing. Keeping the maximum is the honest reduction, and it is the fact
    /// behind "did this session run out of room" - a session whose peak sits near its model's window
    /// was working under pressure whether or not it visibly compacted.
    ///
    /// Null when the agent's driver reports no context reading.
    /// </summary>
    public long? PeakContextTokens { get; set; }

    /// <summary>The first user prompt, trimmed to one line - description source number two (#1862).
    /// Set once from the prompt-log ingest and never overwritten.</summary>
    public string? FirstPromptLine { get; set; }

    /// <summary>Null while open; one of <see cref="Contracts.SessionHistoryEndings"/> once ended.</summary>
    public string? EndingKind { get; set; }

    /// <summary>The folded wording for the ending. Stamped with the kind; clients render it verbatim.</summary>
    public string? EndingLabel { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    // The summary columns below are NO LONGER WRITTEN OR READ. The AI work-history summaries were removed in
    // October 2026 (nobody read them). The columns stay so no schema change is needed; rows that hold an old
    // summary age out with the 90-day retention. Dropping the columns is a separate, owner-approved migration.

    /// <summary>Who wrote the old summary: "sealed", "generated", "none" or "unavailable"; null on newer rows.</summary>
    public string? SummaryKind { get; set; }

    /// <summary>True when the summary describes a partial record - the session ended without a farewell.</summary>
    public bool SummaryIsPartial { get; set; }

    /// <summary>How many times the removed summariser tried for this row.</summary>
    public int SummaryAttempts { get; set; }

    public string? SummaryText { get; set; }

    /// <summary>JSON array of strings; null when absent.</summary>
    public string? WhatWasBuiltJson { get; set; }

    /// <summary>JSON array of strings; null when absent.</summary>
    public string? LeftUnverifiedJson { get; set; }

    /// <summary>JSON array of strings; null when absent.</summary>
    public string? BranchesJson { get; set; }

    /// <summary>JSON array of strings; null when absent.</summary>
    public string? PullRequestsJson { get; set; }

    /// <summary>JSON array of strings; null when absent.</summary>
    public string? CommitsJson { get; set; }
}
