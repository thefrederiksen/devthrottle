using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The real-PostgreSQL half of the MTR-14A proof. The SQLite importer tests
/// (<see cref="DeviceCredentialImportTests"/>) say nothing about Postgres, and by the "prove ON the surface the
/// change touches" law the device-credential table is the surface MTR-14B's authentication read moves onto - so
/// this class proves TWO things on an actual Postgres server: (1) the migration applies CLEAN from nothing,
/// creating both device tables under the <c>gateway</c> schema with the explicit byte-ordinal <c>"C"</c>
/// collation on exactly the natural-key columns; and (2) the importer round-trips every device row through the
/// real provider, driven the SAME way the runtime hosted Gateway is (<c>CC_GATEWAY_DB_CONNECTION</c> selects
/// Postgres inside <see cref="GatewayDatabase"/>).
///
/// GATING. The whole class is gated on <c>CC_GATEWAY_TEST_PG_CONNECTION</c> and reports SKIPPED when it is
/// unset, so the ordinary SQLite test run is untouched. Skipped is not passed: with no server configured it
/// makes no claim rather than a false one. Point the variable at a throwaway Postgres whose database name starts
/// with <c>ccpg</c> to run it.
/// </summary>
public sealed class DeviceCredentialImportPostgresTests
{
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";

    /// <summary>The env var <see cref="GatewayDatabase"/> reads to select Postgres over SQLite at runtime -
    /// the same switch the hosted Gateway uses, set here so the importer proof exercises the real wiring.</summary>
    private const string RuntimeConnectionEnvVar = "CC_GATEWAY_DB_CONNECTION";

    private sealed class RequiresPostgresFactAttribute : FactAttribute
    {
        public RequiresPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} to a Postgres connection string to run the real-Postgres " +
                       "device-credential migration and import proof.";
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
    /// Refuse to drop a database that is not obviously a throwaway - the same guard and reasoning as the other
    /// Postgres proofs: the target database NAME must begin with the dedicated <c>ccpg</c> prefix, a token no
    /// real database would carry, so pointing the env var at a real database can never drop it.
    /// </summary>
    private static void GuardThrowawayDatabase() => PostgresProofDatabase.GuardThrowawayDatabase();

    private static int ScalarInt(GatewayDbContext ctx, string sql)
    {
        var conn = ctx.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { conn.Open(); opened = true; }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        finally { if (opened) conn.Close(); }
    }

