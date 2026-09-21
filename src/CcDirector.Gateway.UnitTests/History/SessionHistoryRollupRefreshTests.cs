using CcDirector.AgentBrain;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Prompts;
using CcDirector.Gateway.Tests.Data;
using Xunit;
// CcDirector.Core.Sessions has a SessionHistoryStore of its own; this is the Gateway's.
using SessionHistoryStore = CcDirector.Gateway.History.SessionHistoryStore;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The history sweep's roll-up pass (devthrottle_internal#2199). It runs every two minutes for every account and
/// used to read thirty days of FULL session rows only to find out which roll-ups were stale. It now decides from a
/// narrow read and reads full rows only for the groups it rewrites. These prove the two things that change must not
/// break: the prompt still gets the full records, and the hash it saves is the one the History report computes from
/// full records - otherwise the report would call every roll-up stale, or the sweep would rewrite them forever.
/// </summary>
public sealed class SessionHistoryRollupRefreshTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _promptDir = Path.Combine(Path.GetTempPath(), "cc-rollup-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_promptDir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private static SessionDto Session(string id, DateTime startedAt, string missionName) => new()
    {
        SessionId = id,
        Name = "A session",
        RepoPath = @"D:\repos\devthrottle",
        RepoName = "thefrederiksen/devthrottle",
        Agent = "ClaudeCode",
        MachineName = "SOREN_NORTH",
        CreatedAt = startedAt,
        ActivityState = "Working",
        Status = "Running",
        MissionName = missionName,
    };

    private sealed class RecordingBrain : IAgentBrain
    {
        public List<string> Prompts { get; } = new();
        public string? SessionId => null;
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(new AskResult { Text = "Work was done on the repository." });
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth());
        public void Dispose() { }
    }

    [Fact]
    public async Task A_stale_rollup_is_written_from_full_records_and_saved_under_the_reports_own_hash()
    {
        var store = new SessionHistoryStore(_harness.Open());
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session("s1", now.AddHours(-2), "Cut the burn"), now);
        store.UpsertLive("dir-1", Session("s2", now.AddHours(-1), "Email improvements"), now);
        var brain = new RecordingBrain();
        var summarizer = new SessionHistorySummarizer(store, new GatewayPromptLog(_promptDir),
            (_, _) => Task.FromResult<IAgentBrain>(brain));

        var written = await summarizer.RefreshRollupsAsync(TenantId.Local, now.Date.AddDays(-1), now.Date, 5, CancellationToken.None);

        Assert.Equal(1, written);
        var prompt = Assert.Single(brain.Prompts);
        // The description comes from the mission name, which the narrow read does not carry: the prompt was built
        // from the full records.
        Assert.Contains("Mission: Cut the burn", prompt);
        Assert.Contains("Mission: Email improvements", prompt);

        // The report computes its hash from FULL records (ReadRange). The saved hash must equal it.
        var full = store.ReadRange(now.Date.AddDays(-1), now.Date.AddDays(1).AddTicks(-1), SessionHistoryStore.MaxListLimit);
        var reportGroup = Assert.Single(SessionHistorySummarizer.RollupGroups(full, now.Date, now.Date));
        var saved = Assert.Single(store.ReadRollups(now.Date.AddDays(-1), now.Date));
        Assert.Equal(reportGroup.InputHash, saved.InputHash);
    }

    [Fact]
    public async Task A_second_pass_with_nothing_changed_writes_nothing_and_a_change_is_written_again()
    {
        var store = new SessionHistoryStore(_harness.Open());
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session("s1", now.AddHours(-2), "Cut the burn"), now);
        var brain = new RecordingBrain();
        var summarizer = new SessionHistorySummarizer(store, new GatewayPromptLog(_promptDir),
            (_, _) => Task.FromResult<IAgentBrain>(brain));
        Assert.Equal(1, await summarizer.RefreshRollupsAsync(TenantId.Local, now.Date, now.Date, 5, CancellationToken.None));

        Assert.Equal(0, await summarizer.RefreshRollupsAsync(TenantId.Local, now.Date, now.Date, 5, CancellationToken.None));

        store.UpsertLive("dir-1", Session("s2", now.AddHours(-1), "Email improvements"), now);
        Assert.Equal(1, await summarizer.RefreshRollupsAsync(TenantId.Local, now.Date, now.Date, 5, CancellationToken.None));
        Assert.Equal(2, brain.Prompts.Count);
    }

    /// <summary>The narrow read groups and hashes exactly as the full read does, over the same rows.</summary>
    [Fact]
    public void The_narrow_read_groups_and_hashes_like_the_full_read()
    {
        var store = new SessionHistoryStore(_harness.Open());
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Session("s1", now.AddDays(-3), "Cut the burn"), now);
        store.UpsertLive("dir-1", Session("s2", now.AddHours(-1), "Email improvements"), now);
        var from = now.Date.AddDays(-7);
        var to = now.Date.AddDays(1).AddTicks(-1);

        var narrow = SessionHistorySummarizer.RollupGroups(store.ReadRollupInputs(from, to), from, now.Date);
        var full = SessionHistorySummarizer.RollupGroups(store.ReadRange(from, to), from, now.Date);

        Assert.Equal(
            full.Select(g => (g.RepoKey, g.Day, g.InputHash)).OrderBy(x => x.Day).ToList(),
            narrow.Select(g => (g.RepoKey, g.Day, g.InputHash)).OrderBy(x => x.Day).ToList());
        Assert.True(full.Count >= 4);   // s1 spans four days, so the comparison is over more than one group
    }
}
