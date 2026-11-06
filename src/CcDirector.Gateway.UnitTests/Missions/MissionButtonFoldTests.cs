using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Missions;
using Xunit;

namespace CcDirector.Gateway.Tests.Missions;

/// <summary>
/// The Mission button's verdict (the Missions First-Class mission): the three states, the goal missing from the
/// document, the record's name winning over a stale cached one, and the stamp through the REAL shared fold that the
/// roster and the desktop push both run - so a verdict computed here but never put on the wire would fail.
/// </summary>
public sealed class MissionButtonFoldTests
{
    private static readonly Guid MissionId = Guid.Parse("aaaaaaaa-1111-4111-8111-000000000001");
    private static readonly Guid ReportId = Guid.Parse("dddddddd-3333-4333-8333-000000000001");
    private static readonly TenantId Tenant = new("tenant-mission-button");

    private static Mission MissionRecord(Guid? document = null, string goal = "", string name = "Missions First-Class") => new()
    {
        MissionId = MissionId,
        MissionName = name,
        DocumentReportId = document,
        Goal = goal,
        TenantId = Tenant.Value,
    };

    private static SessionDto Session(Guid? missionId, string? cachedName = null) => new()
    {
        SessionId = Guid.NewGuid().ToString("D"),
        MissionId = missionId,
        MissionName = cachedName,
    };

    private static Dictionary<Guid, Mission> Store(params Mission[] missions) => missions.ToDictionary(m => m.MissionId);

    [Fact]
    public void For_MissionWithDocument_ShowsNameInMissionColourWithGoalTooltipAndAddress()
    {
        var verdict = MissionButtonFold.For(Session(MissionId),
            Store(MissionRecord(ReportId, goal: "Every session shows its Mission.")));

        Assert.Equal(MissionButtonDisplay.VerdictMissionWithDocument, verdict.Verdict);
        Assert.Equal("Missions First-Class", verdict.Label);
        Assert.Equal(MissionButtonDisplay.ColorMission, verdict.ColorKey);
        Assert.Equal("Mission: Missions First-Class\nGoal: Every session shows its Mission.", verdict.Tooltip);
        Assert.Equal($"/r/{ReportId:D}", verdict.DocumentAddress);
    }

    [Fact]
    public void For_MissionWithDocumentButNoGoal_TooltipSaysTheGoalIsNotSet()
    {
        var verdict = MissionButtonFold.For(Session(MissionId), Store(MissionRecord(ReportId, goal: "   ")));

        Assert.Equal(MissionButtonDisplay.VerdictMissionWithDocument, verdict.Verdict);
        Assert.Equal(MissionButtonDisplay.ColorMission, verdict.ColorKey);
        Assert.Equal("Mission: Missions First-Class\nGoal: not set - the mission document has no goal section.", verdict.Tooltip);
        Assert.Equal($"/r/{ReportId:D}", verdict.DocumentAddress);
    }

    [Fact]
    public void For_MissionWithoutDocument_ShowsNameAsOutlineWithNoAddress()
    {
        var verdict = MissionButtonFold.For(Session(MissionId), Store(MissionRecord(document: null)));

        Assert.Equal(MissionButtonDisplay.VerdictMissionWithoutDocument, verdict.Verdict);
        Assert.Equal("Missions First-Class", verdict.Label);
        Assert.Equal(MissionButtonDisplay.ColorMissionOutline, verdict.ColorKey);
        Assert.Equal("Mission: Missions First-Class\nThis Mission has no document yet.", verdict.Tooltip);
        Assert.Null(verdict.DocumentAddress);
    }

    [Fact]
    public void For_NoMission_ShowsYellowNoMissionWithNoAddress()
    {
        var verdict = MissionButtonFold.For(Session(missionId: null), Store(MissionRecord(ReportId)));

        Assert.Equal(MissionButtonDisplay.VerdictNoMission, verdict.Verdict);
        Assert.Equal("No mission", verdict.Label);
        Assert.Equal(MissionButtonDisplay.ColorWarning, verdict.ColorKey);
        Assert.StartsWith("This session has no Mission.", verdict.Tooltip);
        Assert.Null(verdict.DocumentAddress);
    }

    [Fact]
    public void For_EmptyMissionId_IsNoMission()
    {
        var verdict = MissionButtonFold.For(Session(Guid.Empty), Store());

        Assert.Equal(MissionButtonDisplay.VerdictNoMission, verdict.Verdict);
    }

    [Fact]
    public void For_RenamedMission_RecordNameWinsOverTheSessionsCachedName()
    {
        var verdict = MissionButtonFold.For(Session(MissionId, cachedName: "Old name"),
            Store(MissionRecord(ReportId, goal: "g", name: "New name")));

        Assert.Equal("New name", verdict.Label);
        Assert.StartsWith("Mission: New name", verdict.Tooltip);
    }

    [Fact]
    public void For_MissionNotInTheStore_ReadsNoDocumentUnderTheSessionsName()
    {
        var verdict = MissionButtonFold.For(Session(MissionId, cachedName: "Elsewhere"), Store());

        Assert.Equal(MissionButtonDisplay.VerdictMissionWithoutDocument, verdict.Verdict);
        Assert.Equal("Elsewhere", verdict.Label);
        Assert.Null(verdict.DocumentAddress);
    }

    [Fact]
    public void Stamp_NoMissionsRead_AssignsNullOverADirectorsEcho()
    {
        var echoed = Session(MissionId);
        echoed.MissionButton = new MissionButtonDisplay { Label = "stale echo" };

        MissionButtonFold.Stamp(new[] { echoed }, missions: null);

        Assert.Null(echoed.MissionButton);
    }

    [Fact]
    public void StampFleetRolesAndFold_WithMissions_PutsEachVerdictOnItsRow()
    {
        var linked = Session(MissionId);
        var none = Session(missionId: null);
        var rows = new List<SessionDto> { linked, none };
        var missions = new List<Mission> { MissionRecord(ReportId, goal: "Ship it.") };

        GatewayEndpoints.StampFleetRolesAndFold(rows, rows, tenant: Tenant, missionsFor: t =>
        {
            Assert.Equal(Tenant, t);
            return missions;
        });

        Assert.Equal(MissionButtonDisplay.VerdictMissionWithDocument, linked.MissionButton?.Verdict);
        Assert.Equal($"/r/{ReportId:D}", linked.MissionButton?.DocumentAddress);
        Assert.Equal(MissionButtonDisplay.VerdictNoMission, none.MissionButton?.Verdict);
    }

    [Fact]
    public void StampFleetRolesAndFold_NoMissionSource_StampsNoVerdict()
    {
        var row = Session(MissionId);
        row.MissionButton = new MissionButtonDisplay { Label = "stale echo" };
        var rows = new List<SessionDto> { row };

        GatewayEndpoints.StampFleetRolesAndFold(rows, rows, tenant: Tenant);

        Assert.Null(row.MissionButton);
    }

    [Fact]
    public void MissionsForFold_ReadsEveryStateOfTheTenantsMissions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_missions_button_{Guid.NewGuid()}.json");
        try
        {
            var store = new MissionStore(path, adoptUnattributedAs: null);
            var active = store.Create(Tenant, "Active one");
            var ended = store.Create(Tenant, "Ended one");
            store.SetState(Tenant, ended.MissionId, MissionStates.Complete, new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));
            store.Create(new TenantId("someone-else"), "Not ours");

            var read = GatewayEndpoints.MissionsForFold(store)!(Tenant);

            Assert.Equal(new[] { active.MissionId, ended.MissionId }.OrderBy(g => g), read.Select(m => m.MissionId).OrderBy(g => g));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
