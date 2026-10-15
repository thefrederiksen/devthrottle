using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A FACTORY READY TO PUBLISH, over a real database of either kind (Factory Definition, phase 4). The SQLite tests
/// (<see cref="FactoryPublishApplyTests"/>) and the PostgreSQL test in <c>CcDirector.Gateway.DatabaseTests</c> both
/// drive it, so the failure test is ONE test run on two databases rather than two that could drift.
///
/// The factory is the fixture's <c>devthrottle</c>, registered in its folder. A second factory, <c>invoice-factory</c>,
/// is registered beside it with a schedule and a trigger of its own and NO definition: it is the factory goal 14 says
/// nothing here may touch.
/// </summary>
internal sealed class FactoryPublishScenario
{
    public static readonly DateTime Now = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
    public const string Factory = DefinitionFixture.FactoryId;
    public const string Undefined = "invoice-factory";
    public const string Owner = "the owner (device dev-1)";

    public GatewayDatabase Db { get; }
    public TenantId Tenant { get; }
    public FactoryDefinitionStore Store { get; }
    public FactoryPublishApply Apply { get; }

    public FactoryPublishScenario(GatewayDatabase db, TenantId tenant)
    {
        Db = db;
        Tenant = tenant;
        Store = new FactoryDefinitionStore(db);
        Apply = new FactoryPublishApply(db);
        var registry = new FactoryRegistryStore(db);
        registry.Register(tenant, DefinitionFixture.Registration(), "test", Now.AddDays(-1));
        registry.Register(tenant, new RegisterFactoryRequest
        {
            Factory = Undefined,
            Title = "Invoices",
            Folder = @"D:\ReposFred\invoices",
            Computer = "SOREN_NORTH",
            Seats = { new FactorySeatManifest { Id = "clerk", Name = "Clerk", Role = "Clerk", BriefFile = "briefs/clerk.md" } },
        }, "test", Now.AddDays(-1));
        AddSchedule(Undefined, "clerk", "cj_inv1", "0 6 * * *", "Book the invoices.");
        AddTrigger("invoice-inbox", Undefined);
    }

    /// <summary>Save a draft as a person and return its version.</summary>
    public int Save(IReadOnlyDictionary<string, string> content)
    {
        var newest = Store.Standing(Tenant, Factory).Newest?.Version ?? 0;
        var write = Store.SaveDraft(Tenant, Factory, content, newest, "person", Owner, null, Now);
        Assert.True(write.Ok, write.Refusal);
        return write.Version!.Version;
    }

    public FactoryPublishResult Publish(int version, int? replaces = null, FactoryPublishApply? through = null)
    {
        var content = Store.Find(Tenant, Factory, version)!.Files;
        return (through ?? Apply).Publish(Tenant, Factory, version, Parse(content), content, replaces, Owner, Now);
    }

    public FactoryPublishResult Restore(int version, int? replaces = null, int? expected = null, FactoryPublishApply? through = null)
    {
        var content = Store.Find(Tenant, Factory, version)!.Files;
        return (through ?? Apply).Restore(Tenant, Factory, version, Parse(content), content, replaces, expected, Owner, Now);
    }

    /// <summary>Save, publish, and assert it was published.</summary>
    public FactoryPublishResult SaveAndPublish(IReadOnlyDictionary<string, string> content)
    {
        var result = Publish(Save(content));
        Assert.True(result.Write.Ok, result.Write.Refusal);
        return result;
    }

    public static FactoryDefinition Parse(IReadOnlyDictionary<string, string> content)
    {
        var check = FactoryDefinitionCheck.Check(content, Factory, DefinitionFixture.FleetSkills, DefinitionFixture.Machines,
            DefinitionCheckPurpose.Publish, DefinitionFixture.NotConnected(content));
        Assert.True(check.Definition is not null, string.Join("\n", check.Findings.Where(f => f.IsError).Select(f => $"{f.File}:{f.Line} {f.Message}")));
        return check.Definition!;
    }

