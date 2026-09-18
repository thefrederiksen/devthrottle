using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// PINNING AND THE OFFERED CHANGE OF OWNER (the Fleet Manager mission, step 8), stamped by the REAL roster fold
/// (<see cref="GatewayEndpoints.StampFleetRolesAndFold"/>) that the session list, the Exes page and the single-session
/// read all serve: only the account's live Fleet Manager is pinned, its team is the ownership tree under it, and each
/// row offers exactly the change of owner the owner may make now.
/// </summary>
public sealed class FleetManagerRosterFoldTests
{
    private const string Fm = "30000000-0000-4000-8000-000000000001";
    private const string Worker = "30000000-0000-4000-8000-000000000002";
    private const string WorkersWorker = "30000000-0000-4000-8000-000000000003";
    private const string Plain = "30000000-0000-4000-8000-000000000004";
    private const string Architect = "30000000-0000-4000-8000-000000000005";
    private const string ArchitectWorker = "30000000-0000-4000-8000-000000000006";
    private const string Orphan = "30000000-0000-4000-8000-000000000007";
    private const string Ended = "30000000-0000-4000-8000-000000000008";

    private static SessionDto Row(string sid, string? controller = null, string state = "WaitingForInput") => new()
    {
        SessionId = sid,
        Name = "session " + sid[^1],
        ActivityState = state,
        IsControlled = controller is not null,
        ControllerSessionId = controller,
        CreatedAt = new DateTime(2026, 9, 16, 9, 0, 0, DateTimeKind.Utc),
    };

    private static List<SessionDto> Fleet() => new()
    {
        Row(Plain),
        Row(Fm),
        Row(Worker, controller: Fm, state: "Working"),
        Row(WorkersWorker, controller: Worker, state: "Working"),
        Row(Architect),
        Row(ArchitectWorker, controller: Architect),
        Row(Orphan, controller: "30000000-0000-4000-8000-0000000000ff"),
        Row(Ended, state: "Exited"),
    };

    private static List<SessionDto> Fold(List<SessionDto> fleet, string? marked = Fm)
    {
        GatewayEndpoints.StampFleetRolesAndFold(fleet, fleet, tenant: TenantId.Local, fleetManagerMark: _ => marked);
        return fleet;
    }

    private static SessionDto Get(List<SessionDto> fleet, string sid) => fleet.Single(s => s.SessionId == sid);

    [Fact]
    public void StampFleetRolesAndFold_LiveFleetManager_IsTheOnlyPinnedRowAndWearsTheMark()
    {
        var fleet = Fold(Fleet());

        var pinned = Assert.Single(fleet, s => s.Pin is not null);
        Assert.Equal(Fm, pinned.SessionId);
        Assert.Equal(0, pinned.Pin!.Rank);
        Assert.Equal("Fleet Manager", pinned.Pin.Mark);
        Assert.Equal("Your Fleet Manager. The sessions it owns are under it and report to it, not to you.", pinned.Pin.Title);
        Assert.Equal("Not the Fleet Manager's - they ask you directly", pinned.Pin.OthersHeading);
        Assert.Equal("Hand sessions to the Fleet Manager...", pinned.Pin.HandOverLinkLabel);
        Assert.Null(pinned.OwnerChange);
    }

    [Fact]
    public void StampFleetRolesAndFold_FleetManagersTeam_IsTheOwnershipTreeUnderThePinnedRow()
    {
        var fleet = Fold(Fleet());

        var tree = SessionTree.Build(fleet);
        var fm = Get(fleet, Fm);
        Assert.Contains(fm, tree.Roots);
        Assert.Equal(new[] { Worker, WorkersWorker }, SessionTree.DescendantsOf(tree, fm).Select(d => d.Session.SessionId));
        Assert.DoesNotContain(tree.Roots, r => r.SessionId == Worker || r.SessionId == WorkersWorker);
    }

