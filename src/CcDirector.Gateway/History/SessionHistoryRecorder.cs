using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.History;

/// <summary>
/// The observer that turns the Director push stream into durable work history (issue #2194). Hooked
/// into <see cref="Streaming.DirectorHub"/> beside the other observers; every method is cheap and
/// never throws - a history hiccup must never fail a Director's push.
///
/// WRITE CADENCE - the deliberate cost decision. Directors re-push their FULL roster every ~10
/// seconds, and per-session deltas fire on every activity flip. Writing the row on every push would
/// be a database write per session per 10 seconds across the whole fleet. Instead the recorder keeps
/// an in-memory signature per session and writes only when:
///  - the session is seen for the FIRST time (the insert that makes the record power-cut-proof),
///  - a MATERIAL fact changed (name, repository, model, mission, role, number, machine), or
///  - the row's freshness is older than <see cref="FreshnessInterval"/> (the heartbeat that bounds
///    how stale an interrupted session's "last seen" can be).
/// Activity-state flips are deliberately NOT material: they arrive per turn, and the ruling that
/// needs the final state reads the recorder's in-memory cache, not the row. Worst case the row's
/// stored state is one freshness interval stale - accepted and documented.
///
/// The ENDING rulings (the dumb-client rule - concluded here, stamped once):
///  - Per-session remove while the Director is connected: "finished" when the last pushed facts say
///    the agent's process had already exited (or auto-dismiss ruled it done), else "closed".
///  - A session missing from an authoritative full snapshot: removed while the tunnel was down;
///    ruled "closed" by <see cref="SessionHistoryStore.CloseAbsentSessions"/>.
///  - The Director's clean-shutdown farewell: every remaining open row "director-stopped".
///  - Silence: concluded "interrupted" by the history sweep, not here.
/// </summary>
public sealed class SessionHistoryRecorder
{
    /// <summary>How stale a running session's row may get before a refresh write is due. Also the
    /// bound on how approximate an interrupted row's "last seen" is.</summary>
    public static readonly TimeSpan FreshnessInterval = TimeSpan.FromMinutes(5);

    private readonly SessionHistoryStore _store;
    private readonly KnownRepositoryStore? _knownRepositories;

    private sealed class TrackedSession
    {
        public string Signature = "";
        public DateTime LastWrittenUtc;
        public SessionDto? LastDto;
        public string DirectorId = "";
    }

    // Keyed tenant|sessionId: the hub serves every tenant through one recorder instance.
    private readonly ConcurrentDictionary<string, TrackedSession> _tracked = new(StringComparer.Ordinal);

    // Per tenant|directorId: the session ids present in the last observed snapshot, so the
    // reconcile query only runs when the set actually changed (not on every 10-second re-push).
    private readonly ConcurrentDictionary<string, HashSet<string>> _rosters = new(StringComparer.Ordinal);

    // Session ids whose first prompt is known handled, so prompt ingest does not hit the store per batch.
    private readonly ConcurrentDictionary<string, byte> _firstPromptHandled = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves what the Gateway knows about a Director from its live connection record. Optional
    /// so the tests that only exercise the write cadence need not stand up a registry; when it is
    /// absent every row is written with <see cref="DirectorFacts.Unknown"/>, which is exactly the
    /// behaviour that produced a table with no machine name in it, so production MUST supply one.
    /// </summary>
    private readonly Func<TenantId, string, DirectorFacts>? _directorFacts;

    public SessionHistoryRecorder(SessionHistoryStore store,
        Func<TenantId, string, DirectorFacts>? directorFacts = null,
        KnownRepositoryStore? knownRepositories = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _directorFacts = directorFacts;
        _knownRepositories = knownRepositories;
    }

