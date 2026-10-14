using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE CONTAINMENT of the statistics store's failures, and THE TWO NAMED REASONS.
///
/// This file earns two things that a passing green would not otherwise license.
///
/// FIRST, THAT THE FAULT IS REAL. <see cref="TheSameFault_IsFatal_WhenItIsNotContained"/> runs the SAME
/// connection through the ordinary Entity Framework path with no boundary around it and watches it THROW.
/// Without that arm, every "the store reports unreachable" assertion below could pass against a store that
/// silently never attempted a connection at all - a test that has never been watched failing is decoration,
/// and this is the arm that makes the containment arm mean something.
///
/// SECOND, THAT THE TWO REASONS ARE ACTUALLY DIFFERENT. NOT CONFIGURED and UNREACHABLE are produced side by
/// side, in one test, and asserted to DIFFER from each other rather than each merely being non-empty. A
/// build that collapsed both into one generic "statistics unavailable" would satisfy every individual
/// assertion about either one; only comparing them can see it. That is the ruling: a deploy that simply
/// forgot a variable must not present identically to a database outage.
/// </summary>
public sealed class GatewayStatsStoreContainmentTests : IDisposable
{
    // A clock that stands still. The store bounds its open by eight seconds on its clock; on a busy machine the open
    // of a fresh local file has taken longer than that, and the store then reports "did not answer in time" for a
    // test that is about something else. With this clock that deadline never runs out, so every test here is
    // decided by what the store did and not by how long the machine took.
    private static readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider StoodStill = new();

    /// <summary>
    /// A PostgreSQL endpoint that is not there. Port 1 on the loopback interface: nothing listens on it, and
    /// a connection is refused immediately rather than hanging, so the failure is fast and is a genuine
    /// transport failure rather than a fabricated one. The short timeouts bound it further.
    /// </summary>
    private const string DeadPostgres =
        "Host=127.0.0.1;Port=1;Database=gateway_live;Username=gateway_app;Password=s3cret;" +
        "Timeout=2;Command Timeout=2";

    private readonly ITestOutputHelper _out;
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-stats-store-tests-" + Guid.NewGuid().ToString("N"));

    public GatewayStatsStoreContainmentTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private string SqlitePath => Path.Combine(_dir, "gateway-stats.db");

    // ================================================ the fault is real: watched failing, uncontained

    /// <summary>
    /// THE FAILING DIRECTION, WATCHED. The same unreachable statistics database, opened and migrated the
    /// ordinary way with nothing containing it, THROWS. This is what a Gateway that ran the statistics
    /// migration inside its startup gate would do: refuse to start.
    ///
    /// It is a permanent test rather than a run somebody once did, because the claim it supports is used by
    /// every other test in this file, and a claim that rests on a fixture nobody re-checks goes stale the
    /// first time the fixture stops reaching the network at all.
    /// </summary>
    [Fact]
    public void TheSameFault_IsFatal_WhenItIsNotContained()
    {
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<GatewayStatsDbContext>(o =>
            o.UseNpgsql(DeadPostgres, npg =>
            {
                npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                npg.MigrationsHistoryTable("__EFMigrationsHistory", GatewayStatsDbContext.PostgresSchema);
            }));
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<GatewayStatsDbContext>>();

        using var context = factory.CreateDbContext();

        var thrown = Assert.ThrowsAny<Exception>(() => context.Database.Migrate());

        _out.WriteLine(
            $"UNCONTAINED: {thrown.GetType().FullName}: {thrown.Message}");

        // It is a connection failure, not a mapping or a build failure that would have thrown anywhere.
        Assert.Contains(
            thrown.GetType().Name,
            new[] { "NpgsqlException", "PostgresException", "SocketException", "TimeoutException" });
    }

    // ================================================ the same fault, contained

    [Fact]
    public void StatisticsDatabaseUnreachable_DoesNotThrow_AndReportsUnreachable()
    {
        using var store = new GatewayStatsStore(Choice(statsOverride: DeadPostgres, hosted: true), StoodStill);

        Assert.False(store.IsAvailable);
        Assert.Equal(StatsStoreUnavailableReason.Unreachable, store.Availability.Reason);
        Assert.Equal("unreachable", store.Availability.ReasonCode);

        // There is NO substitute store. Not a file, not a second connection, not an empty in-memory one.
        Assert.Null(store.Factory);
        Assert.False(File.Exists(SqlitePath));

        // The failure is on the health surface in Step 1's shape, with the observer named.
        Assert.Equal(GatewayStatsStore.ObserverName, store.Health.Observer);
        Assert.Equal(1, store.Health.FailureCount);
        Assert.NotNull(store.Health.LastError);
        Assert.Null(store.Health.LastSuccessfulWrite);

        _out.WriteLine($"CONTAINED: reason={store.Availability.ReasonCode}: {store.Availability.Detail}");
    }

