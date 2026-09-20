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

    private static SessionDto Session(string id, string repoPath, PooledWorktreeRef? pooled = null) => new()
    {
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
