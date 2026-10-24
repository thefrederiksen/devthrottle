using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Core.Tests.Tenancy;

/// <summary>
/// THE test the unsafe-collection ingestion map demands: two authenticated tenants using the SAME raw
/// session identifier, proving one cannot READ, MUTATE, SUPPRESS, DELETE or CONTEND with the other's state.
/// Each of those five verbs gets its own test, because each is a distinct way the fourteen session-keyed
/// Gateway collections are used today and a partition can hold for one while failing for another.
///
/// Every test uses the SAME raw session identifier for both tenants. That is the whole point: on a hosted
/// Gateway two Directors are free to choose the same session identifier, and it is exactly that collision
/// the bare-string key could not survive.
/// </summary>
public sealed class TenantSessionMapTests
{
    private static readonly TenantId TenantA = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly TenantId TenantB = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string SharedSessionId = "sess-collide";

    private static TenantSessionKey KeyA => TenantSessionKey.For(TenantA, SharedSessionId);
    private static TenantSessionKey KeyB => TenantSessionKey.For(TenantB, SharedSessionId);

    // ===== READ =====

    [Fact]
    public void Read_OneTenantCannotSeeTheOthersValue()
    {
        var map = new TenantSessionMap<string>();
        map.Set(KeyA, "tenant A secret");

        Assert.False(map.TryGetValue(KeyB, out _));
        Assert.False(map.ContainsKey(KeyB));
        Assert.Null(map.GetValueOrDefault(KeyB));

        // The control: tenant A still reads its own.
        Assert.True(map.TryGetValue(KeyA, out var mine));
        Assert.Equal("tenant A secret", mine);
    }

    [Fact]
    public void Read_PerTenantEnumerationReturnsOnlyThatTenantsEntries()
    {
        // The expiry passes, roster folds and statistics reads all enumerate. An enumeration that crossed
        // the boundary would disclose everything a keyed read does not.
        var map = new TenantSessionMap<int>();
        map.Set(KeyA, 1);
        map.Set(TenantSessionKey.For(TenantA, "sess-a-only"), 2);
        map.Set(KeyB, 99);

        var aKeys = map.KeysFor(TenantA);
        var bKeys = map.KeysFor(TenantB);

        Assert.Equal(2, aKeys.Count);
        Assert.All(aKeys, k => Assert.Equal(TenantA, k.Tenant));
        Assert.Single(bKeys);
        Assert.All(bKeys, k => Assert.Equal(TenantB, k.Tenant));

        Assert.Equal(new[] { 1, 2 }, map.SnapshotFor(TenantA).Select(p => p.Value).OrderBy(v => v));
        Assert.Equal(new[] { 99 }, map.SnapshotFor(TenantB).Select(p => p.Value));
    }

    [Fact]
    public void Read_CountIsPerTenant_NeverAProcessWideTotal()
    {
        // Census row 65 is the current-session concurrency set: a shared count is itself the disclosure.
        var map = new TenantSessionMap<byte>();
        map.Set(KeyA, 1);
        map.Set(TenantSessionKey.For(TenantA, "sess-a-only"), 1);
        map.Set(KeyB, 1);

        Assert.Equal(2, map.CountFor(TenantA));
        Assert.Equal(1, map.CountFor(TenantB));
    }

    [Fact]
    public void Read_AnUnknownTenantSeesNothing_RatherThanEveryoneElse()
    {
        var map = new TenantSessionMap<string>();
        map.Set(KeyA, "tenant A secret");

        var stranger = new TenantId("cccccccc-cccc-cccc-cccc-cccccccccccc");

        Assert.Equal(0, map.CountFor(stranger));
        Assert.Empty(map.KeysFor(stranger));
        Assert.Empty(map.SnapshotFor(stranger));
    }

    // ===== MUTATE =====

    [Fact]
    public void Mutate_OneTenantsWriteDoesNotOverwriteTheOthers()
    {
        var map = new TenantSessionMap<string>();
        map.Set(KeyA, "tenant A value");

        map.Set(KeyB, "tenant B value");

        Assert.Equal("tenant A value", map.GetValueOrDefault(KeyA));
        Assert.Equal("tenant B value", map.GetValueOrDefault(KeyB));
    }

