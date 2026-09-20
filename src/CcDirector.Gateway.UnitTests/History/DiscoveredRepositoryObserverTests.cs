using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The third observer on the repository snapshot a Director already pushes (the one-repository-list
/// mission, phase 2): which pushes it folds, which it refuses to reconcile from, and where it gets the
/// machine name it writes rows under.
/// </summary>
public sealed class DiscoveredRepositoryObserverTests : IDisposable
{
    private const string DirectorId = "observer-director";
    private const string RegisteredMachine = "SOREN_NORTH";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
    private readonly KnownRepositoryStore _catalog;

    /// <summary>What the Director REGISTRATION reports, which is what the read side looks rows up by.</summary>
    private string? _registeredMachine = RegisteredMachine;

    public DiscoveredRepositoryObserverTests()
    {
        _catalog = new KnownRepositoryStore(_harness.Open());
    }

    public void Dispose() => _harness.Dispose();

    private DiscoveredRepositoryObserver NewObserver() =>
        new(_catalog, (_, _) => _registeredMachine);

    private List<KnownRepositoryEntity> AllRows()
    {
        using var context = _harness.Open().CreateContext(TenantId.Local);
        return context.KnownRepositories.OrderBy(row => row.Path).ToList();
    }

    private static RepoStatusDto Pushed(string path, string name, bool provisional = false) => new()
    {
        DirectorId = DirectorId,
        // Deliberately NOT the registered machine: nothing may be written under the payload's name.
        MachineName = "a-name-only-the-payload-uses",
        Path = path,
        Name = name,
        Provisional = provisional,
    };

    [Fact]
    public void ObserveSnapshot_RepositoryUnderARootFolder_IsHeldAsFoundButNeverOpened()
    {
        NewObserver().ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one") }, _now);

        var row = Assert.Single(AllRows());
        Assert.Null(row.LastUsedUtc);
        Assert.Equal(DirectorId, row.DiscoveredByDirectorId);
        Assert.Equal("one", row.Name);
    }

    [Fact]
    public void ObserveSnapshot_MachineName_ComesFromTheRegistrationAndNotThePayload()
    {
        NewObserver().ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one") }, _now);

        // The read side resolves the owned Director and reads director.MachineName. A row written under
        // the payload's machine name would exist and no screen would ever show it. That the two ENDS agree
        // is proved through the hub and the endpoint themselves, in
        // CcDirector.Gateway.Tests.DiscoveredRepositoryTunnelProofTests.
        var row = Assert.Single(AllRows());
        Assert.Equal(RegisteredMachine, row.MachineName);
        Assert.Equal(RegisteredMachine.ToUpperInvariant(), row.MachineKey);
    }

    [Fact]
    public void ObserveSnapshot_RegistrationReportsNoMachineName_WritesNothing()
    {
        _registeredMachine = "";

        NewObserver().ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one") }, _now);

        // There is no second-best machine name to fall back on, so nothing is written at all - a row under
        // the wrong machine is worse than no row, because no screen can ever find it.
        Assert.Empty(AllRows());
    }

    [Fact]
    public void ObserveSnapshot_NoBoundDirectorId_WritesNothing()
    {
        NewObserver().ObserveSnapshot(TenantId.Local, "  ",
            new[] { Pushed("/roots/alpha/one", "one") }, _now);

        Assert.Empty(AllRows());
    }

    [Fact]
    public void ObserveSnapshot_EmptyPush_RemovesNothing()
    {
        var observer = NewObserver();
        observer.ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one") }, _now);

        // A cold start before the first live scan pushes nothing, and that is not "every repository was
        // removed".
        observer.ObserveSnapshot(TenantId.Local, DirectorId, Array.Empty<RepoStatusDto>(), _now.AddHours(2));

        Assert.Equal("/roots/alpha/one", Assert.Single(AllRows()).Path);
    }

    [Fact]
    public void ObserveSnapshot_AllProvisionalPush_RemovesNothing()
    {
        var observer = NewObserver();
        observer.ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one") }, _now);

        // A warm-cache push carries only entries the Director has not re-verified yet, and looks exactly
        // like an emptied root folder.
        observer.ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/two", "two", provisional: true) }, _now.AddHours(2));

        var row = Assert.Single(AllRows());
        Assert.Equal("/roots/alpha/one", row.Path);
    }

    [Fact]
    public void ObserveSnapshot_MixedPush_InsertsWhatItSawAndReconcilesNothing()
    {
        var observer = NewObserver();
        observer.ObserveSnapshot(TenantId.Local, DirectorId, new[]
        {
            Pushed("/roots/alpha/one", "one"),
            Pushed("/roots/alpha/two", "two"),
        }, _now);

        // Some entries verified, others still warming up: a PARTIAL view. Reconciling from it would delete
        // a repository that had simply not finished warming up yet.
        observer.ObserveSnapshot(TenantId.Local, DirectorId, new[]
        {
            Pushed("/roots/alpha/one", "one"),
            Pushed("/roots/alpha/three", "three", provisional: true),
        }, _now.AddHours(2));

        var paths = AllRows().Select(row => row.Path).ToList();
        Assert.Equal(new[] { "/roots/alpha/one", "/roots/alpha/two" }, paths);
    }

    [Fact]
    public void ObserveSnapshot_RootFolderRemoved_ReconcilesOnACompleteObservation()
    {
        var observer = NewObserver();
        observer.ObserveSnapshot(TenantId.Local, DirectorId, new[]
        {
            Pushed("/roots/alpha/one", "one"),
            Pushed("/roots/alpha/two", "two"),
        }, _now);

        observer.ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one") }, _now.AddHours(2));

        Assert.Equal("/roots/alpha/one", Assert.Single(AllRows()).Path);
    }

    [Fact]
    public void ObserveSnapshot_IdenticalRePush_CostsNothingUntilTheLastSeenStampIsDue()
    {
        var observer = NewObserver();
        var push = new[] { Pushed("/roots/alpha/one", "one") };
        observer.ObserveSnapshot(TenantId.Local, DirectorId, push, _now);

        // Delete the row behind the observer's back. An identical re-push inside the freshness interval
        // must not bring it back, because it must not reach the database at all - a Director re-pushes its
        // whole snapshot every ten seconds, and on a hosted Gateway that is every Director of every account.
        using (var context = _harness.Open().CreateContext(TenantId.Local))
        {
            context.KnownRepositories.RemoveRange(context.KnownRepositories);
            context.SaveChanges();
        }
        observer.ObserveSnapshot(TenantId.Local, DirectorId, push,
            _now.Add(KnownRepositoryStore.LastSeenFreshnessInterval).AddMinutes(-1));
        Assert.Empty(AllRows());

        // Once the stamp is due, the fold runs in full again.
        observer.ObserveSnapshot(TenantId.Local, DirectorId, push,
            _now.Add(KnownRepositoryStore.LastSeenFreshnessInterval));
        Assert.Equal("/roots/alpha/one", Assert.Single(AllRows()).Path);
    }

    [Fact]
    public void ObserveSnapshot_ChangedScan_IsFoldedImmediatelyRatherThanWaitingForTheStamp()
    {
        var observer = NewObserver();
        observer.ObserveSnapshot(TenantId.Local, DirectorId,
            new[] { Pushed("/roots/alpha/one", "one") }, _now);

        // Well inside the freshness interval: the skip is keyed on the snapshot's own content, so anything
        // this fold would write defeats it.
        observer.ObserveSnapshot(TenantId.Local, DirectorId, new[]
        {
            Pushed("/roots/alpha/one", "one"),
            Pushed("/roots/alpha/two", "two"),
        }, _now.AddSeconds(10));

        Assert.Equal(2, AllRows().Count);
    }

    [Fact]
    public void ObserveSnapshot_RepositoryThatIsUsed_KeepsItsLastUsedTime()
    {
        var used = _now.AddDays(-2);
        _catalog.Observe(TenantId.Local, RegisteredMachine, "/repos/used", "used", used);

        NewObserver().ObserveSnapshot(TenantId.Local, DirectorId, new[]
        {
            Pushed("/repos/used", "used"),
            Pushed("/roots/alpha/one", "one"),
        }, _now);

        Assert.Equal(used, AllRows().Single(row => row.Path == "/repos/used").LastUsedUtc);
        Assert.Null(AllRows().Single(row => row.Path == "/roots/alpha/one").LastUsedUtc);
    }
}
