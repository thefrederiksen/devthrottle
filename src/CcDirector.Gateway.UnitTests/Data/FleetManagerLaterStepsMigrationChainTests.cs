using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// STEPS 5 TO 9'S MIGRATIONS SIT ON TOP OF STEP 4'S, AND EACH DESIGNER IS THE MODEL AT ITS POINT. Steps 5 to 9 were
/// built on an older copy of step 4 whose migrations had other ids, so their three migrations were renamed to sort
/// after step 4's delivery migration and their Designer files rebuilt on its model. For each provider this walks the
/// chain from step 4's delivery migration: each migration comes straight after the one before it, and the schema
/// difference between the two Designer models is exactly what the migration's Up does - no more, no less. Read with no
/// database.
/// </summary>
public sealed class FleetManagerLaterStepsMigrationChainTests
{
    [Theory]
    [InlineData("sqlite", new[]
    {
        "20260917110100_AddFleetManagerEventDelivery",
        "20260917110200_AddFleetOutcomeAdvice",
        "20260917110300_RemoveAssistantSettings",
        "20260917110400_AddFleetManagerEventOutcomeAnswer",
        "20260917110500_AddTurnVerdictAnswerChoice",
        "20260917110600_AddFleetOutcomeStopIdentity",
        "20260918171353_AddWingmanNarrationCallTrace",
        "20260920021757_AddDiscoveredRepositories",
        "20260920052924_AddRaisedSessions",
    })]
    [InlineData("postgres", new[]
    {
        "20260917110109_AddFleetManagerEventDelivery",
        "20260917110209_AddFleetOutcomeAdvice",
        "20260917110309_RemoveAssistantSettings",
        "20260917110409_AddFleetManagerEventOutcomeAnswer",
        "20260917110509_AddTurnVerdictAnswerChoice",
        "20260917110609_AddFleetOutcomeStopIdentity",
        "20260918181205_AddWingmanNarrationCallTrace",
        "20260920021806_AddDiscoveredRepositories",
        "20260920053001_AddRaisedSessions",
    })]
    public void LaterStepMigrations_EachFollowsTheLast_AndItsDesignerDiffersOnlyByItsOwnSchemaChange(string provider, string[] chain)
    {
        using var context = FleetManagerEventOutcomeAnswerMigrationTests.Context(provider);
        var assembly = context.GetService<IMigrationsAssembly>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var ordered = assembly.Migrations.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(chain[^1], ordered[^1]);

        var changes = 0;
        for (var i = 1; i < chain.Length; i++)
        {
            var (before, id) = (chain[i - 1], chain[i]);
            Assert.True(assembly.Migrations.TryGetValue(id, out var type), $"'{id}' is not discovered for {provider}.");
            Assert.Equal(before, ordered[ordered.IndexOf(id) - 1]);

            var previous = FleetManagerEventOutcomeAnswerMigrationTests.DesignedModel(context, assembly, assembly.Migrations[before]);
            var designed = FleetManagerEventOutcomeAnswerMigrationTests.DesignedModel(context, assembly, type!);
            var modelChange = Describe(differ.GetDifferences(previous.GetRelationalModel(), designed.GetRelationalModel()));
            var migrationChange = Describe(assembly.CreateMigration(type!, context.Database.ProviderName!).UpOperations);

            Assert.Equal(migrationChange, modelChange);
            changes += modelChange.Count;
        }
        // Five advice columns, then three answer columns and their index, then the verdict's answer column, then the
        // record's two stop columns, then the narration call's six trace columns, then the repository catalog's
        // nullable last-used time and its two discovered columns, then the raised sessions table and its two indexes:
        // an empty comparison proves nothing.
        Assert.Equal(24, changes);
    }

    /// <summary>The schema operations, as sorted "kind table.name" lines. Data operations are not schema.</summary>
    private static List<string> Describe(IEnumerable<MigrationOperation> operations)
        => operations.Select(op => op switch
            {
                AddColumnOperation c => $"AddColumn {c.Table}.{c.Name}",
                DropColumnOperation c => $"DropColumn {c.Table}.{c.Name}",
                AlterColumnOperation c => $"AlterColumn {c.Table}.{c.Name}",
                CreateIndexOperation x => $"CreateIndex {x.Table}.{x.Name}",
                DropIndexOperation x => $"DropIndex {x.Table}.{x.Name}",
                CreateTableOperation t => $"CreateTable {t.Name}",
                DropTableOperation t => $"DropTable {t.Name}",
                _ => null,
            })
            .OfType<string>()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
}
