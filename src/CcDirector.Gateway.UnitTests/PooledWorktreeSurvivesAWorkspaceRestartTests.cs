using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.ControlApi;
using CcDirector.ControlApi.Drain;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A pooled seat has to come back from a workspace still holding its own slot.
///
/// A workspace is how a Director restart actually works here: the fleet is captured into one, the
/// Director stops, and each seat is started again from the record. For a seat in a pooled worktree
/// that record has to carry the LEASE, because the lease exists nowhere else once the Director that
/// held it has stopped - cc-worktrees will not issue a second one for a slot that is in use, and its
/// listing does not report the lease of one. Without it the restored session runs in its own slot as
/// a stranger: nothing to give the slot back with, the pool still holding it for a session that no
/// longer exists, and a Director that restarted a few times filling its own pool.
///
/// The three things that have to be true, and each is one test below:
///   * the CAPTURE keeps the lease, because nothing else can recover it;
///   * the RESTORE hands it back, and takes NO NEW SLOT;
///   * a caller writing to the workspace cannot change it - it is an observation, and it is the one
///     on that list a caller would most want to choose, since the Director runs the session in the
///     slot it names.
/// </summary>
public sealed class PooledWorktreeSurvivesAWorkspaceRestartTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Now = new(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc);

    private const string SeatId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c";
    private const string Repo = @"D:\repos\primary";
    private const string Slot = @"D:\repos\primary.worktrees\wt01";

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static PooledWorktreeRef TheSlot => new()
    {
        Repo = Repo,
        Slot = "wt01",
        Path = Slot,
        Lease = "lease-abc",
    };

    // ---- the capture keeps the lease -------------------------------------------------------------

    [Fact]
    public void ACapturedSeat_KeepsTheSlotAndItsLease()
    {
        var seat = WorkspaceCapture.CaptureSeat(new SessionDto
        {
            SessionId = SeatId,
            Name = "a worker",
            Agent = "ClaudeCode",
            // A pooled session's RepoPath IS the slot; the repository the slot came from is on the
            // pooled record, which is the only place it is.
            RepoPath = Slot,
            PooledWorktree = TheSlot,
        }, sortOrder: 0);

        Assert.NotNull(seat.PooledWorktree);
        Assert.Equal(Repo, seat.PooledWorktree!.Repo);
        Assert.Equal("wt01", seat.PooledWorktree.Slot);
        Assert.Equal(Slot, seat.PooledWorktree.Path);
        // The value the whole record exists for.
        Assert.Equal("lease-abc", seat.PooledWorktree.Lease);
    }

    [Fact]
    public void ACapturedSeatInNoPool_CarriesNothingAboutOne()
    {
        var seat = WorkspaceCapture.CaptureSeat(new SessionDto
        {
            SessionId = SeatId, Name = "a worker", Agent = "ClaudeCode", RepoPath = Repo,
        }, sortOrder: 0);

        Assert.Null(seat.PooledWorktree);
    }

    [Fact]
    public void ACapturedSeatWhosePooledRecordIsIncomplete_CarriesNothingAboutOne()
    {
        // Half a record is worse than none: a slot named without its lease would read as a seat that
        // could be restored properly, and the restore would silently be unable to give it back.
        var seat = WorkspaceCapture.CaptureSeat(new SessionDto
        {
            SessionId = SeatId, Name = "a worker", Agent = "ClaudeCode", RepoPath = Slot,
            PooledWorktree = new PooledWorktreeRef { Repo = Repo, Slot = "wt01", Path = Slot, Lease = "" },
        }, sortOrder: 0);

        Assert.Null(seat.PooledWorktree);
    }

    // ---- the restore hands it back ---------------------------------------------------------------

    private static WorkspaceSeat ASeat(PooledWorktreeRef? pooled) => new()
    {
        SessionId = SeatId,
        Name = "a worker",
        Agent = "ClaudeCode",
        RepoPath = Slot,
        PooledWorktree = pooled,
        HandoverPath = @"D:\handovers\a-worker.md",
        Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided },
    };

    [Fact]
    public void TheRestoresCreate_CarriesTheSlotBackToTheDirector()
    {
        var request = DirectorRestore.BuildRequest(ASeat(TheSlot), owner: null, new WorkspaceRestoreOrder());

        Assert.NotNull(request.PooledWorktree);
        Assert.Equal("lease-abc", request.PooledWorktree!.Lease);
        Assert.Equal("wt01", request.PooledWorktree.Slot);
        // The seat runs where it ran: the slot is also the repository path the create is given.
        Assert.Equal(Slot, request.RepoPath);
    }

    [Fact]
    public void TheRestoresCreate_CarriesNothingForASeatThatHeldNoSlot()
    {
        var request = DirectorRestore.BuildRequest(ASeat(pooled: null), owner: null, new WorkspaceRestoreOrder());

        Assert.Null(request.PooledWorktree);
    }

    [Fact]
    public void TheRestoresCreate_DropsAnIncompleteSlotRatherThanSendingHalfOfIt()
    {
        var half = new PooledWorktreeRef { Repo = Repo, Slot = "wt01", Path = Slot, Lease = "   " };

        var request = DirectorRestore.BuildRequest(ASeat(half), owner: null, new WorkspaceRestoreOrder());

        Assert.Null(request.PooledWorktree);
    }

    // ---- and the Director takes NO NEW SLOT ------------------------------------------------------

    /// <summary>
    /// A pool that records every call and, if it IS asked for a slot, hands out a DIFFERENT one.
    ///
    /// Deliberately not a pool that throws. Throwing would make the create fail, and a failed create
    /// is a different symptom from the one this is about: what goes wrong without the re-attach is
    /// that the restore quietly succeeds in the WRONG slot, leaving the first one in use for a
    /// session that no longer exists. Handing out wt02 makes the test fail the way the defect reads.
    /// </summary>
    private sealed class PoolThatHandsOutANewSlot : IWorktreePool
    {
        private readonly string _root;

        public PoolThatHandsOutANewSlot(string root) => _root = root;

        public List<string> Calls { get; } = new();

        public PooledWorktree Get(string repoPath, string holder, int poolSize)
        {
            Calls.Add($"get {repoPath}");
            var second = Path.Combine(_root, "primary.worktrees", "wt02");
            Directory.CreateDirectory(second);
            return new PooledWorktree(Path.Combine(_root, "primary"), "wt02", second, "lease-second");
        }

        public PooledWorktreeReturn Return(PooledWorktree worktree)
        {
            Calls.Add($"return {worktree.Slot}");
            return new PooledWorktreeReturn(worktree.Slot, worktree.Path, Freed: true, HeldReason: null);
        }
    }

    private static string TestShellPath =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? "cmd.exe" : "/bin/sh";

    private static DirectorCommand CreateCommand(NewSessionRequest request) => new()
    {
        CommandId = "c1",
        Verb = "create",
        SessionId = "",
        PayloadJson = JsonSerializer.Serialize(request, Json),
    };

    private static SessionManager PooledManager(IWorktreePool pool, string scratch) => new(
        new Core.Configuration.AgentOptions(),
        log: null,
        reservations: new WorktreeReservationStore(Path.Combine(scratch, "reservations")),
        worktreePool: pool,
        // ON for every repository, so an ordinary create here WOULD take a slot. That is what makes
        // the assertion below mean something: the restore does not, because it already has one.
        worktreePoolSetting: _ => new WorktreePoolSetting(Enabled: true, PoolSize: 4));

    [Fact]
    public async Task ARestoredSeat_RunsInItsOwnSlotOnItsOwnLease_AndTakesNoNewSlot()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "ccd-restore-slot-" + Guid.NewGuid().ToString("N"));
        var slot = Path.Combine(scratch, "primary.worktrees", "wt01");
        Directory.CreateDirectory(slot);
        var pool = new PoolThatHandsOutANewSlot(scratch);
        var manager = PooledManager(pool, scratch);
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(manager, "dir-A", CreateCommand(new NewSessionRequest
            {
                RepoPath = slot,
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "a restored worker",
                PooledWorktree = new PooledWorktreeRef
                {
                    Repo = Path.Combine(scratch, "primary"), Slot = "wt01", Path = slot, Lease = "lease-abc",
                },
            }));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            var session = manager.GetSession(Guid.Parse(dto!.SessionId));
            Assert.NotNull(session);

            // Its own slot, on its own lease.
            Assert.Equal(slot, session!.WorkingDirectory);
            Assert.Equal("lease-abc", session.PooledWorktree?.Lease);
            Assert.Equal("wt01", session.PooledWorktree?.Slot);

            // AND NO NEW SLOT. The pool was not asked for anything - not even the repository's
            // setting was consulted, because the answer was already known.
            Assert.Empty(pool.Calls);

            // It reports the slot back out, so the NEXT capture keeps the lease too.
            Assert.Equal("lease-abc", dto.PooledWorktree?.Lease);

            // And closing it gives that exact slot back, on that exact lease.
            Assert.True(manager.RemoveSession(session.Id));
            Assert.Equal(new[] { "return wt01" }, pool.Calls);
        }
        finally
        {
            manager.Dispose();
            try { Directory.Delete(scratch, recursive: true); } catch { /* scratch; best effort */ }
        }
    }

    [Fact]
    public async Task ACreateCarryingHalfASlot_DoesNotAttachIt()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "ccd-restore-half-" + Guid.NewGuid().ToString("N"));
        var slot = Path.Combine(scratch, "primary.worktrees", "wt01");
        Directory.CreateDirectory(slot);
        var pool = new PoolThatHandsOutANewSlot(scratch);
        var manager = new SessionManager(
            new Core.Configuration.AgentOptions(),
            log: null,
            reservations: new WorktreeReservationStore(Path.Combine(scratch, "reservations")),
            worktreePool: pool,
            // OFF, which is what a slot path really reads: the setting is keyed on the REPOSITORY.
            worktreePoolSetting: _ => new WorktreePoolSetting(Enabled: false, PoolSize: 4));
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(manager, "dir-A", CreateCommand(new NewSessionRequest
            {
                RepoPath = slot,
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "a half restored worker",
                PooledWorktree = new PooledWorktreeRef { Repo = "", Slot = "wt01", Path = slot, Lease = "lease-abc" },
            }));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            var session = manager.GetSession(Guid.Parse(dto!.SessionId));

            // Not attached. The session runs where it was told to run and holds nothing it cannot
            // give back - which is also what says, in the log, that a slot is still out there.
            Assert.Null(session!.PooledWorktree);
            Assert.True(manager.RemoveSession(session.Id));
            Assert.Empty(pool.Calls);
        }
        finally
        {
            manager.Dispose();
            try { Directory.Delete(scratch, recursive: true); } catch { /* scratch; best effort */ }
        }
    }

    // ---- a caller cannot choose a seat's slot ----------------------------------------------------

    private WorkspaceDocument Captured(PooledWorktreeRef? pooled) => new()
    {
        Id = "a-restart",
        Name = "a restart",
        Origin = WorkspaceOrigins.Captured,
        Machine = "a-machine",
        DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1",
        DirectorName = "a-director",
        StartedAtUtc = Now,
        Seats = { ASeat(pooled) },
    };

    [Fact]
    public void WritingToACapturedWorkspace_CannotChangeASeatsSlotOrItsLease()
    {
        var store = new WorkspaceStore(_harness.Open());
        store.Create(Captured(TheSlot), Now);

        var edited = Captured(new PooledWorktreeRef
        {
            Repo = @"D:\somebody\else", Slot = "wt09", Path = @"D:\somebody\else.worktrees\wt09", Lease = "mine",
        });
        edited.Seats[0].Restore = new WorkspaceSeatRestore
        {
            Decision = WorkspaceRestoreDecisions.Restore,
            Why = "it is needed",
            Command = "cc-devthrottle director restore --workspace a-restart",
        };

        var saved = store.Save(edited, Now);

        // The slot is an OBSERVATION, and the Director hands it straight back on the restore's create
        // and runs the session in the directory it names. A caller who could set it could point a
        // restore at somebody else's worktree.
        Assert.Equal("wt01", saved.Seats[0].PooledWorktree!.Slot);
        Assert.Equal("lease-abc", saved.Seats[0].PooledWorktree!.Lease);
        Assert.Equal(Slot, saved.Seats[0].PooledWorktree!.Path);
        // The control: the caller's JUDGMENT on the same seat was kept, so this is not "the write was
        // ignored" - it is the observations being restored and the judgments being taken.
        Assert.Equal(WorkspaceRestoreDecisions.Restore, saved.Seats[0].Restore!.Decision);
        Assert.Equal("it is needed", saved.Seats[0].Restore!.Why);
    }
}
