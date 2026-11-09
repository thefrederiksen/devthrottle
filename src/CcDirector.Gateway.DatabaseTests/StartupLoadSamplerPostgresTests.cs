using System.Text.RegularExpressions;
using CcDirector.Gateway.Diagnostics;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The deploy outage sampler against REAL PostgreSQL: the database pool numbers in its line come from Npgsql's
/// own meter, and this proves the instrument names and tags it listens for are the ones the library actually
/// emits - a pool with a connection open reads used=1, a released one idle=1, and the maximum is the pool's own.
/// If an Npgsql upgrade renamed an instrument, the line would quietly print "-" for it; this is what goes red.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1,
/// against the throwaway PostgreSQL that run built, and reports SKIPPED anywhere else.
/// </summary>
public sealed class StartupLoadSamplerPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_STATS_CONNECTION";
    private static readonly DateTime Now = new(2026, 11, 9, 2, 0, 0, DateTimeKind.Utc);

    private static string Connection =>
        Environment.GetEnvironmentVariable(ConnectionEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionEnvVar} is not set.");

    [RequiresPostgresFact]
    public async Task FormatLine_OpenConnection_ReadsThePoolFromNpgsqlsOwnMeter()
    {
        using var sampler = new StartupLoadSampler();
        // A pool of its own, with a maximum no other pool in the process has, so its group is unambiguous.
        var builder = new NpgsqlConnectionStringBuilder(Connection) { MaxPoolSize = 7 };
        const string poolName = "startup-load-sampler-test";
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString) { Name = poolName };
        await using var dataSource = dataSourceBuilder.Build();
        var label = Regex.Escape(StartupLoadSampler.PoolLabel(poolName));

        string whileOpen;
        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            whileOpen = sampler.FormatLine(TimeSpan.Zero, Now);
        }
        var afterRelease = sampler.FormatLine(TimeSpan.FromSeconds(2), Now.AddSeconds(2));

        Assert.Matches($@" db\[{label}\] used=1 idle=0 max=7 ", whileOpen);
        Assert.Matches($@" db\[{label}\] used=0 idle=1 max=7 ", afterRelease);
        Assert.DoesNotContain(builder.Host!, whileOpen);

        // Every instrument the line reads is one the library really publishes.
        foreach (var name in new[]
                 {
                     "db.client.connections.usage", "db.client.connections.max", "db.client.connections.pending_requests",
                     "db.client.connections.timeouts", "db.client.commands.executing",
                 })
            Assert.Contains(name, sampler.ListenedInstruments);
    }
}
