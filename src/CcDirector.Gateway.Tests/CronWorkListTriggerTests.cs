using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the cron work-list use case (epic #479, #484): the <see cref="DirectorCronWorkListRunner"/>
/// pre-check outcomes (AC3 - no list / empty / already-claimed / no director / machine busy handled
/// cleanly, no duplicate claim) and the <see cref="DirectorWorkListDrainLauncher"/> proving that the
/// launch path CLAIMS the list and drains it IN ORDER through the shipped #274 runner (AC1/AC2).
/// Schedule validation of a work-list action is covered too.
/// </summary>
public sealed class CronWorkListTriggerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-cronwl-tests-" + Guid.NewGuid().ToString("N"));

    private WorkListStore NewListStore() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static CronJobDto WorkListJob(string listName = "Tonight") => new()
    {
        Id = "cj_test",
        Name = "drain",
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", WorkListName = listName },
    };

    private static DirectorCronWorkListRunner Trigger(
        WorkListStore store, IDirectorTargetResolver resolver, WorkListRunnerManager manager, ICronWorkListDrainLauncher launcher) =>
        new(store, resolver, manager, launcher);

    [Fact]
    public async Task Trigger_NoSuchList_ReturnsNoSuchList()
    {
        var store = NewListStore();
        var t = Trigger(store, new FakeResolver("http://d", "machineA"), new WorkListRunnerManager(), new FakeLauncher());
        Assert.Equal(CronWorkListOutcome.NoSuchList, await t.TriggerAsync(WorkListJob(), CancellationToken.None));
    }

    [Fact]
    public async Task Trigger_EmptyList_ReturnsEmptyList()
    {
        var store = NewListStore();
        store.Create("Tonight"); // exists but no items
        var t = Trigger(store, new FakeResolver("http://d", "machineA"), new WorkListRunnerManager(), new FakeLauncher());
        Assert.Equal(CronWorkListOutcome.EmptyList, await t.TriggerAsync(WorkListJob(), CancellationToken.None));
    }

    [Fact]
    public async Task Trigger_AlreadyClaimedList_ReturnsAlreadyClaimed_NoDuplicateClaim()
    {
        var store = NewListStore();
        store.Create("Tonight");
        store.AppendItem("Tonight", new WorkListItemRef { Source = "github", Id = "312" });
        Assert.Equal(WorkListStore.ClaimResult.Granted, store.Claim("Tonight", "someone-else"));

        var launcher = new FakeLauncher();
        var t = Trigger(store, new FakeResolver("http://d", "machineA"), new WorkListRunnerManager(), launcher);

        Assert.Equal(CronWorkListOutcome.AlreadyClaimed, await t.TriggerAsync(WorkListJob(), CancellationToken.None));
        Assert.Equal(0, launcher.LaunchCount);                 // never launched -> no duplicate claim
        Assert.Equal("someone-else", store.Get("Tonight")!.Consumer); // original claim untouched
    }

    [Fact]
    public async Task Trigger_NoSuchDirector_ReturnsNoSuchDirector()
    {
        var store = NewListStore();
        store.Create("Tonight");
        store.AppendItem("Tonight", new WorkListItemRef { Source = "github", Id = "312" });
        var t = Trigger(store, new FakeResolver(null, null), new WorkListRunnerManager(), new FakeLauncher());
        Assert.Equal(CronWorkListOutcome.NoSuchDirector, await t.TriggerAsync(WorkListJob(), CancellationToken.None));
    }

    [Fact]
    public async Task Trigger_MachineBusy_ReturnsMachineBusy()
    {
        var store = NewListStore();
        store.Create("Tonight");
        store.AppendItem("Tonight", new WorkListItemRef { Source = "github", Id = "312" });
        var manager = new WorkListRunnerManager();
        Assert.Equal(WorkListRunnerManager.AdmitResult.Admitted, manager.TryAdmit("workstation-A", "OtherList"));

        var t = Trigger(store, new FakeResolver("http://d", "machineA"), manager, new FakeLauncher());
        Assert.Equal(CronWorkListOutcome.MachineBusy, await t.TriggerAsync(WorkListJob(), CancellationToken.None));
    }

    [Fact]
    public async Task Trigger_Started_LaunchesDrain_WithListAndCronConsumer_ThenReleasesMachine()
    {
        var store = NewListStore();
        store.Create("Tonight");
        store.AppendItem("Tonight", new WorkListItemRef { Source = "github", Id = "312" });
        var manager = new WorkListRunnerManager();
        var launcher = new FakeLauncher();
        var t = Trigger(store, new FakeResolver("http://d", "machineA"), manager, launcher);

        var outcome = await t.TriggerAsync(WorkListJob(), CancellationToken.None);
        Assert.Equal(CronWorkListOutcome.Started, outcome);

        // The drain runs in the background; wait until the launcher is invoked.
        await launcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Tonight", launcher.LastListName);
        Assert.StartsWith("cron:cj_test:", launcher.LastConsumer);
        Assert.Equal("Tonight", manager.ActiveList("workstation-A")); // the machine's slot holds the "Tonight" drain

        launcher.Release.TrySetResult();
        await launcher.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The machine slot is released after the drain completes (poll briefly for the finally).
        await WaitUntilAsync(() => manager.ActiveList("workstation-A") is null, TimeSpan.FromSeconds(10));
        Assert.Null(manager.ActiveList("workstation-A"));
    }

    [Fact]
    public async Task DrainLauncher_ClaimsList_AndDrainsItemsInOrder()
    {
        // The production launcher with a FAKE driver factory: proves that going through the launcher
        // CLAIMS the list and drives the shipped WorkListRunner over the items in order (AC1/AC2).
        var store = NewListStore();
        store.Create("Tonight");
        store.AppendItem("Tonight", new WorkListItemRef { Source = "github", Id = "101" });
        store.AppendItem("Tonight", new WorkListItemRef { Source = "github", Id = "102" });
        store.AppendItem("Tonight", new WorkListItemRef { Source = "github", Id = "103" });

        var driver = new OrderRecordingDriver();
        var launcher = new DirectorWorkListDrainLauncher(store, client: null, driverFactory: (_, _) => driver);

        await launcher.LaunchAsync("http://d", @"D:\repo", "Tonight", "cron:cj_test:abc", CancellationToken.None);

        Assert.Equal(new[] { "101", "102", "103" }, driver.StartOrder.ToArray()); // in list order
        Assert.Null(store.Get("Tonight")!.Consumer);                               // claim released after drain
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }

    // ---- fakes -------------------------------------------------------------------------------

    private sealed class FakeResolver : IDirectorTargetResolver
    {
        private readonly string? _endpoint;
        private readonly string? _directorId;
        public FakeResolver(string? endpoint, string? directorId) { _endpoint = endpoint; _directorId = directorId; }
        public Task<DirectorTargetResult> ResolveAsync(string machine, CancellationToken ct) =>
            Task.FromResult(string.IsNullOrEmpty(_endpoint)
                ? new DirectorTargetResult(null, null, "no director on machine")
                : new DirectorTargetResult(_endpoint, _directorId, null));
    }

    private sealed class FakeLauncher : ICronWorkListDrainLauncher
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LaunchCount { get; private set; }
        public string? LastListName { get; private set; }
        public string LastConsumer { get; private set; } = "";

        public async Task LaunchAsync(string endpoint, string repoPath, string listName, string consumer, CancellationToken ct)
        {
            LaunchCount++;
            LastListName = listName;
            LastConsumer = consumer;
            Entered.TrySetResult();
            await Release.Task;
            Completed.TrySetResult();
        }
    }

    private sealed class OrderRecordingDriver : IImplSessionDriver
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
