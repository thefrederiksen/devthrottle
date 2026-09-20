using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests.DevReports;

/// <summary>
/// Restored sessions inherit dev reports (the Smart Director Restart mission, section 5.3 item 13), over the REAL
/// workspace store and the REAL dev report store on one migrated database. Nothing here hand-builds a restored
/// seat: every seat that "came back" came back the way a restore brings one back - a lease, a "started" mark with a
/// token, and a "restored" mark carrying that token - because the rule under test reads exactly those stored facts,
/// and a hand-built one would prove only that the test can write a document.
/// </summary>
public sealed class DevReportInheritanceTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
    private const string Id = "smart-restart-1";
    private const string DirectorA = "director-a";
    private const string DirectorB = "director-b";
    private const string ReportFile = @"D:\repo\docs\report.html";

    private static readonly string Old = Guid.NewGuid().ToString("D");
    private static readonly string OtherOld = Guid.NewGuid().ToString("D");
    private static readonly string New = Guid.NewGuid().ToString("D");
    private static readonly string Stranger = Guid.NewGuid().ToString("D");

    private readonly GatewayDbTestHarness _h = new();
    private readonly WorkspaceStore _workspaces;
    private readonly DevReportStore _reports;

    public DevReportInheritanceTests()
    {
        var db = _h.Open();
        _workspaces = new WorkspaceStore(db);
        _reports = new DevReportStore(db);
        _workspaces.Create(new WorkspaceDocument
        {
            Id = Id,
            Name = "Smart restart",
            Origin = WorkspaceOrigins.Captured,
            Machine = "MAC",
            DirectorId = "old-director",
            Seats = new() { Seat(Old, "Lead"), Seat(OtherOld, "Developer") },
            RestoreAfterRestart = new() { Old, OtherOld },
        }, Now);
    }

    public void Dispose() => _h.Dispose();

    private static WorkspaceSeat Seat(string id, string name) => new()
    {
        SessionId = id,
        Name = name,
        Agent = "ClaudeCode",
        RepoPath = "/repos/devthrottle",
        DrainState = WorkspaceDrainStates.Drained,
        HandoverPath = $"/handovers/{name}.md",
        Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Restore, Why = "test", Command = "cc-devthrottle director restore ..." },
    };

    /// <summary>Bring a seat back the way a restore does: lease, started with a token, restored with that token.</summary>
    private void BringBack(string seat, string asSession, string director = DirectorA)
    {
        _workspaces.TakeRestoreLease(Id, director, null, Now);
        _workspaces.RecordRestoreMark(Id, new WorkspaceRestoreMark
            { DirectorId = director, Kind = WorkspaceRestoreMarkKinds.Started, SeatSessionId = seat, Token = "tok-" + seat }, Now);
        _workspaces.RecordRestoreMark(Id, new WorkspaceRestoreMark
            { DirectorId = director, Kind = WorkspaceRestoreMarkKinds.Restored, SeatSessionId = seat, Token = "tok-" + seat, RestoredSessionId = asSession }, Now);
    }

    private (Guid Id, int Version, bool Created) Publish(string sessionId, string key = ReportFile, TenantId? tenant = null)
    {
        var (report, created) = _reports.Publish(tenant ?? Tenant, sessionId, key, "<p>html</p>", "waiting-on-you", "Report", Now);
        return (report.Id, report.Version, created);
    }

    private WorkspaceDevReportPassResult Pass(string seat = "", string director = DirectorA, DateTime? at = null)
        => DevReportInheritance.Pass(_workspaces.Get(Id)!,
            new WorkspaceDevReportPassRequest { DirectorId = director, SeatSessionId = seat.Length == 0 ? Old : seat },
            _reports, Tenant, at ?? Now);

    // ================= the defect, and the fix =================

    [Fact]
    public void WithoutThePass_TheRestoredSessionRepublishingTheSameFile_GetsANewReportAtANewLink()
    {
        // The defect this work exists for, pinned so the next test's "same link" is known to be the pass's doing.
        var before = Publish(Old);
        BringBack(Old, New);

        var after = Publish(New);

        Assert.True(after.Created);
        Assert.NotEqual(before.Id, after.Id);
    }

    [Fact]
    public void Pass_ARestoredSeat_TheNewSessionRepublishingTheSameFile_UpdatesTheSameReportAtTheSameLink()
    {
        var before = Publish(Old);
        BringBack(Old, New);

        var result = Pass();

        Assert.Equal((Old, New, 1), (result.FromSessionId, result.ToSessionId, result.Passed));
        Assert.Empty(result.KeptBecauseTheNewSessionAlreadyHasTheKey);
        var after = Publish(New);
        Assert.False(after.Created);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(2, after.Version);
        Assert.Empty(_reports.List(Tenant, Old));
        // Version 1 - what the owner was reading - is still there under the same report.
        Assert.NotNull(_reports.GetVersion(Tenant, before.Id, 1));
    }

    [Fact]
    public void Pass_TheNotesTheOwnerWroteBeforeTheRestart_GoToTheSessionThatHoldsTheReportNow()
    {
        var before = Publish(Old);
        var report = _reports.Get(Tenant, before.Id)!;
        var note = new DevReportItem("n1", DevReportItem.Note, "This number is wrong",
            new DevReportAnchor(DevReportAnchor.TableCell, "#t td", "42", "Gateway", "Failures", null), "", "", "", "", "");
        _reports.AddItems(Tenant, report, [note], DevReportItemStates.HeldState, "device", Now);
        BringBack(Old, New);

        Pass();

        Assert.Empty(_reports.WaitingItemsForSession(Tenant, Old));
        Assert.Equal("n1", Assert.Single(_reports.WaitingItemsForSession(Tenant, New)).ClientItemId);
    }

    [Fact]
    public void Pass_AskedTwice_IsSafe()
    {
        var before = Publish(Old);
        BringBack(Old, New);

        Pass();
        var second = Pass();

        Assert.Equal(0, second.Passed);
        var mine = Assert.Single(_reports.List(Tenant, New));
        Assert.Equal((before.Id, 1), (mine.Id, mine.Version));
    }

    [Fact]
    public void Pass_OnlyThatSeatsReports_AndOnlyInThisAccount()
    {
        var mine = Publish(Old);
        var otherSeat = Publish(OtherOld);
        var otherAccount = new TenantId("tenant-somebody-else");
        var theirs = Publish(Old, tenant: otherAccount);
        BringBack(Old, New);

        var result = Pass();

        Assert.Equal(1, result.Passed);
        Assert.Equal(mine.Id, Assert.Single(_reports.List(Tenant, New)).Id);
        Assert.Equal(otherSeat.Id, Assert.Single(_reports.List(Tenant, OtherOld)).Id);
        Assert.Equal(theirs.Id, Assert.Single(_reports.List(otherAccount, Old)).Id);
    }

    [Fact]
    public void Pass_TheNewSessionAlreadyPublishedThatFile_TheOldReportStaysFrozen_AndTheAnswerNamesIt()
    {
        var old = Publish(Old);
        var other = Publish(Old, @"D:\repo\docs\other.html");
        BringBack(Old, New);
        var early = Publish(New);

        var result = Pass();

        Assert.Equal(1, result.Passed);
        Assert.Equal(ReportFile, Assert.Single(result.KeptBecauseTheNewSessionAlreadyHasTheKey));
        Assert.Equal(old.Id, Assert.Single(_reports.List(Tenant, Old)).Id);
        Assert.Equal(new[] { early.Id, other.Id }.Order(), _reports.List(Tenant, New).Select(r => r.Id).Order());
    }

    // ================= a seat that is not brought back keeps its reports frozen =================

    [Fact]
    public void Pass_ASeatThatHasNotComeBack_IsRefused_AndItsReportsStayWhereTheyAre()
    {
        var before = Publish(OtherOld);
        BringBack(Old, New);

        var ex = Assert.Throws<WorkspaceConflictException>(() => Pass(seat: OtherOld));

        Assert.Contains("has not come back", ex.Message);
        Assert.Equal(before.Id, Assert.Single(_reports.List(Tenant, OtherOld)).Id);
    }

    // ================= only the restore of a recorded seat can cause it =================

    [Fact]
    public void Pass_ARestoredIdWrittenByAnOrdinaryWrite_IsNotAJoin_SoNothingPasses()
    {
        // How a session would try to take over another session's reports: write itself onto the seat as "what it came
        // back as", then have the pass asked. An ordinary write keeps the stored marks, so the seat has not come back.
        var victim = Publish(Old);
        var forged = _workspaces.Get(Id)!;
        forged.Seats.Single(s => s.SessionId == Old).RestoredSessionId = Stranger;
        _workspaces.Save(forged, Now);
        _workspaces.TakeRestoreLease(Id, DirectorA, null, Now);

        var ex = Assert.Throws<WorkspaceConflictException>(() => Pass());

        Assert.Contains("has not come back", ex.Message);
        Assert.Equal(victim.Id, Assert.Single(_reports.List(Tenant, Old)).Id);
        Assert.Empty(_reports.List(Tenant, Stranger));
    }

    [Fact]
    public void Pass_ByADirectorThatDoesNotHoldTheLease_IsRefused_AndNothingPasses()
    {
        var before = Publish(Old);
        BringBack(Old, New);

        var other = Assert.Throws<WorkspaceConflictException>(() => Pass(director: DirectorB));
        var lapsed = Assert.Throws<WorkspaceConflictException>(() => Pass(at: Now + WorkspaceRestoreLease.Expiry + TimeSpan.FromMinutes(1)));
        _workspaces.ReleaseRestoreLease(Id, DirectorA);
        var nobody = Assert.Throws<WorkspaceConflictException>(() => Pass());

        Assert.Contains("does not hold the restore lease", other.Message);
        Assert.Contains("has expired", lapsed.Message);
        Assert.Contains("nobody does", nobody.Message);
        Assert.Equal(before.Id, Assert.Single(_reports.List(Tenant, Old)).Id);
    }

    [Fact]
    public void Pass_ASeatThatIsNotInTheWorkspace_OrABlankRequest_IsRefused()
    {
        BringBack(Old, New);

        Assert.Contains("has no seat", Assert.Throws<WorkspaceValidationException>(() => Pass(seat: Stranger)).Message);
        Assert.Contains("directorId is required", Assert.Throws<WorkspaceValidationException>(() => Pass(director: " ")).Message);
        Assert.Contains("seatSessionId is required", Assert.Throws<WorkspaceValidationException>(() =>
            DevReportInheritance.Pass(_workspaces.Get(Id)!, new WorkspaceDevReportPassRequest { DirectorId = DirectorA }, _reports, Tenant, Now)).Message);
    }

    [Fact]
    public void Pass_AnAuthoredWorkspace_IsRefused()
    {
        // The store refuses restore marks on an authored workspace, so this one document IS built by hand: it is the
        // only way to show the rule does not rest on the store's refusal alone.
        var before = Publish(Old);
        var authored = new WorkspaceDocument
        {
            Id = "typed", Name = "Typed", Origin = WorkspaceOrigins.Authored,
            RestoreLease = new WorkspaceRestoreLease { DirectorId = DirectorA, GrantedAtUtc = Now, RenewedAtUtc = Now },
            Seats = new() { Seat(Old, "Lead") },
        };
        authored.Seats[0].RestoredSessionId = Stranger;

        var ex = Assert.Throws<WorkspaceConflictException>(() => DevReportInheritance.Pass(authored,
            new WorkspaceDevReportPassRequest { DirectorId = DirectorA, SeatSessionId = Old }, _reports, Tenant, Now));

        Assert.Contains("not captured", ex.Message);
        Assert.Equal(before.Id, Assert.Single(_reports.List(Tenant, Old)).Id);
    }
}