    private static List<(string, string)> QueryPairs(GatewayDbContext ctx, string sql)
    {
        var conn = ctx.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open) { conn.Open(); opened = true; }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var rows = new List<(string, string)>();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1)));
            return rows;
        }
        finally { if (opened) conn.Close(); }
    }

    [RequiresPostgresFact]
    public void Migration_AppliesClean_CreatesDeviceTables_WithByteOrdinalCollation_OnRealPostgres()
    {
        using var ctx = NewContext();
        GuardThrowawayDatabase();
        ctx.Database.EnsureDeleted();
        ctx.Database.Migrate();

        // Both device tables landed under the gateway schema - and NEITHER under public.
        foreach (var table in new[] { "device_credentials", "device_import_markers" })
        {
            Assert.Equal(1, ScalarInt(ctx,
                $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'gateway' AND table_name = '{table}'"));
            Assert.Equal(0, ScalarInt(ctx,
                $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{table}'"));
        }

        // The three device natural-key columns - and ONLY those among the device tables - carry an explicit
        // "C" collation. pg_attribute.attcollation reads back "C" only where the migration set COLLATE "C"
        // explicitly, distinguishing our pin from the database's default collation even if that default is C.
        var deviceExplicitC = QueryPairs(ctx,
            "SELECT c.relname, a.attname " +
            "FROM pg_attribute a " +
            "JOIN pg_class c ON c.oid = a.attrelid " +
            "JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "JOIN pg_collation col ON col.oid = a.attcollation " +
            "WHERE n.nspname = 'gateway' AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped " +
            "AND col.collname = 'C' AND c.relname IN ('device_credentials', 'device_import_markers') " +
            "ORDER BY c.relname, a.attname");

        Assert.Equal(new[]
        {
            ("device_credentials", "DeviceId"),
            ("device_credentials", "DeviceKeyHash"),
            ("device_import_markers", "SourcePath"),
        }, deviceExplicitC.ToArray());

        // The DeviceKeyHash lookup index the authentication read (MTR-14B) rides on exists.
        Assert.Equal(1, ScalarInt(ctx,
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'gateway' " +
            "AND tablename = 'device_credentials' AND indexname = 'IX_device_credentials_DeviceKeyHash'"));
    }

    [RequiresPostgresFact]
    public void Import_PreservesEveryRow_OnRealPostgres_ThroughTheRuntimeWiring()
    {
        // Start from a clean schema so the row assertions are about THIS import and nothing left over.
        using (var ctx = NewContext())
        {
            GuardThrowawayDatabase();
            ctx.Database.EnsureDeleted();
        }

        var dir = Path.Combine(Path.GetTempPath(), "cc-device-import-pg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var legacy = Path.Combine(dir, "devices.json");
        File.WriteAllText(legacy, """
            [
              { "DeviceId": "pg-alpha", "MachineName": "ALPHA-PC", "DeviceKeyHash": "aaaa1111", "KeyPrefix": "AbCdEfGh", "KeyLast4": "WxYz", "IssuedAtUtc": "2026-07-20T09:00:00Z", "Status": "active", "Platform": "windows", "DeviceType": "workstation", "AccountSubject": "subject-1", "TenantId": "tenant-one" },
              { "DeviceId": "pg-beta",  "MachineName": "BETA-PC",  "DeviceKeyHash": "bbbb2222", "KeyPrefix": "11112222", "KeyLast4": "3333", "IssuedAtUtc": "2026-07-21T10:30:00Z", "Status": "active", "Platform": "unknown", "DeviceType": "workstation" },
              { "DeviceId": "pg-gamma", "MachineName": "GAMMA-PHONE", "DeviceKeyHash": "cccc3333", "KeyPrefix": "ZzZzZzZz", "KeyLast4": "0000", "IssuedAtUtc": "2026-07-22T11:15:00Z", "Status": "active", "Platform": "android", "DeviceType": "phone", "CloudDeviceId": "cloud-xyz", "AccountSubject": "subject-2", "TenantId": "tenant-two" }
            ]
            """);

        var priorRuntimeConn = Environment.GetEnvironmentVariable(RuntimeConnectionEnvVar);
        try
        {
            // Drive GatewayDatabase down its real Postgres path - the exact runtime selection the hosted Gateway
            // makes - rather than hand-wiring Npgsql, so the importer is proven through the wiring it will use.
            Environment.SetEnvironmentVariable(RuntimeConnectionEnvVar, Connection);

            using var db = new GatewayDatabase(new SingleTenantContext());

            var result = new DeviceRegistryImporter(db, legacy).Import();
            Assert.False(result.Skipped);
            Assert.Equal(3, result.ImportedCount);

            using (var ctx = db.CreateUnscopedContext())
            {
                var rows = ctx.DeviceCredentials.AsNoTracking().OrderBy(d => d.DeviceId).ToList();
                Assert.Equal(3, rows.Count);

                var alpha = rows.Single(r => r.DeviceId == "pg-alpha");
                Assert.Equal("aaaa1111", alpha.DeviceKeyHash);
                Assert.Equal("subject-1", alpha.AccountSubject);
                Assert.Equal("tenant-one", alpha.TenantId);
                Assert.Equal(new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc), alpha.IssuedAtUtc);
                Assert.Equal(DateTimeKind.Utc, alpha.IssuedAtUtc.Kind);
                Assert.Null(alpha.RevokedAtUtc);

                var beta = rows.Single(r => r.DeviceId == "pg-beta");
                Assert.Null(beta.AccountSubject);
                Assert.Null(beta.TenantId);
                Assert.Null(beta.CloudDeviceId);

                var gamma = rows.Single(r => r.DeviceId == "pg-gamma");
                Assert.Equal("cloud-xyz", gamma.CloudDeviceId);
                Assert.Equal("tenant-two", gamma.TenantId);

                var marker = Assert.Single(ctx.DeviceImportMarkers.AsNoTracking().ToList());
                Assert.Equal(legacy, marker.SourcePath);
                Assert.Equal(3, marker.ImportedCount);
            }

            // Idempotent on Postgres too: a second run finds the marker and writes nothing more.
            var second = new DeviceRegistryImporter(db, legacy).Import();
            Assert.True(second.Skipped);
            using (var ctx = db.CreateUnscopedContext())
                Assert.Equal(3, ctx.DeviceCredentials.AsNoTracking().Count());
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeConnectionEnvVar, priorRuntimeConn);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