    [Fact]
    public void Mutate_UpdateOfAnExistingEntryDoesNotReachTheOtherTenant()
    {
        // The transcribing marker's progress refresh: bump a LIVE mark only. Tenant B has no mark, so its
        // refresh must fail - and must certainly not bump tenant A's.
        var map = new TenantSessionMap<int>();
        map.Set(KeyA, 10);

        Assert.False(map.TryUpdateExisting(KeyB, 20));
        Assert.Equal(10, map.GetValueOrDefault(KeyA));
        Assert.False(map.ContainsKey(KeyB));

        // The control: tenant A's own refresh does land.
        Assert.True(map.TryUpdateExisting(KeyA, 30));
        Assert.Equal(30, map.GetValueOrDefault(KeyA));
    }

    // ===== SUPPRESS =====

    [Fact]
    public void Suppress_OneTenantCannotConsumeTheOthersFirstSeenMarker()
    {
        // The seed / first-seen shape (census rows 51 and 53, and the needs-you clock's entry stamp): the
        // FIRST writer is told it created the entry, and a later writer is not. If the partition failed,
        // tenant B writing first would silently steal tenant A's "first" and suppress its accounting.
        var map = new TenantSessionMap<string>();

        map.GetOrAdd(KeyB, _ => "B first", out var bCreated);
        map.GetOrAdd(KeyA, _ => "A first", out var aCreated);

        Assert.True(bCreated);
        Assert.True(aCreated); // NOT suppressed by B having gone first
        Assert.Equal("A first", map.GetValueOrDefault(KeyA));
        Assert.Equal("B first", map.GetValueOrDefault(KeyB));
    }

    [Fact]
    public void Suppress_HoldingAnEntryDoesNotHoldTheOtherTenants()
    {
        // The hold half of entry/hold: a second GetOrAdd for the same tenant reports created=false and keeps
        // the original value, while the OTHER tenant's first call still reports created=true.
        var map = new TenantSessionMap<string>();

        map.GetOrAdd(KeyA, _ => "A original", out var firstA);
        map.GetOrAdd(KeyA, _ => "A replacement", out var secondA);
        map.GetOrAdd(KeyB, _ => "B original", out var firstB);

        Assert.True(firstA);
        Assert.False(secondA);
        Assert.True(firstB);
        Assert.Equal("A original", map.GetValueOrDefault(KeyA));
    }

    // ===== DELETE =====

    [Fact]
    public void Delete_OneTenantCannotRemoveTheOthersEntry()
    {
        var map = new TenantSessionMap<string>();
        map.Set(KeyA, "tenant A value");

        Assert.False(map.TryRemove(KeyB, out _));
        Assert.True(map.ContainsKey(KeyA));
        Assert.Equal("tenant A value", map.GetValueOrDefault(KeyA));
    }

    [Fact]
    public void Delete_RemovingOnesOwnEntryLeavesTheOthersStanding()
    {
        var map = new TenantSessionMap<string>();
        map.Set(KeyA, "tenant A value");
        map.Set(KeyB, "tenant B value");

        Assert.True(map.TryRemove(KeyA, out var removed));

        Assert.Equal("tenant A value", removed);
        Assert.False(map.ContainsKey(KeyA));
        Assert.Equal("tenant B value", map.GetValueOrDefault(KeyB));
    }

    [Fact]
    public void Delete_TenantTeardownDropsOnlyThatTenantsPartition()
    {
        var map = new TenantSessionMap<string>();
        map.Set(KeyA, "tenant A value");
        map.Set(TenantSessionKey.For(TenantA, "sess-a-only"), "tenant A second");
        map.Set(KeyB, "tenant B value");

        Assert.Equal(2, map.RemoveAllFor(TenantA));

        Assert.Equal(0, map.CountFor(TenantA));
        Assert.Equal("tenant B value", map.GetValueOrDefault(KeyB));
    }

    // ===== CONTEND =====

