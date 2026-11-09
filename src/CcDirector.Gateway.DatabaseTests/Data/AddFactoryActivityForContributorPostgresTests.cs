using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of the sentence for a team contributor on the factory activity record (Watch your factory,
/// piece 1): <c>AddFactoryActivityForContributor</c> applies to a real PostgreSQL database right after
/// <c>AddWebsiteInboxKeys</c>, adds one nullable column without touching an existing row, keeps a sentence
/// byte for byte through the model, and its Down removes the column again.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1,
/// against the throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
/// </summary>
public sealed class AddFactoryActivityForContributorPostgresTests
{
    private const string MigrationBefore = "20261108214941_AddWebsiteInboxKeys";
    private const string MigrationUnderTest = "20261109003130_AddFactoryActivityForContributor";

    // Characters a rewording pass would be likely to touch: a non-ASCII letter, a double space, an apostrophe.
    private const string Sentence = "We checked S\u00f8ren's contact form  - it now reaches you, and the old address is gone.";

    private static string Connection => PostgresProofDatabase.Connection;

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

    private static string? Scalar(string sql)
    {
        using var connection = new NpgsqlConnection(Connection);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        return command.ExecuteScalar()?.ToString();
    }

    private static string ColumnPresent() => Scalar("""
        SELECT count(*) FROM information_schema.columns
        WHERE table_schema = 'gateway' AND table_name = 'factory_activity' AND column_name = 'ForContributor'
        """)!;

    [RequiresPostgresFact]
    public void AddFactoryActivityForContributor_AppliesOnPostgres_LeavesOldRowsAlone_KeepsTheSentence_AndItsDownRemovesIt()
    {
        PostgresProofDatabase.GuardThrowawayDatabase();
        using (var ctx = NewContext())
            ctx.Database.EnsureDeleted();

        using (var ctx = NewContext())
        {
            var all = ctx.Database.GetMigrations().ToList();
            Assert.Equal(MigrationBefore, all[all.IndexOf(MigrationUnderTest) - 1]);
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
        }
        // A row written before the migration.
        Scalar($"""
            INSERT INTO gateway.factory_activity ("Id", tenant_id, "Factory", "FactoryAgent", "What", "Outcome", "Actor",
                "OccurredUtc", "RecordedUtc")
            VALUES ('11111111-1111-1111-1111-111111111111', '{TenantId.Local.Value}', 'website-business', 'front-desk',
                'Added an address.', 'done', 'session:old', TIMESTAMPTZ '2026-11-01 10:00:00Z', TIMESTAMPTZ '2026-11-01 10:00:00Z');
            """);
        Assert.Equal("0", ColumnPresent());

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }

        Assert.Equal("1", ColumnPresent());
        Assert.Equal("null", Scalar("""
            SELECT coalesce("ForContributor", 'null') FROM gateway.factory_activity
            WHERE "Id" = '11111111-1111-1111-1111-111111111111'
            """));

        Guid newId;
        using (var ctx = NewContext())
        {
            var entity = new FactoryActivityEntity
            {
                TenantId = TenantId.Local.Value,
                Factory = "website-business",
                FactoryAgent = "front-desk",
                What = "Fixed the contact form.",
                Outcome = "done",
                Actor = "session:new",
                OccurredUtc = DateTime.UtcNow,
                RecordedUtc = DateTime.UtcNow,
                ForContributor = Sentence,
            };
            newId = entity.Id;
            ctx.FactoryActivity.Add(entity);
            ctx.SaveChanges();
        }
        using (var ctx = NewContext())
            Assert.Equal(Sentence, ctx.FactoryActivity.AsNoTracking().Single(e => e.Id == newId).ForContributor);

        // The reversal: back to AddWebsiteInboxKeys removes the column, and both rows survive.
        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
            Assert.Equal(MigrationBefore, ctx.Database.GetAppliedMigrations().Last());
        }
        Assert.Equal("0", ColumnPresent());
        Assert.Equal("2", Scalar("SELECT count(*) FROM gateway.factory_activity"));

        using (var ctx = NewContext())
            ctx.Database.Migrate();
        Assert.Equal("1", ColumnPresent());
    }
}
