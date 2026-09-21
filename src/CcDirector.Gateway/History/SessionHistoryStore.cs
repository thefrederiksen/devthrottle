using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.History;

/// <summary>
/// The durable work-history store over the <c>session_history</c> and <c>session_history_rollups</c>
/// tables (issue #2194). One row per fleet session, written while it RUNS and ruled on when it ends -
/// see <see cref="SessionHistoryEntity"/> for the design. The write cadence is the RECORDER's job
/// (<see cref="SessionHistoryRecorder"/>); this store executes whatever the recorder decided, one
/// operation per call.
///
/// Threading matches the rest of the data layer: single writer, write lock, fresh pooled context per
/// operation, tenant resolved from the ambient scope by <see cref="GatewayDatabase.CreateContext()"/>.
/// </summary>
public sealed class SessionHistoryStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>The hard ceiling on one range read.</summary>
    public const int MaxListLimit = 2000;

    /// <summary>How many times the Gateway summariser tries before a summary is marked unavailable.</summary>
    public const int MaxSummaryAttempts = 3;

    public SessionHistoryStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Insert or refresh one session's row from a pushed <see cref="SessionDto"/>. Called by the
    /// recorder only when it decided a write is due (first sight, material change, or freshness drift),
    /// so this is NOT a write per push. A row previously concluded "interrupted" that reappears on the
    /// stream is REOPENED - the interrupted ruling is an inference from absence, and presence is the
    /// stronger evidence.
    /// </summary>
    public void UpsertLive(string directorId, SessionDto session, DateTime nowUtc, DirectorFacts facts = default)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionHistory.FirstOrDefault(e => e.SessionId == session.SessionId);
            if (entity is null)
            {
                entity = new SessionHistoryEntity
                {
                    TenantId = ctx.ActiveTenant!,
                    SessionId = session.SessionId,
                    StartedAtUtc = Utc(session.CreatedAt),
                };
                ctx.SessionHistory.Add(entity);
                FileLog.Write($"[SessionHistoryStore] first sight: session={session.SessionId} repo={session.RepoName}");
            }
            else if (string.Equals(entity.EndingKind, SessionHistoryEndings.Interrupted, StringComparison.Ordinal))
            {
                entity.EndingKind = null;
                entity.EndingLabel = null;
                entity.EndedAtUtc = null;
                FileLog.Write($"[SessionHistoryStore] reopened interrupted row (session reappeared): session={session.SessionId}");
            }

            entity.DirectorId = directorId;
            entity.SessionNumber = session.Number ?? entity.SessionNumber;
            entity.SessionName = string.IsNullOrWhiteSpace(session.Name) ? entity.SessionName : session.Name;
            // The pushed machine name is believed when it is there, and the Gateway's own record of
            // the connection is used when it is not - which today is always. See the entity's
            // MachineName doc for why the pushed field is empty on every client in the field.
            // Known is never overwritten by unknown, in either direction.
            var machine = !string.IsNullOrWhiteSpace(session.MachineName) ? session.MachineName
                : (!string.IsNullOrWhiteSpace(facts.MachineName) ? facts.MachineName : null);
            entity.MachineName = machine ?? entity.MachineName;
            // The version has no pushed counterpart at all - it exists only on the connection.
            entity.DirectorVersion = string.IsNullOrWhiteSpace(facts.Version) ? entity.DirectorVersion : facts.Version;
            entity.RepoPath = string.IsNullOrWhiteSpace(session.RepoPath) ? entity.RepoPath : session.RepoPath;
            entity.RepoName = string.IsNullOrWhiteSpace(session.RepoName) ? entity.RepoName : session.RepoName;
            entity.AgentKind = string.IsNullOrWhiteSpace(session.Agent) ? entity.AgentKind : session.Agent;
            entity.Model = string.IsNullOrWhiteSpace(session.CurrentModel) ? entity.Model : session.CurrentModel;
            entity.MissionName = string.IsNullOrWhiteSpace(session.MissionName) ? entity.MissionName : session.MissionName;
            // The mission KEY beside the name (issue #982): a name reads well but cannot be joined on -
            // missions get renamed, and two can share a name. Unknown never overwrites known.
            entity.MissionId = session.MissionId ?? entity.MissionId;
            entity.SessionRole = string.IsNullOrWhiteSpace(session.ExplicitRole) ? entity.SessionRole : session.ExplicitRole;
            // Birth facts (devthrottle_internal issue #982). WRITE-ONCE, unlike everything around them:
            // these describe the create call, so the first push that carries them is as good as the
            // last, and a later push that lost them (an older Director taking over mid-upgrade, a
            // reconnect from a build that predates the fields) must not be able to blank them. The
            // guard is on the STORED value being empty, not on first-sight, so a row created by an old
            // Director is still filled in the moment a new one reports the same session.
            //
            // "unknown" counts as a value here and is not overwritten later. It is the honest answer for
            // a create path that did not say, and letting a subsequent push replace it would mean the
            // recorded origin depended on which push happened to arrive - the same fact reading
            // differently run to run.
            if (string.IsNullOrEmpty(entity.OriginKind) && !string.IsNullOrWhiteSpace(session.OriginKind))
                entity.OriginKind = session.OriginKind;
            if (string.IsNullOrEmpty(entity.OriginSurface) && !string.IsNullOrWhiteSpace(session.OriginSurface))
                entity.OriginSurface = session.OriginSurface;
            if (string.IsNullOrEmpty(entity.ParentSessionId) && !string.IsNullOrWhiteSpace(session.ParentSessionId))
                entity.ParentSessionId = session.ParentSessionId;
            // CreatedAt is the Director-measured start and is stable; keep the first non-default value.
            if (entity.StartedAtUtc == default && session.CreatedAt != default)
                entity.StartedAtUtc = Utc(session.CreatedAt);
            if (session.LastActivityAt is { } activity)
                entity.LastActivityUtc = Utc(activity);
            entity.LastActivityState = string.IsNullOrWhiteSpace(session.ActivityState) ? entity.LastActivityState : session.ActivityState;
            var turns = TurnCountOf(session);
            if (turns is { } t && (entity.TurnCount is not { } existing || t > existing))
                entity.TurnCount = t;
            // The supervision facts (internal#625 phase 4). Null means an older Director - unknown
            // never overwrites a known value - and both only move forward, so a Director restart
            // (whose counters start again at zero) cannot erase the run's high-water mark.
            if (session.TurnCount is { } agentTurns && (entity.AgentTurnCount is not { } at || agentTurns > at))
                entity.AgentTurnCount = agentTurns;
            if (session.CumulativeIdleSeconds is { } idle && (entity.CumulativeIdleSeconds is not { } ci || idle > ci))
                entity.CumulativeIdleSeconds = idle;
            // The remaining per-session facts issue #982 asked for, all on the SAME high-water-mark
            // rule as the two above and for the same reason: a Director restart begins its counters
            // again at zero, and a monotonic record must not follow them down. Null is unknown and
            // never overwrites a known value.
            if (session.WaitingStretchCount is { } waits && (entity.WaitingStretchCount is not { } ws || waits > ws))
                entity.WaitingStretchCount = waits;
            if (CharacterCountOf(session) is { } chars && (entity.InputCharacterCount is not { } ic || chars > ic))
                entity.InputCharacterCount = chars;
            if (session.TokenTotals is { } tokens)
            {
                // Cumulative spend, kept per kind rather than pre-summed: cache reads and cache
                // creation are priced differently from plain input, so one total could never be turned
                // back into money - which is the whole point of keeping spend per session.
                if (entity.InputTokens is not { } it || tokens.InputTokens > it) entity.InputTokens = tokens.InputTokens;
                if (entity.OutputTokens is not { } ot || tokens.OutputTokens > ot) entity.OutputTokens = tokens.OutputTokens;
                if (entity.CacheReadTokens is not { } crt || tokens.CacheReadTokens > crt) entity.CacheReadTokens = tokens.CacheReadTokens;
                if (entity.CacheCreationTokens is not { } cct || tokens.CacheCreationTokens > cct) entity.CacheCreationTokens = tokens.CacheCreationTokens;
                // Context occupancy is a GAUGE - it rises through a turn and DROPS on a compaction -
                // so the maximum is the only reduction of it that means anything. Summing observations
                // of a gauge produces a number with no unit.
                if (entity.PeakContextTokens is not { } pct || tokens.ContextTokens > pct) entity.PeakContextTokens = tokens.ContextTokens;
            }
            entity.LastSeenUtc = nowUtc;

            ctx.SaveChanges();
        }
    }

    /// <summary>Total input turns (operator plus agent-driven) off the pushed stats, or null.</summary>
    public static long? TurnCountOf(SessionDto session)
    {
        var stats = session.InputStats;
        if (stats is null) return null;
        return stats.Buckets.Sum(b => b.Turns) + stats.AgentDrivenTurns;
    }

    /// <summary>Total input CHARACTER volume (operator plus agent-driven) off the pushed stats, or
    /// null (issue #982). Counted on the same population as <see cref="TurnCountOf"/> so the two are
    /// comparable: a turn count alone cannot tell a one-word "yes" from a pasted design document.</summary>
    public static long? CharacterCountOf(SessionDto session)
    {
        var stats = session.InputStats;
        if (stats is null) return null;
        return stats.Buckets.Sum(b => b.Characters) + stats.AgentDrivenCharacters;
    }

    /// <summary>
    /// Stamp a farewell ending on one row. Only an OPEN or previously-interrupted row is stamped: a
    /// farewell arriving twice keeps the first ruling. No row is CREATED here - an ending for a session
    /// the Gateway never saw alive has nothing usable to record.
    /// </summary>
    public void RecordEnding(string sessionId, string endingKind, bool crashed, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionHistory.FirstOrDefault(e => e.SessionId == sessionId);
            if (entity is null)
            {
                FileLog.Write($"[SessionHistoryStore] RecordEnding skipped (no row): session={sessionId} kind={endingKind}");
                return;
            }
            if (!string.IsNullOrEmpty(entity.EndingKind)
                && !string.Equals(entity.EndingKind, SessionHistoryEndings.Interrupted, StringComparison.Ordinal))
                return; // already ruled by a farewell; keep the first ruling

            entity.EndingKind = endingKind;
            entity.EndedAtUtc = nowUtc;
            entity.EndingLabel = SessionHistoryFold.EndingLabel(endingKind, crashed, nowUtc);
            ctx.SaveChanges();
            FileLog.Write($"[SessionHistoryStore] ending: session={sessionId} kind={endingKind}");
        }
    }

    /// <summary>
    /// The Director's clean-shutdown farewell: every still-open row of this Director ends
    /// "director-stopped". Sessions the Director already removed individually keep their own ruling.
    /// Returns how many rows were stamped.
    /// </summary>
    public int MarkDirectorStopped(string directorId, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var open = ctx.SessionHistory
                .Where(e => e.DirectorId == directorId && (e.EndingKind == null || e.EndingKind == ""))
                .ToList();
            foreach (var entity in open)
            {
                entity.EndingKind = SessionHistoryEndings.DirectorStopped;
                entity.EndedAtUtc = nowUtc;
                entity.EndingLabel = SessionHistoryFold.EndingLabel(SessionHistoryEndings.DirectorStopped, crashed: false, nowUtc);
            }
            if (open.Count > 0)
            {
                ctx.SaveChanges();
                FileLog.Write($"[SessionHistoryStore] director stopped: director={directorId} stamped {open.Count} open row(s)");
            }
            return open.Count;
        }
    }

    /// <summary>
    /// Reconcile against an authoritative full snapshot: open rows of this Director whose session is
    /// NOT in the snapshot were removed while the tunnel was down (the Director's per-session remove
    /// no-ops when disconnected, and only the next snapshot reconciles). The Director is alive and no
    /// longer runs them, so they ended deliberately on its side: ruled "closed". Returns how many.
    /// </summary>
    public int CloseAbsentSessions(string directorId, IReadOnlySet<string> presentSessionIds, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var open = ctx.SessionHistory
                .Where(e => e.DirectorId == directorId && (e.EndingKind == null || e.EndingKind == ""))
                .ToList();
            var absent = open.Where(e => !presentSessionIds.Contains(e.SessionId)).ToList();
            foreach (var entity in absent)
            {
                entity.EndingKind = SessionHistoryEndings.Closed;
                entity.EndedAtUtc = nowUtc;
                entity.EndingLabel = SessionHistoryFold.EndingLabel(SessionHistoryEndings.Closed, crashed: false, nowUtc);
            }
            if (absent.Count > 0)
            {
                ctx.SaveChanges();
                FileLog.Write($"[SessionHistoryStore] snapshot reconcile: director={directorId} closed {absent.Count} absent row(s)");
            }
            return absent.Count;
        }
    }

    /// <summary>
    /// The silence rule (issue #1862 lifted to the Gateway): every open row not refreshed since
    /// <paramref name="lastSeenBeforeUtc"/> is CONCLUDED interrupted. Nobody reports this ending - the
    /// absence of a goodbye is the evidence. The end time is the last observation, and the label says
    /// "last seen" so the approximation is never read as a measurement. Returns how many were ruled.
    /// </summary>
    public int ConcludeInterrupted(DateTime lastSeenBeforeUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var silent = ctx.SessionHistory
                .Where(e => (e.EndingKind == null || e.EndingKind == "") && e.LastSeenUtc < lastSeenBeforeUtc)
                .ToList();
            foreach (var entity in silent)
            {
                entity.EndingKind = SessionHistoryEndings.Interrupted;
                entity.EndedAtUtc = entity.LastSeenUtc;
                entity.EndingLabel = SessionHistoryFold.EndingLabel(SessionHistoryEndings.Interrupted, crashed: false, entity.LastSeenUtc);
            }
            if (silent.Count > 0)
            {
                ctx.SaveChanges();
                FileLog.Write($"[SessionHistoryStore] concluded interrupted: {silent.Count} row(s) silent since before {lastSeenBeforeUtc:O}");
            }
            return silent.Count;
        }
    }

    /// <summary>Set the first-prompt description source, once. Later prompts never overwrite it.</summary>
    public void SetFirstPrompt(string sessionId, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionHistory.FirstOrDefault(e => e.SessionId == sessionId);
            if (entity is null || !string.IsNullOrEmpty(entity.FirstPromptLine)) return;
            entity.FirstPromptLine = line;
            ctx.SaveChanges();
        }
    }

    /// <summary>
    /// The session seals its own record on a clean shutdown - its account wins over anything the
    /// Gateway generated. Returns false when no row exists for the session.
    /// </summary>
    public bool SealSummary(string sessionId, SealSessionSummaryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Summary))
            throw new ArgumentException("A sealed summary needs prose; an empty seal records nothing.", nameof(request));

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionHistory.FirstOrDefault(e => e.SessionId == sessionId);
            if (entity is null) return false;

            entity.SummaryKind = SessionHistorySummaryKinds.Sealed;
            entity.SummaryIsPartial = false;
            entity.SummaryText = request.Summary.Trim();
            entity.WhatWasBuiltJson = SessionHistoryFold.ToJsonList(request.WhatWasBuilt);
            entity.LeftUnverifiedJson = SessionHistoryFold.ToJsonList(request.LeftUnverified);
            entity.BranchesJson = SessionHistoryFold.ToJsonList(request.Branches);
            entity.PullRequestsJson = SessionHistoryFold.ToJsonList(request.PullRequests);
            entity.CommitsJson = SessionHistoryFold.ToJsonList(request.Commits);
            ctx.SaveChanges();
            FileLog.Write($"[SessionHistoryStore] summary sealed by the session: session={sessionId}");
            return true;
        }
    }

    /// <summary>
    /// Ended rows still owed a summary: no summary kind yet, attempts under the cap, ended before
    /// <paramref name="endedBeforeUtc"/> (a small settling delay so a seal arriving right after the
    /// farewell wins the race). Detached copies, oldest ending first, capped.
    /// </summary>
    public IReadOnlyList<SessionHistoryEntity> PendingSummaries(DateTime endedBeforeUtc, int max)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.SessionHistory.AsNoTracking()
                .Where(e => e.EndingKind != null && e.EndingKind != ""
                            && (e.SummaryKind == null || e.SummaryKind == "")
                            && e.SummaryAttempts < MaxSummaryAttempts
                            && e.EndedAtUtc != null && e.EndedAtUtc < endedBeforeUtc)
                .OrderBy(e => e.EndedAtUtc)
                .Take(Math.Clamp(max, 1, 50))
                .ToList();
        }
    }

    /// <summary>Store a Gateway-generated summary (or the honest "none"/"unavailable" verdicts).
    /// Never overwrites a sealed summary - the session's own account wins.</summary>
    public void StoreGeneratedSummary(string sessionId, string summaryKind, bool isPartial, string? summaryText,
        IReadOnlyList<string>? whatWasBuilt, IReadOnlyList<string>? leftUnverified,
        IReadOnlyList<string>? branches, IReadOnlyList<string>? pullRequests, IReadOnlyList<string>? commits)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionHistory.FirstOrDefault(e => e.SessionId == sessionId);
            if (entity is null) return;
            if (string.Equals(entity.SummaryKind, SessionHistorySummaryKinds.Sealed, StringComparison.Ordinal))
                return;

            entity.SummaryKind = summaryKind;
            entity.SummaryIsPartial = isPartial;
            entity.SummaryText = string.IsNullOrWhiteSpace(summaryText) ? null : summaryText.Trim();
            entity.WhatWasBuiltJson = SessionHistoryFold.ToJsonList(whatWasBuilt);
            entity.LeftUnverifiedJson = SessionHistoryFold.ToJsonList(leftUnverified);
            entity.BranchesJson = SessionHistoryFold.ToJsonList(branches);
            entity.PullRequestsJson = SessionHistoryFold.ToJsonList(pullRequests);
            entity.CommitsJson = SessionHistoryFold.ToJsonList(commits);
            ctx.SaveChanges();
            FileLog.Write($"[SessionHistoryStore] summary stored: session={sessionId} kind={summaryKind} partial={isPartial}");
        }
    }

    /// <summary>
    /// Count one failed summarisation attempt. At <see cref="MaxSummaryAttempts"/> the summary is
    /// marked unavailable - the record stands without one rather than billing a broken path forever.
    /// </summary>
    public void NoteSummaryFailure(string sessionId)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionHistory.FirstOrDefault(e => e.SessionId == sessionId);
            if (entity is null) return;
            entity.SummaryAttempts++;
            if (entity.SummaryAttempts >= MaxSummaryAttempts && string.IsNullOrEmpty(entity.SummaryKind))
            {
                entity.SummaryKind = SessionHistorySummaryKinds.Unavailable;
                FileLog.Write($"[SessionHistoryStore] summary marked unavailable after {entity.SummaryAttempts} attempts: session={sessionId}");
            }
            ctx.SaveChanges();
        }
    }

    /// <summary>
    /// Every session whose observed life overlaps the inclusive UTC window - ended and still running
    /// alike ("what am I working on" and "what did I work on Tuesday" are the same record). Folded
    /// DTOs, newest start first.
    /// </summary>
    public IReadOnlyList<WorkHistorySessionDto> ReadRange(DateTime fromUtc, DateTime toUtc, int limit = 1000)
    {
        var take = Math.Clamp(limit, 1, MaxListLimit);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.SessionHistory.AsNoTracking()
                .Where(e => e.LastSeenUtc >= fromUtc && e.StartedAtUtc <= toUtc)
                .OrderByDescending(e => e.StartedAtUtc)
                .Take(take)
                .ToList()
                .Select(SessionHistoryFold.ToDto)
                .ToList();
        }
    }

    /// <summary>
    /// The same sessions, in the same order and under the same limit as <see cref="ReadRange"/>, carrying ONLY what
    /// the roll-up grouping and its input hash read: the session, its repository, its observed life, and its ending
    /// and summary state (<see cref="SessionHistorySummarizer.RollupGroups"/>, <see cref="SessionHistorySummarizer.InputHash"/>).
    /// Every other field of the returned records is left empty - they are not session records to show anyone.
    ///
    /// The history sweep runs every two minutes for every account and only needs to know WHICH roll-ups are stale;
    /// reading thirty days of full rows (four JSON lists, the first prompt line and more) to find that out was a large,
    /// steady read for nothing (devthrottle_internal#2199). The full rows are read, with
    /// <see cref="ReadMany"/>, only for the groups that are actually rewritten.
    /// </summary>
    public IReadOnlyList<WorkHistorySessionDto> ReadRollupInputs(DateTime fromUtc, DateTime toUtc, int limit = 1000)
    {
        var take = Math.Clamp(limit, 1, MaxListLimit);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.SessionHistory.AsNoTracking()
                .Where(e => e.LastSeenUtc >= fromUtc && e.StartedAtUtc <= toUtc)
                .OrderByDescending(e => e.StartedAtUtc)
                .Take(take)
                .Select(e => new { e.SessionId, e.RepoName, e.RepoPath, e.StartedAtUtc, e.LastSeenUtc, e.EndingKind, e.SummaryKind, e.SummaryText })
                .ToList()
                .Select(e => new WorkHistorySessionDto
                {
                    SessionId = e.SessionId,
                    RepoName = e.RepoName,
                    RepoPath = e.RepoPath,
                    StartedAtUtc = e.StartedAtUtc,
                    LastSeenUtc = e.LastSeenUtc,
                    EndingKind = e.EndingKind,
                    SummaryKind = e.SummaryKind,
                    SummaryText = e.SummaryText,
                    EndingTone = "",
                    DescriptionLine = "",
                })
                .ToList();
        }
    }

    /// <summary>These sessions' full records, folded, in no particular order. A session with no record is absent.</summary>
    public IReadOnlyList<WorkHistorySessionDto> ReadMany(IReadOnlyCollection<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        if (sessionIds.Count == 0) return Array.Empty<WorkHistorySessionDto>();
        var ids = sessionIds.ToList();
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.SessionHistory.AsNoTracking()
                .Where(e => ids.Contains(e.SessionId))
                .ToList()
                .Select(SessionHistoryFold.ToDto)
                .ToList();
        }
    }

    /// <summary>
    /// How the sessions that STARTED in the inclusive UTC window came to exist (devthrottle_internal
    /// issue #982) - the counts behind "what share of sessions do agents start", plus how many carry a
    /// lineage edge at all.
    ///
    /// Counted by START, not by overlap. <see cref="ReadRange"/> deliberately returns every session
    /// whose life TOUCHED the window, because "what was I working on Tuesday" includes a session that
    /// began on Monday. This question is a different one - how sessions come into being - and a session
    /// is born once. Using the overlap window here would count a long-running session in every window
    /// it survived into, which for a fleet whose agent-started sessions are typically short and whose
    /// human-started ones are long would bias the share the wrong way, quietly, and by more the wider
    /// the window.
    ///
    /// A row that predates the origin fields is counted under the null key, kept apart from "unknown":
    /// one means the Gateway was not asking, the other that it asked and got nothing. A caller
    /// reporting a share must say what it did with both.
    /// </summary>
    public SessionOriginTotals OriginTotals(DateTime fromUtc, DateTime toUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var rows = ctx.SessionHistory.AsNoTracking()
                .Where(e => e.StartedAtUtc >= fromUtc && e.StartedAtUtc <= toUtc)
                .Select(e => new { e.OriginKind, e.OriginSurface, e.ParentSessionId, e.StartedAtUtc })
                .ToList();

            var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
            var bySurface = new Dictionary<string, int>(StringComparer.Ordinal);
            var withParent = 0;
            DateTime? earliest = null;
            foreach (var r in rows)
            {
                var kind = string.IsNullOrEmpty(r.OriginKind) ? NotRecorded : r.OriginKind;
                var surface = string.IsNullOrEmpty(r.OriginSurface) ? NotRecorded : r.OriginSurface;
                byKind[kind] = byKind.TryGetValue(kind, out var k) ? k + 1 : 1;
                bySurface[surface] = bySurface.TryGetValue(surface, out var s) ? s + 1 : 1;
                if (!string.IsNullOrEmpty(r.ParentSessionId)) withParent++;
                if (earliest is not { } e || r.StartedAtUtc < e) earliest = r.StartedAtUtc;
            }

            return new SessionOriginTotals(fromUtc, toUtc, earliest, rows.Count, withParent, byKind, bySurface);
        }
    }

    /// <summary>
    /// Every computer the current account has a session history row for: when its earliest kept session started,
    /// which agent that session ran, and when a session there was last seen (the Fleet Manager mission, step 5).
    /// Read inside the caller's account scope, like every other read here. History is kept for
    /// <see cref="SessionHistorySweep.Retention"/>, so "earliest" means the earliest the Gateway still holds.
    /// </summary>
    public IReadOnlyList<MachineHistory> Machines()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            // Grouped in the database: the Settings tab reads this on a timer, and a busy account holds thousands
            // of rows. One more lookup per computer finds the agent of its earliest session.
            var groups = ctx.SessionHistory.AsNoTracking()
                .Where(e => e.MachineName != null && e.MachineName != "")
                .GroupBy(e => e.MachineName!)
                .Select(g => new { Machine = g.Key, First = g.Min(e => e.StartedAtUtc), Last = g.Max(e => e.LastSeenUtc) })
                .ToList();

            return groups
                .GroupBy(g => g.Machine.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(same =>
                {
                    var first = same.OrderBy(g => g.First).First();
                    var agent = ctx.SessionHistory.AsNoTracking()
                        .Where(e => e.MachineName == first.Machine && e.StartedAtUtc == first.First)
                        .Select(e => e.AgentKind)
                        .FirstOrDefault();
                    return new MachineHistory(first.Machine.Trim(), first.First, agent, same.Max(g => g.Last));
                })
                .ToList();
        }
    }

    /// <summary>The bucket key for a row written before the origin fields existed. Deliberately NOT
    /// "unknown", which is a recorded answer.</summary>
    public const string NotRecorded = "notRecorded";

    /// <summary>One session's folded record, or null.</summary>
    public WorkHistorySessionDto? Get(string sessionId)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionHistory.AsNoTracking().FirstOrDefault(e => e.SessionId == sessionId);
            return entity is null ? null : SessionHistoryFold.ToDto(entity);
        }
    }

    /// <summary>Cached roll-ups for days in the inclusive window, any repository group.</summary>
    public IReadOnlyList<SessionHistoryRollupEntity> ReadRollups(DateTime fromDayUtc, DateTime toDayUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.SessionHistoryRollups.AsNoTracking()
                .Where(r => r.DayUtc >= fromDayUtc.Date && r.DayUtc <= toDayUtc.Date)
                .ToList();
        }
    }

    /// <summary>Insert or replace one cached roll-up row.</summary>
    public void SaveRollup(string repoKey, DateTime dayUtc, string? summaryText, string inputHash, int attempts, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var day = dayUtc.Date;
            var entity = ctx.SessionHistoryRollups.FirstOrDefault(r => r.RepoKey == repoKey && r.DayUtc == day);
            if (entity is null)
            {
                entity = new SessionHistoryRollupEntity
                {
                    TenantId = ctx.ActiveTenant!,
                    RepoKey = repoKey,
                    DayUtc = day,
                };
                ctx.SessionHistoryRollups.Add(entity);
            }
            entity.SummaryText = string.IsNullOrWhiteSpace(summaryText) ? null : summaryText.Trim();
            entity.InputHash = inputHash;
            entity.Attempts = attempts;
            entity.ComputedAtUtc = nowUtc;
            ctx.SaveChanges();
        }
    }

    /// <summary>The 90-day retention prune, called per tenant by the history sweep. Only ENDED session
    /// rows are pruned - an open row past the cutoff is the interrupted ruling's job, never retention's.
    /// Returns rows deleted across both tables.</summary>
    public int PurgeOlderThan(DateTime cutoffUtc)
    {
        var cutoff = DateTime.SpecifyKind(cutoffUtc.ToUniversalTime(), DateTimeKind.Utc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var sessions = ctx.SessionHistory
                .Where(e => e.EndingKind != null && e.EndingKind != "" && e.LastSeenUtc < cutoff)
                .ExecuteDelete();
            var rollups = ctx.SessionHistoryRollups
                .Where(r => r.DayUtc < cutoff.Date)
                .ExecuteDelete();
            var deleted = sessions + rollups;
            if (deleted > 0)
                FileLog.Write($"[SessionHistoryStore] PurgeOlderThan: removed {sessions} session row(s) and {rollups} rollup row(s) older than {cutoff:O}");
            return deleted;
        }
    }

    private static DateTime Utc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}

