using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Tests.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A PUBLISH DRIVES THE FACTORY, IN ONE TRANSACTION (Factory Definition, phase 4), over a real SQLite file database:
/// the registry row, the schedules and the triggers after a publish and a restore, the publish whose schedule write
/// fails changing nothing, the stale <c>replaces</c>, and "what changes" against the version really replaced. The same
/// failure test runs on PostgreSQL in <c>CcDirector.Gateway.DatabaseTests</c>.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryPublishApplyTests : IDisposable
{
    private const string TheFactory = FactoryPublishScenario.Factory;

    private readonly GatewayDbTestHarness _h = new();
    private readonly FactoryPublishScenario _s;

    public FactoryPublishApplyTests()
    {
        _s = new FactoryPublishScenario(_h.Open(), TenantId.Local);
    }

    public void Dispose() => _h.Dispose();

    // ---------- a seat renamed, a version restored ----------

    [Fact]
    public void Publish_ASeatRenamedByOnePublish_ShowsTheNewNameInTheRegistrySeatAndInEveryScheduleName()
    {
        var twoStarts = DefinitionFixture.PublishableWith("seats/writer.yaml", """
            seat: writer
            name: Writer
            role: Writer
            brief: Write the posts.
            starts:
              - cron: "0 7 * * *"
                timeZone: Europe/Copenhagen
                prompt: Morning post.
              - cron: "0 19 * * *"
                timeZone: Europe/Copenhagen
                prompt: Evening post.
            """);
        _s.SaveAndPublish(twoStarts);
        var idsBefore = _s.Schedules(TheFactory).Where(j => j.Seat == "writer").Select(j => j.Id).ToList();

        var renamed = new Dictionary<string, string>(twoStarts).Also("seats/writer.yaml", "name: Writer", "name: Author");
        var result = _s.SaveAndPublish(renamed);

        var seat = FactoryRegistryStore.SeatsOf(_s.RegistryRow(TheFactory)).Single(x => x.Id == "writer");
        Assert.Equal("Author", seat.Name);
        var writer = _s.Schedules(TheFactory).Where(j => j.Seat == "writer").ToList();
        Assert.Equal(idsBefore, writer.Select(j => j.Id));
        Assert.Equal(new[] { "DevThrottle - Author", "DevThrottle - Author (2)" },
            writer.OrderBy(j => j.CronExpression == "0 7 * * *" ? 0 : 1).Select(j => j.Name));
        Assert.Contains("Registry: seat writer renamed Writer -> Author.", result.Applied);
        Assert.Equal("definition v2 published by the owner (device dev-1)", _s.RegistryRow(TheFactory).RegisteredBy);
    }

    [Fact]
    public void Restore_OfAnOlderVersion_PutsItsSeatsAndSchedulesBack_AsANewNewestVersion()
    {
        _s.SaveAndPublish(DefinitionFixture.Publishable());
        var bossId = _s.Schedules(TheFactory).Single(j => j.Seat == "boss").Id;
        _s.SaveAndPublish(DefinitionFixture.PublishableEdit("seats/boss.yaml", "\"0 8 * * 1-5\"", "\"0 7 * * 1-5\"")
            .Also("seats/writer.yaml", "name: Writer", "name: Author"));
        Assert.Equal("0 7 * * 1-5", _s.Schedules(TheFactory).Single(j => j.Seat == "boss").CronExpression);

        var restored = _s.Restore(1, replaces: 2, expected: 2);

        Assert.True(restored.Write.Ok, restored.Write.Refusal);
        Assert.Equal(3, restored.Write.Version!.Version);
        Assert.Equal(1, restored.Write.Version.RestoredFrom);
        Assert.Equal(FactoryDefinitionStates.Published, restored.Write.Version.State);
        Assert.Equal(3, _s.Store.Standing(_s.Tenant, TheFactory).Published!.Version);
        Assert.Equal("Writer", FactoryRegistryStore.SeatsOf(_s.RegistryRow(TheFactory)).Single(x => x.Id == "writer").Name);
        var boss = _s.Schedules(TheFactory).Single(j => j.Seat == "boss");
        Assert.Equal(bossId, boss.Id);
        Assert.Equal("0 8 * * 1-5", boss.CronExpression);
        Assert.Equal("Definition: version 3 written, a copy of version 1, and published.", restored.Applied[0]);
        Assert.Contains("Registry: seat writer renamed Author -> Writer.", restored.Applied);
        Assert.Contains($"Schedule {bossId} (DevThrottle - Boss) moved: Weekdays 07:00 Europe/Copenhagen -> Weekdays 08:00 Europe/Copenhagen.", restored.Applied);
        Assert.Equal("definition v3 published by the owner (device dev-1)", _s.RegistryRow(TheFactory).RegisteredBy);
    }

    // ---------- the first publish over a factory that already runs ----------

    [Fact]
    public void Publish_TheFirstOne_KeepsAMatchingSchedule_DeletesTheRestAndEveryTrigger_AndSaysEachStep()
    {
        _s.AddSchedule(TheFactory, "boss", "cj_boss", "0 8 * * 1-5", "Morning round.");
        _s.AddSchedule(TheFactory, "writer", "cj_old", "0 12 * * *", "An old start.");
        _s.AddTrigger("inbox-watch", TheFactory);

        var result = _s.SaveAndPublish(DefinitionFixture.Publishable());

        var schedules = _s.Schedules(TheFactory);
        var boss = schedules.Single(j => j.Seat == "boss");
        Assert.Equal("cj_boss", boss.Id);
        Assert.Equal("DevThrottle - Boss", boss.Name);
        Assert.Equal(@"D:\ReposFred\devthrottle", boss.Action.RepoPath);
        Assert.DoesNotContain(schedules, j => j.Id == "cj_old");
        var outreach = schedules.Single(j => j.Seat == "outreach");
        Assert.Equal("SOREN_SOUTH", outreach.Target.Machine);
        Assert.Empty(_s.Triggers(TheFactory));

        Assert.Equal("Definition: version 1 published.", result.Applied[0]);
        Assert.Contains(@"Schedule cj_boss (DevThrottle - Boss) changed: name ""devthrottle - boss"" -> ""DevThrottle - Boss""; folder D:\somewhere -> D:\ReposFred\devthrottle.", result.Applied);
        Assert.Contains($"Schedule {outreach.Id} (DevThrottle - Outreach) created for seat outreach: Weekdays 09:00 Europe/Copenhagen.", result.Applied);
        Assert.Contains("Schedule cj_old (devthrottle - writer) deleted: no start in the definition names it; its runs are kept.", result.Applied);
        Assert.Equal("Trigger inbox-watch deleted: checks from a definition run from phase 5b, so until then a defined factory has no triggers; its run history is kept.",
            result.Applied[^1]);
    }

    // ---------- the failure: nothing changes ----------

    [Fact]
    public void Publish_WhoseScheduleWriteFails_ChangesNothing_RegistrySchedulesTriggersAndThePublishMark()
    {
        var failing = FactoryPublishScenario.Failing(b => b.UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = _h.DbPath,
            ForeignKeys = true,
        }.ToString()));

        FactoryPublishScenario.PublishWhoseScheduleWriteFails_ChangesNothing(_s, failing);
    }

    [Fact]
    public void Restore_WhoseScheduleWriteFails_ChangesNothing_AndWritesNoCopy()
    {
        var failing = FactoryPublishScenario.Failing(b => b.UseSqlite(new SqliteConnectionStringBuilder { DataSource = _h.DbPath, ForeignKeys = true }.ToString()));
        _s.SaveAndPublish(DefinitionFixture.Publishable());
        _s.SaveAndPublish(DefinitionFixture.PublishableEdit("seats/boss.yaml", "\"0 8 * * 1-5\"", "\"0 7 * * 1-5\""));
        var bossId = _s.Schedules(TheFactory).Single(j => j.Seat == "boss").Id;
        var before = _s.Snapshot();

        var result = _s.Restore(1, through: failing);

        Assert.Equal(FactoryDefinitionOutcome.WriteFailed, result.Write.Outcome);
        Assert.Equal($"The restore changed nothing: writing schedule {bossId} for seat 'boss' failed: {FailTheScheduleWrite.Message}", result.Write.Refusal);
        Assert.Equal(before, _s.Snapshot());
        Assert.Equal(2, _s.Store.Standing(_s.Tenant, TheFactory).Newest!.Version);
    }

    // ---------- what it replaces ----------

    [Fact]
    public void Publish_ReplacesAVersionThatIsNoLongerPublished_IsRefused409_AndWritesNothing()
    {
        _s.SaveAndPublish(DefinitionFixture.Publishable());
        var v2 = _s.Save(DefinitionFixture.PublishableEdit("seats/writer.yaml", "name: Writer", "name: Author"));
        var before = _s.Snapshot();

        var stale = _s.Publish(v2, replaces: 0);

        Assert.Equal(FactoryDefinitionOutcome.Stale, stale.Write.Outcome);
        Assert.Equal("Not published: version 1 is the published definition now, not 0. Read what changes again and publish once more.",
            stale.Write.Refusal);
        Assert.Equal(before, _s.Snapshot());
    }

    [Fact]
    public void Restore_ReplacesAVersionThatIsNoLongerPublished_IsRefused409_AndWritesNothing()
    {
        _s.SaveAndPublish(DefinitionFixture.Publishable());
        _s.SaveAndPublish(DefinitionFixture.PublishableEdit("seats/writer.yaml", "name: Writer", "name: Author"));
        var before = _s.Snapshot();

        var stale = _s.Restore(1, replaces: 1);

        Assert.Equal(FactoryDefinitionOutcome.Stale, stale.Write.Outcome);
        Assert.Equal("Not restored: version 2 is the published definition now, not 1. Read what changes again and restore once more.",
            stale.Write.Refusal);
        Assert.Equal(before, _s.Snapshot());
    }

    [Fact]
    public void Publish_FirstPublishStatingNone_ReplacesZero_IsAccepted()
    {
        var first = _s.Publish(_s.Save(DefinitionFixture.Publishable()), replaces: 0);

        Assert.True(first.Write.Ok, first.Write.Refusal);
        Assert.True(first.Changes!.FirstPublish);
    }

    [Fact]
    public void Publish_TwoInARow_EachAnswersWhatChangesAgainstTheVersionItReallyReplaced()
    {
        var v1 = _s.Save(DefinitionFixture.Publishable());
        var v2 = _s.Save(DefinitionFixture.PublishableEdit("seats/writer.yaml", "name: Writer", "name: Author"));
        var v3 = _s.Save(DefinitionFixture.PublishableEdit("seats/writer.yaml", "name: Writer", "name: Author")
            .Also("seats/boss.yaml", "\"0 8 * * 1-5\"", "\"0 7 * * 1-5\""));
        Assert.True(_s.Publish(v1).Write.Ok);

        // Neither states what it replaces: the answer must still be against the version each really replaced.
        var second = _s.Publish(v2);
        var third = _s.Publish(v3);

        Assert.False(second.Changes!.FirstPublish);
        Assert.Contains(second.Changes.Seats, c => c.Seat == "writer" && c.Details.Contains("name Writer -> Author"));
        Assert.Empty(second.Changes.Starts);
        Assert.DoesNotContain(third.Changes!.Seats, c => c.Seat == "writer");
        var moved = Assert.Single(third.Changes.Starts);
        Assert.Equal("Boss: start moved, Weekdays 08:00 Europe/Copenhagen -> Weekdays 07:00 Europe/Copenhagen.", moved.Sentence);
    }

    // ---------- refusals before any write ----------

    [Fact]
    public void Publish_OfAFactoryTheRegistryDoesNotHold_IsRefused_AndWritesNothing()
    {
        using (var ctx = _s.Db.CreateContext(_s.Tenant))
        {
            ctx.FactoryRegistry.Remove(ctx.FactoryRegistry.Single(f => f.Factory == TheFactory));
            ctx.SaveChanges();
        }
        var v1 = _s.Save(DefinitionFixture.Publishable());
        var before = _s.Snapshot();

        var refused = _s.Publish(v1);

        Assert.Equal(FactoryDefinitionOutcome.NotRegistered, refused.Write.Outcome);
        Assert.Equal("devthrottle is not registered, and until phase 5 a defined factory runs in its registered folder. Register it first, then publish.",
            refused.Write.Refusal);
        Assert.Equal(before, _s.Snapshot());
    }

    [Fact]
    public void Publish_OfAnArchivedFactory_IsRefused_AndWritesNothing()
    {
        new FactoryRegistryStore(_s.Db).Archive(_s.Tenant, TheFactory, "the owner", Array.Empty<string>(), FactoryPublishScenario.Now);
        var v1 = _s.Save(DefinitionFixture.Publishable());
        var before = _s.Snapshot();

        var refused = _s.Publish(v1);

        Assert.Equal(FactoryDefinitionOutcome.Archived, refused.Write.Outcome);
        Assert.Equal("devthrottle is archived. Restore it, then publish.", refused.Write.Refusal);
        Assert.Equal(before, _s.Snapshot());
    }

    [Fact]
    public void Publish_AVersionAlreadyPublished_IsTheStoresOwnRuling_AndWritesNothing()
    {
        _s.SaveAndPublish(DefinitionFixture.Publishable());
        var before = _s.Snapshot();

        var again = _s.Publish(1);

        Assert.Equal(FactoryDefinitionOutcome.AlreadyPublished, again.Write.Outcome);
        Assert.Equal("version 1 is already the published definition of devthrottle", again.Write.Refusal);
        Assert.Equal(before, _s.Snapshot());
    }

    // ---------- goal 14 ----------

    [Fact]
    public void Publish_And_Restore_And_Plan_LeaveTheUNDEFINED_FACTORY_UntouchedWhole()
    {
        string Undefined() => System.Text.Json.JsonSerializer.Serialize(new
        {
            registry = _s.RegistryRow(FactoryPublishScenario.Undefined),
            schedules = _s.Schedules(FactoryPublishScenario.Undefined),
            triggers = _s.Triggers(FactoryPublishScenario.Undefined),
        });
        var before = Undefined();

        _s.SaveAndPublish(DefinitionFixture.Publishable());
        _s.SaveAndPublish(DefinitionFixture.PublishableEdit("seats/writer.yaml", "name: Writer", "name: Author"));
        Assert.True(_s.Restore(1).Write.Ok);
        _s.Apply.Plan(_s.Tenant, FactoryPublishScenario.Parse(DefinitionFixture.Publishable()));

        Assert.Equal(before, Undefined());
        Assert.Null(_s.Store.Standing(_s.Tenant, FactoryPublishScenario.Undefined).Newest);
    }

    [Fact]
    public void Plan_ReadsTheLiveFactory_AndWritesNothing()
    {
        _s.AddSchedule(TheFactory, "writer", "cj_old", "0 12 * * *", "An old start.");
        var before = _s.Snapshot();

        var plan = _s.Apply.Plan(_s.Tenant, FactoryPublishScenario.Parse(DefinitionFixture.Publishable()));

        Assert.Contains("Schedule cj_old (devthrottle - writer) deleted: no start in the definition names it; its runs are kept.", plan.Plan!.Sentences());
        Assert.Equal(before, _s.Snapshot());
    }
}
