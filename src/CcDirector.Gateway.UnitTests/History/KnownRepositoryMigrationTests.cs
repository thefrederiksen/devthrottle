using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

public sealed class KnownRepositoryMigrationTests
{
    [Fact]
    public void AddKnownRepositories_RetainedHistoryExists_BackfillsAndOutlivesTheSourceRow()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "cc-known-repository-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "gateway.db");

        try
        {
            var options = new DbContextOptionsBuilder<GatewayDbContext>()
                .UseSqlite("Data Source=" + path + ";Pooling=False")
                .Options;
            using var context = new GatewayDbContext(options)
            {
                ActiveTenant = TenantId.Local.Value,
            };
            var migrator = context.Database.GetService<IMigrator>();

            // Build the real schema immediately before this feature's migration, then place a retained
            // history row into it. The final migrate call must create and backfill the catalog.
            migrator.Migrate("20260902003414_AddSessionTurns");
            var lastSeen = new DateTime(2026, 8, 31, 14, 30, 0, DateTimeKind.Utc);
            const string machineName = "Søren_North";
            // WRITTEN AS SQL, NOT THROUGH THE MODEL, and that is the point of this test rather than a detail of it:
            // the row has to be the row that EXISTED at the old schema. Inserting it with the current entity makes
            // the test assert against today's columns, so it failed the moment session_history gained one (the
            // Factory Memory mission's Factory column was the one that caught it). Only the columns this migration
            // reads, plus the ones that were NOT NULL back then, are named.
            context.Database.ExecuteSqlRaw("""
                INSERT INTO "session_history"
                    ("tenant_id", "SessionId", "DirectorId", "MachineName", "RepoPath", "RepoName",
                     "StartedAtUtc", "LastSeenUtc", "SummaryIsPartial", "SummaryAttempts")
                VALUES (@p0, 'backfill-session', 'backfill-director', @p1, 'D:\Repositories\historical',
                     'Historical repository', @p2, @p3, 0, 0);
                """,
                TenantId.Local.Value, machineName,
                lastSeen.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss"),
                lastSeen.ToString("yyyy-MM-dd HH:mm:ss"));

            migrator.Migrate();
            context.ChangeTracker.Clear();

            var catalogRow = Assert.Single(context.KnownRepositories.AsNoTracking());
            Assert.Equal(machineName, catalogRow.MachineName);
            Assert.Equal(@"D:\Repositories\historical", catalogRow.Path);
            Assert.Equal("Historical repository", catalogRow.Name);
            Assert.Equal(lastSeen, catalogRow.LastUsedUtc);

            context.SessionHistory.Remove(Assert.Single(context.SessionHistory));
            context.SaveChanges();
            context.ChangeTracker.Clear();

            Assert.Single(context.KnownRepositories.AsNoTracking());

            using var database = new GatewayDatabase(new SingleTenantContext(), path);
            var store = new KnownRepositoryStore(database);
            var retained = Assert.Single(store.ReadForMachine(TenantId.Local, machineName));
            Assert.Equal(@"D:\Repositories\historical", retained.Path);
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of a throwaway migration database.
            }
        }
    }

    [Fact]
    public void AddDiscoveredRepositories_CatalogAlreadyHasRows_KeepsEveryLastUsedTime()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "cc-known-repository-discovered-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "gateway.db");

        try
        {
            var options = new DbContextOptionsBuilder<GatewayDbContext>()
                .UseSqlite("Data Source=" + path + ";Pooling=False")
                .Options;
            using var context = new GatewayDbContext(options)
            {
                ActiveTenant = TenantId.Local.Value,
            };
            var migrator = context.Database.GetService<IMigrator>();

            // The real schema immediately BEFORE the last-used time became nullable, with a catalog row in
            // it - which is what every existing install looks like. Making a column nullable rebuilds the
            // table on SQLite, so "the rows survive it" is a thing to prove rather than assume.
            migrator.Migrate("20260918171353_AddWingmanNarrationCallTrace");
            var lastUsed = new DateTime(2026, 9, 1, 9, 15, 0, DateTimeKind.Utc);
            const string machineName = "Søren_North";
            // Written as SQL rather than through the entity, because the entity is the CURRENT model and the
            // database is deliberately one migration behind it. This is the shape an existing install holds.
            context.Database.ExecuteSqlRaw(
                "INSERT INTO known_repositories (Id, tenant_id, MachineKey, PathKey, MachineName, Path, Name, LastUsedUtc) "
                + "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7})",
                Guid.NewGuid().ToString(), TenantId.Local.Value, machineName.ToUpperInvariant(),
                "D:/REPOSITORIES/EXISTING", machineName, @"D:\Repositories\existing", "Existing repository",
                lastUsed.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));

            migrator.Migrate();
            context.ChangeTracker.Clear();

            var row = Assert.Single(context.KnownRepositories.AsNoTracking());
            Assert.Equal(lastUsed, row.LastUsedUtc);
            Assert.Equal("Existing repository", row.Name);
            // An existing row reads as the USED half, which is the truth about it: nothing has ever reported
            // it under a root folder.
            Assert.Null(row.DiscoveredByDirectorId);
            Assert.Null(row.LastSeenUtc);

            // And the read side still serves it, unchanged.
            using var database = new GatewayDatabase(new SingleTenantContext(), path);
            var store = new KnownRepositoryStore(database);
            var served = Assert.Single(store.ReadForMachine(TenantId.Local, machineName));
            Assert.Equal(@"D:\Repositories\existing", served.Path);
            Assert.Equal(lastUsed, served.LastUsed);
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of a throwaway migration database.
            }
        }
    }
}
