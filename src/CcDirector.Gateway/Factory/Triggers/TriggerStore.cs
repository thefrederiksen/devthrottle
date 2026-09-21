using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Triggers;

/// <summary>What recording one check wrote: the run row, and the trigger as it now stands.</summary>
public sealed record TriggerCheckRecorded(TriggerRunEntity Run, TriggerEntity Trigger);

/// <summary>
/// THE TRIGGERS AND THEIR RUN HISTORY, per account, over the <c>triggers</c> and <c>trigger_runs</c> tables (the
/// Website Business Factory mission, product track). Tenant-partitioned by construction: every method takes the
/// account and opens a context scoped to it.
///
/// Rows handed out belong to a context that is already disposed, so changing one changes nothing stored. Writes
/// are serialised by one lock, the Gateway being a single writer.
///
/// THE HISTORY IS KEPT: every check adds a row, and only rows older than the newest <see cref="RunsKept"/> of a
/// trigger are ever dropped. Deleting a trigger keeps its history.
/// </summary>
public sealed class TriggerStore
{
    /// <summary>The run history is never pruned below this many rows per trigger.</summary>
    public const int RunsKept = 500;

    // Prune in batches rather than on every insert: the history may grow this far past RunsKept before the
    // oldest rows go, which keeps "never below 500" true while not deleting one row per check.
    private const int PruneSlack = 50;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public TriggerStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Every trigger of the account, by name.</summary>
    public IReadOnlyList<TriggerEntity> List(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.Triggers.AsNoTracking().ToList()
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>One trigger by its id or its name (names compared ignoring case), or null.</summary>
    public TriggerEntity? Find(TenantId tenant, string idOrName)
    {
        using var ctx = _db.CreateContext(tenant);
        return FindIn(ctx.Triggers.AsNoTracking(), idOrName);
    }

    /// <summary>Create a trigger. Returns the stored row, or the reason it was refused. The request must already
    /// have passed <see cref="TriggerDefinition.Validate"/> as a create.</summary>
    public (TriggerEntity? trigger, string? error) Create(
        TenantId tenant, TriggerDefinitionRequest req, string createdBy, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(req);
        FileLog.Write($"[TriggerStore] Create: tenant={tenant.ToLogString()}, name={req.Name}, machine={req.Machine}");
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var name = req.Name!.Trim();
            if (NameTaken(ctx, name, exceptId: null))
                return (null, $"a trigger named '{name}' already exists in this account");

            var row = new TriggerEntity
            {
                Name = name,
                Factory = req.Factory!.Trim(),
                FactoryAgent = req.FactoryAgent!.Trim(),
                Machine = req.Machine!.Trim(),
                RepoPath = req.RepoPath!.Trim(),
                CheckCommand = req.CheckCommand!.Trim(),
                IntervalSeconds = req.IntervalSeconds!.Value,
                Prompt = req.Prompt!,
                Paused = req.Paused ?? false,
                CreatedBy = Cap(createdBy, 256),
                CreatedUtc = Utc(nowUtc),
            };
            row.TenantId = ctx.ActiveTenant!;
            ctx.Triggers.Add(row);
            ctx.SaveChanges();
            FileLog.Write($"[TriggerStore] Create: id={row.Id}, name={row.Name}");
            return (row, null);
        }
    }