    [Fact]
    public async Task Contend_ConcurrentWritersOnTheSameRawIdentifierNeverMeet()
    {
        // The collision under load. Both tenants hammer the SAME raw session identifier; each must end
        // holding exactly its own value and see exactly one entry.
        var map = new TenantSessionMap<string>();
        const int iterations = 2000;

        var writeA = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++) map.Set(KeyA, "A");
        });
        var writeB = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++) map.Set(KeyB, "B");
        });
        await Task.WhenAll(writeA, writeB);

        Assert.Equal("A", map.GetValueOrDefault(KeyA));
        Assert.Equal("B", map.GetValueOrDefault(KeyB));
        Assert.Equal(1, map.CountFor(TenantA));
        Assert.Equal(1, map.CountFor(TenantB));
    }

    [Fact]
    public async Task Contend_OneTenantsRemovalStormCannotClearTheOthersEntry()
    {
        // The suppression race in its sharpest form: B removes the shared raw identifier as fast as it can
        // while A holds a single entry. A's entry must survive untouched.
        var map = new TenantSessionMap<string>();
        map.Set(KeyA, "tenant A value");

        var removeB = Task.Run(() =>
        {
            for (var i = 0; i < 5000; i++) map.TryRemove(KeyB, out _);
        });
        await removeB;

        Assert.True(map.ContainsKey(KeyA));
        Assert.Equal("tenant A value", map.GetValueOrDefault(KeyA));
    }

    // ===== SAME-TENANT IDENTITY =====
    // The tenancy dimension is only half of what a keyed collection owes. Inside ONE tenant, two sessions
    // must not share an entry either.

    [Fact]
    public void SameTenant_DistinctSessions_AreSeparateEntries()
    {
        var map = new TenantSessionMap<string>();
        var one = TenantSessionKey.For(TenantA, "sess-1");
        var two = TenantSessionKey.For(TenantA, "sess-2");

        map.Set(one, "first");
        map.Set(two, "second");

        Assert.Equal("first", map.GetValueOrDefault(one));
        Assert.Equal("second", map.GetValueOrDefault(two));
        Assert.True(map.TryRemove(one, out _));
        Assert.Equal("second", map.GetValueOrDefault(two));
    }

    // ===== CONCURRENCY ON ONE KEY =====
    // Both tests below carry a WITNESS assertion that the race actually interleaved. A concurrency test
    // that never interleaves is dead coverage that reads as a pass, so a run that failed to produce the
    // contended condition fails loudly rather than reporting success.

    [Fact]
    public async Task Contend_UpdateRacingRemoval_NeverResurrectsTheRemovedEntry()
    {
        // TryUpdateExisting promises to keep a live mark alive and NEVER to bring a cleared one back. A
        // check-then-assign implementation breaks that promise: a removal landing between the check and
        // the assignment turns the assignment into an insert. This drives that exact interleaving.
        const int rounds = 4000;
        var map = new TenantSessionMap<string>();
        var key = TenantSessionKey.For(TenantA, "sess-race");

        using var barrier = new Barrier(2);
        var resurrections = 0;
        var bothSucceeded = 0;
        var removeResult = false;
        var updateResult = false;

        var remover = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                map.Set(key, "live");
                barrier.SignalAndWait();
                removeResult = map.TryRemove(key, out _);
                barrier.SignalAndWait();

                if (removeResult && updateResult) bothSucceeded++;
                if (removeResult && map.ContainsKey(key)) resurrections++;
                map.Remove(key); // reset for the next round regardless of outcome
                barrier.SignalAndWait();
            }
        });

        var updater = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                barrier.SignalAndWait();
                updateResult = map.TryUpdateExisting(key, "updated");
                barrier.SignalAndWait();
                barrier.SignalAndWait();
            }
        });

        await Task.WhenAll(remover, updater);

        Assert.True(bothSucceeded > 0,
            "the update and the removal never both succeeded in one round, so the interleaving under test " +
            "never occurred and this test proved nothing");
        Assert.Equal(0, resurrections);
    }

    [Fact]
    public async Task Contend_GetOrAddOnOneKey_HasExactlyOneWinnerPerRound()
    {
        // The first-seen / seed marker contract: exactly one caller is told it created the entry. Using the
        // concurrent dictionary's own get-or-add with a flag set inside the factory breaks this, because it
        // may run the factory in several racing callers while keeping only one value - so several callers
        // are told they were first.
        //
        // The contention is forced, not hoped for: the factory holds every caller until all of them are inside
        // it, so no caller can add the entry before every other caller has already missed it. Every round
        // therefore runs the factory once per worker, whatever the scheduler does. Left to chance, a fast
        // caller could finish its add before the others looked, and a round would test nothing.
        const int rounds = 100;
        var workers = Math.Max(4, Environment.ProcessorCount);
        var map = new TenantSessionMap<int>();
        var key = TenantSessionKey.For(TenantA, "sess-seed");

        using var barrier = new Barrier(workers);
        using var allInFactory = new CountdownEvent(workers);
        var winners = new int[rounds];
        var factoryRuns = new int[rounds];

        var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
        {
            for (var round = 0; round < rounds; round++)
            {
                barrier.SignalAndWait();
                map.GetOrAdd(key, _ =>
                {
                    Interlocked.Increment(ref factoryRuns[round]);
                    allInFactory.Signal();
                    // A guard against a hang, never a verdict: on correct code every worker arrives at once.
                    if (!allInFactory.Wait(TimeSpan.FromSeconds(30)))
                        throw new InvalidOperationException("not every caller reached the value factory");
                    return worker;
                }, out var added);
                if (added) Interlocked.Increment(ref winners[round]);
                barrier.SignalAndWait();

                if (worker == 0)
                {
                    map.Remove(key); // reset for the next round
                    allInFactory.Reset();
                }
                barrier.SignalAndWait();
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.All(factoryRuns, runs => Assert.Equal(workers, runs));
        Assert.All(winners, w => Assert.Equal(1, w));
    }

    // ===== DENY BY DEFAULT =====

    [Fact]
    public void ADefaultKeyIsRefusedOnEveryMember_NotTreatedAsAMiss()
    {
        // A default key is the only way to hold one without having resolved a tenant. A quiet miss is how an
        // unpartitioned access survives review, so every member fails loud instead.
        var map = new TenantSessionMap<string>();

        Assert.Throws<ArgumentException>(() => map.Set(default, "x"));
        Assert.Throws<ArgumentException>(() => map.TryAdd(default, "x"));
        Assert.Throws<ArgumentException>(() => map.TryGetValue(default, out _));
        Assert.Throws<ArgumentException>(() => map.GetValueOrDefault(default));
        Assert.Throws<ArgumentException>(() => map.ContainsKey(default));
        Assert.Throws<ArgumentException>(() => map.GetOrAdd(default, _ => "x", out _));
        Assert.Throws<ArgumentException>(() => map.TryUpdateExisting(default, "x"));
        Assert.Throws<ArgumentException>(() => map.TryRemove(default, out _));
        Assert.Throws<ArgumentException>(() => map.Remove(default));
    }

    [Fact]
    public void AnUnresolvedTenantIsRefusedOnEveryTenantWideMember()
    {
        var map = new TenantSessionMap<string>();

        Assert.Throws<ArgumentException>(() => map.CountFor(default));
        Assert.Throws<ArgumentException>(() => map.KeysFor(default));
        Assert.Throws<ArgumentException>(() => map.SnapshotFor(default));
        Assert.Throws<ArgumentException>(() => map.RemoveAllFor(default));
    }

    [Fact]
    public void SelfHostIsUnchangedInBehaviour_OneTenantBehavesLikeAPlainMap()
    {
        // The control for the whole change: on self-host every key is Local, so the map must behave exactly
        // as the bare dictionary it replaces.
        var map = new TenantSessionMap<string>();
        var one = TenantSessionKey.For(TenantId.Local, "sess-1");
        var two = TenantSessionKey.For(TenantId.Local, "sess-2");

        map.Set(one, "first");
        map.Set(two, "second");

        Assert.Equal("first", map.GetValueOrDefault(one));
        Assert.Equal("second", map.GetValueOrDefault(two));
        Assert.Equal(2, map.CountFor(TenantId.Local));
        Assert.True(map.TryRemove(one, out _));
        Assert.Equal(1, map.CountFor(TenantId.Local));
    }
}
