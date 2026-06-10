using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="WorkListRunner"/> (issue #274): ordered single-in-flight draining,
/// terminal-signal recording per item, source gating (github-only), consumer-claim release, and the
/// double-claim refusal. A fake <see cref="IImplSessionDriver"/> stands in for a live Director so
/// the sequencing logic is provable without a running app.
/// </summary>
public sealed class WorkListRunnerTests
{
    private static WorkListItemRef Ref(string source, string id, string? area = null) =>
        new() { Source = source, Id = id, Area = area };

    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Records the exact order of start calls and serves a canned IMPL-LOOP-TERMINAL block per issue
    /// the moment its session is read - so the runner observes a terminal signal on the first poll.
    /// Crucially it asserts SINGLE-IN-FLIGHT: a start while another session is still "open" trips a
    /// flag the test checks (criterion 1).
    /// </summary>
    private sealed class FakeDriver : IImplSessionDriver
    {
        private readonly Dictionary<string, ImplLoopSignal> _signalByIssue;
        private int _openSessions;

        public List<string> StartOrder { get; } = new();
        public bool EverOverlapped { get; private set; }

        public FakeDriver(Dictionary<string, ImplLoopSignal> signalByIssue) => _signalByIssue = signalByIssue;

        public Task<(string? sessionId, string? error)> StartImplementationSessionAsync(string issueId, CancellationToken ct)
        {
            StartOrder.Add(issueId);
            // If a session is still open when a new one starts, the runner overlapped two items.
            if (Interlocked.Increment(ref _openSessions) > 1)
                EverOverlapped = true;
            return Task.FromResult<(string?, string?)>(($"sid-{issueId}", null));
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
        var store = new WorkListStore();
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
    public async Task DrainAsync_DevopsAndJiraItems_NeverStarted_LeftInList()
    {
        var store = new WorkListStore();
        store.Create("mixed");
        store.AppendItem("mixed", Ref("devops", "1203"));
        store.AppendItem("mixed", Ref("github", "262"));
        store.AppendItem("mixed", Ref("jira", "CCD-44"));

        var driver = new FakeDriver(new() { ["262"] = ImplLoopSignal.Done });
        var runner = new WorkListRunner(store, driver, FastPoll);

        var result = await runner.DrainAsync("mixed", "consumer-a");

        // Criterion 3: only the github item was ever started; no /implementation-loop for non-github.
        Assert.Equal(new[] { "262" }, driver.StartOrder.ToArray());

        Assert.Equal(WorkListItemOutcome.SkippedNonGithub, result.Items[0].Outcome);
        Assert.Equal(WorkListItemOutcome.Ran, result.Items[1].Outcome);
        Assert.Equal(WorkListItemOutcome.SkippedNonGithub, result.Items[2].Outcome);

        // The non-github items are untouched in the list (the runner never writes status back).
        var list = store.Get("mixed");
        Assert.NotNull(list);
        Assert.Equal(new[] { "1203", "262", "CCD-44" }, list.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task DrainAsync_AlreadyClaimed_Throws_ClaimRefused()
    {
        var store = new WorkListStore();
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
        var store = new WorkListStore();
        var driver = new FakeDriver(new());
        var runner = new WorkListRunner(store, driver, FastPoll);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.DrainAsync("ghost", "consumer-a"));
    }

    [Fact]
    public async Task DrainAsync_StartFailure_RecordedAndAdvances()
    {
        var store = new WorkListStore();
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

        public Task<(string? sessionId, string? error)> StartImplementationSessionAsync(string issueId, CancellationToken ct)
        {
            _calls++;
            return _calls == 1
                ? Task.FromResult<(string?, string?)>((null, "director unreachable"))
                : Task.FromResult<(string?, string?)>(($"sid-{issueId}", null));
        }

        public Task<string?> ReadTranscriptAsync(string sessionId, CancellationToken ct)
        {
            var issueId = sessionId[4..];
            return Task.FromResult<string?>($"IMPL-LOOP-TERMINAL\nissue: {issueId}\nsignal: done\npr: none\nmerged: yes\nreason: ok\n");
        }
    }
}