    /// <summary>Change a trigger's definition. A null field keeps its value. Returns the stored row, null when
    /// there is no such trigger, or the reason it was refused.</summary>
    public (TriggerEntity? trigger, string? error) Update(TenantId tenant, string idOrName, TriggerDefinitionRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        FileLog.Write($"[TriggerStore] Update: tenant={tenant.ToLogString()}, trigger={idOrName}");
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = FindIn(ctx.Triggers, idOrName);
            if (row is null) return (null, null);

            if (req.Name is not null)
            {
                var name = req.Name.Trim();
                if (NameTaken(ctx, name, exceptId: row.Id))
                    return (null, $"a trigger named '{name}' already exists in this account");
                row.Name = name;
            }
            if (req.Factory is not null) row.Factory = req.Factory.Trim();
            if (req.FactoryAgent is not null) row.FactoryAgent = req.FactoryAgent.Trim();
            if (req.Machine is not null && !string.Equals(row.Machine, req.Machine.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // A trigger moved to another machine is no longer the business of the Director that held it.
                row.Machine = req.Machine.Trim();
                row.ClaimedByDirectorId = "";
                row.ClaimedUtc = null;
            }
            if (req.RepoPath is not null) row.RepoPath = req.RepoPath.Trim();
            if (req.CheckCommand is not null) row.CheckCommand = req.CheckCommand.Trim();
            if (req.IntervalSeconds is { } interval) row.IntervalSeconds = interval;
            if (req.Prompt is not null) row.Prompt = req.Prompt;
            if (req.Paused is { } paused) row.Paused = paused;
            ctx.SaveChanges();
            return (row, null);
        }
    }

