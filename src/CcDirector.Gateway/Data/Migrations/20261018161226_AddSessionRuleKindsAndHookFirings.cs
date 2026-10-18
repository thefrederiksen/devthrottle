using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionRuleKindsAndHookFirings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppliesToExceptMachines",
                table: "session_rules",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "AppliesToKind",
                table: "session_rules",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "everywhere");

            migrationBuilder.AddColumn<string>(
                name: "AppliesToMachines",
                table: "session_rules",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "AppliesToRepo",
                table: "session_rules",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HookEvent",
                table: "session_rules",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HookHandler",
                table: "session_rules",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HookMatch",
                table: "session_rules",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "HookResult",
                table: "session_rules",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HookTool",
                table: "session_rules",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "session_rules",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "screen");

            migrationBuilder.AddColumn<string>(
                name: "Agent",
                table: "session_rule_firings",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AgentEvent",
                table: "session_rule_firings",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AgentTool",
                table: "session_rule_firings",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DecisionId",
                table: "session_rule_firings",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EvaluationMicroseconds",
                table: "session_rule_firings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "HookEvent",
                table: "session_rule_firings",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HookTool",
                table: "session_rule_firings",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "session_rule_firings",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "screen");

            migrationBuilder.AddColumn<string>(
                name: "Machine",
                table: "session_rule_firings",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MatchedRuleIds",
                table: "session_rule_firings",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "OverriddenRuleIds",
                table: "session_rule_firings",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "Repo",
                table: "session_rule_firings",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                table: "session_rule_firings",
                type: "TEXT",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_firings_tenant_id_DecisionId",
                table: "session_rule_firings",
                columns: new[] { "tenant_id", "DecisionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_session_rule_firings_tenant_id_DecisionId",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "AppliesToExceptMachines",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "AppliesToKind",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "AppliesToMachines",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "AppliesToRepo",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "HookEvent",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "HookHandler",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "HookMatch",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "HookResult",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "HookTool",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "Agent",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "AgentEvent",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "AgentTool",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "DecisionId",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "EvaluationMicroseconds",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "HookEvent",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "HookTool",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "Machine",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "MatchedRuleIds",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "OverriddenRuleIds",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "Repo",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "Subject",
                table: "session_rule_firings");
        }
    }
}
