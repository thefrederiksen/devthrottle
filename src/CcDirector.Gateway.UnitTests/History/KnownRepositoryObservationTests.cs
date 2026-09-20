using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The one-repository-list mission, phase 1: what the Gateway's repository catalogue records when it
/// sees a session, and WHICH repository that is.
///
/// This catalogue is the recency signal the mission builds on, because the session history recorder
/// runs for every session the Gateway sees whatever surface started it. These tests hold it to that:
/// a session the desktop dialog never touched still moves its repository to the top, and a session
/// running in a pooled worktree records the repository the slot came out of rather than the slot -
/// nobody picks a throwaway worktree out of a repository list, and the pool deletes it.
/// </summary>
public sealed class KnownRepositoryObservationTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private (SessionHistoryRecorder Recorder, KnownRepositoryStore Repositories) New()
    {
        var database = _harness.Open();
        var repositories = new KnownRepositoryStore(database);
        var recorder = new SessionHistoryRecorder(
            new SessionHistoryStore(database),
            directorFacts: (_, _) => new DirectorFacts("SOREN_NORTH", "2.7.2"),
            knownRepositories: repositories);
        return (recorder, repositories);
    }

    private static SessionDto Session(string id, string repoPath, PooledWorktreeRef? pooled = null,
        string? primaryRepoPath = null) => new()
    {
        // The repository the DIRECTOR resolved this session's folder to when the folder is a linked
        // worktree - null for a session in a repository proper, and null from every Director that
        // predates the field. The Gateway never works this out for itself: it is a Linux container
        // holding paths written by Windows and macOS machines and is never the machine a path
        // describes.
        PrimaryRepoPath = primaryRepoPath,
        SessionId = id,
        Name = "Build the thing",
        Number = 200,
        RepoPath = pooled?.Path ?? repoPath,
        RepoName = "thefrederiksen/devthrottle",
        PooledWorktree = pooled,
        Agent = "ClaudeCode",
        MachineName = "",
        CreatedAt = DateTime.UtcNow,
        ActivityState = "Working",
        Status = "Running",
        // The surface that started it: NOT the desktop dialog, which is the whole point of the
        // catalogue - it is the only recency signal that sees these at all.
        OriginKind = "agent",
        OriginSurface = "cockpit",
    };

    [Fact]
    public void A_session_the_desktop_dialog_never_started_is_recorded_as_a_use()
    {
        var (recorder, repositories) = New();

        recorder.Observe(TenantId.Local, "dir-1", Session("s1", "/repos/devthrottle"));

        var row = Assert.Single(repositories.ReadForMachine(TenantId.Local, "SOREN_NORTH"));
        Assert.Equal("/repos/devthrottle", row.Path);
    }

    [Fact]
    public void The_newest_session_puts_its_repository_at_the_top()
    {
        var (recorder, repositories) = New();

        recorder.Observe(TenantId.Local, "dir-1", Session("s1", "/repos/older"));
        recorder.Observe(TenantId.Local, "dir-1", Session("s2", "/repos/newest"));

        var rows = repositories.ReadForMachine(TenantId.Local, "SOREN_NORTH");
        Assert.Equal(2, rows.Count);
        Assert.Equal("/repos/newest", rows[0].Path);
    }

    [Fact]
    public void A_pooled_session_records_the_repository_the_slot_came_from_not_the_slot()
    {
        var (recorder, repositories) = New();
        var pooled = new PooledWorktreeRef
        {
            Repo = "/repos/devthrottle",
            Slot = "wt01",
            Path = "/pool/devthrottle/wt01",
            Lease = "lease-1",
        };

        recorder.Observe(TenantId.Local, "dir-1", Session("s1", "/repos/devthrottle", pooled));

        var row = Assert.Single(repositories.ReadForMachine(TenantId.Local, "SOREN_NORTH"));
        Assert.Equal("/repos/devthrottle", row.Path);
    }

    // A WORKTREE IS NOT A REPOSITORY (the one-repository-list mission). Every agent session runs in a
    // worktree, so every worktree that hosted one became a row: one Windows machine's catalogue served
    // 559 repositories, 110 of which were live worktrees of four repositories.

    [Fact]
    public void A_session_in_a_worktree_records_the_repository_it_is_a_worktree_of()
    {
        var (recorder, repositories) = New();

        recorder.Observe(TenantId.Local, "dir-1",
            Session("s1", "/repos/devthrottle-p5-run-a", primaryRepoPath: "/repos/devthrottle"));

        var row = Assert.Single(repositories.ReadForMachine(TenantId.Local, "SOREN_NORTH"));
        Assert.Equal("/repos/devthrottle", row.Path);
    }

    [Fact]
    public void Sessions_in_many_worktrees_of_one_repository_are_one_row_that_moves()
    {
        // What the owner actually saw, in miniature: four worktrees of one repository at the top of
        // his list. They are now one row, and the newest session is what puts it there.
        var (recorder, repositories) = New();

        recorder.Observe(TenantId.Local, "dir-1", Session("s0", "/repos/mindzieWeb"));
        recorder.Observe(TenantId.Local, "dir-1",
            Session("s1", "/repos/worktrees/idle-p5-a", primaryRepoPath: "/repos/devthrottle"));
        recorder.Observe(TenantId.Local, "dir-1",
            Session("s2", "/repos/worktrees/idle-p5-b", primaryRepoPath: "/repos/devthrottle"));
        recorder.Observe(TenantId.Local, "dir-1",
            Session("s3", "/repos/devthrottle-p5-run-a", primaryRepoPath: "/repos/devthrottle"));

        var rows = repositories.ReadForMachine(TenantId.Local, "SOREN_NORTH");
        Assert.Equal(2, rows.Count);
        Assert.Equal("/repos/devthrottle", rows[0].Path);
        Assert.Equal("/repos/mindzieWeb", rows[1].Path);
    }

    [Fact]
    public void A_session_in_a_worktree_the_Director_could_not_resolve_records_the_worktree()
    {
        // FAILURE CASE, and the property that makes this safe: when the Director cannot prove which
        // repository a folder belongs to - the repository deleted, a .git file pointing at nothing -
        // it sends nothing, and the Gateway records the folder exactly as it did before. It never
        // guesses, because it is not the machine that holds the disk.
        var (recorder, repositories) = New();

        recorder.Observe(TenantId.Local, "dir-1", Session("s1", "/repos/orphaned-worktree"));

        var row = Assert.Single(repositories.ReadForMachine(TenantId.Local, "SOREN_NORTH"));
        Assert.Equal("/repos/orphaned-worktree", row.Path);
    }

    [Fact]
    public void A_pooled_session_that_also_carries_a_resolved_repository_still_records_the_pools()
    {
        // A WRONG RULE NOTHING REMOVED. A pooled slot IS a git worktree, so both answers are now
        // available; the pool's own record must keep its precedence, because it is a fact the pool
        // wrote down and it is right even when the slot has been handed back. This fails if a later
        // change ever reorders the two.
        var (recorder, repositories) = New();
        var pooled = new PooledWorktreeRef
        {
            Repo = "/repos/devthrottle",
            Slot = "wt01",
            Path = "/pool/devthrottle/wt01",
            Lease = "lease-1",
        };

        recorder.Observe(TenantId.Local, "dir-1",
            Session("s1", "/repos/devthrottle", pooled, primaryRepoPath: "/repos/somewhere-else"));

        var row = Assert.Single(repositories.ReadForMachine(TenantId.Local, "SOREN_NORTH"));
        Assert.Equal("/repos/devthrottle", row.Path);
    }

    [Fact]
    public void A_session_with_no_repository_records_nothing()
    {
        var (recorder, repositories) = New();

        recorder.Observe(TenantId.Local, "dir-1", Session("s1", "   "));

        Assert.Empty(repositories.ReadForMachine(TenantId.Local, "SOREN_NORTH"));
    }

    [Fact]
    public void A_catalogue_that_is_not_wired_records_nothing_and_the_session_history_is_unharmed()
    {
        // The failure case for the wiring itself: the catalogue is an optional dependency of the
        // recorder, and production MUST supply one. With none, the session is still recorded as
        // history - a catalogue hiccup can never cost a session's row.
        var database = _harness.Open();
        var store = new SessionHistoryStore(database);
        var recorder = new SessionHistoryRecorder(store,
            directorFacts: (_, _) => new DirectorFacts("SOREN_NORTH", "2.7.2"));

        recorder.Observe(TenantId.Local, "dir-1", Session("s1", "/repos/devthrottle"));

        Assert.Empty(new KnownRepositoryStore(database).ReadForMachine(TenantId.Local, "SOREN_NORTH"));
        Assert.Contains(
            store.ReadRange(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)),
            r => r.SessionId == "s1");
    }
}
