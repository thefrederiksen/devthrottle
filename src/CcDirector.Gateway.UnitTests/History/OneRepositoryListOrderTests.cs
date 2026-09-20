using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// THE ONE LIST, IN THE ONE ORDER (the one-repository-list mission, phase 3). The Gateway serves the
/// union of both halves of the catalog already sorted - most recently used first, never-opened beneath
/// everything that has been used - so that no client sorts for itself and no two screens can show the
/// same machine differently. It is Critical Rule 7 (CLAUDE.md) applied to a list instead of a verdict.
///
/// The flow and the failure cases, never one success run: the mixed machine this phase exists for, the
/// order not depending on what the database hands over, an empty catalog, a machine that is not this
/// one, a Director that has gone away, and the two halves meeting on one repository.
/// </summary>
public sealed class OneRepositoryListOrderTests : IDisposable
{
    private const string Machine = "SOREN_NORTH";
    private const string DirectorOne = "director-one";
    private const string DirectorTwo = "director-two";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _harness.Dispose();

    private KnownRepositoryStore NewStore() => new(_harness.Open());

    private static DiscoveredRepository Found(string path, string name) => new(path, name);

    /// <summary>
    /// THE CASE THAT MATTERS MOST, and the one a screen shows: a machine whose repositories are a MIX of
    /// used and never-opened. Every used repository is above every never-opened one, and the used ones
    /// are in recency order - the owner was asked directly and chose recency over frequency.
    /// </summary>
    [Fact]
    public void ReadForMachine_MixOfUsedAndNeverOpened_PutsTheNeverOpenedOnesAtTheBottom()
    {
        var store = NewStore();
        store.Observe(TenantId.Local, Machine, "/repos/oldest", "oldest", _now.AddDays(-9));
        store.Observe(TenantId.Local, Machine, "/repos/newest", "newest", _now.AddMinutes(-2));
        store.Observe(TenantId.Local, Machine, "/repos/middle", "middle", _now.AddDays(-1));
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne, new[]
        {
            // The scan finds the used ones too - it is a scan of the disk, not of a history.
            Found("/repos/oldest", "oldest"),
            Found("/repos/newest", "newest"),
            Found("/repos/middle", "middle"),
            Found("/roots/alpha/zulu", "zulu"),
            Found("/roots/alpha/kilo", "kilo"),
        }, _now, reconcile: true);

        var served = store.ReadForMachine(TenantId.Local, Machine);

