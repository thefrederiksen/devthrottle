using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Registry;

/// <summary>
/// DELETING AN ARCHIVED FACTORY (issue #4324), as ONE scenario run on SQLite in the unit tests and on PostgreSQL in
/// the database tests, so the two can never disagree about what a delete removes and what it keeps. It goes through
/// the real <see cref="FactoryRegistryStore.Delete"/>: the registry entry, the definition versions, the seat briefs,
/// the linked schedules and the triggers - one of them written with capitals - go; another factory's trigger, a plain
/// schedule and every activity row stay, and the owner's row is written in the same transaction.
/// </summary>
internal static class FactoryDeleteScenario
{
    private static readonly DateTime Now = new(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);

    public static void DeleteRemovesWhatMakesTheFactory_AndKeepsItsHistory(GatewayDatabase db, TenantId tenant)
    {
        var registry = new FactoryRegistryStore(db);
        var record = new FactoryActivityRecord(db);
        ScheduleLinkSeed.Link(db, tenant, "eric", "boss", "cj_eric", enabled: false, name: "Eric - Boss - morning");
        ScheduleLinkSeed.Link(db, tenant, null, null, "cj_plain");
        registry.Register(tenant, new RegisterFactoryRequest
        {
            Factory = "eric",
            Title = "Eric",
            Folder = @"D:\eric",
            Computer = "SOREN_NORTH",
            BossSeat = "boss",
            Seats = { new FactorySeatManifest { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml", Schedules = { "cj_eric" } } },
        }, "the owner", Now.AddDays(-30));
        using (var ctx = db.CreateContext(tenant))
        {
            ctx.FactoryDefinitions.Add(new FactoryDefinitionVersionEntity
                { TenantId = tenant.Value, Factory = "eric", Version = 1, Content = "{}", AuthorKind = "person", WrittenAtUtc = Now.AddDays(-30) });
            ctx.FactoryBriefs.Add(new FactoryBriefVersionEntity
                { TenantId = tenant.Value, Factory = "eric", Seat = "boss", Version = 1, Text = "brief", AuthorKind = "person", WrittenAtUtc = Now.AddDays(-30) });
            ctx.Triggers.Add(new TriggerEntity
                { TenantId = tenant.Value, Name = "eric-mail", Factory = "Eric", FactoryAgent = "boss", Machine = "SOREN_NORTH", CreatedBy = "test", CreatedUtc = Now.AddDays(-30) });
            ctx.Triggers.Add(new TriggerEntity
                { TenantId = tenant.Value, Name = "other-mail", Factory = "other", FactoryAgent = "boss", Machine = "SOREN_NORTH", CreatedBy = "test", CreatedUtc = Now.AddDays(-30) });
            ctx.SaveChanges();
        }
        record.Append(tenant, new AppendFactoryActivityRequest
            { Factory = "eric", FactoryAgent = "boss", Outcome = FactoryActivityOutcome.Done, What = "Ran once", OccurredUtc = Now.AddDays(-29) }, "session:test");

        // Not archived: refused, and nothing is deleted.
        var refused = Assert.Throws<FactoryViewValidationException>(() =>
            registry.Delete(tenant, "eric", "the owner", new[] { "cj_eric" }, _ => Array.Empty<AppendFactoryActivityRequest>()));
        Assert.Equal("Eric is not archived. Archive it first: only an archived factory can be deleted.", refused.Message);
        Assert.NotNull(registry.Find(tenant, "eric"));

        registry.Archive(tenant, "eric", "the owner", Array.Empty<string>(), Now.AddDays(-1));

        // A confirm that named other schedules: refused, and nothing is deleted.
        Assert.Throws<FactoryOwnerActionConflictException>(() =>
            registry.Delete(tenant, "eric", "the owner", Array.Empty<string>(), _ => Array.Empty<AppendFactoryActivityRequest>()));
        Assert.NotNull(registry.Find(tenant, "eric"));

        var result = registry.Delete(tenant, "eric", "the owner", new[] { "cj_eric" }, r => new[]
        {
            FactoryOwnerActions.DeleteRow(r.Factory, r.Title, r.Schedules.Select(j => j.Name).ToList(), r.Triggers.Select(t => t.Name).ToList(),
                r.DefinitionVersions, r.BriefVersions, handled: 0, "the owner", Now),
        });

        Assert.Equal(("eric", "Eric", 1, 1), (result.Factory, result.Title, result.DefinitionVersions, result.BriefVersions));
        Assert.Equal(new[] { "cj_eric" }, result.Schedules.Select(j => j.Id));
        Assert.Equal(new[] { "eric-mail" }, result.Triggers.Select(t => t.Name));
        Assert.Null(registry.Find(tenant, "eric"));
        using (var ctx = db.CreateContext(tenant))
        {
            Assert.Equal(0, ctx.FactoryRegistry.Count());
            Assert.Equal(0, ctx.FactoryDefinitions.Count());
            Assert.Equal(0, ctx.FactoryBriefs.Count());
            Assert.Equal(new[] { "cj_plain" }, ctx.CronJobs.Select(j => j.Id).ToList());
            Assert.Equal(new[] { "other-mail" }, ctx.Triggers.Select(t => t.Name).ToList());
        }
        var rows = record.Query(tenant, factory: "eric", limit: 100).Rows;
        Assert.Contains(rows, r => r.What == "Ran once");
        var owner = Assert.Single(rows, r => r.FactoryAgent == FactoryOwnerActions.OwnerAgent);
        Assert.Equal("The owner deleted Eric: its registry entry, 1 definition version, 1 brief version, schedules Eric - Boss - morning, triggers eric-mail. Its history is kept.", owner.What);

        // Gone: a second delete finds nothing registered.
        Assert.Throws<FactoryNotRegisteredException>(() =>
            registry.Delete(tenant, "eric", "the owner", Array.Empty<string>(), _ => Array.Empty<AppendFactoryActivityRequest>()));
    }
}
