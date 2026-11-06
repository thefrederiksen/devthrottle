using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.DevReports;
using Xunit;

namespace CcDirector.Gateway.Tests.Missions;

/// <summary>
/// Publishing a report as a mission document (the Missions First-Class mission): the link, the relink on a
/// republish, a newer report taking the link over, the refusal for a session with no Mission and for a Mission
/// another account owns, and the why and goal read from the document - all over a real mission store on disk, read
/// back through a second store so the link is proven persisted, not only held in memory.
/// </summary>
public sealed class MissionDocumentLinkerTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant-mission-documents");
    private static readonly TenantId Other = new("tenant-mission-documents-other");
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    private const string Sid = "40490db1-ba99-44fc-9013-debdb708d460";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"test_mission_documents_{Guid.NewGuid()}.json");
    private readonly MissionStore _store;
    private readonly Dictionary<string, Guid> _attached = new(StringComparer.Ordinal);

    public MissionDocumentLinkerTests()
    {
        _store = new MissionStore(_path, adoptUnattributedAs: null);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private MissionDocumentLinker Linker() => new(_store, (tenant, sid) =>
    {
        Assert.Equal(Tenant, tenant);
        return _attached.TryGetValue(sid, out var id) ? id : null;
    });

    private static string Document(string? why = "Too many questions.", string? goal = "Every session shows its Mission.")
        => "<!doctype html><html><body>" +
           (why is null ? "" : $"<section data-dev-report=\"why\"><p>{why}</p></section>") +
           (goal is null ? "" : $"<section data-dev-report=\"goal\"><p>{goal}</p></section>") +
           "</body></html>";

    private Mission Reread(Guid missionId) => new MissionStore(_path, adoptUnattributedAs: null).Get(Tenant, missionId)!;

    [Fact]
    public void Resolve_SessionWithNoMission_RefusesWithNoMission()
    {
        var target = Linker().Resolve(Tenant, Sid);

        Assert.True(target.Refused);
        Assert.Equal("no_mission", target.RefusalCode);
        Assert.Contains("attached to no Mission", target.Refusal);
        Assert.Contains("Nothing was published", target.Refusal);
        // An attach made a moment ago reaches the Gateway on the Director's next push, so the refusal says so rather
        // than send the agent round to attach again (review finding 5).
        Assert.Contains("may not have reached the Gateway yet", target.Refusal);
    }

    [Fact]
    public void Resolve_MissionOwnedByAnotherAccount_RefusesAsUnknown()
    {
        var theirs = _store.Create(Other, "Not ours");
        _attached[Sid] = theirs.MissionId;

        var target = Linker().Resolve(Tenant, Sid);

        Assert.True(target.Refused);
        Assert.Equal("mission_unknown", target.RefusalCode);
    }

    [Fact]
    public void Resolve_SessionWithItsOwnMission_ReturnsThatMission()
    {
        var mine = _store.Create(Tenant, "Missions First-Class");
        _attached[Sid] = mine.MissionId;

        var target = Linker().Resolve(Tenant, Sid);

        Assert.False(target.Refused);
        Assert.Equal(mine.MissionId, target.Mission!.MissionId);
    }

    [Fact]
    public void Link_FirstPublish_LinksTheReportAndStampsGoalAndWhy()
    {
        var mission = _store.Create(Tenant, "Missions First-Class");
        var report = Guid.NewGuid();

        var result = Linker().Link(Tenant, mission, report, Document(), T0);

        Assert.NotNull(result);
        Assert.Null(result!.ReplacedReportId);
        Assert.Empty(result.Notes);
        var stored = Reread(mission.MissionId);
        Assert.Equal(report, stored.DocumentReportId);
        Assert.Equal(T0, stored.DocumentLinkedAt);
        Assert.Equal("Every session shows its Mission.", stored.Goal);
        Assert.Equal("Too many questions.", stored.Why);
        Assert.Equal(T0, stored.WhyUpdatedAt);
    }

    [Fact]
    public void Link_RepublishOfTheSameReport_KeepsTheLinkAndUpdatesTheGoal()
    {
        var mission = _store.Create(Tenant, "Missions First-Class");
        var report = Guid.NewGuid();
        Linker().Link(Tenant, mission, report, Document(goal: "First goal."), T0);

        var result = Linker().Link(Tenant, mission, report, Document(goal: "Sharper goal."), T0.AddHours(1));

        Assert.Null(result!.ReplacedReportId);
        var stored = Reread(mission.MissionId);
        Assert.Equal(report, stored.DocumentReportId);
        Assert.Equal("Sharper goal.", stored.Goal);
    }

    [Fact]
    public void Link_ADifferentReport_ReplacesTheDocumentAndSaysWhichItReplaced()
    {
        var mission = _store.Create(Tenant, "Missions First-Class");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Linker().Link(Tenant, mission, first, Document(), T0);

        var result = Linker().Link(Tenant, mission, second, Document(), T0.AddHours(1));

        Assert.Equal(first, result!.ReplacedReportId);
        Assert.Contains(result.Notes, n => n.Contains(first.ToString("D")));
        Assert.Equal(second, Reread(mission.MissionId).DocumentReportId);
    }

    [Fact]
    public void Link_DocumentWithNoGoalSection_LeavesTheMissionWithNoGoalAndSaysSo()
    {
        var mission = _store.Create(Tenant, "Missions First-Class");
        var report = Guid.NewGuid();
        Linker().Link(Tenant, mission, report, Document(goal: "Old goal."), T0);

        var result = Linker().Link(Tenant, mission, report, Document(goal: null), T0.AddHours(1));

        Assert.False(result!.GoalFound);
        Assert.Contains(result.Notes, n => n.Contains("no goal section"));
        Assert.Equal("", Reread(mission.MissionId).Goal);
    }

    [Fact]
    public void Link_DocumentWithNoWhySection_LeavesAHandSetWhyAsItWas()
    {
        var mission = _store.Create(Tenant, "Missions First-Class");
        _store.SetWhy(Tenant, mission.MissionId, "Set by hand.", T0);

        var result = Linker().Link(Tenant, mission, Guid.NewGuid(), Document(why: null), T0.AddHours(1));

        Assert.False(result!.WhyFound);
        Assert.Contains(result.Notes, n => n.Contains("no why section"));
        var stored = Reread(mission.MissionId);
        Assert.Equal("Set by hand.", stored.Why);
        Assert.Equal(T0, stored.WhyUpdatedAt);
    }

    [Fact]
    public void Link_MissionRemovedFromTheStoreBeforeTheLink_ReturnsNull()
    {
        var mission = _store.Create(Tenant, "Gone soon");
        _store.Delete(Tenant, mission.MissionId);

        Assert.Null(Linker().Link(Tenant, mission, Guid.NewGuid(), Document(), T0));
    }

    [Fact]
    public void SetDocument_AnotherAccountsMission_ChangesNothing()
    {
        var theirs = _store.Create(Other, "Not ours");

        Assert.Null(_store.SetDocument(Tenant, theirs.MissionId, Guid.NewGuid(), "g", "w", T0));
        Assert.Null(_store.Get(Other, theirs.MissionId)!.DocumentReportId);
    }

    [Fact]
    public void MissionStore_RecordWrittenBeforeDocuments_ReadsAsNoDocumentAndNoGoal()
    {
        var id = Guid.NewGuid();
        File.WriteAllText(_path,
            $"[{{\"MissionId\":\"{id}\",\"MissionName\":\"Old\",\"Why\":\"\",\"State\":\"active\"," +
            $"\"CreatedAt\":\"2026-08-01T00:00:00+00:00\",\"TenantId\":\"{Tenant.Value}\"}}]");

        var old = new MissionStore(_path, adoptUnattributedAs: null).Get(Tenant, id)!;

        Assert.Null(old.DocumentReportId);
        Assert.Null(old.DocumentLinkedAt);
        Assert.Equal("", old.Goal);
    }
}