/// <summary>
/// How the sessions born in one window came to exist (devthrottle_internal issue #982), as counted by
/// <see cref="SessionHistoryStore.OriginTotals"/>.
///
/// Counts only - no share, no percentage. The interesting ratio ("agents start X% of sessions") depends
/// on what its author decides to do with the <see cref="SessionHistoryStore.NotRecorded"/> and
/// "unknown" buckets, and there is no single right answer: excluding them measures the sessions we can
/// account for, including them measures the fleet. Computing one here would fix that choice for every
/// reader and hide it from all of them, which is how a number becomes a claim nobody can check.
/// </summary>
/// <param name="FromUtc">Inclusive start of the birth window ASKED FOR - not where the record
/// actually begins. See <paramref name="EarliestStartUtc"/>.</param>
/// <param name="ToUtc">Inclusive end of the birth window.</param>
/// <param name="EarliestStartUtc">The oldest birth actually found, or null when the window is empty.
/// This is where the RECORD begins, which is rarely where the window does and is never where the fleet
/// does: retention prunes from the front, and the origin fields only started being written the day they
/// shipped. A caller quoting a share over "all time" has to say this date out loud, or it is quoting a
/// denominator it has not got.</param>
/// <param name="Sessions">How many sessions started in the window - the denominator.</param>
/// <param name="WithParent">How many of those name a parent session. Never larger than the "agent"
/// count in <paramref name="ByKind"/>: a parent is only kept on an agent origin.</param>
/// <param name="ByKind">Session count per origin kind, plus the not-recorded bucket.</param>
/// <param name="BySurface">Session count per origin surface, plus the not-recorded bucket.</param>
public sealed record SessionOriginTotals(
    DateTime FromUtc,
    DateTime ToUtc,
    DateTime? EarliestStartUtc,
    int Sessions,
    int WithParent,
    IReadOnlyDictionary<string, int> ByKind,
    IReadOnlyDictionary<string, int> BySurface);

/// <summary>One computer's session history, as <see cref="SessionHistoryStore.Machines"/> reads it.</summary>
/// <param name="MachineName">The computer's name, as its earliest kept session recorded it.</param>
/// <param name="FirstStartedUtc">When that earliest kept session started.</param>
/// <param name="FirstAgent">The agent that session ran, or null when it was not recorded.</param>
/// <param name="LastSeenUtc">When any session on this computer was last seen.</param>
public sealed record MachineHistory(string MachineName, DateTime FirstStartedUtc, string? FirstAgent, DateTime LastSeenUtc);
