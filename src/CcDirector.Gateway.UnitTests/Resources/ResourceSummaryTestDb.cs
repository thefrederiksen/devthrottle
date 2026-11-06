using CcDirector.Core.Machine;
using CcDirector.Core.Machine.ResourceSummaries;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CcDirector.Gateway.UnitTests.Resources;

/// <summary>
/// A statistics database for the resource summary tests: a SQLite file built by RUNNING the real migration chain, so
/// these tests use the tables the migration creates rather than tables the model would create. Deleted on dispose.
/// </summary>
internal sealed class ResourceSummaryTestDb : IDisposable
{
    private readonly List<ServiceProvider> _providers = new();
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "cc-resource-summary-" + Guid.NewGuid().ToString("N") + ".db");

    public ResourceSummaryTestDb()
    {
        using var db = Factory.CreateDbContext();
        db.Database.Migrate();
    }

    private IDbContextFactory<GatewayStatsDbContext>? _factory;

    public IDbContextFactory<GatewayStatsDbContext> Factory
    {
        get
        {
            if (_factory is not null) return _factory;
            var services = new ServiceCollection();
            services.AddPooledDbContextFactory<GatewayStatsDbContext>(o =>
                o.UseSqlite(new SqliteConnectionStringBuilder { DataSource = _path, DefaultTimeout = 30 }.ToString()));
            var provider = services.BuildServiceProvider();
            _providers.Add(provider);
            _factory = provider.GetRequiredService<IDbContextFactory<GatewayStatsDbContext>>();
            return _factory;
        }
    }

    public GatewayStatsDbContext Open() => Factory.CreateDbContext();

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { /* temporary file */ }
    }
}

/// <summary>Hand-made windows, built by the Director's own builder so the shape is the real one.</summary>
internal static class ResourceWindows
{
    /// <summary>A binary gigabyte, the unit the Gateway's labels use.</summary>
    public const long Gb = 1L << 30;

    public static ResourceSummarySample Sample(DateTime at, double cpu, long usedBytes,
        ResourceLevel cpuLevel = ResourceLevel.Normal, params ResourceSummarySessionSample[] sessions) =>
        new(at, cpu, null, cpuLevel, usedBytes, 16 * Gb, null, ResourceLevel.Normal, sessions);

    /// <summary>A whole window at a steady processor and memory figure, with the given sessions in every sample.</summary>
    public static ResourceSummaryWindow Steady(DateTime start, double cpu, long usedBytes, string machine = "devlinux",
        params ResourceSummarySessionSample[] sessions) =>
        ResourceSummaryBuilder.Build(start, machine,
            Enumerable.Range(0, 60).Select(i => Sample(start.AddSeconds(5 * i), cpu, usedBytes, ResourceLevel.Normal, sessions)));

    /// <summary>A window at 20% with a 90-second red processor spike to 99% starting two minutes in.</summary>
    public static ResourceSummaryWindow WithRedSpike(DateTime start, string machine = "devlinux")
    {
        var samples = Enumerable.Range(0, 60).Select(i =>
        {
            var spike = i >= 24 && i < 42;
            return Sample(start.AddSeconds(5 * i), spike ? 99 : 20, 8 * Gb, spike ? ResourceLevel.Critical : ResourceLevel.Normal);
        });
        return ResourceSummaryBuilder.Build(start, machine, samples);
    }

    public static ResourceSummaryBatch Batch(params ResourceSummaryWindow[] windows) => new() { Windows = windows.ToList() };
}
