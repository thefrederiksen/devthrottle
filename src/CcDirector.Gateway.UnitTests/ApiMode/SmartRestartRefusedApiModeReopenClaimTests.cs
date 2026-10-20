using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.UnitTests.ApiMode;

/// <summary>
/// THE STORE HALF OF GIVING BACK A REFUSED REOPEN'S CLAIM (API mode mission #4070, the #4257 review, finding 2).
///
/// A released v2.23 or v2.24 Director claims its API-mode seat on the record before it sends the reopen's create, and
/// never gives the claim back. When the Gateway refuses that create for the Director's version, nothing was started, so
/// the claim is given back. These tests pin how narrow that is: only the claim of the create being refused, never an
/// older claim whose answer never came back, a filled one, another Director's, or a terminal seat's.
///
/// ITS NAME CARRIES "SmartRestart" ON PURPOSE, so the Smart Director Restart mission's check runs it too.
/// </summary>
public sealed class SmartRestartRefusedApiModeReopenClaimTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private static readonly DateTime Now = new(2026, 10, 19, 21, 0, 0, DateTimeKind.Utc);
    private const string Id = "smart-shutdown-1";
    private const string Seat = "a0000000-0000-0000-0000-000000000001";
    private const string Conversation = "the-saved-pi-conversation";
    private const string DirectorA = "director-a";
    private const string DirectorB = "director-b";

    public void Dispose() => _h.Dispose();

    private WorkspaceStore Captured(string? connectionMode = ConnectionModes.Api, string id = Id)
    {
        var store = new WorkspaceStore(_h.Open());
        Create(store, connectionMode, id);
        return store;
    }

    private static void Create(WorkspaceStore store, string? connectionMode, string id)
        => store.Create(new WorkspaceDocument
        {
            Id = id,
            Name = "Smart shutdown",
            Origin = WorkspaceOrigins.Captured,
            Machine = "MAC",
            DirectorId = DirectorA,
            ShutdownKind = WorkspaceShutdownKinds.SmartShutdown,
            Seats = new()
            {
                new WorkspaceSeat
                {
                    SessionId = Seat,
                    Name = "A Pi seat",
                    Agent = "Pi",
                    RepoPath = "/repos/devthrottle",
                    DrainState = WorkspaceDrainStates.EndedAtLimit,
                    ClaudeSessionId = Conversation,
                    ConnectionMode = connectionMode,
                    Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided },
                },
            },
        }, Now.AddHours(-1));

    private static WorkspaceRestoreMark Reopen(string director = DirectorA, string? reopenedAs = null) => new()
    {
        DirectorId = director,
        Kind = WorkspaceRestoreMarkKinds.Reopened,
        SeatSessionId = Seat,
        ReopenedSessionId = reopenedAs,
    };

    private static WorkspaceSeatRestore RestoreOf(WorkspaceStore store, string id = Id) => store.Get(id)!.Seats.Single().Restore!;

    [Fact]
    public void ReleaseRefusedReopenClaim_TheClaimJustWritten_IsGivenBack_AndTheSeatCanBeClaimedAgain()
    {
        // Arrange
        var store = Captured();
        store.RecordRestoreMark(Id, Reopen(), Now.AddSeconds(-2));

        // Act
        var released = store.ReleaseRefusedReopenClaim(DirectorA, Conversation, Now);

        // Assert
        Assert.Equal($"{Id}/{Seat}", released);
        Assert.Null(RestoreOf(store).ReopenedAtUtc);
        Assert.Null(RestoreOf(store).ReopenedByDirectorId);
        store.RecordRestoreMark(Id, Reopen(), Now.AddMinutes(30));
        Assert.Equal(Now.AddMinutes(30), RestoreOf(store).ReopenedAtUtc);
    }

    [Fact]
    public void ReleaseRefusedReopenClaim_AClaimOlderThanTheWindow_Stands()
    {
        // Arrange: an older unfilled claim is a start whose answer never came back; it may have happened (issue 3230).
        var store = Captured();
        store.RecordRestoreMark(Id, Reopen(), Now - WorkspaceStore.RefusedReopenClaimWindow - TimeSpan.FromSeconds(1));

        // Act
        var released = store.ReleaseRefusedReopenClaim(DirectorA, Conversation, Now);

        // Assert
        Assert.Null(released);
        Assert.NotNull(RestoreOf(store).ReopenedAtUtc);
    }

    [Fact]
    public void ReleaseRefusedReopenClaim_AClaimThatNamesASession_Stands()
    {
        // Arrange: the seat came back as a session; that is never undone.
        var store = Captured();
        store.RecordRestoreMark(Id, Reopen(reopenedAs: "the-new-session"), Now.AddSeconds(-2));

        // Act
        var released = store.ReleaseRefusedReopenClaim(DirectorA, Conversation, Now);

        // Assert
        Assert.Null(released);
        Assert.Equal("the-new-session", RestoreOf(store).ReopenedSessionId);
    }

    [Fact]
    public void ReleaseRefusedReopenClaim_AnotherDirectorsClaim_Stands()
    {
        // Arrange
        var store = Captured();
        store.RecordRestoreMark(Id, Reopen(director: DirectorB), Now.AddSeconds(-2));

        // Act
        var released = store.ReleaseRefusedReopenClaim(DirectorA, Conversation, Now);

        // Assert
        Assert.Null(released);
        Assert.Equal(DirectorB, RestoreOf(store).ReopenedByDirectorId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("terminal")]
    public void ReleaseRefusedReopenClaim_ATerminalSeatsClaim_Stands(string? connectionMode)
    {
        // Arrange: the API mode guard never refuses a terminal seat, so its claim is never the refused one.
        var store = Captured(connectionMode);
        store.RecordRestoreMark(Id, Reopen(), Now.AddSeconds(-2));

        // Act
        var released = store.ReleaseRefusedReopenClaim(DirectorA, Conversation, Now);

        // Assert
        Assert.Null(released);
        Assert.NotNull(RestoreOf(store).ReopenedAtUtc);
    }

    [Fact]
    public void ReleaseRefusedReopenClaim_AnotherConversation_ReleasesNothing()
    {
        // Arrange
        var store = Captured();
        store.RecordRestoreMark(Id, Reopen(), Now.AddSeconds(-2));

        // Act
        var released = store.ReleaseRefusedReopenClaim(DirectorA, "some-other-conversation", Now);

        // Assert
        Assert.Null(released);
        Assert.NotNull(RestoreOf(store).ReopenedAtUtc);
    }

    [Fact]
    public void ReleaseRefusedReopenClaim_TwoRecordsHoldingTheSeat_GivesBackOnlyTheNewestClaim()
    {
        // Arrange: two captures of the same seat, both claimed by this Director inside the window.
        var store = Captured();
        Create(store, ConnectionModes.Api, "smart-shutdown-2");
        store.RecordRestoreMark(Id, Reopen(), Now.AddMinutes(-5));
        store.RecordRestoreMark("smart-shutdown-2", Reopen(), Now.AddSeconds(-2));

        // Act
        var released = store.ReleaseRefusedReopenClaim(DirectorA, Conversation, Now);

        // Assert
        Assert.Equal($"smart-shutdown-2/{Seat}", released);
        Assert.Null(RestoreOf(store, "smart-shutdown-2").ReopenedAtUtc);
        Assert.NotNull(RestoreOf(store).ReopenedAtUtc);
    }
}