        Assert.Equal(
            new[] { "/repos/newest", "/repos/middle", "/repos/oldest", "/roots/alpha/kilo", "/roots/alpha/zulu" },
            served.Select(row => row.Path).ToArray());
        // Said on the wire rather than inferred from an absent date by whichever client is rendering it.
        Assert.Equal(new[] { false, false, false, true, true }, served.Select(row => row.NeverOpened).ToArray());
        Assert.Equal(new[] { true, true, true, false, false }, served.Select(row => row.LastUsed.HasValue).ToArray());
    }

    /// <summary>
    /// THE TRAP PHASE 2 LEFT, MET HEAD ON. The order must be decided by the Gateway and not by whichever
    /// database it happens to be running on, and those two disagree: a descending sort in PostgreSQL puts
    /// NULLS FIRST, which would stand every never-opened repository at the top of every screen - the exact
    /// inversion of goal 2. No test in this repository can catch that by running a query, because every
    /// database-backed test here runs on SQLite and SQLite agrees with C#.
    ///
    /// So this test does not ask a database anything. It hands the ordering the rows in the EXACT order
    /// PostgreSQL's ORDER BY ... DESC would hand them over - nulls first, then newest to oldest - and
    /// proves the served order is the mission's, not the input's. That is the property that makes the
    /// provider irrelevant, and it can only hold while the sort is over a materialized list.
    /// </summary>
    [Fact]
    public void OrderOneList_HandedRowsInPostgresNullsFirstOrder_StillPutsNeverOpenedLast()
    {
        var postgresOrder = new List<KnownRepositoryEntity>
        {
            Row("/roots/alpha/zulu", "zulu", null),
            Row("/roots/alpha/kilo", "kilo", null),
            Row("/repos/newest", "newest", _now.AddMinutes(-2)),
            Row("/repos/middle", "middle", _now.AddDays(-1)),
            Row("/repos/oldest", "oldest", _now.AddDays(-9)),
        };

        var served = KnownRepositoryStore.OrderOneList(postgresOrder, Machine);

        Assert.Equal(
            new[] { "/repos/newest", "/repos/middle", "/repos/oldest", "/roots/alpha/kilo", "/roots/alpha/zulu" },
            served.Select(row => row.Path).ToArray());
    }

    /// <summary>
    /// The same rows, shuffled into several deliberately unhelpful orders, come back as one order. A
    /// client reading twice sees the same list, and two clients reading at once see each other's.
    /// </summary>
    [Fact]
    public void OrderOneList_WhateverOrderTheRowsArriveIn_TheServedOrderIsTheSame()
    {
        var rows = new List<KnownRepositoryEntity>
        {
            Row("/repos/newest", "newest", _now.AddMinutes(-2)),
            Row("/repos/oldest", "oldest", _now.AddDays(-9)),
            Row("/roots/alpha/kilo", "kilo", null),
            Row("/roots/alpha/zulu", "zulu", null),
        };
        var expected = KnownRepositoryStore.OrderOneList(rows, Machine).Select(row => row.Path).ToArray();

        foreach (var arrival in Permutations(rows))
        {
            Assert.Equal(expected, KnownRepositoryStore.OrderOneList(arrival, Machine)
                .Select(row => row.Path).ToArray());
        }

        // And it is the mission's order, not merely a consistent one.
        Assert.Equal(new[] { "/repos/newest", "/repos/oldest", "/roots/alpha/kilo", "/roots/alpha/zulu" }, expected);
    }

    /// <summary>
    /// Two repositories with the SAME name under different root folders - which is ordinary, a worktree
    /// checkout beside its origin - are still a total order, by path. Without the tiebreak the two
    /// never-opened rows could arrive either way round and a screen would reshuffle between reads.
    /// </summary>
    [Fact]
    public void OrderOneList_TwoNeverOpenedRepositoriesShareAName_AreOrderedByPath()
    {
        var rows = new List<KnownRepositoryEntity>
        {
            Row("/roots/beta/devthrottle", "devthrottle", null),
            Row("/roots/alpha/devthrottle", "devthrottle", null),
        };

        Assert.Equal(new[] { "/roots/alpha/devthrottle", "/roots/beta/devthrottle" },
            KnownRepositoryStore.OrderOneList(rows, Machine).Select(row => row.Path).ToArray());
    }

    /// <summary>
    /// The two halves meeting on ONE repository: a repository that is found by the scan AND has been
    /// opened is served ONCE, with its time, in the used half. This is why both halves live in one table.
    /// </summary>
    [Fact]
    public void ReadForMachine_RepositoryIsBothFoundAndUsed_IsServedOnceInTheUsedHalf()
    {
        var store = NewStore();
        var used = _now.AddHours(-3);
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/repos/alpha", "alpha") }, _now.AddHours(-4), reconcile: true);
        store.Observe(TenantId.Local, Machine, "/repos/alpha", "alpha", used);

        var row = Assert.Single(store.ReadForMachine(TenantId.Local, Machine));
        Assert.Equal("/repos/alpha", row.Path);
        Assert.Equal(used, row.LastUsed);
        Assert.False(row.NeverOpened);
    }

    /// <summary>
    /// FAILURE CASE - the Director that found them has gone away. The never-opened half is durable and
    /// machine-keyed precisely so that the list survives that, because a Director being unreachable is
    /// exactly when the Cockpit and the phone still need the list. Nothing here re-reports anything.
    /// </summary>
    [Fact]
    public void ReadForMachine_TheDirectorThatFoundThemIsGone_StillServesThem()
    {
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne, new[]
        {
            Found("/roots/alpha/one", "one"),
            Found("/roots/alpha/two", "two"),
        }, _now, reconcile: true);

        // No further push, ever. A second store over the same database is a Gateway that has restarted
        // since, with no memory of the Director at all.
        var afterRestart = new KnownRepositoryStore(_harness.Open());

        Assert.Equal(new[] { "/roots/alpha/one", "/roots/alpha/two" },
            afterRestart.ReadForMachine(TenantId.Local, Machine).Select(row => row.Path).ToArray());
    }

    /// <summary>
    /// FAILURE CASE - a machine with nothing. An empty catalog is an empty list and not an error, because
    /// a machine whose Director has never scanned and never run a session is an ordinary new machine.
    /// </summary>
    [Fact]
    public void ReadForMachine_NothingIsKnownAboutTheMachine_ServesAnEmptyList()
    {
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/alpha/one", "one") }, _now, reconcile: true);

        Assert.Empty(store.ReadForMachine(TenantId.Local, "SOME-OTHER-MACHINE"));
    }

    /// <summary>
    /// FAILURE CASE - one machine's never-opened repositories must never appear on another's list. The
    /// catalog is keyed by machine because repositories live on a machine's disk, and a path from one
    /// machine is not a path a session can be started in on another.
    /// </summary>
    [Fact]
    public void ReadForMachine_AnotherMachineWasScanned_ServesOnlyThisMachinesList()
    {
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/alpha/mine", "mine") }, _now, reconcile: true);
        store.ObserveDiscovered(TenantId.Local, "SOREN_SOUTH", DirectorTwo,
            new[] { Found("/roots/alpha/theirs", "theirs") }, _now, reconcile: true);

        Assert.Equal("/roots/alpha/mine",
            Assert.Single(store.ReadForMachine(TenantId.Local, Machine)).Path);
        Assert.Equal("/roots/alpha/theirs",
            Assert.Single(store.ReadForMachine(TenantId.Local, "SOREN_SOUTH")).Path);
    }

    /// <summary>
    /// Two Directors on one machine are ONE list, not two. Each owns the rows it reported - that is the
    /// reconciliation scope - but a screen asks about a MACHINE and gets everything on it, once.
    /// </summary>
    [Fact]
    public void ReadForMachine_TwoDirectorsOnOneMachine_ServeOneListBetweenThem()
    {
        var store = NewStore();
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorOne,
            new[] { Found("/roots/alpha/one", "one"), Found("/roots/shared/both", "both") }, _now, reconcile: true);
        store.ObserveDiscovered(TenantId.Local, Machine, DirectorTwo,
            new[] { Found("/roots/beta/two", "two"), Found("/roots/shared/both", "both") }, _now, reconcile: true);

        // "both" was reported by each of them and appears once.
        Assert.Equal(new[] { "/roots/shared/both", "/roots/alpha/one", "/roots/beta/two" },
            store.ReadForMachine(TenantId.Local, Machine).Select(row => row.Path).ToArray());
    }

    /// <summary>
    /// The same repository written under two spellings of one machine name is one entry, and the entry
    /// that has been USED wins. Path comparison decides Windows-ness from the path's own shape - the
    /// Gateway is a Linux container holding paths from Windows and macOS machines and is never the
    /// machine a path describes.
    /// </summary>
    [Fact]
    public void OrderOneList_OneRepositoryWrittenTwoWays_IsServedOnceWithItsTime()
    {
        var used = _now.AddDays(-2);
        var rows = new List<KnownRepositoryEntity>
        {
            Row(@"D:\Repos\alpha", "alpha", null, machineName: "soren_north"),
            Row("D:/Repos/alpha/", "alpha", used, machineName: "SOREN_NORTH"),
        };

        var row = Assert.Single(KnownRepositoryStore.OrderOneList(rows, Machine));
        Assert.Equal(used, row.LastUsed);
        Assert.False(row.NeverOpened);
    }

    private KnownRepositoryEntity Row(string path, string name, DateTime? lastUsed, string machineName = Machine) =>
        new()
        {
            TenantId = TenantId.Local.Value,
            MachineKey = KnownRepositoryStore.NormalizeMachineKey(machineName),
            PathKey = KnownRepositoryStore.NormalizePathKey(path),
            MachineName = machineName,
            Path = path,
            Name = name,
            LastUsedUtc = lastUsed,
        };

    /// <summary>Every arrival order of a small set of rows, so "the input order does not decide" is
    /// proved over all of them rather than over one shuffle that happened to be chosen.</summary>
    private static IEnumerable<List<KnownRepositoryEntity>> Permutations(List<KnownRepositoryEntity> rows)
    {
        if (rows.Count <= 1)
        {
            yield return rows;
            yield break;
        }
        for (var index = 0; index < rows.Count; index++)
        {
            var rest = rows.Where((_, position) => position != index).ToList();
            foreach (var tail in Permutations(rest))
                yield return new List<KnownRepositoryEntity> { rows[index] }.Concat(tail).ToList();
        }
    }
}
