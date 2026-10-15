using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// A PUBLISH DRIVES THE FACTORY, IN ONE TRANSACTION (Factory Definition, phase 4; mission 5.3 "Applied as one unit",
/// design section 2). A publish, or a restore, writes the publish mark (or the restored copy), the registry row, the
/// schedules and the triggers through ONE database context of its own and ONE transaction, so any failure leaves the
/// factory exactly as it was and the error names the step and the row.
///
/// WHY ITS OWN CONTEXT. The registry, the schedule and the trigger stores each open their own context per call, so
/// their methods cannot share a transaction. This class is the one writer for the apply; it reuses their pure parts
/// (<see cref="CronJobStore.NewEntity"/>, <see cref="CronJobStore.ApplyDefinition"/>, the definition store's rulings
/// and its append) and never calls their writing methods.
///
/// THE ORDER INSIDE THE TRANSACTION. The factory's registry row is locked first, so two publishes of one factory -
/// on two Gateway replicas - take turns. Then everything is read again: the published version (refused when it is not
/// the one the caller says it replaces), the version's own rulings, the live registry row, schedules and triggers. The
/// plan is composed from what was read (<see cref="FactoryPublishPlan.Compose"/>, pure; its refusals write nothing).
/// Then: the publish mark or the restored copy, the registry row, each schedule, each trigger, commit.
///
/// "WHAT CHANGES" IS COMPUTED INSIDE, against the version this publish really replaces, so it is never shown against
/// the wrong version (phase 1, decision 8).
///
/// EVERY READER IS CORRECT AFTER COMMIT. The cron engine reads every schedule from the database on every sweep
/// (<c>CronJobStore.ListAll</c>), and a Director's trigger claim reads the trigger table on every ask
/// (<c>TriggerStore.ClaimForDirector</c>); the registry is read per request. None of the three keeps a copy in memory,
/// so there is nothing to refresh.
/// </summary>
public sealed class FactoryPublishApply
{
    private const string Committing = "committing";

    private readonly object _gate = new();
    private readonly Func<TenantId, GatewayDbContext> _openContext;

    public FactoryPublishApply(GatewayDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _openContext = db.CreateContext;
    }

    /// <summary>
    /// THE TEST SEAM: a context opener other than the database's own pool, so a test can open contexts over the same
    /// database with an interceptor that makes one write fail. Production never calls it.
    /// </summary>
    internal FactoryPublishApply(Func<TenantId, GatewayDbContext> openContext)
    {
        _openContext = openContext ?? throw new ArgumentNullException(nameof(openContext));
    }

    /// <summary>
    /// Publish <paramref name="version"/> and drive the factory to it. <paramref name="definition"/> is the version's
    /// content, already through the whole-definition check (a stored version's content never changes).
    /// <paramref name="replaces"/> is the published version the caller saw (0 = none), or null for no staleness check.
    /// </summary>
    public FactoryPublishResult Publish(TenantId tenant, string factory, int version, FactoryDefinition definition,
        IReadOnlyDictionary<string, string> content, int? replaces, string? authorId, DateTime nowUtc)
        => Apply(tenant, factory, version, definition, content, replaces, restore: false, expectedVersion: null, authorId, nowUtc);

    /// <summary>
    /// Restore <paramref name="version"/>: a copy of it becomes the newest version, already published, and the factory
    /// is driven to it. <paramref name="expectedVersion"/> is the newest version the person was looking at, as for
    /// <see cref="FactoryDefinitionStore.Restore"/>.
    /// </summary>
    public FactoryPublishResult Restore(TenantId tenant, string factory, int version, FactoryDefinition definition,
        IReadOnlyDictionary<string, string> content, int? replaces, int? expectedVersion, string? authorId, DateTime nowUtc)
        => Apply(tenant, factory, version, definition, content, replaces, restore: true, expectedVersion, authorId, nowUtc);

    /// <summary>
    /// What a publish of <paramref name="definition"/> WOULD do to the live factory now: the plan composed against the
    /// rows as they are, read without a transaction and without writing anything (the diff's <c>plan</c>).
    /// </summary>
    public FactoryPublishComposition Plan(TenantId tenant, FactoryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        using var ctx = _openContext(tenant);
        var factory = definition.Factory;
        var (registry, schedules, triggers) = ReadLive(ctx, factory, tracked: false);
        return FactoryPublishPlan.Compose(definition, registry, schedules, triggers);
    }

