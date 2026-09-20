using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE STORE HALF OF "HE CAN CLEAR IT WITHOUT USING IT" (the Smart Director Restart mission, the owner's
/// ruling of 20 September 2026).
///
/// His case, in his own words: "it could be that they shut down but they don't want to use it and they don't
/// want to see it on every upstart." A clearing this Director remembered in its own process would be
/// forgotten by the very restart it exists to survive - that was product issue 3230 for the reopen claim - so
/// it is a fact on the RECORD, written by a mark of kind <see cref="WorkspaceRestoreMarkKinds.Cleared"/>.
///
/// These tests pin the five things that mark must do: be writable without a restore lease and take none, name
/// no seat, be harmless to press twice, refuse to cut across another Director's restore, and be unforgeable
/// and unerasable by an ordinary write.
///
/// IT LIVES UNDER Restart/ AND ITS NAME CARRIES "SmartRestart" ON PURPOSE. The mission's check is
/// <c>--filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"</c>, and a test whose name matches
/// neither is a test the mission never runs - which is a proof that certifies nothing.
/// </summary>
public sealed class SmartRestartClearMarkStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private static readonly DateTime Now = new(2026, 9, 20, 12, 36, 0, DateTimeKind.Utc);
    private const string Id = "smart-shutdown-to-clear";
    private const string Ended = "e0000000-0000-0000-0000-000000000000";
    private const string Lead = "10000000-0000-0000-0000-000000000000";
    private const string DirectorA = "director-a";
    private const string DirectorB = "director-b";

    public void Dispose() => _h.Dispose();

    private WorkspaceStore Captured()
    {
        var store = new WorkspaceStore(_h.Open());
        store.Create(new WorkspaceDocument
        {
            Id = Id,
            Name = "Smart shutdown",
            Origin = WorkspaceOrigins.Captured,
            Machine = "MAC",
            DirectorId = "old-director",
            ShutdownKind = WorkspaceShutdownKinds.SmartShutdown,
            Seats = new()
            {
                new WorkspaceSeat
                {
                    SessionId = Lead,
                    Name = "A lead",
                    Agent = "ClaudeCode",
                    RepoPath = "/repos/devthrottle",
                    DrainState = WorkspaceDrainStates.Drained,
                    HandoverPath = "/handovers/lead.md",
                    Restore = new WorkspaceSeatRestore
                    {
                        Decision = WorkspaceRestoreDecisions.Restore,
                        Why = "it was mid-task",
                        Command = "cc-devthrottle director restore ...",
                    },
                },
                new WorkspaceSeat
                {
                    SessionId = Ended,
                    Name = "A busy worker",
                    Agent = "ClaudeCode",
                    RepoPath = "/repos/devthrottle",
                    DrainState = WorkspaceDrainStates.EndedAtLimit,
                    ClaudeSessionId = "the-saved-conversation",
                    Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided },
                },
            },
            RestoreAfterRestart = new() { Lead },
        }, Now);
        return store;
    }

    private static WorkspaceRestoreMark Clear(string director = DirectorA) => new()
    {
        DirectorId = director,
        Kind = WorkspaceRestoreMarkKinds.Cleared,
    };

    /// <summary>
    /// A CLEARING NEEDS NO RESTORE LEASE AND NAMES NO SEAT, and this is the test that says both out loud. The
    /// lease is granted only by asking for a restore, and clearing is not a restore: it starts nothing and
    /// brings nothing back. A clearing that needed a lease could never be recorded at all.
    ///
    /// It also takes NO lease as a side effect - a clearing that quietly leased the workspace would block the
    /// bring back beside it for the next fifteen minutes.
    /// </summary>
    [Fact]
    public void AClearMark_IsWrittenWithNoRestoreLease_NamesNoSeat_AndTakesNone()
    {
        var store = Captured();

        var saved = store.RecordRestoreMark(Id, Clear(), Now);

        Assert.Equal(Now, saved.ClearedFromStartUpOfferAtUtc);
        Assert.Equal(DirectorA, saved.ClearedFromStartUpOfferByDirectorId);
        Assert.Null(saved.RestoreLease);

        // AND IT TOOK NOTHING AWAY. Both seats are there, as captured, with nothing decided for them by this.
        Assert.Equal(2, saved.Seats.Count);
        Assert.All(saved.Seats, seat => Assert.Null(seat.RestoredSessionId));
        Assert.All(saved.Seats, seat => Assert.Null(seat.Restore!.ReopenedAtUtc));
        Assert.Equal(WorkspaceDrainStates.EndedAtLimit, saved.Seats.Single(s => s.SessionId == Ended).DrainState);
    }

    /// <summary>
    /// CLEARING A RECORD THAT IS ALREADY CLEARED IS NOT AN ERROR, and the FIRST clearing's moment stands.
    ///
    /// It is deliberately unlike a second <see cref="WorkspaceRestoreMarkKinds.Reopened"/>, which is refused:
    /// a second reopen would put a second agent into one saved conversation, which is real harm. Here there is
    /// none - after either call the record has stopped appearing, which is the whole of what was asked for -
    /// and refusing it would hand the owner a red sentence for pressing a harmless button.
    /// </summary>
    [Fact]
    public void AClearMark_OnARecordAlreadyCleared_KeepsTheFirstMomentAndIsNotAnError()
    {
        var store = Captured();
        store.RecordRestoreMark(Id, Clear(), Now);

        var again = store.RecordRestoreMark(Id, Clear(director: DirectorB), Now.AddMinutes(5));

        Assert.Equal(Now, again.ClearedFromStartUpOfferAtUtc);
        Assert.Equal(DirectorA, again.ClearedFromStartUpOfferByDirectorId);
    }

    /// <summary>
    /// A DIRECTOR DOES NOT CHANGE WHAT A RECORD OFFERS UNDERNEATH ANOTHER DIRECTOR'S RUNNING RESTORE. It is
    /// the one thing a clearing must not cut across, so it is the one lease check it keeps - and the SAME
    /// Director's own lease is no obstacle to it, nor is it released by it.
    /// </summary>
    [Fact]
    public void AClearMark_WhileAnotherDirectorIsRestoring_IsRefused()
    {
        var store = Captured();
        store.TakeRestoreLease(Id, DirectorB, null, Now);

        var refused = Assert.Throws<WorkspaceConflictException>(
            () => store.RecordRestoreMark(Id, Clear(director: DirectorA), Now));
        Assert.Contains("is restoring workspace", refused.Message);
        Assert.Null(store.Get(Id)!.ClearedFromStartUpOfferAtUtc);

        var saved = store.RecordRestoreMark(Id, Clear(director: DirectorB), Now);
        Assert.Equal(Now, saved.ClearedFromStartUpOfferAtUtc);
        Assert.Equal(DirectorB, saved.RestoreLease!.DirectorId);
    }

    /// <summary>
    /// AN ORDINARY WRITE CAN NEITHER FORGE THE CLEARING NOR ERASE IT. Both halves matter and they fail in
    /// opposite directions: a writer who could set it would make a record vanish from the owner's start-up,
    /// and a writer who could clear it would put a record he has dismissed back in front of him at every
    /// start - which is exactly what he asked us to stop.
    /// </summary>
    [Fact]
    public void AnOrdinaryWrite_CanNeitherForgeAClearingNorEraseOne()
    {
        var store = Captured();

        // Forging one: a caller writes the record back saying it was cleared, which nobody ever asked for.
        var forging = store.Get(Id)!;
        forging.ClearedFromStartUpOfferAtUtc = Now;
        forging.ClearedFromStartUpOfferByDirectorId = "somebody-else";
        store.Save(forging, Now);

        var afterForging = store.Get(Id)!;
        Assert.Null(afterForging.ClearedFromStartUpOfferAtUtc);
        Assert.Null(afterForging.ClearedFromStartUpOfferByDirectorId);

        // Erasing one: the clearing is recorded, and a caller writes the record back with it removed.
        store.RecordRestoreMark(Id, Clear(), Now);
        var erasing = store.Get(Id)!;
        erasing.ClearedFromStartUpOfferAtUtc = null;
        erasing.ClearedFromStartUpOfferByDirectorId = null;
        store.Save(erasing, Now.AddMinutes(1));

        var afterErasing = store.Get(Id)!;
        Assert.Equal(Now, afterErasing.ClearedFromStartUpOfferAtUtc);
        Assert.Equal(DirectorA, afterErasing.ClearedFromStartUpOfferByDirectorId);
    }

    /// <summary>
    /// THE CLOSED LIST IS WHAT AN OLDER GATEWAY TRIPS ON, and this build's list now names the clearing. A
    /// Gateway that has not been deployed does not know it and refuses it by name with the kinds it does know
    /// - which is what the Director's own refusal carries through to the person, so it reads as a Gateway that
    /// is behind rather than as a mystery.
    /// </summary>
    [Fact]
    public void ThisBuildsListOfMarkKinds_NamesTheClearing()
    {
        Assert.Contains(WorkspaceRestoreMarkKinds.Cleared, WorkspaceRestoreMarkKinds.All);

        var store = Captured();
        var refused = Assert.Throws<WorkspaceValidationException>(
            () => store.RecordRestoreMark(
                Id, new WorkspaceRestoreMark { DirectorId = DirectorA, Kind = "dismissed" }, Now));

        Assert.Contains("kind must be one of:", refused.Message);
        Assert.Contains(WorkspaceRestoreMarkKinds.Cleared, refused.Message);
    }
}
