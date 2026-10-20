using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Workspaces;
using Xunit;
using static CcDirector.Gateway.Tests.MailDesk.MailDeskRoutingRig;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE RESTART RECORD AS THE MAIL DESK ROUTER READS IT (<see cref="WorkspaceStore.SeatsCapturedFrom"/>, phase 4 contract
/// section 3.2 step 3): the seat a Smart Restart captured from each session, in the account named, from records created
/// in the window, the newest record first. Over the rig's throwaway database; the clock is the rig's.
/// </summary>
public sealed class MailDeskRestartRecordReadTests : IAsyncLifetime
{
    private const string SessionOne = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string SessionTwo = "aaaaaaaa-0000-4000-8000-000000000002";
    private MailDeskRoutingRig _rig = null!;

    public Task InitializeAsync()
    {
        _rig = new MailDeskRoutingRig();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    private WorkspaceStore Store => new(_rig.Db);

    private static WorkspaceSeat Seat(string sessionId, string? restoredAs = null) => new()
    {
        SessionId = sessionId,
        Name = "Mail Desk - qa-books/clerk - 1 email - 2026-10-19 09:00",
        RestoredSessionId = restoredAs,
    };

    [Fact]
    public void SeatsCapturedFrom_TwoRecordsHoldTheSession_TheNewestRecordAnswers()
    {
        // Arrange
        _rig.SaveRestartRecord("restart-old", Seat(SessionOne, restoredAs: "old-answer"));
        _rig.Now = _rig.Now.AddMinutes(10);
        _rig.SaveRestartRecord("restart-new", Seat(SessionOne, restoredAs: "new-answer"), Seat(SessionTwo));

        // Act
        var seats = Store.SeatsCapturedFrom(MailDeskRoutingRig.Account, new[] { SessionOne, SessionTwo, "not-captured" }, _rig.Now.AddHours(-1));

        // Assert
        Assert.Equal(2, seats.Count);
        Assert.Equal(("restart-new", "new-answer"), (seats[SessionOne].WorkspaceId, seats[SessionOne].BroughtBackAs));
        Assert.Null(seats[SessionTwo].BroughtBackAs);
        Assert.False(seats.ContainsKey("not-captured"));
    }

    [Fact]
    public void SeatsCapturedFrom_ARecordCreatedBeforeTheWindow_IsNotRead()
    {
        // Arrange
        _rig.SaveRestartRecord("restart-before", Seat(SessionOne));

        // Act
        var seats = Store.SeatsCapturedFrom(MailDeskRoutingRig.Account, new[] { SessionOne }, _rig.Now.AddSeconds(1));

        // Assert
        Assert.Empty(seats);
    }

    [Fact]
    public void SeatsCapturedFrom_ACancelledRecordsKeptRunningSeat_IsNotRead_AndTheRestartTimeIsTheRecordsCreation()
    {
        // Arrange
        _rig.SaveRestartRecord("restart-cancelled", cancelledAtUtc: _rig.Now, Seat(SessionOne));
        var restartedAt = _rig.Now.AddMinutes(5);
        _rig.Now = restartedAt;
        _rig.SaveRestartRecord("restart-real", Seat(SessionTwo));

        // Act
        var seats = Store.SeatsCapturedFrom(MailDeskRoutingRig.Account, new[] { SessionOne, SessionTwo }, _rig.Now.AddHours(-1));

        // Assert
        var (id, seat) = Assert.Single(seats);
        Assert.Equal((SessionTwo, restartedAt), (id, seat.RecordedUtc));
    }

    [Fact]
    public void SeatsCapturedFrom_ACancelledRecord_ReadsTheSeatsItHadShutDown_NotTheSeatsItKeptRunning()
    {
        // Arrange
        var shutDown = Seat(SessionOne);
        shutDown.ClosedAtUtc = _rig.Now.AddMinutes(-1);
        _rig.SaveRestartRecord("restart-cancelled", cancelledAtUtc: _rig.Now, shutDown, Seat(SessionTwo));

        // Act
        var seats = Store.SeatsCapturedFrom(MailDeskRoutingRig.Account, new[] { SessionOne, SessionTwo }, _rig.Now.AddHours(-1));

        // Assert
        Assert.Equal(SessionOne, Assert.Single(seats).Key);
    }

    [Fact]
    public void SeatsCapturedFrom_ADamagedRecord_IsSkipped_AndTheOthersAreRead()
    {
        // Arrange
        _rig.SaveRestartRecord("restart-good", Seat(SessionOne));
        using (var ctx = _rig.Db.CreateContext(MailDeskRoutingRig.Account))
        {
            ctx.Workspaces.Add(new WorkspaceEntity
            {
                TenantId = ctx.ActiveTenant!, Id = "restart-damaged", Name = "restart-damaged",
                Origin = WorkspaceOrigins.Captured, CreatedUtc = _rig.Now, UpdatedUtc = _rig.Now, DocumentJson = "{ not json",
            });
            ctx.SaveChanges();
        }

        // Act
        var seats = Store.SeatsCapturedFrom(MailDeskRoutingRig.Account, new[] { SessionOne }, _rig.Now.AddHours(-1));

        // Assert
        Assert.Equal("restart-good", Assert.Single(seats).Value.WorkspaceId);
    }

    [Fact]
    public void WorkspaceSeatOnRecord_ReopenedConversation_IsBroughtBackAsTheReopenedSession_AndCloseIsNotToBeBroughtBack()
    {
        // Arrange
        var reopened = new WorkspaceSeat
        {
            SessionId = SessionOne,
            Restore = new WorkspaceSeatRestore { ReopenedSessionId = SessionTwo },
        };
        var closed = new WorkspaceSeat
        {
            SessionId = SessionOne,
            Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Close },
        };

        // Act
        var a = new WorkspaceSeatOnRecord("r", DateTime.MinValue, reopened);
        var b = new WorkspaceSeatOnRecord("r", DateTime.MinValue, closed);

        // Assert
        Assert.Equal((SessionTwo, false), (a.BroughtBackAs, a.NotToBeBroughtBack));
        Assert.Equal((null, true), (b.BroughtBackAs, b.NotToBeBroughtBack));
    }
}
