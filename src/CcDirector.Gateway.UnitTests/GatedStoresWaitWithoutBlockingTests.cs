using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Activity;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Skills;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The deploy outage fix (measured 9 November 2026: every Director's reconnect queued on one process-wide lock around
/// synchronous database work, the waiting requests held every thread-pool worker, and the Gateway stopped answering for
/// 70 seconds). The trigger claim, the skill placement push and the activity batch now wait on a gate KEYED per account
/// (per account and Director for skill placement), and they AWAIT it.
///
/// Each store test holds one key's gate itself and proves three things, with no clock and no sleep:
///  - a call for the held key returns an UNFINISHED task: it waits, and it waits by awaiting - a call that blocked its
///    thread would never return to this test at all;
///  - a call for another key finishes while the first is still waiting: one account no longer queues behind another;
///  - once the gate is released, the waiting call finishes with the same answer it always gave.
/// The answers themselves are the existing suites' job (trigger claims, placement rows, the ledger), which all run
/// against the async paths.
/// </summary>
public sealed class GatedStoresWaitWithoutBlockingTests : IDisposable
{
    private static readonly TenantId AccountA = new("tenant-gate-aaaa");
    private static readonly TenantId AccountB = new("tenant-gate-bbbb");
    private static readonly DateTime Now = new(2026, 11, 9, 23, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    // ---------- the gate ----------

    [Fact]
    public async Task EnterAsync_SameKeyHeld_WaitsUntilReleased()
    {
        var gate = new KeyedAsyncGate();
        var first = await gate.EnterAsync("k");

        var second = gate.EnterAsync("k");

        Assert.False(second.IsCompleted);
        first.Dispose();
        (await second).Dispose();
        Assert.Equal(0, gate.ActiveKeys);
    }

    [Fact]
    public async Task EnterAsync_OtherKey_DoesNotWait()
    {
        var gate = new KeyedAsyncGate();
        using var first = await gate.EnterAsync("account-a");

        var other = gate.EnterAsync("account-b");

        Assert.True(other.IsCompletedSuccessfully);
        (await other).Dispose();
    }

    [Fact]
    public async Task Enter_BlockingHolder_StillSerialisesAnAsyncWaiter()
    {
        var gate = new KeyedAsyncGate();
        var held = gate.Enter("k");

        var waiter = gate.EnterAsync("k");

        Assert.False(waiter.IsCompleted);
        held.Dispose();
        (await waiter).Dispose();
    }

    [Fact]
    public async Task EnterAsync_CancelledWaiter_LeavesNoEntryBehind()
    {
        var gate = new KeyedAsyncGate();
        var held = await gate.EnterAsync("k");
        using var cancel = new CancellationTokenSource();

        var waiter = gate.EnterAsync("k", cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        held.Dispose();
        Assert.Equal(0, gate.ActiveKeys);
    }

    [Fact]
    public async Task Dispose_Twice_ReleasesOnce()
    {
        var gate = new KeyedAsyncGate();
        var held = await gate.EnterAsync("k");
        held.Dispose();
        held.Dispose();

        var again = await gate.EnterAsync("k");
        var waiter = gate.EnterAsync("k");

        // Had the second Dispose released again, the semaphore would admit two holders at once.
        Assert.False(waiter.IsCompleted);
        again.Dispose();
        (await waiter).Dispose();
    }

    // ---------- the trigger claim ----------

    [Fact]
    public async Task ClaimForDirectorAsync_AccountGateHeld_WaitsWhileAnotherAccountIsServed()
    {
        var db = _harness.Open();
        var triggerA = AddCommandTrigger(db, AccountA, "check-a");
        var triggerB = AddCommandTrigger(db, AccountB, "check-b");
        var store = new TriggerStore(db);
        var held = store.EnterGateForTest(AccountA);

        var claimA = store.ClaimForDirectorAsync(AccountA, "dir-a", "SOREN_NORTH", Now, runsDefinitionChecks: true);
        var claimB = await store.ClaimForDirectorAsync(AccountB, "dir-b", "SOREN_NORTH", Now, runsDefinitionChecks: true);

        Assert.False(claimA.IsCompleted);
        Assert.Equal(triggerB, Assert.Single(claimB).Id);
        held.Dispose();
        Assert.Equal(triggerA, Assert.Single(await claimA).Id);
    }

    [Fact]
    public async Task ClaimForDirectorAsync_TwoDirectorsOnOneMachineAtOnce_ExactlyOneHoldsTheTrigger()
    {
        // The guarantee the gate exists for: two Directors on one machine never both run one check.
        var db = _harness.Open();
        AddCommandTrigger(db, AccountA, "check-a");
        var store = new TriggerStore(db);
        var held = store.EnterGateForTest(AccountA);

        var first = store.ClaimForDirectorAsync(AccountA, "dir-1", "SOREN_NORTH", Now, runsDefinitionChecks: true);
        var second = store.ClaimForDirectorAsync(AccountA, "dir-2", "SOREN_NORTH", Now, runsDefinitionChecks: true);
        held.Dispose();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(r => r.Count == 1));
        Assert.Equal(1, results.Count(r => r.Count == 0));
    }

    // ---------- the skill placement push ----------

    [Fact]
    public async Task StoreBatchAsync_DirectorGateHeld_WaitsWhileAnotherDirectorIsServed()
    {
        var store = new SkillPlacementStore(_harness.Open(new AsyncLocalTenantContext()));
        var held = store.EnterGateForTest(AccountA, "dir-1");

        var pushOne = store.StoreBatchAsync(AccountA, "dir-1", "SOREN_NORTH", new[] { Placement("ClaudeCode") }, Now);
        var pushTwo = await store.StoreBatchAsync(AccountA, "dir-2", "SOREN_SOUTH", new[] { Placement("ClaudeCode") }, Now);

        Assert.False(pushOne.IsCompleted);
        Assert.Equal(1, pushTwo);
        held.Dispose();
        Assert.Equal(1, await pushOne);
        Assert.Equal(new[] { "dir-1", "dir-2" },
            store.ReadAll(AccountA).Rows.Select(r => r.DirectorId).OrderBy(d => d, StringComparer.Ordinal));
    }

    [Fact]
    public async Task StoreBatchAsync_TwoPushesFromOneDirectorAtOnce_LeaveOneRowWithTheLaterAnswer()
    {
        // Serialised per Director, so two pushes never both insert the same row; the second overwrites the first.
        var store = new SkillPlacementStore(_harness.Open(new AsyncLocalTenantContext()));
        var held = store.EnterGateForTest(AccountA, "dir-1");

        var first = store.StoreBatchAsync(AccountA, "dir-1", "SOREN_NORTH", new[] { Placement("ClaudeCode", held: 3, reachable: 1) }, Now);
        var second = store.StoreBatchAsync(AccountA, "dir-1", "SOREN_NORTH", new[] { Placement("ClaudeCode", held: 3, reachable: 3) }, Now.AddSeconds(1));
        held.Dispose();
        await Task.WhenAll(first, second);

        var row = Assert.Single(store.ReadAll(AccountA).Rows);
        Assert.Equal(3, row.Reachable);
    }

    // ---------- the activity batch ----------

    [Fact]
    public async Task AppendBatchAsync_AccountGateHeld_WaitsWhileAnotherAccountIsServed()
    {
        var tenants = new AsyncLocalTenantContext();
        var store = new ActivityEventStore(_harness.Open(tenants));
        var held = store.EnterGateForTest(AccountA.Value);
        var eventId = Guid.NewGuid();

        Task<(int Written, int Duplicates)> appendA;
        using (tenants.Enter(AccountA))
            appendA = store.AppendBatchAsync(new[] { Event(eventId) });
        (int Written, int Duplicates) appendB;
        using (tenants.Enter(AccountB))
            appendB = await store.AppendBatchAsync(new[] { Event(Guid.NewGuid()) });

        Assert.False(appendA.IsCompleted);
        Assert.Equal((1, 0), appendB);
        held.Dispose();
        Assert.Equal((1, 0), await appendA);

        // The same answer as ever: a replay of the same id is a duplicate, never a second row.
        using (tenants.Enter(AccountA))
            Assert.Equal((0, 1), await store.AppendBatchAsync(new[] { Event(eventId) }));
    }

    // ---------- helpers ----------

    private static Guid AddCommandTrigger(GatewayDatabase db, TenantId tenant, string name)
    {
        using var ctx = db.CreateContext(tenant);
        var row = new TriggerEntity
        {
            Name = name,
            Factory = "website",
            FactoryAgent = "builder",
            Machine = "SOREN_NORTH",
            RepoPath = "D:/repos/site",
            CheckCommand = "python check.py",
            IntervalSeconds = 600,
            Prompt = "Work: {count}",
            CreatedBy = "test",
            CreatedUtc = Now.AddMinutes(-5),
        };
        row.TenantId = ctx.ActiveTenant!;
        ctx.Triggers.Add(row);
        ctx.SaveChanges();
        return row.Id;
    }

    private static SkillPlacementReportDto Placement(string agentKind, int held = 3, int reachable = 3) => new()
    {
        AgentKind = agentKind,
        Held = held,
        Reachable = reachable,
        StoreMissing = false,
        Problems = new List<SkillPlacementProblemDto>(),
        ObservedAtUtc = Now,
    };

    private static ActivityEventRecord Event(Guid id) => new()
    {
        EventId = id,
        DirectorSequence = 1,
        OccurredUtc = Now,
        DirectorId = "dir-1",
        SessionId = "s1",
        Machine = "SOREN_NORTH",
        AgentKind = "Claude",
        EventType = ActivityEventTypes.ActivityTransition,
        PreviousState = "WaitingForInput",
        NewState = "Working",
        Cause = ActivityCauses.TerminalOutputOnly,
        DetectorMode = "byte",
        DetectorVersion = "v1",
        OutputByteCount = 512,
        BeforeScreenHash = "aaa",
        AfterScreenHash = "bbb",
        BoundedScreenDiff = "row 41: [watcher] tick",
    };
}
