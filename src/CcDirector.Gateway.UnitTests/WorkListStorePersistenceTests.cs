using System.Text.Json;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Persistence tests for <see cref="WorkListStore"/> over the EF data layer (Hosted Gateway mission, Step
/// 1b): the store survives a "restart" - modeled as a brand-new store over the same database, exactly what
/// a new Gateway process does. Covers the round-trip (lists, item order, mixed sources), the load-time
/// stale-claim release, the lossless one-time JSON import (name/case + item order + the claim carried into
/// the database, then released as stale), the fail-loud corrupt-import contract, and interrupted-drain
/// recovery.
/// </summary>
public sealed class WorkListStorePersistenceTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();

    private string LegacyPath() => _h.LegacyPath("worklists-" + Guid.NewGuid().ToString("N") + ".json");
    private WorkListStore NewStore(string legacy) => new(Db, legacy);

    public void Dispose() => _h.Dispose();

    private static WorkListItemRef Ref(string source, string id, string? area = null) =>
        new() { Source = source, Id = id, Area = area };

    [Fact]
    public void RoundTrip_ListsOrderAndMixedSources_SurviveReload()
    {
        var legacy = LegacyPath();
        var store = NewStore(legacy);
        store.Create("backlog");
        store.AppendItem("backlog", Ref("github", "262", "Gateway"));
        store.AppendItem("backlog", Ref("devops", "1203"));
        store.AppendItem("backlog", Ref("jira", "CCD-44", "Web"));
        store.Create("today");
        store.AppendItem("today", Ref("github", "301"));

        // "Restart": a fresh store over the same database, as a new Gateway process would do.
        var reloaded = NewStore(legacy);

        var names = reloaded.ListAll().Select(l => l.Name).ToArray();
        Assert.Equal(new[] { "backlog", "today" }, names);

        var backlog = reloaded.Get("backlog");
        Assert.NotNull(backlog);
        Assert.Equal(new[] { "262", "1203", "CCD-44" }, backlog.Items.Select(i => i.Id).ToArray());
        Assert.Equal(new[] { "github", "devops", "jira" }, backlog.Items.Select(i => i.Source).ToArray());
        Assert.Equal("Gateway", backlog.Items[0].Area);
        Assert.Null(backlog.Items[1].Area);
        Assert.Equal("Web", backlog.Items[2].Area);

        Assert.Equal(new[] { "301" }, reloaded.Get("today")!.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void Reload_PersistedClaim_IsReleasedAsStale()
    {
        var legacy = LegacyPath();
        var store = NewStore(legacy);
        store.Create("backlog");
        store.AppendItem("backlog", Ref("github", "262"));
        Assert.Equal(WorkListStore.ClaimResult.Granted, store.Claim("backlog", "dead-runner-token"));

        // "Restart": the persisted claim belongs to a runner that died with the Gateway.
        var reloaded = NewStore(legacy);
        var list = reloaded.Get("backlog");
        Assert.NotNull(list);
        Assert.Null(list.Consumer);
        Assert.Equal(new[] { "262" }, list.Items.Select(i => i.Id).ToArray());
        Assert.Equal(1, reloaded.LastLoadStaleClaimsReleased);

        // A new runner can re-claim immediately.
        Assert.Equal(WorkListStore.ClaimResult.Granted, reloaded.Claim("backlog", "new-runner-token"));

        // The released state persisted: another fresh store still sees no consumer and nothing left to release.
        reloaded.Release("backlog");
        var again = NewStore(legacy);
        Assert.Null(again.Get("backlog")!.Consumer);
        Assert.Equal(0, again.LastLoadStaleClaimsReleased);
    }

    [Fact]
    public void EveryMutation_IsVisibleToAFreshStore()
    {
        var legacy = LegacyPath();
        var store = NewStore(legacy);

        store.Create("wt");
        Assert.NotNull(NewStore(legacy).Get("wt"));

        store.AppendItem("wt", Ref("github", "1"));
        store.AppendItem("wt", Ref("github", "2"));
        Assert.Equal(new[] { "1", "2" }, NewStore(legacy).Get("wt")!.Items.Select(i => i.Id).ToArray());

        store.Reorder("wt", new List<WorkListItemRef> { Ref("github", "2"), Ref("github", "1") });
        Assert.Equal(new[] { "2", "1" }, NewStore(legacy).Get("wt")!.Items.Select(i => i.Id).ToArray());

        store.RemoveItem("wt", "github", "2");
        Assert.Equal(new[] { "1" }, NewStore(legacy).Get("wt")!.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void ManyItems_SurviveReload_InOrder()
    {
        var legacy = LegacyPath();
        var store = NewStore(legacy);
        store.Create("backlog");
        for (var i = 0; i < 50; i++)
            store.AppendItem("backlog", Ref("github", i.ToString()));

        var reloaded = NewStore(legacy).Get("backlog");
        Assert.NotNull(reloaded);
        Assert.Equal(Enumerable.Range(0, 50).Select(i => i.ToString()).ToArray(),
            reloaded.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void NoLegacyFile_StartsEmpty()
    {
        var store = NewStore(LegacyPath());
        Assert.Empty(store.ListAll());
    }

    [Fact]
    public void LegacyJson_ImportedOnce_NameCaseAndItemOrderSurvive_ClaimCarriedThenReleasedAsStale()
    {
        // A legacy worklists.json written by the old store: a claimed list with a MIXED-CASE name and
        // ORDERED items, plus a second unclaimed list.
        var legacy = LegacyPath();
        WriteLegacyFile(legacy,
            new WorkListDto
            {
                Name = "MyBacklog",
                Consumer = "runner-that-died",
                Items =
                {
                    Ref("github", "262", "Gateway"),
                    Ref("devops", "1203"),
                    Ref("jira", "CCD-44", "Web"),
                },
            },
            new WorkListDto { Name = "today", Items = { Ref("github", "301") } });

        var store = NewStore(legacy);

        // Name (case preserved) + item ORDER + fields survived the import.
        var backlog = store.Get("mybacklog"); // case-insensitive lookup finds it
        Assert.NotNull(backlog);
        Assert.Equal("MyBacklog", backlog.Name);
        Assert.Equal(new[] { "262", "1203", "CCD-44" }, backlog.Items.Select(i => i.Id).ToArray());
        Assert.Equal(new[] { "github", "devops", "jira" }, backlog.Items.Select(i => i.Source).ToArray());
        Assert.Equal("Gateway", backlog.Items[0].Area);
        Assert.Equal("Web", backlog.Items[2].Area);
        Assert.Equal(new[] { "301" }, store.Get("today")!.Items.Select(i => i.Id).ToArray());

        // The consumer was CARRIED INTO THE DATABASE by the import (else there would be nothing to release),
        // and the load-time stale-release then cleared it - so it is released as stale, Consumer null, exactly
        // as a real restart. The stale-release count of 1 is the honest proof the import did not drop it.
        Assert.Equal(1, store.LastLoadStaleClaimsReleased);
        Assert.Null(backlog.Consumer);

        // The legacy file is renamed aside (kept as a backup) and not re-imported by a fresh store.
        Assert.False(File.Exists(legacy));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(legacy)!, Path.GetFileName(legacy) + ".migrated-*"));
        var fresh = NewStore(legacy);
        Assert.Equal("MyBacklog", fresh.Get("MYBACKLOG")!.Name);
        Assert.Equal(0, fresh.LastLoadStaleClaimsReleased); // nothing left to release
    }

    [Fact]
    public void CorruptLegacyJson_FailsLoud_AndLeavesTheFileInPlace()
    {
        var legacy = LegacyPath();
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        const string corrupt = "{ this is not json !!!";
        File.WriteAllText(legacy, corrupt);

        Assert.Throws<InvalidOperationException>(() => NewStore(legacy));
        Assert.True(File.Exists(legacy));
        Assert.Equal(corrupt, File.ReadAllText(legacy));
    }

    [Fact]
    public void Import_RenameAsideFails_NextConstructionRenamesAside_WithoutReimporting()
    {
        // Its own directory, because BlockedRename closes the whole directory to writing on macOS and Linux
        // and the harness's database lives in the directory above.
        var legacy = _h.LegacyPath(Path.Combine("import-" + Guid.NewGuid().ToString("N"), "worklists.json"));
        WriteLegacyFile(legacy, new WorkListDto { Name = "backlog", Items = { Ref("github", "1") } });

        // First construction: the import reads the file and COMMITS to the database, but the rename-aside
        // fails because the file cannot be renamed - each system's own way of refusing, proved by the fixture
        // before the test proceeds. The failed rename is best-effort, so construction still completes and the
        // file lingers.
        using (new BlockedRename(legacy))
        {
            var store1 = NewStore(legacy);
            Assert.Single(store1.ListAll());        // imported into the database
            Assert.NotNull(store1.Get("backlog"));
            Assert.True(File.Exists(legacy));       // the rename failed, so the legacy file lingers
        }

        // Next construction (file released): the table is already populated, so it does NOT re-import - it
        // renames the lingering file aside (idempotent recovery), self-healing the earlier failed rename.
        var store2 = NewStore(legacy);
        Assert.Single(store2.ListAll());            // still exactly one list -> never re-imported over existing rows
        Assert.Equal(new[] { "1" }, store2.Get("backlog")!.Items.Select(i => i.Id).ToArray());
        Assert.False(File.Exists(legacy));          // renamed aside on recovery
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(legacy)!, Path.GetFileName(legacy) + ".migrated-*"));
    }

    /// <summary>
    /// Interrupted-drain recovery (issue #301 AC, D-2): a runner that died mid-drain left its claim persisted
    /// (a hard crash never runs a graceful release), the queue survives, the stale claim is released on the
    /// next construction, and a NEW runner re-claims and drains the persisted queue IN ORDER to completion.
    /// </summary>
    [Fact]
    public async Task InterruptedDrain_AfterRestart_NewRunnerReclaims_ContinuesInPersistedOrder()
    {
        var legacy = LegacyPath();

        // Before the restart: a queue is built and a runner holds the claim, then the Gateway is killed - a
        // hard crash, so the claim stays persisted (no graceful release runs).
        var store = NewStore(legacy);
        store.Create("queue");
        store.AppendItem("queue", Ref("github", "101"));
        store.AppendItem("queue", Ref("github", "102"));
        store.AppendItem("queue", Ref("github", "103"));
        Assert.Equal(WorkListStore.ClaimResult.Granted, store.Claim("queue", "runner-before-restart"));
        // While claimed, another consumer is refused - the claim is genuinely held.
        Assert.Equal(WorkListStore.ClaimResult.AlreadyClaimed, store.Claim("queue", "someone-else"));

        // After the restart: a fresh store over the same database releases the stale claim; list + order intact.
        var reloaded = NewStore(legacy);
        var queue = reloaded.Get("queue");
        Assert.NotNull(queue);
        Assert.Equal(new[] { "101", "102", "103" }, queue.Items.Select(i => i.Id).ToArray());
        Assert.Null(queue.Consumer);
        Assert.Equal(1, reloaded.LastLoadStaleClaimsReleased);

        // A NEW runner re-claims and drains the persisted queue in order to completion.
        var driver = new RecordingDriver();
        var newRunner = new WorkListRunner(reloaded, driver, pollInterval: TimeSpan.FromMilliseconds(5));
        var result = await newRunner.DrainAsync("queue", "runner-after-restart");

        Assert.Equal(new[] { "101", "102", "103" }, driver.StartOrder.ToArray());
        Assert.All(result.Items, i => Assert.Equal(WorkListItemOutcome.Ran, i.Outcome));
        Assert.All(result.Items, i => Assert.Equal(ImplLoopSignal.Done, i.Signal));
        Assert.True(result.ConsumerReleased);
    }

    private static void WriteLegacyFile(string path, params WorkListDto[] lists)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(new { Lists = lists }, options));
    }

    /// <summary>Records start order and completes every session on the first poll.</summary>
    private sealed class RecordingDriver : IImplSessionDriver
    {
        public List<string> StartOrder { get; } = new();

        public Task<(string? sessionId, string? error)> StartImplementationSessionAsync(string itemId, string seedPrompt, CancellationToken ct)
        {
            StartOrder.Add(itemId);
            return Task.FromResult<(string?, string?)>(($"sid-{itemId}", null));
        }

        public Task<string?> ReadTranscriptAsync(string sessionId, CancellationToken ct)
        {
            var issueId = sessionId["sid-".Length..];
            return Task.FromResult<string?>(
                $"IMPL-LOOP-TERMINAL\nissue: {issueId}\nsignal: done\npr: none\nmerged: yes\nreason: test\n");
        }
    }
}