    /// <summary>Pause or resume a trigger. A paused trigger is still checked - every check is still recorded -
    /// but it starts nothing. Returns the stored row, or null when there is no such trigger.</summary>
    public TriggerEntity? SetPaused(TenantId tenant, string idOrName, bool paused)
    {
        FileLog.Write($"[TriggerStore] SetPaused: tenant={tenant.ToLogString()}, trigger={idOrName}, paused={paused}");
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = FindIn(ctx.Triggers, idOrName);
            if (row is null) return null;
            row.Paused = paused;
            ctx.SaveChanges();
            return row;
        }
    }

    /// <summary>
    /// Resume a trigger, and when it was paused, release its one-at-a-time lock: forget the session it last started,
    /// so the next check that counts work starts a new one even if the Gateway still believes that session lives.
    /// This is the owner's way out of the RED "session ... has not ended" without deleting the trigger - pause, then
    /// resume. Resuming a trigger that was not paused changes nothing. Returns the stored row and the session id the
    /// lock was released from (null when nothing was released), or a null row when there is no such trigger.
    /// </summary>
    public (TriggerEntity? Trigger, string? ReleasedSessionId) Resume(TenantId tenant, string idOrName)
    {
        FileLog.Write($"[TriggerStore] Resume: tenant={tenant.ToLogString()}, trigger={idOrName}");
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = FindIn(ctx.Triggers, idOrName);
            if (row is null) return (null, null);

            string? released = null;
            if (row.Paused && !string.IsNullOrEmpty(row.LastSessionId))
            {
                released = row.LastSessionId;
                row.LastSessionId = null;
                row.LastStartedUtc = null;
            }
            row.Paused = false;
            ctx.SaveChanges();
            FileLog.Write($"[TriggerStore] Resume: trigger={row.Name}, released={released ?? "nothing"}");
            return (row, released);
        }
    }

    /// <summary>Delete a trigger's definition. Its run history is kept. False when there is no such trigger.</summary>
    public bool Delete(TenantId tenant, string idOrName)
    {
        FileLog.Write($"[TriggerStore] Delete: tenant={tenant.ToLogString()}, trigger={idOrName}");
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = FindIn(ctx.Triggers, idOrName);
            if (row is null) return false;
            ctx.Triggers.Remove(row);
            ctx.SaveChanges();
            return true;
        }
    }

    /// <summary>A trigger's checks, newest first, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<TriggerRunEntity> ListRuns(TenantId tenant, Guid triggerId, int limit)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.TriggerRuns.AsNoTracking()
            .Where(r => r.TriggerId == triggerId)
            .ToList()
            .OrderByDescending(r => r.RecordedUtc)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    /// <summary>
    /// The triggers this Director must check: every trigger on its machine that no other Director holds. A trigger
    /// is held by the Director that claimed it for as long as that Director keeps asking; a claim not renewed for
    /// two intervals and a minute lapses, and the next Director on the machine to ask takes it over. So several
    /// Directors on one machine never all run one check, and a Director that goes away does not strand it.
    /// </summary>
    public IReadOnlyList<TriggerEntity> ClaimForDirector(TenantId tenant, string directorId, string machine, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var now = Utc(nowUtc);
            var mine = new List<TriggerEntity>();
            foreach (var row in ctx.Triggers.ToList()
                         .Where(t => string.Equals(t.Machine, machine, StringComparison.OrdinalIgnoreCase)))
            {
                if (IsHeldByAnother(row, directorId, now)) continue;
                row.ClaimedByDirectorId = directorId;
                row.ClaimedUtc = now;
                mine.Add(row);
            }
            ctx.SaveChanges();
            FileLog.Write($"[TriggerStore] ClaimForDirector: tenant={tenant.ToLogString()}, director={directorId}, machine={machine}, triggers={mine.Count}");
            return mine;
        }
    }

    /// <summary>True when a Director other than <paramref name="directorId"/> holds this trigger's check now.</summary>
    public static bool IsHeldByAnother(TriggerEntity trigger, string directorId, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(trigger.ClaimedByDirectorId)) return false;
        if (string.Equals(trigger.ClaimedByDirectorId, directorId, StringComparison.OrdinalIgnoreCase)) return false;
        return trigger.ClaimedUtc is { } claimed && Utc(nowUtc) - claimed <= ClaimLapse(trigger.IntervalSeconds);
    }

    /// <summary>How long a claim lasts without being renewed: two intervals and a minute.</summary>
    public static TimeSpan ClaimLapse(int intervalSeconds) => TimeSpan.FromSeconds(intervalSeconds * 2.0 + 60);

    /// <summary>
    /// Record one check: add its run row, bring the trigger's last-check facts up to date, renew the reporting
    /// Director's claim, and - on a <see cref="TriggerRunOutcome.Started"/> row - make the started session the
    /// one the one-at-a-time lock now waits on. Returns null when the trigger no longer exists.
    /// </summary>
    public TriggerCheckRecorded? RecordCheck(
        TenantId tenant, Guid triggerId, string directorId, DateTime checkedUtc, string outcome, int? count,
        string? sessionId, string? reason, DateTime nowUtc)
        => Record(tenant, triggerId, directorId, checkedUtc, outcome, count, sessionId, reason, nowUtc,
            endsPendingStart: false);

    /// <summary>
    /// Take the one-at-a-time lock BEFORE a session start begins: the trigger now has a start time and no session
    /// yet, which <see cref="TriggerService.IsLastSessionAlive"/> reads as a start in flight. Also brings the
    /// last-check time and the reporting Director's claim up to date, as recording a check does, so the trigger does
    /// not read as silent while its start runs. No run row is written here: the start's own result writes it, once.
    /// Returns false when the trigger no longer exists.
    /// </summary>
    public bool BeginStart(TenantId tenant, Guid triggerId, string directorId, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var trigger = ctx.Triggers.FirstOrDefault(t => t.Id == triggerId);
            if (trigger is null) return false;

            var now = Utc(nowUtc);
            trigger.LastCheckUtc = now;
            trigger.ClaimedByDirectorId = Cap(directorId, 64);
            trigger.ClaimedUtc = now;
            trigger.LastSessionId = null;
            trigger.LastStartedUtc = now;
            ctx.SaveChanges();
            FileLog.Write($"[TriggerStore] BeginStart: trigger={triggerId}, director={directorId}, lock taken with no session yet");
            return true;
        }
    }

    /// <summary>
    /// Record what a start begun by <see cref="BeginStart"/> came to - the one run row of the check that asked for
    /// it. A <see cref="TriggerRunOutcome.Started"/> row makes its session the lock; a
    /// <see cref="TriggerRunOutcome.Failed"/> row releases the pending lock, so the next check that counts work
    /// tries again. Returns null when the trigger no longer exists.
    /// </summary>
    public TriggerCheckRecorded? RecordStartResult(
        TenantId tenant, Guid triggerId, string directorId, DateTime checkedUtc, string outcome, int count,
        string? sessionId, string? reason, DateTime nowUtc)
    {
        if (outcome != TriggerRunOutcome.Started && outcome != TriggerRunOutcome.Failed)
            throw new ArgumentException($"a start ends as '{TriggerRunOutcome.Started}' or '{TriggerRunOutcome.Failed}', not '{outcome}'", nameof(outcome));
        return Record(tenant, triggerId, directorId, checkedUtc, outcome, count, sessionId, reason, nowUtc,
            endsPendingStart: true);
    }

    private TriggerCheckRecorded? Record(
        TenantId tenant, Guid triggerId, string directorId, DateTime checkedUtc, string outcome, int? count,
        string? sessionId, string? reason, DateTime nowUtc, bool endsPendingStart)
    {
        if (Array.IndexOf(TriggerRunOutcome.All, outcome) < 0)
            throw new ArgumentException($"'{outcome}' is not a trigger run outcome", nameof(outcome));

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var trigger = ctx.Triggers.FirstOrDefault(t => t.Id == triggerId);
            if (trigger is null) return null;

            var now = Utc(nowUtc);
            var run = new TriggerRunEntity
            {
                TriggerId = triggerId,
                CheckedUtc = Utc(checkedUtc),
                RecordedUtc = now,
                Outcome = outcome,
                Count = count,
                SessionId = sessionId,
                Reason = reason is null ? null : Cap(reason, 1024),
                DirectorId = Cap(directorId, 64),
            };
            run.TenantId = ctx.ActiveTenant!;
            ctx.TriggerRuns.Add(run);

            trigger.LastCheckUtc = now;
            trigger.LastOutcome = outcome;
            trigger.LastReason = run.Reason;
            trigger.ClaimedByDirectorId = run.DirectorId;
            trigger.ClaimedUtc = now;
            if (outcome == TriggerRunOutcome.Started)
            {
                trigger.LastSessionId = sessionId;
                trigger.LastStartedUtc = now;
            }
            else if (endsPendingStart && string.IsNullOrEmpty(trigger.LastSessionId))
            {
                // The start failed: release the lock BeginStart took. Only a pending lock - no session - is released;
                // the owner may have resumed the trigger meanwhile, and there is nothing else to undo.
                trigger.LastStartedUtc = null;
            }
            ctx.SaveChanges();

            PruneIn(ctx, triggerId);
            FileLog.Write($"[TriggerStore] RecordCheck: trigger={triggerId}, outcome={outcome}, count={count?.ToString() ?? "none"}, session={sessionId ?? "none"}");
            return new TriggerCheckRecorded(run, trigger);
        }
    }

    private static void PruneIn(GatewayDbContext ctx, Guid triggerId)
    {
        var total = ctx.TriggerRuns.Count(r => r.TriggerId == triggerId);
        if (total <= RunsKept + PruneSlack) return;

        var old = ctx.TriggerRuns.Where(r => r.TriggerId == triggerId).ToList()
            .OrderByDescending(r => r.RecordedUtc)
            .Skip(RunsKept)
            .ToList();
        ctx.TriggerRuns.RemoveRange(old);
        ctx.SaveChanges();
        FileLog.Write($"[TriggerStore] PruneIn: trigger={triggerId}, dropped {old.Count} run(s) older than the newest {RunsKept}");
    }

    private static TriggerEntity? FindIn(IQueryable<TriggerEntity> triggers, string idOrName)
    {
        var key = (idOrName ?? "").Trim();
        if (key.Length == 0) return null;
        if (Guid.TryParse(key, out var id))
            return triggers.FirstOrDefault(t => t.Id == id);
        return triggers.ToList().FirstOrDefault(t => string.Equals(t.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    private static bool NameTaken(GatewayDbContext ctx, string name, Guid? exceptId)
        => ctx.Triggers.AsNoTracking().ToList()
            .Any(t => t.Id != exceptId && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
