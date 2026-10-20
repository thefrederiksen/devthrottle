using System.Reflection;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Rules;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// Session Rules part 2, phase 1 (<c>AddSessionRuleKindsAndHookFirings</c>): every rule gains a kind and a
/// place it applies, hook rules gain their trigger, and firings gain the hook decision's columns. A rule and
/// a firing stored BEFORE the migration must come out of it as a screen rule that applies everywhere and a
/// screen firing, readable through the real model - the plan's "existing rows become kind screen and appliesTo
/// everywhere". The PostgreSQL apply is <c>SessionRuleKindsPostgresTests</c>.
/// </summary>
public sealed class SessionRuleKindsMigrationTests
{
    private const string SqliteBefore = "20261020002835_AddSessionRuleChanges";
    private const string SqliteUnderTest = "20261020014625_AddSessionRuleKindsAndHookFirings";

    [Fact]
    public void AddSessionRuleKindsAndHookFirings_RowsStoredBefore_BecomeScreenRulesThatApplyEverywhere()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cc-rule-kinds-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = "Data Source=" + Path.Combine(directory, "gateway.db") + ";Pooling=False";
        var ruleId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        try
        {
            var options = new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options;
            using (var context = new GatewayDbContext(options) { ActiveTenant = "tenant-one" })
            {
                var migrator = context.Database.GetService<IMigrator>();
                var all = context.Database.GetMigrations().ToList();
                var index = all.IndexOf(SqliteUnderTest);
                Assert.True(index > 0, $"'{SqliteUnderTest}' is not in the SQLite migration set.");
                Assert.Equal(SqliteBefore, all[index - 1]);

                migrator.Migrate(SqliteBefore);
                Execute(connection, $"""
                    INSERT INTO "session_rules" ("Id", "Instruction", "ScreenDescription", "TriggerWords", "CooldownSeconds",
                        "DailyCap", "State", "PromotedBy", "CreatedUtc", "UpdatedUtc", "tenant_id", "Calls")
                    VALUES ('{ruleId.ToString().ToUpperInvariant()}', 'When I run out of allowance, switch me to Opus.',
                        'A session stopped on a provider allowance notice.', '["limit"]', 300, 5, 'dry_run', '',
                        '2026-09-02 12:00:00', '2026-09-02 12:00:00', 'tenant-one', '[]');
                    INSERT INTO "session_rule_firings" ("Id", "RuleId", "SessionId", "OccurredUtc", "ScreenText", "Understanding",
                        "Decision", "Reason", "TypedText", "Outcome", "Grounding", "tenant_id", "PrimitiveRuns")
                    VALUES ('22222222-2222-2222-2222-222222222222', '{ruleId.ToString().ToUpperInvariant()}', 'sid-1',
                        '2026-09-02 12:01:00', 'screen', 'understood', 'decline', 'not this one', '', 'nothing', 'grounding', 'tenant-one', '[]');
                    """);

                migrator.Migrate(SqliteUnderTest);
                Assert.Equal(SqliteUnderTest, context.Database.GetAppliedMigrations().Last());
            }

            // Read back through the real model, which is what the store does.
            using var reader = new GatewayDbContext(options) { ActiveTenant = "tenant-one" };
            var rule = reader.SessionRules.AsNoTracking().Single();
            Assert.Equal(RuleKinds.Screen, rule.Kind);
            Assert.Equal(RuleAppliesToKinds.Everywhere, rule.AppliesToKind);
            Assert.Empty(rule.AppliesToMachines);
            Assert.Empty(rule.AppliesToExceptMachines);
            Assert.Equal("", rule.AppliesToRepo);
            Assert.Empty(rule.HookMatch);
            Assert.Equal("", rule.HookEvent);
            Assert.Equal(new[] { "limit" }, rule.TriggerWords);

            var firing = reader.SessionRuleFirings.AsNoTracking().Single();
            Assert.Equal(RuleKinds.Screen, firing.Kind);
            Assert.Null(firing.DecisionId);
            Assert.Empty(firing.OverriddenRuleIds);
            Assert.Empty(firing.MatchedRuleIds);
            Assert.Equal("decline", firing.Decision);
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection(connection));
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* best-effort cleanup of a throwaway database */ }
        }
    }

    /// <summary>Each provider's Designer is found through its attributes and carries the new columns.</summary>
    [Theory]
    [InlineData("sqlite", "20261020014625_AddSessionRuleKindsAndHookFirings")]
    [InlineData("postgres", "20261020014715_AddSessionRuleKindsAndHookFirings")]
    public void AddSessionRuleKindsAndHookFirings_Designer_IsDiscoveredAndCarriesTheNewColumns(string provider, string id)
    {
        using var context = FleetManagerEventOutcomeAnswerMigrationTests.Context(provider);
        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.True(assembly.Migrations.TryGetValue(id, out var type), $"'{id}' is not discovered for {provider}.");
        Assert.Equal("AddSessionRuleKindsAndHookFirings", type!.Name);
        Assert.Equal(typeof(GatewayDbContext), type.GetCustomAttribute<DbContextAttribute>()!.ContextType);

        var designed = FleetManagerEventOutcomeAnswerMigrationTests.DesignedModel(context, assembly, type);
        var rules = designed.GetEntityTypes().Single(e => e.Name.EndsWith(".SessionRuleEntity", StringComparison.Ordinal));
        foreach (var column in new[] { "Kind", "AppliesToKind", "AppliesToMachines", "AppliesToExceptMachines", "AppliesToRepo",
                     "HookEvent", "HookTool", "HookHandler", "HookMatch", "HookResult" })
            Assert.NotNull(rules.FindProperty(column));
        var firings = designed.GetEntityTypes().Single(e => e.Name.EndsWith(".SessionRuleFiringEntity", StringComparison.Ordinal));
        foreach (var column in new[] { "Kind", "DecisionId", "HookEvent", "HookTool", "Subject", "Machine", "Repo",
                     "Agent", "AgentEvent", "AgentTool", "OverriddenRuleIds", "MatchedRuleIds", "EvaluationMicroseconds" })
            Assert.NotNull(firings.FindProperty(column));

        // Generated on top of phase 2's AddSessionRuleChanges, so the model it targets holds the change history too.
        Assert.Contains(designed.GetEntityTypes(), e => e.Name.EndsWith(".SessionRuleChangeEntity", StringComparison.Ordinal));
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