    [Fact]
    public void StampFleetRolesAndFold_EachRowOffersOnlyTheChangeTheOwnerMayMakeNow()
    {
        var fleet = Fold(Fleet());

        // The Fleet Manager's own session is handed back; the owner's own and the orphan are handed over.
        Assert.Equal("owner", Get(fleet, Worker).OwnerChange!.To);
        Assert.Equal("Hand back to me", Get(fleet, Worker).OwnerChange!.Label);
        Assert.Equal("fleet-manager", Get(fleet, Plain).OwnerChange!.To);
        Assert.Equal("Hand to the Fleet Manager", Get(fleet, Plain).OwnerChange!.Label);
        Assert.Equal("fleet-manager", Get(fleet, Orphan).OwnerChange!.To);
        // An Architect with a crew still asks the owner directly; its crew are its own.
        Assert.Equal("fleet-manager", Get(fleet, Architect).OwnerChange!.To);
        Assert.Null(Get(fleet, ArchitectWorker).OwnerChange);
        // Only the DIRECT owner counts: a session under the Fleet Manager's worker is that worker's.
        Assert.Null(Get(fleet, WorkersWorker).OwnerChange);
        Assert.Null(Get(fleet, Ended).OwnerChange);
        Assert.All(fleet.Where(s => s.SessionId != Fm), s => Assert.Null(s.Pin));
    }

    [Fact]
    public void StampFleetRolesAndFold_NoMark_PinsNothingAndOffersNothing()
    {
        var fleet = Fold(Fleet(), marked: null);

        Assert.All(fleet, s => Assert.Null(s.Pin));
        Assert.All(fleet, s => Assert.Null(s.OwnerChange));
    }

    [Fact]
    public void StampFleetRolesAndFold_MarkedSessionHasEnded_PinsNothingAndOffersNothing()
    {
        var fleet = Fleet();
        Get(fleet, Fm).ActivityState = "Exited";

        Fold(fleet);

        Assert.All(fleet, s => Assert.Null(s.Pin));
        Assert.All(fleet, s => Assert.Null(s.OwnerChange));
    }

    [Fact]
    public void StampFleetRolesAndFold_MarkedSessionIsOwnedBySomeone_IsNotPinned()
    {
        var fleet = Fleet();
        Get(fleet, Fm).IsControlled = true;
        Get(fleet, Fm).ControllerSessionId = Architect;

        Fold(fleet);

        Assert.All(fleet, s => Assert.Null(s.Pin));
    }

    [Fact]
    public void StampFleetRolesAndFold_AnswersADirectorSentAreOverwritten()
    {
        var fleet = Fleet();
        Get(fleet, Plain).Pin = new SessionPinDto { Mark = "Fleet Manager" };
        Get(fleet, Architect).OwnerChange = new SessionOwnerChangeDto { To = "owner", Label = "Hand back to me" };

        Fold(fleet);

        Assert.Null(Get(fleet, Plain).Pin);
        Assert.Equal("fleet-manager", Get(fleet, Architect).OwnerChange!.To);
    }

    [Fact]
    public void StampFleetRolesAndFold_FilteredResponse_IsStampedFromTheWholeAccount()
    {
        // The Fleet Manager is filtered OUT of the response: its worker still offers the hand back, because the
        // live Fleet Manager is found in the unfiltered account.
        var fleet = Fleet();
        var onlyWorker = new List<SessionDto> { Get(fleet, Worker) };

        GatewayEndpoints.StampFleetRolesAndFold(fleet, onlyWorker, tenant: TenantId.Local, fleetManagerMark: _ => Fm);

        Assert.Equal("owner", onlyWorker[0].OwnerChange!.To);
    }

    [Fact]
    public void AsksOwnerDirectly_IsTheRuleThePageCountAndTheRowOfferShare()
    {
        var fleet = Fold(Fleet());

        foreach (var s in fleet.Where(s => s.SessionId != Fm))
            Assert.Equal(FleetManagerSessions.AsksOwnerDirectly(s, Fm), s.OwnerChange?.To == "fleet-manager");
    }
}
