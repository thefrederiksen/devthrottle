using System.Security.Claims;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// THE GATEWAY'S HALF of "the registry reaches the Gateway" (the one-repository-list mission).
///
/// A Director's repository push now carries two kinds of row: the ones its root-folder scan MEASURED,
/// and the ones that exist only in the machine's hand-built registered list, which no scan ever reached.
/// The second kind is marked <see cref="RepoStatusDto.StatusNotComputed"/> and carries a path and a name
/// and nothing else.
///
/// <c>DirectorHub.PushRepoSnapshot</c> is where that distinction is acted on, and these drive it through
/// the real hub against the three real observers: the catalog gets every row, and the two observers whose
/// job is to report STATUS never see a row that has none. That second half is the one worth a test,
/// because its failure mode is silent - the morning report would simply start carrying measurements
/// nobody took.
/// </summary>
public sealed class TheRegistryReachesTheCatalogTests : IDisposable
{
    private const string DirectorId = "director-with-a-hand-built-list";
    private const string Machine = "SOREN_NORTH";
    private const string Connection = "connection-1";

    private readonly string _tempDir;
    private readonly GatewayDbTestHarness _harness = new();
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _sessions = new();
    private readonly GatewayInputStatsAggregator _inputStats;
    private readonly PushedRepositoryStore _statusStore = new();
    private readonly RepoHistoryStore _driftHistory;
    private readonly string _driftHistoryPath;
    private readonly KnownRepositoryStore _catalog;
    private readonly DateTime _now = new(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc);

    public TheRegistryReachesTheCatalogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ccd-registry-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _registry = new DirectorRegistry(_tempDir);
        _inputStats = new GatewayInputStatsAggregator(Path.Combine(_tempDir, "gateway-stats.db"));
        _driftHistoryPath = Path.Combine(_tempDir, "repo-history.jsonl");
        _driftHistory = new RepoHistoryStore(_driftHistoryPath);
        _catalog = new KnownRepositoryStore(_harness.Open());
    }

    public void Dispose()
    {
        _registry.Dispose();
        _harness.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception) { /* best-effort temp cleanup */ }
    }

    private static CcDirector.Gateway.Tenancy.HostedTenantBoundary SelfHostBoundary() =>
        new(new SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry());

    /// <summary>A bound hub with all three observers on the accepted push, as GatewayHost wires them.</summary>
    private DirectorHub NewBoundHub()
    {
        var hub = new DirectorHub(_sessions, _registry, InputStatsHandle.Available(_inputStats),
            new GatewayStreamRegistry(), SelfHostBoundary(),
            repositoryStore: _statusStore,
            repoHistory: _driftHistory,
            // The machine name comes from the Director REGISTRATION, which is what the read side looks
            // rows up by - never from anything in the payload.
            discoveredRepositories: new DiscoveredRepositoryObserver(_catalog, (_, _) => Machine))
        { Context = new FakeCallerContext(Connection) };
        hub.Hello(new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
        return hub;
    }

    /// <summary>A repository the scan measured.</summary>
    private static RepoStatusDto Measured(string path, string name, int uncommitted = 0) => new()
    {
        DirectorId = DirectorId,
        MachineName = "a-name-only-the-payload-uses",
        Path = path,
        Name = name,
        Branch = "main",
        IsClean = uncommitted == 0,
        UncommittedCount = uncommitted,
        WorktreeCount = 2,
    };

    /// <summary>A repository from the hand-built registered list, carrying identity and nothing else.</summary>
    private static RepoStatusDto IdentityOnly(string path, string name) => new()
    {
        DirectorId = DirectorId,
        MachineName = "a-name-only-the-payload-uses",
        Path = path,
        Name = name,
        StatusNotComputed = true,
    };

    private List<KnownRepositoryEntity> CatalogRows()
    {
        using var context = _harness.Open().CreateContext(TenantId.Local);
        return context.KnownRepositories.OrderBy(row => row.Path).ToList();
    }

    private IReadOnlyList<RepoStatusDto> StatusRows()
        => _statusStore.TryGetFresh(TenantId.Local, DirectorId, TimeSpan.FromHours(1))?.Repositories
           ?? Array.Empty<RepoStatusDto>();

    private string DriftHistoryFile()
        => File.Exists(_driftHistoryPath) ? File.ReadAllText(_driftHistoryPath) : "";

    // ---------- THE FLOW ----------

    [Fact]
    public void PushRepoSnapshot_AHandAddedRepositoryNoScanEverReached_IsHeldInTheCatalogAsNeverOpened()
    {
        NewBoundHub().PushRepoSnapshot(1, new[] { IdentityOnly(@"D:\ReposFred\hand-added", "hand-added") });

        var row = Assert.Single(CatalogRows());
        Assert.Equal(@"D:\ReposFred\hand-added", row.Path);
        Assert.Equal("hand-added", row.Name);
        Assert.Null(row.LastUsedUtc);                    // never opened - it sorts beneath everything used
        Assert.Equal(DirectorId, row.DiscoveredByDirectorId);
        Assert.Equal(Machine, row.MachineName);          // the registration's name, not the payload's
    }

    [Fact]
    public void PushRepoSnapshot_BothKindsOfRow_AllReachTheCatalog()
    {
        NewBoundHub().PushRepoSnapshot(1, new[]
        {
            Measured("/roots/work/alpha", "alpha"),
            IdentityOnly("/elsewhere/bravo", "bravo"),
        });

        Assert.Equal(new[] { "/elsewhere/bravo", "/roots/work/alpha" }, CatalogRows().Select(row => row.Path));
    }

    // ---------- THE FAILURE CASE THAT WOULD BE SILENT: A FABRICATED MEASUREMENT ----------

    [Fact]
    public void PushRepoSnapshot_AnIdentityOnlyRow_NeverReachesTheStoreThatReportsRepositoryStatus()
    {
        // GET /repositories and GET /worktrees are served from here. An identity-only row would appear
        // on both with a blank branch and zero worktrees - a repository the product had never looked at,
        // presented beside ones it had measured, with no way for a reader to tell them apart.
        NewBoundHub().PushRepoSnapshot(1, new[]
        {
            Measured("/roots/work/alpha", "alpha"),
            IdentityOnly("/elsewhere/bravo", "bravo"),
        });

        var served = Assert.Single(StatusRows());
        Assert.Equal("/roots/work/alpha", served.Path);
    }

    [Fact]
    public void PushRepoSnapshot_AnIdentityOnlyRow_NeverBecomesADailyDriftRow()
    {
        // The morning report reads these rows. A repository nobody measured would be recorded as zero
        // uncommitted files, zero commits behind main and zero worktrees - three findings the product
        // would have made up about a repository it has never read.
        NewBoundHub().PushRepoSnapshot(1, new[]
        {
            Measured("/roots/work/alpha", "alpha", uncommitted: 4),
            IdentityOnly("/elsewhere/bravo", "bravo"),
        });

        var written = DriftHistoryFile();
        Assert.Contains("/roots/work/alpha", written);
        Assert.DoesNotContain("/elsewhere/bravo", written);
    }

    [Fact]
    public void PushRepoSnapshot_NothingButIdentityOnlyRows_StillLeavesTheStatusSurfacesEmpty()
    {
        // A machine with a hand-built list and no watched folders at all. It has a real repository list
        // and NO status to report, and those two facts must not be confused for each other.
        NewBoundHub().PushRepoSnapshot(1, new[] { IdentityOnly("/elsewhere/bravo", "bravo") });

        Assert.Empty(StatusRows());
        Assert.DoesNotContain("/elsewhere/bravo", DriftHistoryFile());
        Assert.Single(CatalogRows());
    }

    [Fact]
    public void PushRepoSnapshot_APushOfMeasuredRowsAlone_IsUntouchedByAnyOfThis()
    {
        // An older Director sets the flag on nothing, and every surface must behave exactly as before.
        NewBoundHub().PushRepoSnapshot(1, new[]
        {
            Measured("/roots/work/alpha", "alpha", uncommitted: 4),
            Measured("/roots/work/charlie", "charlie"),
        });

        Assert.Equal(2, StatusRows().Count);
        Assert.Contains("/roots/work/charlie", DriftHistoryFile());
        Assert.Equal(2, CatalogRows().Count);
    }

    // ---------- ONE SOURCE MUST NOT DELETE THE OTHER'S ROWS ----------

    [Fact]
    public void PushRepoSnapshot_ARepositoryLeavesTheRegisteredList_TakesOnlyItsOwnRowAndLeavesTheScansAlone()
    {
        var hub = NewBoundHub();
        hub.PushRepoSnapshot(1, new[]
        {
            Measured("/roots/work/alpha", "alpha"),
            IdentityOnly("/elsewhere/bravo", "bravo"),
        });
        Assert.Equal(2, CatalogRows().Count);

        // The user removes the hand-added one from the Director's list. The next push is the Director's
        // whole view again, minus that entry.
        hub.PushRepoSnapshot(2, new[] { Measured("/roots/work/alpha", "alpha") });

        var row = Assert.Single(CatalogRows());
        Assert.Equal("/roots/work/alpha", row.Path);
    }

    [Fact]
    public void PushRepoSnapshot_AWatchedFolderIsUnwatched_LeavesAHandAddedRepositoryWhereItWas()
    {
        var hub = NewBoundHub();
        hub.PushRepoSnapshot(1, new[]
        {
            Measured("/roots/work/alpha", "alpha"),
            IdentityOnly("/elsewhere/bravo", "bravo"),
        });

        hub.PushRepoSnapshot(2, new[] { IdentityOnly("/elsewhere/bravo", "bravo") });

        var row = Assert.Single(CatalogRows());
        Assert.Equal("/elsewhere/bravo", row.Path);
    }

    // ---------- A REPOSITORY THAT HAS BEEN USED KEEPS ITS TIME ----------

    [Fact]
    public void PushRepoSnapshot_AHandAddedRepositoryThatHasBeenUsed_KeepsItsLastUsedTime()
    {
        // The Gateway saw a session start in this repository, so the catalog already holds it with a
        // time. The Director's registered list then names the same folder. The push must not blank,
        // re-stamp or duplicate it - the used half is untouchable from the discovered side.
        var used = new DateTime(2026, 9, 18, 14, 30, 0, DateTimeKind.Utc);
        _catalog.Observe(TenantId.Local, Machine, @"D:\ReposFred\hand-added", "hand-added", used);

        NewBoundHub().PushRepoSnapshot(1, new[] { IdentityOnly(@"D:\ReposFred\hand-added", "hand-added") });

        var row = Assert.Single(CatalogRows());
        Assert.Equal(used, row.LastUsedUtc);
        Assert.Equal("hand-added", row.Name);
    }

    [Fact]
    public void PushRepoSnapshot_AUsedRepositoryDropsOutOfTheRegisteredList_IsNotRemovedFromTheCatalog()
    {
        // Reconciliation removes never-opened rows only. A repository somebody has actually worked in
        // does not stop existing because it was taken off one Director's list.
        var used = new DateTime(2026, 9, 18, 14, 30, 0, DateTimeKind.Utc);
        _catalog.Observe(TenantId.Local, Machine, "/elsewhere/bravo", "bravo", used);

        var hub = NewBoundHub();
        hub.PushRepoSnapshot(1, new[] { Measured("/roots/work/alpha", "alpha"), IdentityOnly("/elsewhere/bravo", "bravo") });
        hub.PushRepoSnapshot(2, new[] { Measured("/roots/work/alpha", "alpha") });

        var rows = CatalogRows();
        Assert.Equal(2, rows.Count);
        Assert.Equal(used, Assert.Single(rows, row => row.Path == "/elsewhere/bravo").LastUsedUtc);
    }

    [Fact]
    public void PushRepoSnapshot_TheSameRepositoryWrittenTwoWaysByTheTwoSources_IsOneCatalogRow()
    {
        // The Director de-duplicates before it pushes, but the Gateway is not allowed to depend on that:
        // it keys by its own path rule, which decides Windows-ness from the path's own shape because the
        // Gateway is a Linux container holding paths from Windows and macOS machines.
        NewBoundHub().PushRepoSnapshot(1, new[]
        {
            Measured(@"D:\ReposFred\alpha", "alpha"),
            IdentityOnly(@"d:\reposfred\alpha", "alpha"),
        });

        Assert.Single(CatalogRows());
    }

    private sealed class FakeCallerContext : HubCallerContext
    {
        public FakeCallerContext(string connectionId)
        {
            ConnectionId = connectionId;
            // The self-host boundary resolves Local only for a connection that has an HttpContext, as a
            // real SignalR negotiate does.
            Features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(
                new HttpContextFeatureImpl { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() });
        }

        private sealed class HttpContextFeatureImpl : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
        {
            public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
