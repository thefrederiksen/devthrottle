using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// A DATABASE ALREADY AT THE REJECTED ROUND-THREE STATE UPGRADES CLEANLY TO THE TIP CHAIN.
///
/// WHY THIS EXISTS. The tenant guard was rewritten three times, and twice a superseded migration was
/// COLLAPSED out of the chain rather than left in it. Collapsing is safe here for one reason and one
/// reason only - those migrations have never existed anywhere but an unmerged branch, so no database can
/// carry them in its history. That argument is about REACHABILITY, and it is not a substitute for showing
/// the transition works.
///
/// One database CAN be at the rejected state: a rig that a previous run of this branch migrated. That is
/// the last unproven edge on this work, and this fact closes it. Round three proved the analogous case for
/// an earlier collapse; nobody has proved this one.
///
/// THIS IS NOT A RECONSTRUCTION OF THAT DATABASE, AND IT CANNOT BE ONE. Four versions of this comment
/// tried to describe it as one, each more carefully than the last, and the attempt was doomed from the
/// start for a structural reason rather than through insufficient care: every predecessor row in the
/// history here is written by the CURRENT assembly, so the table carries today's product version no matter
/// what is done about the one fabricated row. A fixture that runs today's code cannot produce a database
/// that yesterday's code wrote.
///
/// WHAT IT ACTUALLY PROVES, which is narrower and is still worth having: the tip chain tolerates a history
/// that NAMES the rejected migration id. That is the property the repository's migrations README warns
/// about - a history row naming an id the chain no longer contains can make Entity Framework treat a
/// migration as pending and re-run its <c>Up()</c> against a schema that already exists - and it is a real
/// regression guard, cheap enough to run on every gate.
///
/// So the schema is wound back to the round-three predicate under the round-three constraint names, and
/// the history is made to name <c>20260801200050_TenantMustContainANonWhitespaceCharacter</c>.
///
/// THE PRODUCT VERSION IS NOT HISTORICAL AND IS NOT ASSERTED. The real rejected assembly recorded 9.0.2;
/// this fixture records whatever today's assembly writes. Nothing here checks it against the historical
/// value, because nothing here could. It is decoration, and it is not evidence of anything.
///
/// WHERE THE REAL EVIDENCE IS. An independent inspection built the genuine c5089c5f7 migration assembly,
/// applied it from nothing, and upgraded it to tip with NO reconstruction of any kind - and it worked.
/// That is the proof that the transition is sound. This fact is the regression guard that keeps it sound;
/// it is not the proof, and it should not be cited as one.
/// </summary>
public sealed class TheRejectedChainUpgradesToTipTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_STATS_CONNECTION";

    /// <summary>The leaf migration of the REJECTED round-three chain, recovered from git history at
    /// c5089c5f7. The tip chain does not contain this id.</summary>
    private const string RejectedLeafMigrationId = "20260801200050_TenantMustContainANonWhitespaceCharacter";

    /// <summary>The migration immediately BEFORE the rejected leaf, and the last one the rejected chain
    /// shares with tip. Migrating to this target reproduces the real pre-rejected schema, names and all.</summary>
    private const string MigrationBeforeTheRejectedLeaf = "20260801170503_TenantRequiredNoDefaultAndNonEmpty";

    /// <summary>The round-three predicate, verbatim. It accepts U+00A0, which is the defect.</summary>
    private const string RejectedPredicate = "\"tenant\" ~ '[^[:space:]]'";

    private static bool RigIsAbsent => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar));

    private sealed class RequiresPostgresStatsFactAttribute : FactAttribute
    {
        public RequiresPostgresStatsFactAttribute()
        {
            if (RigIsAbsent)
                Skip = $"Set {ConnectionEnvVar} (scripts\\pg-stats-proof-rig.ps1 -Verb up) to prove the " +
                       "rejected chain upgrades to tip.";
        }
    }

    private static string Connection =>
        Environment.GetEnvironmentVariable(ConnectionEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionEnvVar} is not set.");

    private readonly ITestOutputHelper _out;

    public TheRejectedChainUpgradesToTipTests(ITestOutputHelper output) => _out = output;

    private static DbContextOptions<GatewayStatsDbContext> Options() =>
        new DbContextOptionsBuilder<GatewayStatsDbContext>()
            .UseNpgsql(Connection, npg =>
            {
                npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                npg.MigrationsHistoryTable("__EFMigrationsHistory", GatewayStatsDbContext.PostgresSchema);
            })
            .Options;

    private static NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(Connection);
        connection.Open();
        return connection;
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [RequiresPostgresStatsFact]
    public void A_database_at_the_rejected_round_three_state_upgrades_to_tip_and_gains_the_allowlist()
    {
        var database = new NpgsqlConnectionStringBuilder(Connection).Database ?? "";
        if (!database.StartsWith("ccpg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to rebuild the schema in '{database}'.");

        var schema = GatewayStatsDbContext.PostgresSchema;

        // ---- 1. Build the rejected state by migrating TO THE MIGRATION BEFORE IT, then applying the
        //         rejected predicate over whatever that produced.
        //
        // Migrating to a NAMED TARGET rather than applying the whole chain and winding back is what makes
        // this faithful. An earlier version of this fact applied the tip chain and then re-created the
        // constraints from a computed name, which invented a state no database has been in - the tip
        // migration renames three concurrency constraints, so after a full apply the OLD names are simply
        // gone and cannot be read back out of the catalog. Stopping at the previous migration produces the
        // real pre-rejected schema, names and all, with nothing guessed.
        using (var ctx = new GatewayStatsDbContext(Options()))
        {
            // The const, not the local, so the interpolation is a compile-time constant and the
            // SQL-injection analyzer is satisfied by construction rather than suppressed.
            ctx.Database.ExecuteSqlRaw($"DROP SCHEMA IF EXISTS {GatewayStatsDbContext.PostgresSchema} CASCADE");
            ctx.GetService<IMigrator>().Migrate(MigrationBeforeTheRejectedLeaf);
        }

        using var connection = Open();

        // Every guard, read from the catalog as (table, CONSTRAINT NAME) pairs rather than from a list here.
        //
        // THE NAME MUST BE PRESERVED, not recomputed, and getting that wrong is what this fact caught first
        // time out. Recomputing it from TenantNotEmptyConstraint() re-created the concurrency constraints
        // under their CORRECTED names, so the wind-back produced a state no database has ever been in and
        // the upgrade then failed trying to drop a name that was not there. A reconstruction that
        // "improves" the state it is reconstructing is not a reconstruction.
        var guards = new List<(string Table, string Constraint)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText =
                "SELECT c.relname, k.conname FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid " +
                "JOIN pg_namespace n ON n.oid = c.relnamespace " +
                $"WHERE n.nspname = '{schema}' AND k.conname LIKE 'ck_%_tenant_not_empty'";
            using var reader = read.ExecuteReader();
            while (reader.Read()) guards.Add((reader.GetString(0), reader.GetString(1)));
        }
        Assert.NotEmpty(guards);
        _out.WriteLine($"winding {guards.Count} guards back to the rejected round-three predicate, names preserved");

        foreach (var (table, name) in guards)
        {
            Execute(connection, $"ALTER TABLE {schema}.\"{table}\" DROP CONSTRAINT \"{name}\"");
            Execute(connection, $"ALTER TABLE {schema}.\"{table}\" ADD CONSTRAINT \"{name}\" CHECK ({RejectedPredicate})");
        }

        // The history now names the REJECTED leaf, an id the tip chain does not contain, and no longer
        // names the tip leaf. That naming is the property under test; it is not a claim that the table
        // matches what the rejected chain wrote - see the class summary for why it cannot.
        Execute(connection,
            $"DELETE FROM {schema}.\"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%TenantIsAnAllowlistedSpelling'");
        // The product version for the fabricated row is taken from the rows already in the table, so the
        // history stays internally consistent instead of carrying two versions.
        //
        // THAT IS ALL IT DOES, AND THE PREVIOUS COMMENT HERE OVERSOLD IT. It said a further drift was
        // "unexpressible". It is not: this reads whatever the current assembly wrote and copies it, so if a
        // future package made that value change, this would faithfully copy the new wrong thing and the
        // one-distinct-value check below would still pass. The safeguard cannot detect the error it was
        // written to discuss - it proves internal uniformity and nothing else.
        //
        // The value is not asserted against the historical one because it cannot be. See the class summary:
        // the fixture proves the tip chain tolerates a history NAMING the rejected id, and the product
        // version is decoration.
        string productVersion;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = $"SELECT DISTINCT \"ProductVersion\" FROM {schema}.\"__EFMigrationsHistory\"";
            var versions = new List<string>();
            using (var reader = read.ExecuteReader())
                while (reader.Read()) versions.Add(reader.GetString(0));

            // One value, or the table is incoherent before the fabricated row is added.
            productVersion = Assert.Single(versions);
        }
        _out.WriteLine($"history rows written by this assembly report ProductVersion {productVersion}; " +
                       "the fabricated row will carry the same value");

        Execute(connection,
            $"INSERT INTO {schema}.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
            $"VALUES ('{RejectedLeafMigrationId}', '{productVersion}')");

        // And the table is still internally consistent afterwards - the fabricated row did not introduce a
        // second version. Internal consistency only; it says nothing about the historical value.
        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"SELECT COUNT(DISTINCT \"ProductVersion\") FROM {schema}.\"__EFMigrationsHistory\"";
            Assert.Equal(1L, Convert.ToInt64(check.ExecuteScalar()));
        }

        // ---- 2. THE STATE IS REAL: the rejected predicate really does accept the character that defeated
        //         it. Without this the upgrade below could be fixing a state that was never broken.
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText =
                $"INSERT INTO {schema}.stat_delta " +
                "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
                "VALUES (chr(160), '2026-08-01T12', 's-nbsp', 'typed', 'desktop', false, 1, false, 1, 1)";
            probe.ExecuteNonQuery();
        }
        _out.WriteLine("confirmed: at the rejected state, U+00A0 is accepted as a tenant");

        // The row has to go before the upgrade - the tip constraint is validated against existing rows, so
        // leaving it would make the migration fail for the right reason at the wrong moment, and this fact
        // is about the TRANSITION, not about what to do with already-corrupt data.
        Execute(connection, $"DELETE FROM {schema}.stat_delta WHERE session_id = 's-nbsp'");

        // ---- 3. THE UPGRADE. This is the whole test.
        using (var ctx = new GatewayStatsDbContext(Options()))
        {
            var pending = ctx.Database.GetPendingMigrations().ToList();
            _out.WriteLine("pending at the rejected state: " + string.Join(", ", pending));
            ctx.Database.Migrate();
        }

        // ---- 4. THE RESULT: the allowlist is in force, and the row that defeated round three is refused.
        using (var refused = connection.CreateCommand())
        {
            refused.CommandText =
                $"INSERT INTO {schema}.stat_delta " +
                "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
                "VALUES (chr(160), '2026-08-01T12', 's-nbsp-2', 'typed', 'desktop', false, 1, false, 1, 1)";
            var thrown = Assert.ThrowsAny<PostgresException>(() => refused.ExecuteNonQuery());
            Assert.Equal("23514", thrown.SqlState);
            _out.WriteLine($"after the upgrade, U+00A0 is refused: {thrown.ConstraintName}");
        }

        // And a legitimate tenant still stores, so the upgrade did not simply seize the table shut.
        using (var accepted = connection.CreateCommand())
        {
            accepted.CommandText =
                $"INSERT INTO {schema}.stat_delta " +
                "(tenant, hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, turns, chars) " +
                "VALUES ('local', '2026-08-01T12', 's-after-upgrade', 'typed', 'desktop', false, 1, false, 2, 20)";
            Assert.Equal(1, accepted.ExecuteNonQuery());
        }
    }
}