    private FactoryPublishResult Apply(TenantId tenant, string factory, int version, FactoryDefinition definition,
        IReadOnlyDictionary<string, string> content, int? replaces, bool restore, int? expectedVersion, string? authorId,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(content);
        var act = restore ? "restore" : "publish";
        FileLog.Write($"[FactoryPublishApply] {act}: factory={factory}, version={version}, replaces={replaces?.ToString() ?? "unstated"}, by={authorId ?? "(unknown)"}");
        if (!string.Equals(definition.Factory, factory, StringComparison.Ordinal))
            throw new ArgumentException($"The definition is of '{definition.Factory}', not '{factory}'.", nameof(definition));

        lock (_gate)
        {
            using var ctx = _openContext(tenant);
            using var tx = ctx.Database.BeginTransaction();
            var step = $"locking the registry row of {factory}";
            var attemptedVersion = 0;
            try
            {
                // THE FACTORY'S ROW IS LOCKED FIRST: a second publish of this factory, on any replica, waits here until
                // this one commits, and then reads what this one wrote. The value is written again below.
                ctx.FactoryRegistry.Where(f => f.Factory == factory)
                    .ExecuteUpdate(set => set.SetProperty(f => f.RegisteredAtUtc, nowUtc));

                step = "reading the published version";
                var standing = FactoryDefinitionStore.StandingInside(ctx, factory);
                if (replaces is { } stated && (standing.Published?.Version ?? 0) != stated)
                    return Refuse(tx, factory, act, FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.Stale,
                        NotReplacing(restore, standing.Published?.Version, stated), standing.Published));

                FactoryDefinitionWrite? ruled = restore
                    ? FactoryDefinitionStore.WhyNotRestore(ctx, factory, version, out _)
                    : FactoryDefinitionStore.WhyNotPublish(ctx, factory, version);
                if (ruled is not null) return Refuse(tx, factory, act, ruled);

                var (changes, notice) = ChangesAgainst(ctx, factory, standing.Published, content);

                step = "reading the live factory";
                var (registry, schedules, triggers) = ReadLive(ctx, factory, tracked: true);
                var composed = FactoryPublishPlan.Compose(definition, registry, schedules, triggers);
                if (composed.Refusal is { } refusal)
                    return Refuse(tx, factory, act, FactoryDefinitionWrite.Refused(refusal.Outcome, refusal.Sentence, standing.Published),
                        refusal.Findings);
                var plan = composed.Plan!;
                var applied = new List<string>();

                // 2. The publish mark, or the restored copy.
                int publishedVersion;
                if (restore)
                {
                    if (FactoryDefinitionStore.PrepareAppend(ctx, tenant, factory, content,
                            FactoryDefinitionStore.ExpectedForRestore(ctx, factory, expectedVersion), published: true,
                            restoredFrom: version, FactoryMemoryAuthorKinds.Person, authorId, changeNote: null, nowUtc,
                            out var copy) is { } notAppended)
                        return Refuse(tx, factory, act, notAppended);
                    attemptedVersion = copy!.Version;
                    step = $"writing version {copy.Version}, the restored copy of version {version}";
                    ctx.FactoryDefinitions.Add(copy);
                    ctx.SaveChanges();
                    publishedVersion = copy.Version;
                    applied.Add($"Definition: version {copy.Version} written, a copy of version {version}, and published.");
                }
                else
                {
                    step = $"writing the publish mark on version {version}";
                    if (FactoryDefinitionStore.MarkPublished(ctx, factory, version, authorId, nowUtc) == 0)
                        return Refuse(tx, factory, act, FactoryDefinitionStore.PublishedFirstBySomebodyElse(factory, version,
                            FactoryDefinitionStore.SummaryOf(ctx, factory, version)));
                    publishedVersion = version;
                    applied.Add($"Definition: version {version} published.");
                }

                // 3. The registry row: every field the definition owns; the archive fields and goal numbers untouched.
                step = $"writing the registry row of {factory}";
                // A FIRST PUBLISH REGISTERS THE FACTORY (phase 5): a defined factory runs where its definition says, so
                // it needs no registration made by hand first, and its row has no folder.
                var row = registry ?? new FactoryRegistryEntity { TenantId = ctx.ActiveTenant!, Factory = factory };
                if (registry is null) ctx.FactoryRegistry.Add(row);
                row.Title = plan.Registry.Title;
                row.Folder = plan.Registry.Folder;
                row.Computer = plan.Registry.Computer;
                row.BossSeat = plan.Registry.BossSeat;
                row.GoalText = plan.Registry.GoalText;
                row.GoalFile = plan.Registry.GoalFile;
                row.GoalApprovedOn = plan.Registry.GoalApprovedOn;
                row.Purpose = plan.Registry.Purpose;
                row.SeatsJson = plan.Registry.SeatsJson;
                row.RegisteredBy = RegisteredBy(publishedVersion, authorId);
                row.RegisteredAtUtc = nowUtc;
                ctx.SaveChanges();
                applied.AddRange(plan.RegistrySentences);

                // 4. The schedules, one write each, so a failure names the row it failed on.
                var byId = schedules.ToDictionary(j => j.Id, StringComparer.Ordinal);
                foreach (var s in plan.Schedules)
                {
                    switch (s.Action)
                    {
                        case PlannedScheduleAction.Keep:
                            // A kept schedule with a check reads it from the version this publish makes the published
                            // one (phase 5b), exactly as a kept definition trigger does; nothing else is written.
                            if (s.Job!.HasCheck)
                            {
                                step = $"writing schedule {s.Id}'s check version for seat '{s.Seat}'";
                                byId[s.Id!].DefinitionVersion = publishedVersion;
                                ctx.SaveChanges();
                            }
                            applied.Add(s.Sentence(version: publishedVersion));
                            break;
                        case PlannedScheduleAction.Update:
                            step = $"writing schedule {s.Id} for seat '{s.Seat}'";
                            var entity = byId[s.Id!];
                            CronJobStore.ApplyDefinition(entity, s.Job!);
                            CronJobStore.ApplyCheck(entity, s.Job!, publishedVersion);
                            entity.NotifyOn = CronNotify.Normalize(s.Job!.NotifyOn);
                            entity.NextRunUtc = CronSchedule.ComputeNextRunUtc(CronJobStore.ToDto(entity), nowUtc);
                            ctx.SaveChanges();
                            applied.Add(s.Sentence(version: publishedVersion));
                            break;
                        case PlannedScheduleAction.Create:
                            var created = CronJobStore.NewEntity(ctx, s.Job!, nowUtc);
                            CronJobStore.ApplyCheck(created, s.Job!, publishedVersion);
                            step = $"writing schedule {created.Id} for seat '{s.Seat}'";
                            ctx.CronJobs.Add(created);
                            ctx.SaveChanges();
                            applied.Add(s.Sentence(created.Id, publishedVersion));
                            break;
                        case PlannedScheduleAction.Delete:
                            step = $"deleting schedule {s.Id}";
                            ctx.CronJobs.Remove(byId[s.Id!]);
                            ctx.SaveChanges();
                            applied.Add(s.Sentence());
                            break;
                    }
                }

                // 5. The triggers (phase 5b): deletes first, so a name a deleted trigger held is free for a new one, then
                // each kept, changed and created definition trigger. A deleted trigger's run history is kept
                // (TriggerStore.Delete's rule: the runs are not removed).
                var triggerById = triggers.ToDictionary(t => t.Id);
                foreach (var t in plan.Triggers.Where(t => t.Action == PlannedTriggerAction.Delete))
                {
                    step = $"deleting trigger {t.Name}";
                    ctx.Triggers.Remove(triggerById[t.Id!.Value]);
                    ctx.SaveChanges();
                    applied.Add(t.Sentence(publishedVersion));
                }
                // A kept trigger renamed because its start moved in the list may take a name another kept trigger
                // holds until its own write: every rename goes through a name no trigger has first, so the account's
                // one-name-per-trigger key never sees two at once.
                foreach (var t in plan.Triggers.Where(t => t.Action == PlannedTriggerAction.Update && t.Live!.Name != t.Row!.Name))
                {
                    step = $"renaming trigger {t.Live!.Name} ({t.Id}) out of the way";
                    triggerById[t.Id!.Value].Name = $"renaming-{t.Id:N}";
                    ctx.SaveChanges();
                }
                foreach (var t in plan.Triggers.Where(t => t.Action != PlannedTriggerAction.Delete))
                {
                    step = t.Action == PlannedTriggerAction.Create
                        ? $"writing trigger {t.Name} for seat '{t.Seat}'"
                        : $"writing trigger {t.Name} ({t.Id}) for seat '{t.Seat}'";
                    var entity = t.Action == PlannedTriggerAction.Create
                        ? NewDefinitionTrigger(ctx, factory, authorId, nowUtc)
                        : triggerById[t.Id!.Value];
                    WriteDefinitionTrigger(entity, t, publishedVersion);
                    ctx.SaveChanges();
                    applied.Add(t.Sentence(publishedVersion));
                }

                step = Committing;
                tx.Commit();

                var summary = FactoryDefinitionStore.SummaryOf(ctx, factory, publishedVersion)!;
                FileLog.Write($"[FactoryPublishApply] {act}: factory={factory}, version {publishedVersion} published; {applied.Count} steps: {string.Join(" | ", applied)}");
                return new FactoryPublishResult(FactoryDefinitionWrite.Published(summary), Array.Empty<DefinitionFinding>(),
                    changes, notice, applied);
            }
            catch (Exception ex)
            {
                // THE ENTRY POINT OF THE TRANSACTION: whatever failed, nothing it wrote may stay. A commit that failed
                // has already ended the transaction, and there is nothing left to roll back.
                if (step != Committing) tx.Rollback();
                var reason = Innermost(ex).Message;
                FileLog.Write($"[FactoryPublishApply] {act} FAILED and was rolled back: factory={factory}, version={version}, step={step}: {reason}");

                // A restore's copy can lose its version to another writer: that is the stale refusal, not a failure.
                if (restore && attemptedVersion > 0 && ex is DbUpdateException)
                {
                    using var fresh = _openContext(tenant);
                    var after = FactoryDefinitionStore.AfterAFailedAppend(fresh, factory, attemptedVersion, ex);
                    if (after.Outcome == FactoryDefinitionOutcome.Stale)
                        return new FactoryPublishResult(after, Array.Empty<DefinitionFinding>(), null, null, Array.Empty<string>());
                }

                return new FactoryPublishResult(FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.WriteFailed,
                        $"The {act} changed nothing: {step} failed: {reason}", null),
                    Array.Empty<DefinitionFinding>(), null, null, Array.Empty<string>());
            }
        }
    }

    /// <summary>A new definition trigger row, added to the context, its fields written next. Running, as a create
    /// through the trigger routes is by default; who created it says it came from the definition.</summary>
    private static TriggerEntity NewDefinitionTrigger(GatewayDbContext ctx, string factory, string? authorId, DateTime nowUtc)
    {
        var row = new TriggerEntity
        {
            Factory = factory,
            Paused = false,
            CreatedBy = $"definition of {factory}, published by {authorId ?? "a person"}" is var by && by.Length <= 256 ? by : by[..256],
            CreatedUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
        };
        row.TenantId = ctx.ActiveTenant!;
        ctx.Triggers.Add(row);
        return row;
    }

    /// <summary>
    /// Write what the definition says onto a definition trigger: every field the start owns, and the version its check
    /// is now read from - on a kept trigger too, because a check's file can change while its path does not. What is the
    /// person's - the pause switch - and what the runs keep - the lock, the last check, the run history - are left
    /// alone. A trigger moved to another machine is no longer the business of the Director that held it
    /// (<see cref="Triggers.TriggerStore.Update"/>'s rule).
    /// </summary>
    private static void WriteDefinitionTrigger(TriggerEntity entity, PlannedTrigger t, int version)
    {
        var row = t.Row!;
        if (!string.IsNullOrEmpty(entity.Machine) && !string.Equals(entity.Machine, row.Machine, StringComparison.OrdinalIgnoreCase))
        {
            entity.ClaimedByDirectorId = "";
            entity.ClaimedUtc = null;
        }
        entity.Name = row.Name;
        entity.FactoryAgent = row.FactoryAgent;
        entity.Machine = row.Machine;
        entity.RepoPath = row.RepoPath;
        entity.CheckCommand = "";
        entity.IntervalSeconds = row.IntervalSeconds;
        entity.Prompt = row.Prompt;
        entity.Seat = t.Seat;
        entity.CheckPath = row.CheckPath;
        entity.SecretsJson = row.SecretsJson;
        entity.DefinitionVersion = version;
    }

    /// <summary>The 409 when the caller states a published version that is no longer the one (design section 5).</summary>
    private static string NotReplacing(bool restore, int? current, int stated)
        => (restore ? "Not restored: " : "Not published: ")
           + (current is { } c
               ? $"version {c} is the published definition now, not {stated}."
               : $"no version is the published definition now, not {stated}.")
           + $" Read what changes again and {(restore ? "restore" : "publish")} once more.";

    private static string RegisteredBy(int version, string? authorId)
    {
        var by = $"definition v{version} published by {authorId ?? "a person"}";
        return by.Length <= 256 ? by : by[..256];
    }

    private static FactoryPublishResult Refuse(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, string factory,
        string act, FactoryDefinitionWrite refused, IReadOnlyList<DefinitionFinding>? findings = null)
    {
        tx.Rollback();
        FileLog.Write($"[FactoryPublishApply] {act} REFUSED ({refused.Outcome}) for {factory}, nothing written: {refused.Refusal}");
        return new FactoryPublishResult(refused, findings ?? Array.Empty<DefinitionFinding>(), null, null, Array.Empty<string>());
    }

    /// <summary>The live factory: its registry row, the schedules that name it, and EVERY trigger of the account - the
    /// plan narrows them to this factory, and needs the rest only for the names a new trigger may not take.</summary>
    private static (FactoryRegistryEntity? Registry, List<CronJobEntity> Schedules, List<TriggerEntity> Triggers) ReadLive(
        GatewayDbContext ctx, string factory, bool tracked)
    {
        IQueryable<FactoryRegistryEntity> registry = ctx.FactoryRegistry;
        IQueryable<CronJobEntity> schedules = ctx.CronJobs;
        IQueryable<TriggerEntity> triggers = ctx.Triggers;
        if (!tracked)
        {
            registry = registry.AsNoTracking();
            schedules = schedules.AsNoTracking();
            triggers = triggers.AsNoTracking();
        }
        return (registry.FirstOrDefault(f => f.Factory == factory),
            schedules.Where(j => j.Factory == factory).ToList(),
            triggers.ToList());
    }

    /// <summary>
    /// What publishing <paramref name="content"/> changes against <paramref name="published"/> - everything, listed as
    /// added, when nothing is published. A published version that no longer passes the format check (the rules
    /// tightened since) cannot be read for a comparison, so the changes are listed against nothing and a notice says
    /// which version, where, and why. It never refuses.
    /// </summary>
    internal static (FactoryDefinitionChangesDto Changes, DefinitionFinding? Notice) ChangesAgainst(GatewayDbContext ctx,
        string factory, FactoryDefinitionVersionSummary? published, IReadOnlyDictionary<string, string> content)
    {
        if (published is null) return (FactoryDefinitionChanges.Compare(null, content), null);
        var before = FactoryDefinitionStore.FindInside(ctx, factory, published.Version)!.Files;
        if (FirstFormatError(before) is not { } problem)
            return (FactoryDefinitionChanges.Compare(before, content), null);

        FileLog.Write($"[FactoryPublishApply] {factory}: version {published.Version} no longer passes the format check at {Where(problem)}; what changes is listed from nothing");
        return (FactoryDefinitionChanges.Compare(null, content), new DefinitionFinding(DefinitionSeverity.Notice, problem.File, problem.Line,
            $"Version {published.Version}, published until now, no longer passes the format check at {Where(problem)} " +
            $"({problem.Message}), so what changes is listed as if nothing had been published before."));
    }

    /// <summary>The first error of the format stage alone, or null when the content parses: whether a stored version
    /// can be read for "what changes".</summary>
    internal static DefinitionFinding? FirstFormatError(IReadOnlyDictionary<string, string> content)
        => FactoryDefinitionCheck.Read(content).Findings.FirstOrDefault(f => f.IsError);

    internal static string Where(DefinitionFinding f) => f.Line > 0 ? $"{f.File}:{f.Line}" : f.File;

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is { } inner) ex = inner;
        return ex;
    }
}

/// <summary>
/// What a publish or a restore that drives the factory did. <see cref="Write"/> is the published version, or the
/// refusal (the store's outcomes, the plan's, the stale <c>replaces</c>, or <see cref="FactoryDefinitionOutcome.WriteFailed"/>
/// when the transaction failed and changed nothing). <see cref="RefusalFindings"/> carries a plan refusal's findings.
/// On success, <see cref="Changes"/> is what changed against the version really replaced, <see cref="ChangesNotice"/>
/// says when that version could not be read, and <see cref="Applied"/> is one sentence per step, in order.
/// </summary>
public sealed record FactoryPublishResult(FactoryDefinitionWrite Write, IReadOnlyList<DefinitionFinding> RefusalFindings,
    FactoryDefinitionChangesDto? Changes, DefinitionFinding? ChangesNotice, IReadOnlyList<string> Applied);
