using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// THE HOSTED STATISTICS SCHEMA REFUSES A ROW THAT NAMES NO OWNER - both ways of failing to name one.
///
/// WHY THIS IS A SCHEMA RULE AND NOT A CODE REVIEW. Every write path in the statistics store passes a
/// tenant today; that was checked, and it is true. It is also not the point. On a shared hosted schema the
/// question is not "does the current code remember" but "what happens to the next writer that forgets" -
/// and the answer was: the row is accepted and silently filed under somebody. One customer's numbers land
/// in another customer's partition, quietly, with nothing to notice.
///
/// TWO HOLES, AND CLOSING ONE WOULD HAVE BEEN HALF A FIX:
///
///  1. THE OMITTED COLUMN. The hosted PostgreSQL tables were created with <c>DEFAULT 'local'</c>, carried
///     over from the SQLite schema where it is harmless because self-host has exactly one tenant. On the
///     multi-tenant schema it is fail-open: an INSERT that never mentions the tenant column gets Local.
///     Removed - on PostgreSQL only, because ripping it out of SQLite would force a table rebuild on every
///     statistics file already on disk to buy nothing.
///  2. THE EMPTY STRING. Dropping the default alone does not close it. The CLR property initialises to
///     <c>""</c>, so a write that forgets to set the tenant does not send NULL and hit the missing default -
///     it sends an empty string, which is a perfectly valid value naming nobody. A check constraint refuses
///     it.
///
/// Unattributed is unattributed either way, so the database refuses both rather than making one of them
/// merely harder.
///
/// Gated on <c>CC_GATEWAY_TEST_PG_STATS_CONNECTION</c> - the restricted-role rig connection - because the
/// constraint being proved is a PostgreSQL one and SQLite deliberately still has the default.
/// </summary>
public sealed class HostedSchemaRefusesAnUnownedRowTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_STATS_CONNECTION";

    private const string SkipReason =
        "Set " + ConnectionEnvVar + " (scripts\\pg-stats-proof-rig.ps1 -Verb up) to prove the hosted schema " +
        "refuses an unowned row.";

    private static bool RigIsAbsent => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar));

    private sealed class RequiresPostgresStatsFactAttribute : FactAttribute
    {
        public RequiresPostgresStatsFactAttribute()
        {
            if (RigIsAbsent) Skip = SkipReason;
        }
    }

    /// <summary>
    /// THE THEORY EQUIVALENT, AND IT HAS TO EXIST RATHER THAN BE A RUNTIME CHECK INSIDE THE METHOD.
    ///
    /// The positive controls started life as a plain <c>[Theory]</c> with an early return at the top of the
    /// method when the rig was absent. That does not work, and the way it fails is worth understanding
    /// because it is not obvious from reading the method: xUnit does not construct a test class for a fact
    /// it has already decided to SKIP, but a fact with no Skip is constructed before its body runs - and
    /// this class's CONSTRUCTOR calls <c>Reset()</c>, which needs the connection. So the constructor threw
    /// before the method could decline, and the three cases FAILED instead of skipping.
    ///
    /// That is not a private problem. It turns every ordinary Gateway run on this machine - every session,
    /// not just this mission's - red by three tests, and it contradicts the contract the mission's own
    /// baseline document records: the hosted database facts are OPTIONAL locally, and W1's stronger gate
    /// separately requires them with the rig up.
    ///
    /// The gate therefore has to sit where the fixture cannot get in front of it: at DISCOVERY, in the
    /// attribute, exactly like the Fact version above.
    /// </summary>
    private sealed class RequiresPostgresStatsTheoryAttribute : TheoryAttribute
    {
        public RequiresPostgresStatsTheoryAttribute()
        {
            if (RigIsAbsent) Skip = SkipReason;
        }
    }

    private static string Connection =>
        Environment.GetEnvironmentVariable(ConnectionEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionEnvVar} is not set.");

    private readonly ITestOutputHelper _out;

    public HostedSchemaRefusesAnUnownedRowTests(ITestOutputHelper output)
    {
        _out = output;
        Reset();
    }

    /// <summary>Build the schema from the MIGRATION CHAIN, not from the model. The constraint being proved
    /// has to be one a real hosted deploy would get, and a hosted deploy gets whatever the migrations
    /// create - so a schema built from the model could pass here while the shipped chain never applied it.</summary>
    private static void Reset()
    {
        var database = new NpgsqlConnectionStringBuilder(Connection).Database ?? "";
        if (!database.StartsWith("ccpg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to drop the statistics schema in '{database}': it must be a throwaway rig database " +
                "whose name begins with 'ccpg'.");

        // CONFIGURED THE WAY GatewayStatsStore CONFIGURES IT, and both lines are load-bearing. A plain
        // UseNpgsql leaves Entity Framework looking for migrations in the assembly that holds the context -
        // which is the SQLITE chain - so Migrate() runs the wrong chain against PostgreSQL and stops with
        // "the model has pending changes". That is what happened on the first run of this file, and the
        // failure was the test's, not the schema's.
        var options = new DbContextOptionsBuilder<GatewayStatsDbContext>()
            .UseNpgsql(Connection, npg =>
            {
                npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                npg.MigrationsHistoryTable("__EFMigrationsHistory", GatewayStatsDbContext.PostgresSchema);
            })
            .Options;
        using var ctx = new GatewayStatsDbContext(options);
        ctx.Database.ExecuteSqlRaw($"DROP SCHEMA IF EXISTS {GatewayStatsDbContext.PostgresSchema} CASCADE");
        ctx.Database.Migrate();
    }

    private static NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(Connection);
        connection.Open();
        return connection;
    }

    /// <summary>A row with every required column EXCEPT the tenant. Before this change the missing default
    /// filed it under Local; now there is no default and the column is NOT NULL, so the insert fails.</summary>
    [RequiresPostgresStatsFact]
    public void An_insert_that_omits_the_tenant_is_refused()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {GatewayStatsDbContext.PostgresSchema}.stat_delta " +
            "(hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
            "VALUES ('2026-08-01T12', 's-no-tenant', 'typed', 'desktop', false, 1, false, 1, 1)";

        var thrown = Assert.ThrowsAny<PostgresException>(() => cmd.ExecuteNonQuery());
        _out.WriteLine($"OMITTED TENANT refused: {thrown.SqlState} {thrown.MessageText}");

        // 23502 is not_null_violation - the row was refused for the stated reason, not for an unrelated one
        // like a missing column, which would make this pass while proving nothing.
        Assert.Equal("23502", thrown.SqlState);
    }

    /// <summary>The hole dropping the default does NOT close: the tenant is present and empty, which is
    /// what a forgotten assignment through the CLR model actually sends.</summary>
    [RequiresPostgresStatsFact]
    public void An_insert_whose_tenant_is_empty_is_refused()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {GatewayStatsDbContext.PostgresSchema}.stat_delta " +
            "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
            "VALUES ('', '2026-08-01T12', 's-empty-tenant', 'typed', 'desktop', false, 1, false, 1, 1)";

        var thrown = Assert.ThrowsAny<PostgresException>(() => cmd.ExecuteNonQuery());
        _out.WriteLine($"EMPTY TENANT refused: {thrown.SqlState} {thrown.MessageText}");

        // 23514 is check_violation, and the constraint is named, so this cannot pass on some other check.
        Assert.Equal("23514", thrown.SqlState);
        Assert.Equal(GatewayStatsDbContext.TenantNotEmptyConstraint("stat_delta"), thrown.ConstraintName);
    }

    /// <summary>
    /// THE THIRD SPELLING OF "NAMES NOBODY", and the one that got through.
    ///
    /// The first version of the constraint was <c>tenant &lt;&gt; ''</c>, which refuses the empty string and
    /// nothing else. An inspector stood up the restricted-role rig, applied this exact chain, inserted a
    /// tenant of THREE SPACES and read it back at length 3. That is not a hypothetical: whitespace was
    /// storable, and <see cref="TenantId"/> itself rejects a whitespace tenant, so the schema was enforcing
    /// a spelling of the invariant rather than the invariant.
    ///
    /// The two facts above tested only the two spellings that already worked, which is why the hole
    /// survived. This one exists because the fix is not "add btrim" - it is "assert the case that got
    /// through".
    /// </summary>
    [RequiresPostgresStatsFact]
    public void An_insert_whose_tenant_is_only_whitespace_is_refused()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {GatewayStatsDbContext.PostgresSchema}.stat_delta " +
            "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
            "VALUES ('   ', '2026-08-01T12', 's-whitespace-tenant', 'typed', 'desktop', false, 1, false, 1, 1)";

        var thrown = Assert.ThrowsAny<PostgresException>(() => cmd.ExecuteNonQuery());
        _out.WriteLine($"WHITESPACE TENANT refused: {thrown.SqlState} {thrown.MessageText}");

        Assert.Equal("23514", thrown.SqlState);
        Assert.Equal(GatewayStatsDbContext.TenantNotEmptyConstraint("stat_delta"), thrown.ConstraintName);

        // And nothing was stored under it - the refusal is the whole point, not a warning.
        using var count = connection.CreateCommand();
        count.CommandText =
            $"SELECT COUNT(*) FROM {GatewayStatsDbContext.PostgresSchema}.stat_delta WHERE session_id = 's-whitespace-tenant'";
        Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar()));
    }

    /// <summary>
    /// EVERY CHARACTER .NET CALLS WHITESPACE, walked one at a time - not a hand-picked handful.
    ///
    /// This is the fact the previous three predicates each failed, and hand-picking is exactly how they
    /// survived. `tenant &lt;&gt; ''` passed a space. `btrim(tenant) &lt;&gt; ''` passed a tab. And
    /// `tenant ~ '[^[:space:]]'` passed FOUR characters that .NET calls whitespace and POSIX does not -
    /// U+0085, U+00A0, U+2007, U+202F - one of which an inspector inserted as a real row's tenant and read
    /// back at length 1.
    ///
    /// So the test enumerates the set from <see cref="char.IsWhiteSpace(char)"/> itself rather than from a
    /// list somebody typed. If a future runtime adds a character to that set, this fact walks it too,
    /// without anyone remembering to come back. The allowlist predicate refuses all of them as a side
    /// effect of not allowing them, which is why an allowlist is the right shape and a denylist never was.
    /// </summary>
    [RequiresPostgresStatsFact]
    public void Every_character_dotnet_calls_whitespace_is_refused_as_a_tenant()
    {
        var whitespace = Enumerable.Range(0, char.MaxValue + 1)
            .Select(c => (char)c)
            .Where(char.IsWhiteSpace)
            .ToList();

        // The enumeration must be REAL. A bug that produced an empty list would make this fact pass
        // having asserted nothing at all - the same shape of vacuous green the controls exist to stop.
        Assert.True(whitespace.Count >= 20,
            $"expected .NET to report around 25 whitespace characters, got {whitespace.Count}");
        _out.WriteLine($"walking {whitespace.Count} whitespace characters: " +
                       string.Join(" ", whitespace.Select(c => "U+" + ((int)c).ToString("X4"))));

        using var connection = Open();
        var accepted = new List<string>();

        foreach (var c in whitespace)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"INSERT INTO {GatewayStatsDbContext.PostgresSchema}.stat_delta " +
                "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
                "VALUES (@t, '2026-08-01T12', 's-ws', 'typed', 'desktop', false, 1, false, 1, 1)";
            cmd.Parameters.AddWithValue("t", c.ToString());

            try
            {
                cmd.ExecuteNonQuery();
                accepted.Add("U+" + ((int)c).ToString("X4"));
            }
            catch (PostgresException ex) when (ex.SqlState == "23514")
            {
                // Refused by the named check constraint, which is the whole point.
                Assert.Equal(GatewayStatsDbContext.TenantNotEmptyConstraint("stat_delta"), ex.ConstraintName);
            }
        }

        Assert.True(accepted.Count == 0,
            "these characters are whitespace to .NET and were STORED as a tenant: " + string.Join(", ", accepted));
    }

    /// <summary>The same walk, for a whitespace character embedded in an otherwise plausible tenant - a
    /// value that CONTAINS a legal character would satisfy any predicate asking merely whether one exists,
    /// which is what the anchored allowlist is for.</summary>
    [RequiresPostgresStatsFact]
    public void A_tenant_with_whitespace_inside_an_otherwise_legal_value_is_refused()
    {
        using var connection = Open();
        foreach (var spelling in new[] { "loc al", "local\t", " local", "local x", "local\n" })
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"INSERT INTO {GatewayStatsDbContext.PostgresSchema}.stat_delta " +
                "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
                "VALUES (@t, '2026-08-01T12', 's-inner', 'typed', 'desktop', false, 1, false, 1, 1)";
            cmd.Parameters.AddWithValue("t", spelling);

            var thrown = Assert.ThrowsAny<PostgresException>(() => cmd.ExecuteNonQuery());
            Assert.Equal("23514", thrown.SqlState);
        }
    }

    /// <summary>
    /// THE CONTROL. Without it every refusal above would pass against a table that refuses EVERY insert - a
    /// typo in the column list, a schema that never got created, a constraint that is simply wrong. A row
    /// that DOES name its owner must still be stored.
    /// </summary>
    /// <param name="tenant">EVERY SPELLING PRODUCTION ACTUALLY MINTS, and the reason the list is exactly
    /// these three is in the constraint's own derivation: TenantId.Local, TenantId.System, and a real
    /// account, which is <c>Guid.NewGuid().ToString()</c> from TenantRegistry. An allowlist is only safe
    /// if it admits everything legitimate, so refusing one of these would be a far worse defect than the
    /// one being fixed - and this is the fact that would say so.</param>
    [RequiresPostgresStatsTheory]
    [InlineData("local")]
    [InlineData("system")]
    [InlineData("9f2c1b7e-4d3a-4c5e-8b6f-0a1d2e3f4a5b")]
    public void A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored(string tenant)
    {
        // The spelling is a REAL TenantId, not merely a string this test likes the look of - so a pattern
        // that drifted away from what TenantId accepts would fail here rather than pass quietly.
        Assert.True(new TenantId(tenant).IsValid);

        using var connection = Open();
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                $"INSERT INTO {GatewayStatsDbContext.PostgresSchema}.stat_delta " +
                "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
                "VALUES (@t, '2026-08-01T12', @s, 'typed', 'desktop', false, 1, false, 3, 30)";
            insert.Parameters.AddWithValue("t", tenant);
            insert.Parameters.AddWithValue("s", "s-owned-" + tenant);
            Assert.Equal(1, insert.ExecuteNonQuery());
        }

        using var read = connection.CreateCommand();
        read.CommandText =
            $"SELECT turns FROM {GatewayStatsDbContext.PostgresSchema}.stat_delta WHERE session_id = @s";
        read.Parameters.AddWithValue("s", "s-owned-" + tenant);
        Assert.Equal(3L, Convert.ToInt64(read.ExecuteScalar()));
    }
}