    public void AddSchedule(string factory, string? seat, string id, string cron, string prompt)
    {
        using var ctx = Db.CreateContext(Tenant);
        ctx.CronJobs.Add(new CronJobEntity
        {
            Id = id,
            TenantId = Tenant.Value,
            Name = $"{factory} - {seat}",
            Enabled = true,
            ScheduleKind = CronSchedule.KindRecurring,
            CronExpression = cron,
            TimeZoneId = "Europe/Copenhagen",
            Factory = factory,
            Seat = seat,
            Target = new CronJobTarget { Machine = "SOREN_NORTH" },
            Action = new CronJobAction { RepoPath = @"D:\somewhere", Seed = prompt },
            PreventOverlap = true,
            NotifyOn = CronNotify.None,
            CreatedUtc = Now.AddDays(-2),
        });
        ctx.SaveChanges();
    }

    public void AddTrigger(string name, string factory)
    {
        using var ctx = Db.CreateContext(Tenant);
        ctx.Triggers.Add(new TriggerEntity
        {
            TenantId = Tenant.Value,
            Name = name,
            Factory = factory,
            FactoryAgent = "Clerk",
            Machine = "SOREN_NORTH",
            RepoPath = @"D:\somewhere",
            CheckCommand = "python check.py",
            IntervalSeconds = 600,
            Prompt = "Go.",
            CreatedBy = "test",
            CreatedUtc = Now.AddDays(-2),
        });
        ctx.SaveChanges();
    }

    public FactoryRegistryEntity RegistryRow(string factory)
    {
        using var ctx = Db.CreateContext(Tenant);
        return ctx.FactoryRegistry.AsNoTracking().Single(f => f.Factory == factory);
    }

    public List<CronJobEntity> Schedules(string factory)
    {
        using var ctx = Db.CreateContext(Tenant);
        return ctx.CronJobs.AsNoTracking().Where(j => j.Factory == factory).ToList().OrderBy(j => j.Id, StringComparer.Ordinal).ToList();
    }

    public List<TriggerEntity> Triggers(string factory)
    {
        using var ctx = Db.CreateContext(Tenant);
        return ctx.Triggers.AsNoTracking().Where(t => t.Factory == factory).ToList();
    }

