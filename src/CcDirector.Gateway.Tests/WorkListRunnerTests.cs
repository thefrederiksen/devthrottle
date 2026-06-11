using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="WorkListRunner"/> (issue #274, adapter dispatch since #300): ordered
/// single-in-flight draining, terminal-signal recording per item, per-source adapter dispatch
/// (github + devops runnable, jira skip-with-note), consumer-claim release, and the double-claim
/// refusal. A fake <see cref="IImplSessionDriver"/> stands in for a live Director so the sequencing
/// logic is provable without a running app.
/// </summary>
public sealed class WorkListRunnerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-worklist-runner-tests-" + Guid.NewGuid().ToString("N"));

    private WorkListStore NewStore() =>
        new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static WorkListItemRef Ref(string source, string id, string? area = null) =>
        new() { Source = source, Id = id, Area = area };

    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Records the exact order of start calls (and each seed prompt, #300) and serves a canned
    /// IMPL-LOOP-TERMINAL block per issue the moment its session is read - so the runner observes a
    /// terminal signal on the first poll. Crucially it asserts SINGLE-IN-FLIGHT: a start while
    /// another session is still "open" trips a flag the test checks (criterion 1).
    /// </summary>
    private sealed class FakeDriver : IImplSessionDriver
    {
        private readonly Dictionary<string, ImplLoopSignal> _signalByIssue;
        private int _openSessions;

        public List<string> StartOrder { get; } = new();
        public List<string> Seeds { get; } = new();
        public bool EverOverlapped { get; private set; }

        public FakeDriver(Dictionary<string, ImplLoopSignal> signalByIssue) => _signalByIssue = signalByIssue;

        public Task<(string? sessionId, string? error)> StartImplementationSessionAsync(string itemId, string seedPrompt, CancellationToken ct)
        {
            StartOrder.Add(itemId);
            Seeds.Add(seedPrompt);
            // If a session is still open when a new one starts, the runner overlapped two items.
            if (Interlocked.Increment(ref _openSessions) > 1)
                EverOverlapped = true;
            return Task.FromResult<(string?, string?)>(($"sid-{itemId}", null));
        }

        public Task<string?> ReadTranscriptAsync(string sessionId, CancellationToken ct)
        {
            // sessionId is "sid-<issue>"; serve the canned terminal block for that issue, then close it.
            var issueId = sessionId.StartsWith("sid-", StringComparison.Ordinal) ? sessionId[4..] : sessionId;
            var signal = _signalByIssue.TryGetValue(issueId, out var s) ? s : ImplLoopSignal.Done;
            var word = signal switch
            {
                ImplLoopSignal.Done => "done",
                ImplLoopSignal.NeedsHuman => "needs-human",
                _ => "failed",
            };
            Interlocked.Decrement(ref _openSessions);
            var block = $"IMPL-LOOP-TERMINAL\nissue: {issueId}\nsignal: {word}\npr: none\nmerged: {(signal == ImplLoopSignal.Done ? "yes" : "no")}\nreason: test\n";
            return Task.FromResult<string?>(block);
        }
    }

    [Fact]
    public async Task DrainAsync_TwoGithubItems_StartsInOrder_NeverOverlapping()
    {
        var store = NewStore();
        store.Create("today");
        store.AppendItem("today", Ref("github", "262"));
        store.AppendItem("today", Ref("github", "263"));

        var driver = new FakeDriver(new()
        {
            ["262"] = ImplLoopSignal.Done,
            ["263"] = ImplLoopSignal.NeedsHuman,
        });
        var runner = new WorkListRunner(store, driver, FastPoll);

        var result = await runner.DrainAsync("today", "consumer-a");

        // Criterion 1: started in list order, exactly one in flight at a time.
        Assert.Equal(new[] { "262", "263" }, driver.StartOrder.ToArray());
        Assert.False(driver.EverOverlapped);

        // Criterion 2: per-item recorded signal matches what the session emitted.
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(ImplLoopSignal.Done, result.Items[0].Signal);
        Assert.Equal(ImplLoopSignal.NeedsHuman, result.Items[1].Signal);
        Assert.All(result.Items, i => Assert.Equal(WorkListItemOutcome.Ran, i.Outcome));

        // Criterion 4: claim released at the end; a fresh claim now succeeds.
        Assert.True(result.ConsumerReleased);
        Assert.Equal(WorkListStore.ClaimResult.Granted, store.Claim("today", "consumer-next"));
    }

    [Fact]
    public async Task DrainAsync_MixedSources_DispatchPerAdapter_DevopsRuns_JiraSkipped()
    {
        var store = NewStore();
        store.Create("mixed");
        store.AppendItem("mixed", Ref("devops", "1203"));
        store.AppendItem("mixed", Ref("github", "262"));
        store.AppendItem("mixed", Ref("jira", "CCD-44"));

        var driver = new FakeDriver(new()
        {
            ["1203"] = ImplLoopSignal.Done,
            ["262"] = ImplLoopSignal.Done,
        });
        var runner = new WorkListRunner(store, driver, FastPoll);

        var result = await runner.DrainAsync("mixed", "consumer-a");

        // #300 dispatch: devops AND github items are started, in list order; jira never is.
        Assert.Equal(new[] { "1203", "262" }, driver.StartOrder.ToArray());
        Assert.False(driver.EverOverlapped);

        // Per-source seed prompts (D-2): devops mode for the devops item, plain for github.
        Assert.Equal("/implementation-loop --source devops 1203", driver.Seeds[0]);
        Assert.Equal("/implementation-loop 262", driver.Seeds[1]);

        // Devops sentinel correlated by work item id; jira skipped with the note, left in list.
        Assert.Equal(WorkListItemOutcome.Ran, result.Items[0].Outcome);
        Assert.Equal(ImplLoopSignal.Done, result.Items[0].Signal);
        Assert.Equal(WorkListItemOutcome.Ran, result.Items[1].Outcome);
        Assert.Equal(WorkListItemOutcome.SkippedNonGithub, result.Items[2].Outcome);
        Assert.Contains("source 'jira' is not runnable", result.Items[2].Note, StringComparison.Ordinal);

        // The skipped jira item is untouched in the list (the runner never writes status back).
        var list = store.Get("mixed");
        Assert.NotNull(list);
        Assert.Equal(new[] { "1203", "262", "CCD-44" }, list.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task DrainAsync_DevopsItem_SeededWithDevopsMode_SignalRecorded()
    {
        var store = NewStore();
        store.Create("devops-only");
        store.AppendItem("devops-only", Ref("devops", "4711", "Gateway"));

        var driver = new FakeDriver(new() { ["4711"] = ImplLoopSignal.NeedsHuman });
        var runner = new WorkListRunner(store, driver, FastPoll);

        var result = await runner.DrainAsync("devops-only", "consumer-a");

        // The devops ref is dispatched: seed emitted in devops mode, sentinel correlated by the
        // work item id, terminal signal recorded (issue #300 acceptance criterion 1).
        Assert.Equal(new[] { "4711" }, driver.StartOrder.ToArray());
        Assert.Equal(new[] { "/implementation-loop --source devops 4711" }, driver.Seeds.ToArray());
        Assert.Equal(WorkListItemOutcome.Ran, result.Items[0].Outcome);
        Assert.Equal(ImplLoopSignal.NeedsHuman, result.Items[0].Signal);
        Assert.True(result.ConsumerReleased);
    }

    [Fact]
    public async Task DrainAsync_JiraItem_NeverStarted_LeftInList()
    {
        var store = NewStore();
        store.Create("jira-only");
        store.AppendItem("jira-only", Ref("jira", "CCD-44"));

        var driver = new FakeDriver(new());
        var runner = new WorkListRunner(store, driver, FastPoll);

        var result = await runner.DrainAsync("jira-only", "consumer-a");

        // jira still has no adapter: never started, skipped with the note, left in the list.
        Assert.Empty(driver.StartOrder);
        Assert.Equal(WorkListItemOutcome.SkippedNonGithub, result.Items[0].Outcome);
        Assert.Contains("source 'jira' is not runnable", result.Items[0].Note, StringComparison.Ordinal);
        var list = store.Get("jira-only");
        Assert.NotNull(list);
        Assert.Equal(new[] { "CCD-44" }, list.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task DrainAsync_DevopsNonNumericId_StartFailed_CannotCorrelate()
    {
        var store = NewStore();
        store.Create("bad-devops");
        store.AppendItem("bad-devops", Ref("devops", "not-a-number"));

        var driver = new FakeDriver(new());
        var runner = new WorkListRunner(store, driver, FastPoll);

        var result = await runner.DrainAsync("bad-devops", "consumer-a");

        // Same shape as the github non-numeric guard: the session starts but the runner cannot
        // correlate a sentinel for it, so it records StartFailed and advances.
        Assert.Equal(WorkListItemOutcome.StartFailed, result.Items[0].Outcome);
        Assert.Contains("cannot correlate", result.Items[0].Note, StringComparison.Ordinal);
        Assert.True(result.ConsumerReleased);
    }

    [Fact]
    public async Task DrainAsync_AlreadyClaimed_Throws_ClaimRefused()
    {
        var store = NewStore();
        store.Create("today");
        store.AppendItem("today", Ref("github", "262"));
        // Someone else holds the claim already (criterion 5).
        Assert.Equal(WorkListStore.ClaimResult.Granted, store.Claim("today", "other-consumer"));

        var driver = new FakeDriver(new() { ["262"] = ImplLoopSignal.Done });
        var runner = new WorkListRunner(store, driver, FastPoll);

        await Assert.ThrowsAsync<WorkListClaimRefusedException>(
            () => runner.DrainAsync("today", "consumer-a"));

        // The runner never started anything because it could not claim.
        Assert.Empty(driver.StartOrder);
    }

    [Fact]
    public async Task DrainAsync_NoSuchList_Throws()
    {
        var store = NewStore();
        var driver = new FakeDriver(new());
        var runner = new WorkListRunner(store, driver, FastPoll);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.DrainAsync("ghost", "consumer-a"));
    }

    [Fact]
    public async Task DrainAsync_StartFailure_RecordedAndAdvances()
    {
        var store = NewStore();
        store.Create("today");
        store.AppendItem("today", Ref("github", "262"));
        store.AppendItem("today", Ref("github", "263"));

        var driver = new FailFirstDriver();
        var runner = new WorkListRunner(store, driver, FastPoll);

        var result = await runner.DrainAsync("today", "consumer-a");

        Assert.Equal(WorkListItemOutcome.StartFailed, result.Items[0].Outcome);
        Assert.Equal(WorkListItemOutcome.Ran, result.Items[1].Outcome);
        Assert.True(result.ConsumerReleased);
    }

    private sealed class FailFirstDriver : IImplSessionDriver
    {
        private int _calls;

        public Task<(string? sessionId, string? error)> StartImplementationSessionAsync(string itemId, string seedPrompt, CancellationToken ct)
        {
            _calls++;
            return _calls == 1
                ? Task.FromResult<(string?, string?)>((null, "director unreachable"))
                : Task.FromResult<(string?, string?)>(($"sid-{itemId}", null));
        }

        public Task<string?> ReadTranscriptAsync(string sessionId, CancellationToken ct)
        {
            var issueId = sessionId[4..];
            return Task.FromResult<string?>($"IMPL-LOOP-TERMINAL\nissue: {issueId}\nsignal: done\npr: none\nmerged: yes\nreason: ok\n");
        }
    }
}