    /// <summary>
    /// Using an unavailable store is an explicit failure that NAMES the reason - never a context over a
    /// substitute store, and never a silently empty one.
    /// </summary>
    [Fact]
    public void CreateContext_WhenUnavailable_ThrowsAndNamesTheReason()
    {
        using var store = new GatewayStatsStore(Choice(statsOverride: DeadPostgres, hosted: true), StoodStill);

        var thrown = Assert.Throws<InvalidOperationException>(() => store.CreateContext());
        Assert.Contains("unreachable", thrown.Message, StringComparison.Ordinal);
    }

    // ================================================ THE RULING: the two reasons are distinguishable

    /// <summary>
    /// NOT CONFIGURED and UNREACHABLE, produced SIDE BY SIDE and compared to each other.
    ///
    /// The comparison is the test. Asserting only that each one is non-empty, or that each equals its own
    /// expected value, would ALSO pass against a build that had collapsed them - which is exactly the defect
    /// this ruling exists to prevent, and exactly the shape a fixture that cannot distinguish the bug from
    /// its absence has. So the assertions here are inequalities between the two states.
    /// </summary>
    [Fact]
    public void NotConfiguredAndUnreachable_AreDifferentNamedReasons()
    {
        // NOT CONFIGURED: the override is SET BUT BLANK, a real operator error.
        using var notConfigured = new GatewayStatsStore(Choice(statsOverride: "", hosted: true), StoodStill);

        // UNREACHABLE: the settings are right and the database is not there.
        using var unreachable = new GatewayStatsStore(Choice(statsOverride: DeadPostgres, hosted: true), StoodStill);

        // Both are unavailable, so "is it available" cannot tell them apart. That is the premise.
        Assert.False(notConfigured.IsAvailable);
        Assert.False(unreachable.IsAvailable);

        // The enum reason differs.
        Assert.Equal(StatsStoreUnavailableReason.NotConfigured, notConfigured.Availability.Reason);
        Assert.Equal(StatsStoreUnavailableReason.Unreachable, unreachable.Availability.Reason);
        Assert.NotEqual(notConfigured.Availability.Reason, unreachable.Availability.Reason);

        // The stable code an operator greps for, and a surface keys off, differs.
        Assert.Equal("not_configured", notConfigured.Availability.ReasonCode);
        Assert.Equal("unreachable", unreachable.Availability.ReasonCode);
        Assert.NotEqual(notConfigured.Availability.ReasonCode, unreachable.Availability.ReasonCode);

        // The operator-facing sentence differs, and each says the thing that sends somebody to the right
        // place: one names the setting to fix, the other says the setting is already right.
        Assert.NotEqual(notConfigured.Availability.Detail, unreachable.Availability.Detail);
        Assert.Contains(
            StatsConnectionSelection.StatsConnectionEnvVar,
            notConfigured.Availability.Detail,
            StringComparison.Ordinal);
        Assert.Contains(
            "rather than a missing setting", unreachable.Availability.Detail, StringComparison.Ordinal);

        _out.WriteLine($"NOT CONFIGURED: {notConfigured.Availability.ReasonCode}: {notConfigured.Availability.Detail}");
        _out.WriteLine($"UNREACHABLE:    {unreachable.Availability.ReasonCode}: {unreachable.Availability.Detail}");
    }

    // ================================================ the ordinary self-host path still works

    /// <summary>
    /// Reopening the same self-host store is the steady state, not an exception: the chain reads its own
    /// history and applies nothing. A second open that re-ran the baseline would fail on tables that already
    /// exist, so this is the assertion that the chain is genuinely tracking the file.
    /// </summary>
    [Fact]
    public void SelfHost_ReopeningTheSameFileIsTheSteadyState()
    {
        using (var first = new GatewayStatsStore(Choice(statsOverride: null, hosted: false), StoodStill))
            Assert.True(first.IsAvailable);

        using var second = new GatewayStatsStore(Choice(statsOverride: null, hosted: false), StoodStill);
        Assert.True(second.IsAvailable);
        Assert.Equal(StatsStoreUnavailableReason.None, second.Availability.Reason);
    }

    // ================================================ the health surface

    [Fact]
    public void Health_CountsDropsSeparatelyFromFailures()
    {
        using var store = new GatewayStatsStore(Choice(statsOverride: DeadPostgres, hosted: true), StoodStill);

        var failuresBefore = store.Health.FailureCount;
        store.RecordDrop();
        store.RecordDrop();

        // A drop is an observation deliberately not attempted; a failure is an attempt that went wrong.
        // Collapsing them would hide the containment doing its job.
        Assert.Equal(2, store.Health.DropCount);
        Assert.Equal(failuresBefore, store.Health.FailureCount);
    }

    private StatsConnectionChoice Choice(string? statsOverride, bool hosted) =>
        StatsConnectionSelection.Resolve(
            statsOverride: statsOverride,
            gatewayConnection: null,
            hosted: hosted,
            sqlitePath: SqlitePath);
}
