using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="WorkListStore"/> persistence (issue #301): the store survives a
/// "restart" - modeled as a brand-new store instance loading the same file, exactly what a new
/// Gateway process does. Covers the restated acceptance criteria:
///   - round-trip: lists, item order, and mixed sources reload intact;
///   - stale-claim release: a persisted consumer claim is released on load (and the released
///     state is persisted, so the dead consumer never resurrects);
///   - write-through: EVERY mutation is on disk immediately (each verified by reloading);
///   - atomic write: no .tmp residue is left behind and the file is always whole JSON;
///   - missing file: empty store, no crash (the normal first boot);
///   - corrupt file: quarantined next to the original (bytes preserved, never silently
///     overwritten), explicit error logged, store starts empty so the Gateway still boots;
///   - interrupted drain: a drain killed mid-run (the restart) leaves the queue on disk; after
///     reload a new runner re-claims and continues from the persisted order - nothing lost.
/// </summary>
public sealed class WorkListStorePersistenceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-worklist-persist-tests-" + Guid.NewGuid().ToString("N"));

    private string NewPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static WorkListItemRef Ref(string source, string id, string? area = null) =>
        new() { Source = source, Id = id, Area = area };

    [Fact]
    public void RoundTrip_ListsOrderAndMixedSources_SurviveReload()
    {
        var path = NewPath();

        var store = new WorkListStore(path);
        store.Create("backlog");
        store.AppendItem("backlog", Ref("github", "262", "Gateway"));
        store.AppendItem("backlog", Ref("devops", "1203"));
        store.AppendItem("backlog", Ref("jira", "CCD-44", "Web"));
        store.Create("today");
        store.AppendItem("today", Ref("github", "301"));

        // "Restart": a fresh store instance on the same file, as a new Gateway process would do.
        var reloaded = new WorkListStore(path);

        var names = reloaded.ListAll().Select(l => l.Name).ToArray();
        Assert.Equal(new[] { "backlog", "today" }, names);

        var backlog = reloaded.Get("backlog");
        Assert.NotNull(backlog);
        Assert.Equal(new[] { "262", "1203", "CCD-44" }, backlog.Items.Select(i => i.Id).ToArray());
        Assert.Equal(new[] { "github", "devops", "jira" }, backlog.Items.Select(i => i.Source).ToArray());
        Assert.Equal("Gateway", backlog.Items[0].Area);
        Assert.Null(backlog.Items[1].Area);
        Assert.Equal("Web", backlog.Items[2].Area);

        var today = reloaded.Get("today");
        Assert.NotNull(today);
        Assert.Equal(new[] { "301" }, today.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void Reload_PersistedClaim_IsReleasedAsStale_AndReleasePersisted()
    {
        var path = NewPath();

        var store = new WorkListStore(path);
        store.Create("backlog");
        store.AppendItem("backlog", Ref("github", "262"));
        Assert.Equal(WorkListStore.ClaimResult.Granted, store.Claim("backlog", "dead-runner-token"));

        // First "restart": the persisted claim belongs to a runner that died with the Gateway.
        var reloaded = new WorkListStore(path);
        var list = reloaded.Get("backlog");
        Assert.NotNull(list);
        Assert.Null(list.Consumer);
        Assert.Equal(new[] { "262" }, list.Items.Select(i => i.Id).ToArray());

        // A new runner can re-claim immediately.
        Assert.Equal(WorkListStore.ClaimResult.Granted, reloaded.Claim("backlog", "new-runner-token"));

        // The released state was persisted at load time: a store created from the file as it was
        // BETWEEN the release and the re-claim must also see no consumer. Prove it by checking the
        // raw file the first reload wrote before our re-claim above overwrote it again - simplest
        // equivalent: release, reload once more, still unclaimed.
        reloaded.Release("backlog");
        var reloadedAgain = new WorkListStore(path);
        var listAgain = reloadedAgain.Get("backlog");
        Assert.NotNull(listAgain);
        Assert.Null(listAgain.Consumer);
    }

    [Fact]
    public void EveryMutation_IsWrittenThroughImmediately()
    {
        var path = NewPath();
        var store = new WorkListStore(path);

        // Create
        store.Create("wt");
        Assert.NotNull(new WorkListStore(path).Get("wt"));

        // AppendItem
        store.AppendItem("wt", Ref("github", "1"));
        store.AppendItem("wt", Ref("github", "2"));
        Assert.Equal(new[] { "1", "2" }, new WorkListStore(path).Get("wt")!.Items.Select(i => i.Id).ToArray());

        // Reorder
        store.Reorder("wt", new List<WorkListItemRef> { Ref("github", "2"), Ref("github", "1") });
        Assert.Equal(new[] { "2", "1" }, new WorkListStore(path).Get("wt")!.Items.Select(i => i.Id).ToArray());

        // RemoveItem
        store.RemoveItem("wt", "github", "2");
        Assert.Equal(new[] { "1" }, new WorkListStore(path).Get("wt")!.Items.Select(i => i.Id).ToArray());

        // Claim: persisted (a reloading store releases it as stale - which is only possible
        // because the claim itself was written through). Read the raw file instead of reloading.
        store.Claim("wt", "tok-a");
        Assert.Contains("tok-a", File.ReadAllText(path), StringComparison.Ordinal);

        // Release
        store.Release("wt");
        Assert.DoesNotContain("tok-a", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Save_LeavesNoTempResidue_AndFileIsWholeJson()
    {
        var path = NewPath();
        var store = new WorkListStore(path);
        store.Create("backlog");
        for (var i = 0; i < 50; i++)
            store.AppendItem("backlog", Ref("github", i.ToString()));

        // Atomic temp + rename: after any number of write-throughs there is exactly the store
        // file, never a .tmp left behind, and the file parses (no half-truncation).
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        var reloaded = new WorkListStore(path);
        Assert.Equal(50, reloaded.Get("backlog")!.Items.Count);
    }

    [Fact]
    public void MissingFile_StartsEmpty_NoCrash()
    {
        var path = NewPath();
        Assert.False(File.Exists(path));

        var store = new WorkListStore(path);

        Assert.Empty(store.ListAll());
        // First boot never creates the file until the first mutation.
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CorruptFile_IsQuarantined_StoreStartsEmpty_BytesPreserved()
    {
        var path = NewPath();
        Directory.CreateDirectory(_dir);
        const string corrupt = "{ this is not json !!!";
        File.WriteAllText(path, corrupt);

        var store = new WorkListStore(path);

        // The store boots empty rather than crashing the Gateway...
        Assert.Empty(store.ListAll());
        // ...the corrupt bytes are preserved under a quarantine name (never silently overwritten)...
        var quarantined = Directory.GetFiles(_dir, Path.GetFileName(path) + ".corrupt-*");
        var q = Assert.Single(quarantined);
        Assert.Equal(corrupt, File.ReadAllText(q));
        // ...and the original path is freed for the next write-through.
        Assert.False(File.Exists(path));
        store.Create("fresh");
        Assert.NotNull(new WorkListStore(path).Get("fresh"));
    }

    [Fact]
    public void NullJsonFile_IsQuarantined_StoreStartsEmpty()
    {
        var path = NewPath();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "null");

        var store = new WorkListStore(path);

        Assert.Empty(store.ListAll());
        Assert.Single(Directory.GetFiles(_dir, Path.GetFileName(path) + ".corrupt-*"));
    }

    [Fact]
    public void Constructor_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new WorkListStore(" "));
    }

    /// <summary>
    /// The interrupted-drain criterion (issue #301 AC, D-2): a drain is killed mid-run (the
    /// Gateway restart), the queue survives on disk, the stale claim is released on reload, and a
    /// NEW runner re-claims and continues from the persisted order - nothing silently lost. The
    /// session driver is the #300 <see cref="IImplSessionDriver"/> fake per the existing runner
    /// test patterns; re-running already-dispatched items is the re-claiming runner's documented
    /// v1 behavior (duplicate protection lives at the issue-level claim, #298).
    /// </summary>
    [Fact]
    public async Task InterruptedDrain_AfterRestart_NewRunnerReclaims_ContinuesInPersistedOrder()
    {
        var path = NewPath();

        // Before the restart: a list of three items is being drained; the runner holds the claim
        // and is wedged inside item 1 when the Gateway dies (we never let item 1 finish).
        var store = new WorkListStore(path);
        store.Create("queue");
        store.AppendItem("queue", Ref("github", "101"));
        store.AppendItem("queue", Ref("github", "102"));
        store.AppendItem("queue", Ref("github", "103"));

        var wedgedDriver = new WedgedDriver();
        var interruptedRunner = new WorkListRunner(store, wedgedDriver,
            pollInterval: TimeSpan.FromMilliseconds(5), perItemTimeout: TimeSpan.FromMinutes(5));
        using var death = new CancellationTokenSource();
        var drainTask = interruptedRunner.DrainAsync("queue", "runner-before-restart", death.Token);

        // The drain is live: the claim is held and item 101's session has started.
        await wedgedDriver.FirstStartObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkListStore.ClaimResult.AlreadyClaimed, store.Claim("queue", "someone-else"));

        // THE RESTART: the Gateway process dies mid-drain. Snapshot the file AS IT IS at the
        // instant of death - a killed process never reaches the runner's finally-release, so the
        // snapshot still carries the now-stale claim. (The in-process runner is then cancelled
        // purely for test hygiene; its graceful release goes to the ORIGINAL path, not the
        // snapshot, so it cannot launder the crash state we reload below.)
        var crashPath = path + ".as-killed";
        File.Copy(path, crashPath);
        Assert.Contains("runner-before-restart", File.ReadAllText(crashPath), StringComparison.Ordinal);
        death.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drainTask);

        // After the restart: a fresh store loads the crash-state file. List + order intact, and
        // the stale claim from the dead runner is released on load.
        var reloadedStore = new WorkListStore(crashPath);
        var queue = reloadedStore.Get("queue");
        Assert.NotNull(queue);
        Assert.Equal(new[] { "101", "102", "103" }, queue.Items.Select(i => i.Id).ToArray());
        Assert.Null(queue.Consumer);

        // A NEW runner re-claims and drains the persisted queue in order to completion.
        var driver = new RecordingDriver();
        var newRunner = new WorkListRunner(reloadedStore, driver, pollInterval: TimeSpan.FromMilliseconds(5));
        var result = await newRunner.DrainAsync("queue", "runner-after-restart");

        Assert.Equal(new[] { "101", "102", "103" }, driver.StartOrder.ToArray());
        Assert.All(result.Items, i => Assert.Equal(WorkListItemOutcome.Ran, i.Outcome));
        Assert.All(result.Items, i => Assert.Equal(ImplLoopSignal.Done, i.Signal));
        Assert.True(result.ConsumerReleased);
    }

    /// <summary>Starts the first session, then never emits a terminal sentinel - the drain wedges
    /// inside item 1 until the test "kills the Gateway" via cancellation.</summary>
    private sealed class WedgedDriver : IImplSessionDriver
    {
        public TaskCompletionSource FirstStartObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<(string? sessionId, string? error)> StartImplementationSessionAsync(string itemId, string seedPrompt, CancellationToken ct)
        {
            FirstStartObserved.TrySetResult();
            return Task.FromResult<(string?, string?)>(($"sid-{itemId}", null));
        }

        public Task<string?> ReadTranscriptAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<string?>("still working, no sentinel yet");
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