    /// <summary>
    /// The whole live state, every factory's: the registry rows, the schedules, the triggers and every definition
    /// version's standing - as one text, so "unchanged" is compared whole, never field by field.
    /// </summary>
    public string Snapshot()
    {
        using var ctx = Db.CreateContext(Tenant);
        var state = new
        {
            registry = ctx.FactoryRegistry.AsNoTracking().ToList().OrderBy(f => f.Factory, StringComparer.Ordinal).ToList(),
            schedules = ctx.CronJobs.AsNoTracking().ToList().OrderBy(j => j.Id, StringComparer.Ordinal).ToList(),
            triggers = ctx.Triggers.AsNoTracking().ToList().OrderBy(t => t.Name, StringComparer.Ordinal).ToList(),
            definitions = ctx.FactoryDefinitions.AsNoTracking().ToList().OrderBy(v => v.Factory, StringComparer.Ordinal)
                .ThenBy(v => v.Version).Select(v => new { v.Factory, v.Version, v.State, v.PublishedAtUtc, v.PublishedBy, v.RestoredFrom }).ToList(),
            checkState = ctx.FactoryCheckStates.AsNoTracking().ToList().OrderBy(r => r.OwnerKey, StringComparer.Ordinal)
                .ThenBy(r => r.Sequence).ToList(),
        };
        return JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// THE ONE FAILURE TEST, on whichever database the caller opened. Version 1 is published (the boss starts at 08:00,
    /// the outreach seat at 09:00) with a trigger the factory had before; version 2 moves the boss to 07:00 and renames
    /// the writer. Version 2's publish runs through contexts whose cron_jobs write throws. The registry, the schedules,
    /// the triggers and the publish marks - of BOTH factories - are read before and after and compared whole, and the
    /// answer is the 500 sentence naming the step and the row.
    /// </summary>
    public static void PublishWhoseScheduleWriteFails_ChangesNothing(FactoryPublishScenario s, FactoryPublishApply failing)
    {
        s.SaveAndPublish(DefinitionFixture.Publishable());
        s.AddTrigger("inbox-watch", Factory);
        var bossSchedule = s.Schedules(Factory).Single(j => j.Seat == "boss").Id;
        var v2 = s.Save(DefinitionFixture.PublishableEdit("seats/boss.yaml", "\"0 8 * * 1-5\"", "\"0 7 * * 1-5\"")
            .Also("seats/writer.yaml", "name: Writer", "name: Author"));
        var before = s.Snapshot();

        var result = s.Publish(v2, replaces: 1, through: failing);

        Assert.Equal(FactoryDefinitionOutcome.WriteFailed, result.Write.Outcome);
        Assert.Equal($"The publish changed nothing: writing schedule {bossSchedule} for seat 'boss' failed: {FailTheScheduleWrite.Message}",
            result.Write.Refusal);
        Assert.Empty(result.Applied);
        Assert.Equal(before, s.Snapshot());
        var standing = s.Store.Standing(s.Tenant, Factory);
        Assert.Equal(1, standing.Published!.Version);
        Assert.Equal(FactoryDefinitionStates.Draft, s.Store.Find(s.Tenant, Factory, v2)!.Summary.State);

        // And the same publish through the database's own contexts goes through: the failure was the injected one.
        Assert.True(s.Publish(v2, replaces: 1).Write.Ok);
        Assert.NotEqual(before, s.Snapshot());
    }

    /// <summary>
    /// Contexts over the same database with <see cref="FailTheScheduleWrite"/> installed - the test-only seam, through
    /// <see cref="FactoryPublishApply"/>'s context opener. The options are built ONCE, so one test does not build an
    /// internal service provider per context.
    /// </summary>
    public static FactoryPublishApply Failing(Action<DbContextOptionsBuilder<GatewayDbContext>> provider)
    {
        var builder = new DbContextOptionsBuilder<GatewayDbContext>();
        builder.WithGatewayInterceptors();
        provider(builder);
        builder.AddInterceptors(new FailTheScheduleWrite());
        var options = builder.Options;
        return new FactoryPublishApply(t => new GatewayDbContext(options) { ActiveTenant = t.Value });
    }

    /// <summary>As <see cref="Failing"/>, with <see cref="FailTheTriggerWrite"/> installed instead: the publish fails
    /// INSIDE a trigger write, after its schedule writes went through in the same transaction.</summary>
    public static FactoryPublishApply FailingTriggers(Action<DbContextOptionsBuilder<GatewayDbContext>> provider)
    {
        var builder = new DbContextOptionsBuilder<GatewayDbContext>();
        builder.WithGatewayInterceptors();
        provider(builder);
        builder.AddInterceptors(new FailTheTriggerWrite());
        var options = builder.Options;
        return new FactoryPublishApply(t => new GatewayDbContext(options) { ActiveTenant = t.Value });
    }
}

/// <summary>
/// THE INJECTED FAILURE, for a trigger: throws when a save would write a trigger row - an insert, an update or a
/// delete. Test-only; production never installs it.
/// </summary>
internal sealed class FailTheTriggerWrite : SaveChangesInterceptor
{
    public const string Message = "the database refused the triggers write (injected by the test)";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context!.ChangeTracker.Entries<TriggerEntity>()
            .Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException(Message);
        return result;
    }
}

/// <summary>
/// THE INJECTED FAILURE: an EF Core interceptor that throws when a save would write a schedule row (cron_jobs) - an
/// insert, an update or a delete. Test-only; production never installs it.
/// </summary>
internal sealed class FailTheScheduleWrite : SaveChangesInterceptor
{
    public const string Message = "the database refused the cron_jobs write (injected by the test)";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context!.ChangeTracker.Entries<CronJobEntity>()
            .Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException(Message);
        return result;
    }
}

internal static class ContentEdits
{
    /// <summary>One more exact edit of a content map (the piece must occur).</summary>
    public static Dictionary<string, string> Also(this Dictionary<string, string> content, string path, string oldText, string newText)
    {
        Assert.Contains(oldText, content[path]);
        content[path] = content[path].Replace(oldText, newText, StringComparison.Ordinal);
        return content;
    }
}
