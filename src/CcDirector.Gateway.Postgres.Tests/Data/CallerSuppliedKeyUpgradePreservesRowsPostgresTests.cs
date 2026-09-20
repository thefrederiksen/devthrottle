using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of the upgrade proof: an actual pre-change Postgres database, with rows in it, carried
/// through this exact migration.
///
/// The SQLite sibling (<see cref="CallerSuppliedKeyUpgradePreservesRowsTests"/>) proves nothing about this
/// provider, and neither do the existing Postgres proof tests. Those migrate an EMPTY schema from nothing, so
/// they exercise SQL generation and constraint naming but never carry a single row across a primary-key
/// change. The two providers also do genuinely different things here: SQLite cannot alter a primary key in
/// place and rebuilds the whole table, while Postgres issues <c>ALTER TABLE ... DROP CONSTRAINT</c> plus
/// <c>ADD PRIMARY KEY</c> and rewrites nothing. Postgres therefore has strictly less to lose - but "strictly
/// less to lose" is a reason to expect it to pass, not evidence that it does, and reasoning from the generated
/// DDL is exactly what this test exists to replace.
///
/// GATING. Like <see cref="PostgresProviderProofTests"/>, the whole class is gated on the
/// <c>CC_GATEWAY_TEST_PG_CONNECTION</c> environment variable and reports SKIPPED when it is unset, so the
/// ordinary SQLite test run is untouched. Skipped is not passed: with no server configured this test makes no
/// claim at all rather than a false one. Point the variable at a throwaway Postgres to run it.
///
/// The test must start from a database with NO gateway schema, because it migrates forward from a specific
/// earlier migration. It therefore drops first, and <see cref="GuardThrowawayDatabase"/> refuses to drop
/// anything whose database name does not begin with the dedicated <c>ccpg</c> prefix - the same guard, and the
/// same reasoning, as the existing Postgres proofs.
/// </summary>
public sealed class CallerSuppliedKeyUpgradePreservesRowsPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";

    /// <summary>The migration immediately before the change under test - the "pre-change database".</summary>
    private const string MigrationBeforeTheChange = "CompositeTenantKeys";

    /// <summary>The change under test.</summary>
    private const string MigrationUnderTest = "CallerSuppliedKeysScopedByTenant";

    /// <summary>A Fact that skips itself when <see cref="ConnectionEnvVar"/> is unset, so the default test run
    /// (SQLite, no Postgres server) is unaffected. Setting Skip in the attribute reports the test as SKIPPED
    /// rather than passed, so an unconfigured machine never mistakes absence for proof.</summary>
    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} to a Postgres connection string to run the real-Postgres " +
                       "pre-change-row upgrade proof.";
        }
    }

    // Per RUN, not per operator: PostgresProofDatabase appends a unique suffix to the supplied
    // database name so two concurrent runs cannot EnsureDeleted() each other's schema (issue #1156).
    private static string Connection => PostgresProofDatabase.Connection;

    /// <summary>The same wiring the runtime hosted Gateway uses: Npgsql, the Postgres migrations assembly, and
    /// the migrations history table in the <c>gateway</c> schema.</summary>
    private static GatewayDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(Connection, npg =>
            {
                npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                npg.MigrationsHistoryTable("__EFMigrationsHistory", "gateway");
            })
            .Options;
        return new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
    }

    /// <summary>
    /// Refuse to drop a database that is not obviously a throwaway. <c>EnsureDeleted()</c> would DROP whatever
    /// the connection points at, so the target database NAME must begin with the dedicated throwaway prefix
    /// "ccpg" - a token no real database would carry. A loose substring marker like "test" is deliberately NOT
    /// used: it matches ordinary names such as "latest" or "contest", which would defeat the guard.
    /// </summary>
    private static void GuardThrowawayDatabase() => PostgresProofDatabase.GuardThrowawayDatabase();

    private static void MigrateTo(string? target)
    {
        using var ctx = NewContext();
        AccessorExtensions.GetService<IMigrator>(ctx).Migrate(target);
    }

    /// <summary>
    /// The assumption this whole proof rests on, asserted instead of assumed: that
    /// <see cref="MigrationBeforeTheChange"/> is the migration IMMEDIATELY before
    /// <see cref="MigrationUnderTest"/> in the POSTGRES migration set, so migrating to it produces the exact
    /// pre-change schema and the second migrate applies THIS change and nothing else.
    ///
    /// It is checked because breaking it is SILENT, and independently so per provider - the two sets are
    /// generated separately, so one can gain a migration in between while the other does not. If that
    /// happens, nothing in this file changes, a range-diff of this branch shows everything identical, and
    /// every assertion below still passes: the rows are still preserved and the key still ends up composite.
    /// The test would simply have stopped isolating this change. Text-identity is not meaning-identity.
    /// </summary>
    private static void AssertThisProofStillIsolatesTheMigrationUnderTest()
    {
        using var ctx = NewContext();
        var all = ctx.Database.GetMigrations().ToList();

        var underTest = all.FindIndex(m => m.EndsWith(MigrationUnderTest, StringComparison.Ordinal));
        Assert.True(underTest >= 0,
            $"'{MigrationUnderTest}' is not in the Postgres migration set at all - this proof is pointed at " +
            "a migration that no longer exists.");
        Assert.True(underTest > 0, $"'{MigrationUnderTest}' is now the FIRST Postgres migration, so there " +
            "is no pre-change schema to upgrade from.");

        Assert.True(all[underTest - 1].EndsWith(MigrationBeforeTheChange, StringComparison.Ordinal),
            $"This proof assumes '{MigrationBeforeTheChange}' is the migration immediately before " +
            $"'{MigrationUnderTest}' on Postgres, but the migration before it is now " +
            $"'{all[underTest - 1]}'. Another change has landed in between, so migrating to the named one no " +
            "longer produces the pre-change schema and this test would be carrying rows across that other " +
            "migration too while reporting the result as evidence about this one. Re-point the constant and " +
            "re-read the fixture against the new predecessor - do not simply update the name.");
    }

    private static void Execute(string sql)
    {
        using var connection = new NpgsqlConnection(Connection);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private static List<Dictionary<string, object?>> Query(string sql)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var connection = new NpgsqlConnection(Connection);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The columns of a table's PRIMARY KEY, in key order, straight from the Postgres catalogue.</summary>
    private static List<string> PrimaryKeyColumns(string table)
        => Query(
                "SELECT a.attname FROM pg_index i " +
                "JOIN pg_class c ON c.oid = i.indrelid " +
                "JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = ANY(i.indkey) " +
                $"WHERE n.nspname = 'gateway' AND c.relname = '{table}' AND i.indisprimary " +
                "ORDER BY array_position(i.indkey, a.attnum)")
            .Select(r => (string)r["attname"]!)
            .ToList();

    [RequiresPostgresFact]
    public void UpgradingAPostgresDatabaseThatAlreadyHasRows_KeepsEveryRow_AndMakesTheKeyComposite()
    {
        GuardThrowawayDatabase();
        using (var ctx = NewContext())
            ctx.Database.EnsureDeleted();

        // 0. The proof still isolates THIS migration - see the method for why this cannot be assumed.
        AssertThisProofStillIsolatesTheMigrationUnderTest();

        // 1. A database at exactly the pre-change schema.
        MigrateTo(MigrationBeforeTheChange);

        // The pre-change keys really are the single-column ones - otherwise step 3 would prove nothing,
        // because the schema would already have been what we are trying to migrate TO.
        Assert.Equal(new[] { "SessionId" }, PrimaryKeyColumns("snoozes"));
        Assert.Equal(new[] { "SessionId" }, PrimaryKeyColumns("session_spend"));
        Assert.Equal(new[] { "Endpoint" }, PrimaryKeyColumns("push_subscriptions"));

        // 2. Rows written the way a database in the field holds them - two tenants, and nullable columns
        //    exercised both null and populated.
        Execute("""
            INSERT INTO gateway.snoozes ("SessionId", tenant_id, "DirectorId", "OwnerTurnBaselineUtc", "PendingMinutes", "SnoozeUntilUtc")
            VALUES ('session-alpha', 'tenant-one', 'director-a', TIMESTAMPTZ '2026-07-20 09:00:00Z',   15, TIMESTAMPTZ '2026-07-20 10:00:00Z'),
                   ('session-beta',  'tenant-two', 'director-b', NULL,                               NULL, TIMESTAMPTZ '2026-07-21 11:30:00Z');

            INSERT INTO gateway.session_spend
                ("SessionId", tenant_id, "AgentKind", "BillingMode", "CacheCreationTokens", "CacheReadTokens",
                 "FirstObservedUtc", "InputTokens", "LastObservedUtc", "MeteredCostMicros", "Model",
                 "OutputTokens", "RepoPath", "TokensCaptured")
            VALUES ('session-alpha', 'tenant-one', 'claude', 'subscription', 11, 22,
                    TIMESTAMPTZ '2026-07-20 08:00:00Z', 33, TIMESTAMPTZ '2026-07-20 09:00:00Z', 1234567,
                    'opus', 44, 'D:\ReposFred\devthrottle', true),
                   ('session-gamma', 'tenant-two', 'codex', 'metered', 0, 0,
                    TIMESTAMPTZ '2026-07-20 09:15:00Z', 5, TIMESTAMPTZ '2026-07-20 09:30:00Z', NULL,
                    NULL, 6, NULL, false);

            INSERT INTO gateway.push_subscriptions ("Endpoint", tenant_id, "Auth", "CreatedAtUtc", "P256dh")
            VALUES ('https://push.example/one', 'tenant-one', 'auth-one', TIMESTAMPTZ '2026-07-19 08:00:00Z', 'key-one'),
                   ('https://push.example/two', 'tenant-two', 'auth-two', TIMESTAMPTZ '2026-07-19 08:05:00Z', 'key-two');
            """);

        var snoozesBefore = Query("""SELECT * FROM gateway.snoozes ORDER BY "SessionId" """);
        var spendBefore = Query("""SELECT * FROM gateway.session_spend ORDER BY "SessionId" """);
        var pushBefore = Query("""SELECT * FROM gateway.push_subscriptions ORDER BY "Endpoint" """);
        Assert.Equal(2, snoozesBefore.Count);
        Assert.Equal(2, spendBefore.Count);
        Assert.Equal(2, pushBefore.Count);

        // 3. The upgrade under test.
        MigrateTo(target: null);

        // 4a. The migration genuinely ran - by name out of the history table, and by its effect on the schema.
        //     Without both, a migration that had silently done nothing would sail through the row assertions
        //     below: the rows would be intact precisely because nothing had touched them.
        Assert.Contains(
            Query("""SELECT "MigrationId" FROM gateway."__EFMigrationsHistory" """)
                .Select(r => (string)r["MigrationId"]!),
            id => id.EndsWith(MigrationUnderTest, StringComparison.Ordinal));

        Assert.Equal(new[] { "tenant_id", "SessionId" }, PrimaryKeyColumns("snoozes"));
        Assert.Equal(new[] { "tenant_id", "SessionId" }, PrimaryKeyColumns("session_spend"));
        Assert.Equal(new[] { "tenant_id", "Endpoint" }, PrimaryKeyColumns("push_subscriptions"));

        // 4b. Every row survived, with every column value exactly what it was before the upgrade.
        AssertRowsUnchanged(snoozesBefore, Query("""SELECT * FROM gateway.snoozes ORDER BY "SessionId" """), "snoozes");
        AssertRowsUnchanged(spendBefore, Query("""SELECT * FROM gateway.session_spend ORDER BY "SessionId" """), "session_spend");
        AssertRowsUnchanged(pushBefore, Query("""SELECT * FROM gateway.push_subscriptions ORDER BY "Endpoint" """), "push_subscriptions");
    }

    /// <summary>
    /// Proves the Postgres upgrade also DELIVERS what it is for: after it, two tenants can each hold the same
    /// caller-supplied session id, which the old single-column key made impossible. Before the change the
    /// second insert is refused on the primary key - that refusal being both the cross-tenant squat and the
    /// existence oracle this change removes.
    /// </summary>
    [RequiresPostgresFact]
    public void AfterThePostgresUpgrade_TwoTenantsCanHoldTheSameCallerSuppliedIdentifier()
    {
        GuardThrowawayDatabase();
        using (var ctx = NewContext())
            ctx.Database.EnsureDeleted();

        MigrateTo(MigrationBeforeTheChange);
        Execute("""
            INSERT INTO gateway.snoozes ("SessionId", tenant_id, "DirectorId", "SnoozeUntilUtc")
            VALUES ('shared-session', 'tenant-one', 'director-first', TIMESTAMPTZ '2026-07-20 10:00:00Z');
            """);

        // The pre-change schema refuses the second tenant outright - the defect, demonstrated on Postgres.
        var squat = Assert.Throws<PostgresException>(() => Execute("""
            INSERT INTO gateway.snoozes ("SessionId", tenant_id, "DirectorId", "SnoozeUntilUtc")
            VALUES ('shared-session', 'tenant-two', 'director-second', TIMESTAMPTZ '2026-07-20 10:00:00Z');
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, squat.SqlState);

        MigrateTo(target: null);

        // After the upgrade the same insert is accepted, and both rows coexist.
        Execute("""
            INSERT INTO gateway.snoozes ("SessionId", tenant_id, "DirectorId", "SnoozeUntilUtc")
            VALUES ('shared-session', 'tenant-two', 'director-second', TIMESTAMPTZ '2026-07-20 10:00:00Z');
            """);

        var rows = Query("""
            SELECT tenant_id, "DirectorId" FROM gateway.snoozes
            WHERE "SessionId" = 'shared-session' ORDER BY tenant_id
            """);
        Assert.Equal(2, rows.Count);
        Assert.Equal("tenant-one", rows[0]["tenant_id"]);
        Assert.Equal("director-first", rows[0]["DirectorId"]);
        Assert.Equal("tenant-two", rows[1]["tenant_id"]);
        Assert.Equal("director-second", rows[1]["DirectorId"]);
    }

    private static void AssertRowsUnchanged(
        List<Dictionary<string, object?>> before,
        List<Dictionary<string, object?>> after,
        string table)
    {
        Assert.True(before.Count == after.Count,
            $"{table}: the upgrade changed the row count from {before.Count} to {after.Count} - rows were " +
            "lost or duplicated.");

        for (var i = 0; i < before.Count; i++)
        {
            Assert.True(before[i].Count == after[i].Count,
                $"{table} row {i}: the upgrade changed the column count from {before[i].Count} to " +
                $"{after[i].Count}.");

            foreach (var (column, value) in before[i])
            {
                Assert.True(after[i].ContainsKey(column),
                    $"{table} row {i}: column '{column}' is gone after the upgrade.");
                Assert.True(Equals(value, after[i][column]),
                    $"{table} row {i}: column '{column}' changed across the upgrade - was '{value}', " +
                    $"now '{after[i][column]}'.");
            }
        }
    }
}
