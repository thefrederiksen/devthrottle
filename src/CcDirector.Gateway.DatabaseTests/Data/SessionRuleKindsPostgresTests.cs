using CcDirector.Gateway.Data;
using CcDirector.Gateway.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The PostgreSQL half of <c>AddSessionRuleKindsAndHookFirings</c> (Session Rules part 2, phase 1): a real
/// PostgreSQL database at the schema before the change, holding a rule and a firing, carried through the
/// migration. The new list columns are NOT NULL arrays, and adding one to a table that already has rows needs a
/// default - which the generator did not write and was added by hand - so this is the proof that it applies and
/// that the rows come out as a screen rule that applies everywhere and a screen firing.
///
/// GATING. [RequiresPostgresFact], the one rule for this project: it runs under scripts\test-database.ps1,
/// against the throwaway PostgreSQL that run built, and reports SKIPPED anywhere else. Skipped is not passed.
/// </summary>
public sealed class SessionRuleKindsPostgresTests
{
    private const string MigrationBefore = "20261018153213_AddMailDesk";
    private const string MigrationUnderTest = "20261018230534_AddSessionRuleKindsAndHookFirings";

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
        return new GatewayDbContext(options) { ActiveTenant = "tenant-one" };
    }

    private static string? Scalar(string sql)
    {
        using var connection = new NpgsqlConnection(Connection);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        return command.ExecuteScalar()?.ToString();
    }

    [RequiresPostgresFact]
    public void AddSessionRuleKindsAndHookFirings_RowsStoredBefore_BecomeScreenRulesThatApplyEverywhere()
    {
        PostgresProofDatabase.GuardThrowawayDatabase();
        using (var ctx = NewContext())
            ctx.Database.EnsureDeleted();

        using (var ctx = NewContext())
        {
            var all = ctx.Database.GetMigrations().ToList();
            var index = all.IndexOf(MigrationUnderTest);
            Assert.True(index > 0, $"'{MigrationUnderTest}' is not in the Postgres migration set.");
            Assert.Equal(MigrationBefore, all[index - 1]);
            ctx.GetService<IMigrator>().Migrate(MigrationBefore);
        }

        Scalar("""
            INSERT INTO gateway.session_rules ("Id", "Instruction", "ScreenDescription", "TriggerWords", "CooldownSeconds",
                "DailyCap", "State", "PromotedBy", "CreatedUtc", "UpdatedUtc", tenant_id, "Calls")
            VALUES ('11111111-1111-1111-1111-111111111111', 'When I run out of allowance, switch me to Opus.',
                'A session stopped on a provider allowance notice.', ARRAY['limit'], 300, 5, 'dry_run', '',
                TIMESTAMPTZ '2026-09-02 12:00:00Z', TIMESTAMPTZ '2026-09-02 12:00:00Z', 'tenant-one', '[]');
            INSERT INTO gateway.session_rule_firings ("Id", "RuleId", "SessionId", "OccurredUtc", "ScreenText",
                "Understanding", "Decision", "Reason", "TypedText", "Outcome", "Grounding", tenant_id, "PrimitiveRuns")
            VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'sid-1',
                TIMESTAMPTZ '2026-09-02 12:01:00Z', 'screen', 'understood', 'decline', 'not this one', '', 'nothing',
                'grounding', 'tenant-one', '[]');
            """);

        using (var ctx = NewContext())
        {
            ctx.GetService<IMigrator>().Migrate(MigrationUnderTest);
            Assert.Equal(MigrationUnderTest, ctx.Database.GetAppliedMigrations().Last());
        }

        using var reader = NewContext();
        var rule = reader.SessionRules.AsNoTracking().Single();
        Assert.Equal(RuleKinds.Screen, rule.Kind);
        Assert.Equal(RuleAppliesToKinds.Everywhere, rule.AppliesToKind);
        Assert.Empty(rule.AppliesToMachines);
        Assert.Empty(rule.AppliesToExceptMachines);
        Assert.Empty(rule.HookMatch);
        Assert.Equal(new[] { "limit" }, rule.TriggerWords);

        var firing = reader.SessionRuleFirings.AsNoTracking().Single();
        Assert.Equal(RuleKinds.Screen, firing.Kind);
        Assert.Null(firing.DecisionId);
        Assert.Empty(firing.OverriddenRuleIds);
        Assert.Empty(firing.MatchedRuleIds);
    }
}