    /// <summary>The Director's machine and version, or Unknown. Never throws and never lets a
    /// registry hiccup fail a push - a row without these facts is worth far more than a dropped one.</summary>
    private DirectorFacts FactsFor(TenantId tenant, string directorId)
    {
        if (_directorFacts is null) return DirectorFacts.Unknown;
        try { return _directorFacts(tenant, directorId); }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionHistoryRecorder] director facts lookup FAILED (swallowed): {ex.Message}");
            return DirectorFacts.Unknown;
        }
    }

    /// <summary>An authoritative full snapshot: upsert what is due, and reconcile removals that
    /// happened while the tunnel was down. Runs inside the hub's bound tenant scope.</summary>
    public void ObserveSnapshot(TenantId tenant, string directorId, IReadOnlyList<SessionDto> sessions)
    {
        try
        {
            var now = DateTime.UtcNow;
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var session in sessions)
            {
                if (string.IsNullOrEmpty(session.SessionId)) continue;
                present.Add(session.SessionId);
                ObserveCore(tenant, directorId, session, now);
            }

            // Reconcile only when this Director's roster actually changed (or on its first snapshot
            // after a recorder start): the query is cheap but there is no reason to run it every 10s.
            var rosterKey = Key(tenant, directorId);
            var previous = _rosters.GetValueOrDefault(rosterKey);
            if (previous is null || !previous.SetEquals(present))
            {
                _rosters[rosterKey] = present;
                _store.CloseAbsentSessions(directorId, present, now);
                if (previous is not null)
                    foreach (var gone in previous.Where(id => !present.Contains(id)))
                        _tracked.TryRemove(Key(tenant, gone), out _);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionHistoryRecorder] ObserveSnapshot FAILED (swallowed): {ex.Message}");
        }
    }

    /// <summary>A single-session delta. Runs inside the hub's bound tenant scope.</summary>
    public void Observe(TenantId tenant, string directorId, SessionDto session)
    {
        try
        {
            if (string.IsNullOrEmpty(session.SessionId)) return;
            // Deliberately no roster-set update here: the sets are only ever replaced whole by
            // ObserveSnapshot (a HashSet is not safe to mutate concurrently). A session that arrives
            // as a delta before its first snapshot merely makes the next snapshot's set-compare
            // differ once, which re-runs the (cheap, idempotent) reconcile.
            ObserveCore(tenant, directorId, session, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionHistoryRecorder] Observe FAILED (swallowed): {ex.Message}");
        }
    }

    /// <summary>
    /// A per-session remove from a connected Director - the farewell for one session. The ruling
    /// reads the last pushed facts from the in-memory cache; after a Gateway restart the cache may be
    /// empty, in which case the honest default is "closed" (the Director removed it deliberately).
    /// </summary>
    public void ObserveRemoval(TenantId tenant, string directorId, string sessionId)
    {
        try
        {
            // The roster set is not touched here (only ObserveSnapshot replaces it whole - see
            // Observe): the next snapshot's set-compare differs once and re-runs the idempotent
            // reconcile, which finds this row already ended.
            _tracked.TryRemove(Key(tenant, sessionId), out var tracked);
            _firstPromptHandled.TryRemove(Key(tenant, sessionId), out _);

            var last = tracked?.LastDto;
            var agentExited = last is not null &&
                (string.Equals(last.Status, "Exited", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(last.Status, "Failed", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(last.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(last.DismissVerdict, "done", StringComparison.OrdinalIgnoreCase));
            var kind = agentExited ? SessionHistoryEndings.Finished : SessionHistoryEndings.Closed;
            _store.RecordEnding(sessionId, kind, crashed: last?.Crashed == true, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionHistoryRecorder] ObserveRemoval FAILED (swallowed): {ex.Message}");
        }
    }

    /// <summary>The Director's clean-shutdown farewell. Runs inside the hub's bound tenant scope.</summary>
    public void ObserveDirectorStopping(TenantId tenant, string directorId)
    {
        try
        {
            _store.MarkDirectorStopped(directorId, DateTime.UtcNow);
            _rosters.TryRemove(Key(tenant, directorId), out var roster);
            if (roster is not null)
                foreach (var id in roster)
                    _tracked.TryRemove(Key(tenant, id), out _);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionHistoryRecorder] ObserveDirectorStopping FAILED (swallowed): {ex.Message}");
        }
    }

    /// <summary>
    /// Prompt-log ingest feed: capture each session's FIRST user prompt as a description source
    /// (#1862 priority two). Must be called inside the request tenant's ambient scope. Cheap: one
    /// store call per session ever (memoized; the store itself never overwrites).
    /// </summary>
    public void ObservePrompts(TenantId tenant, IReadOnlyList<PromptRecord> records)
    {
        try
        {
            foreach (var group in records.Where(r => string.Equals(r.Role, "user", StringComparison.OrdinalIgnoreCase)
                                                     && !string.IsNullOrEmpty(r.SessionId))
                                          .GroupBy(r => r.SessionId, StringComparer.Ordinal))
            {
                var memoKey = Key(tenant, group.Key);
                if (_firstPromptHandled.ContainsKey(memoKey)) continue;
                var first = group.OrderBy(r => r.TsUtc).First();
                var line = SessionHistoryFold.FirstPromptLine(first.Text);
                if (line is not null)
                    _store.SetFirstPrompt(group.Key, line);
                _firstPromptHandled[memoKey] = 1;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionHistoryRecorder] ObservePrompts FAILED (swallowed): {ex.Message}");
        }
    }

    private void ObserveCore(TenantId tenant, string directorId, SessionDto session, DateTime nowUtc)
    {
        var key = Key(tenant, session.SessionId);
        var facts = FactsFor(tenant, directorId);
        var signature = MaterialSignature(session, facts);
        var tracked = _tracked.GetOrAdd(key, static _ => new TrackedSession());

        bool writeDue;
        lock (tracked)
        {
            writeDue = tracked.LastWrittenUtc == default
                       || !string.Equals(tracked.Signature, signature, StringComparison.Ordinal)
                       || nowUtc - tracked.LastWrittenUtc >= FreshnessInterval;
            // The cache always holds the freshest facts - the removal ruling reads them even when no
            // row write was due.
            tracked.LastDto = session;
            tracked.DirectorId = directorId;
            if (writeDue)
            {
                tracked.Signature = signature;
                tracked.LastWrittenUtc = nowUtc;
            }
        }

        if (writeDue)
        {
            _store.UpsertLive(directorId, session, nowUtc, facts);
            ObserveKnownRepository(tenant, session, facts, nowUtc);
        }
    }

    /// <summary>The repository catalog is additive support for the picker. A catalog write failure is
    /// contained here so it cannot suppress the existing session-history observation.
    ///
    /// WHICH repository is the one the session was STARTED IN, not always the one it runs in: a
    /// session holding a pooled worktree runs in a throwaway slot, and recording the slot put a
    /// directory into the picker that nobody would ever choose and that the pool deletes when it takes
    /// the slot back - while the real repository it came out of never moved up the list at all. The
    /// rule is <see cref="Core.Configuration.RepositoryUsage.StartedIn"/>, shared with the Director's
    /// own registry so the two catalogs cannot disagree about which repository was used last.</summary>
    private void ObserveKnownRepository(TenantId tenant, SessionDto session, DirectorFacts facts, DateTime nowUtc)
    {
        if (_knownRepositories is null)
            return;

        var repository = Core.Configuration.RepositoryUsage.StartedIn(
            session.RepoPath, session.PooledWorktree?.Repo, session.PrimaryRepoPath);
        if (repository is null)
            return;

        var machine = string.IsNullOrWhiteSpace(session.MachineName) ? facts.MachineName : session.MachineName;
        if (string.IsNullOrWhiteSpace(machine))
            return;

        try
        {
            _knownRepositories.Observe(tenant, machine, repository, session.RepoName, nowUtc);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionHistoryRecorder] known repository observation FAILED (swallowed): {ex.Message}");
        }
    }

    /// <summary>The facts whose change forces an immediate write. Activity state and last-activity
    /// time are deliberately absent - see the class doc.</summary>
    private static string MaterialSignature(SessionDto s, DirectorFacts facts)
        // The Director's machine and VERSION are part of the signature, so an upgrade that happens
        // while a long-lived session is running is written the moment it is observed rather than
        // waiting on the five-minute heartbeat. Sessions here run for days; a version stamped at
        // first sight and never revisited would misattribute the whole run to the old build.
        => string.Join('|', facts.MachineName, facts.Version,
            s.Name, s.Number?.ToString(), s.MachineName, s.RepoPath, s.RepoName,
            s.Agent, s.CurrentModel, s.MissionName, s.ExplicitRole,
            // The birth facts (devthrottle_internal issue #982). They are stamped before launch and
            // never change, so on the normal path they arrive with the first push and this costs
            // nothing. What it buys is the ONE case where they do not: a session first seen through a
            // Director too old to report them, then re-reported by a current one mid-upgrade. Without
            // this the fill-in waits for the five-minute freshness heartbeat; with it the row is
            // corrected on the next push. The store's write-once guard is what makes adding them here
            // safe - a change in these can only ever fill a blank, never overwrite a recorded value.
            s.OriginKind, s.OriginSurface, s.ParentSessionId);

    private static string Key(TenantId tenant, string id) => $"{tenant.Value}|{id}";
}
