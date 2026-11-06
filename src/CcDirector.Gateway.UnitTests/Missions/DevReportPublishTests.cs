using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Missions;

/// <summary>
/// The ORDER of a dev report publish (review finding 7) and the refresh of a republished mission document without the
/// flag (review finding 4, owner ruling), over the real report store on a throwaway database and a real mission
/// store on disk. The publish step is the one the route calls; the route only turns its outcome into a response.
/// </summary>
public sealed class DevReportPublishTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private static readonly DateTime T0 = new(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);
    private const string Sid = "40490db1-ba99-44fc-9013-debdb708d460";
    private const string Key = "d:\\briefs\\mission.html";

    private readonly GatewayDbTestHarness _db = new();
    private readonly DevReportStore _reports;
    private readonly string _missionsPath = Path.Combine(Path.GetTempPath(), $"test_publish_missions_{Guid.NewGuid()}.json");
    private readonly MissionStore _missions;
    private readonly Dictionary<string, Guid> _attached = new(StringComparer.Ordinal);

    public DevReportPublishTests()
    {
        _reports = new DevReportStore(_db.Open());
        _missions = new MissionStore(_missionsPath, adoptUnattributedAs: null);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_missionsPath)) File.Delete(_missionsPath);
    }

    private MissionDocumentLinker Linker() =>
        new(_missions, (_, sid) => _attached.TryGetValue(sid, out var id) ? id : null);

    private static string Document(string goal) =>
        $"<section data-dev-report=\"why\"><p>Fewer questions.</p></section><section data-dev-report=\"goal\"><p>{goal}</p></section>";

    private DevReportPublishOutcome Publish(string html, bool asMissionDocument, MissionDocumentLinker? linker = null, DateTime? at = null)
        => DevReportPublish.Run(_reports, linker ?? Linker(), DevReportAuthorAnswer.PersonalAccount, Tenant, Sid, Key, html,
            "agent-working", "Mission", at ?? T0, factorySeat: null, asMissionDocument);

    [Fact]
    public void Run_MissionDocumentFromASessionWithNoMission_RefusesAndWritesNoReport()
    {
        var outcome = Publish(Document("g"), asMissionDocument: true);

        Assert.True(outcome.Refused);
        Assert.Equal("no_mission", outcome.RefusalCode);
        Assert.Null(outcome.Report);
        Assert.Empty(_reports.List(Tenant, Sid));
    }

    [Fact]
    public void Run_MissionDocument_PublishesAndLinks()
    {
        var mission = _missions.Create(Tenant, "Missions First-Class");
        _attached[Sid] = mission.MissionId;

        var outcome = Publish(Document("Every session shows its Mission."), asMissionDocument: true);

        Assert.False(outcome.Refused);
        Assert.Equal(1, outcome.Report!.Version);
        Assert.Equal(mission.MissionId, outcome.Link!.MissionId);
        Assert.Equal(outcome.Report.Id, _missions.Get(Tenant, mission.MissionId)!.DocumentReportId);
    }

    [Fact]
    public void Run_MissionRemovedBetweenTheLookupAndTheLink_ReportIsWrittenAndTheLinkIsLost()
    {
        // Two stores over one file, as two Gateway containers share it during a deploy. "Here" still holds the Mission
        // in its in-memory copy (a fixed clock keeps that copy fresh), so the lookup before the publish finds it; the
        // other container has already removed it from the file, so the link - which always reads the file - does not.
        var clock = () => T0;
        var here = new MissionStore(_missionsPath, adoptUnattributedAs: null, utcNow: clock);
        var mission = here.Create(Tenant, "Gone soon");
        _attached[Sid] = mission.MissionId;
        new MissionStore(_missionsPath, adoptUnattributedAs: null, utcNow: clock).Delete(Tenant, mission.MissionId);
        var linker = new MissionDocumentLinker(here, (_, sid) => _attached.TryGetValue(sid, out var id) ? id : null);

        var outcome = Publish(Document("g"), asMissionDocument: true, linker);

        Assert.False(outcome.Refused);
        Assert.True(outcome.LinkLost);
        Assert.Equal(mission.MissionId, outcome.LinkLostMissionId);
        Assert.Equal(1, outcome.Report!.Version);
        Assert.Single(_reports.List(Tenant, Sid));
    }

    [Fact]
    public void Run_RepublishWithoutTheFlagOfTheMissionsDocument_RefreshesTheGoal()
    {
        var mission = _missions.Create(Tenant, "Missions First-Class");
        _attached[Sid] = mission.MissionId;
        Publish(Document("First goal."), asMissionDocument: true);

        var outcome = Publish(Document("Sharper goal."), asMissionDocument: false, at: T0.AddHours(1));

        Assert.Equal(2, outcome.Report!.Version);
        Assert.NotNull(outcome.Link);
        Assert.Equal("Sharper goal.", _missions.Get(Tenant, mission.MissionId)!.Goal);
    }

    [Fact]
    public void Run_OrdinaryReportWithoutTheFlag_LeavesTheMissionAlone()
    {
        var mission = _missions.Create(Tenant, "Missions First-Class");
        _attached[Sid] = mission.MissionId;

        var outcome = Publish(Document("Not a mission document."), asMissionDocument: false);

        Assert.Null(outcome.Link);
        var stored = _missions.Get(Tenant, mission.MissionId)!;
        Assert.Null(stored.DocumentReportId);
        Assert.Equal("", stored.Goal);
    }
}
