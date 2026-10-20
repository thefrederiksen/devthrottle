using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionRuleKindsAndHookFirings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "AppliesToExceptMachines",
                schema: "gateway",
                table: "session_rules",
                type: "text[]",
                nullable: false,
                // Existing rows get an empty list, as SQLite's "[]" does: NOT NULL with no default
                // cannot be added to a table that already has rows.
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "AppliesToKind",
                schema: "gateway",
                table: "session_rules",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "everywhere");

            migrationBuilder.AddColumn<List<string>>(
                name: "AppliesToMachines",
                schema: "gateway",
                table: "session_rules",
                type: "text[]",
                nullable: false,
                // Existing rows get an empty list, as SQLite's "[]" does: NOT NULL with no default
                // cannot be added to a table that already has rows.
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "AppliesToRepo",
                schema: "gateway",
                table: "session_rules",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HookEvent",
                schema: "gateway",
                table: "session_rules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HookHandler",
                schema: "gateway",
                table: "session_rules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<List<string>>(
                name: "HookMatch",
                schema: "gateway",
                table: "session_rules",
                type: "text[]",
                nullable: false,
                // Existing rows get an empty list, as SQLite's "[]" does: NOT NULL with no default
                // cannot be added to a table that already has rows.
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "HookResult",
                schema: "gateway",
                table: "session_rules",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HookTool",
                schema: "gateway",
                table: "session_rules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "gateway",
                table: "session_rules",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "screen");

            migrationBuilder.AddColumn<string>(
                name: "Agent",
                schema: "gateway",
                table: "session_rule_firings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AgentEvent",
                schema: "gateway",
                table: "session_rule_firings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AgentTool",
                schema: "gateway",
                table: "session_rule_firings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DecisionId",
                schema: "gateway",
                table: "session_rule_firings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EvaluationMicroseconds",
                schema: "gateway",
                table: "session_rule_firings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "HookEvent",
                schema: "gateway",
                table: "session_rule_firings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HookTool",
                schema: "gateway",
                table: "session_rule_firings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "gateway",
                table: "session_rule_firings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "screen");

            migrationBuilder.AddColumn<string>(
                name: "Machine",
                schema: "gateway",
                table: "session_rule_firings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<List<Guid>>(
                name: "MatchedRuleIds",
                schema: "gateway",
                table: "session_rule_firings",
                type: "uuid[]",
                nullable: false,
                // Existing rows get an empty list, as SQLite's "[]" does: NOT NULL with no default
                // cannot be added to a table that already has rows.
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<List<Guid>>(
                name: "OverriddenRuleIds",
                schema: "gateway",
                table: "session_rule_firings",
                type: "uuid[]",
                nullable: false,
                // Existing rows get an empty list, as SQLite's "[]" does: NOT NULL with no default
                // cannot be added to a table that already has rows.
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "Repo",
                schema: "gateway",
                table: "session_rule_firings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                schema: "gateway",
                table: "session_rule_firings",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_firings_tenant_id_DecisionId",
                schema: "gateway",
                table: "session_rule_firings",
                columns: new[] { "tenant_id", "DecisionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_session_rule_firings_tenant_id_DecisionId",
                schema: "gateway",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "AppliesToExceptMachines",
                schema: "gateway",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "AppliesToKind",
                schema: "gateway",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "AppliesToMachines",
                schema: "gateway",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "AppliesToRepo",
                schema: "gateway",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "HookEvent",
                schema: "gateway",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "HookHandler",
                schema: "gateway",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "HookMatch",
                schema: "gateway",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "HookResult",
                schema: "gateway",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "HookTool",
                schema: "gateway",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "gateway",
                table: "session_rules");

            migrationBuilder.DropColumn(
                name: "Agent",
                schema: "gateway",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "AgentEvent",
                schema: "gateway",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "AgentTool",
                schema: "gateway",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "DecisionId",
                schema: "gateway",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "EvaluationMicroseconds",
                schema: "gateway",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "HookEvent",
                schema: "gateway",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "HookTool",
                schema: "gateway",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "gateway",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "Machine",
                schema: "gateway",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "MatchedRuleIds",
                schema: "gateway",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "OverriddenRuleIds",
                schema: "gateway",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "Repo",
                schema: "gateway",
                table: "session_rule_firings");

            migrationBuilder.DropColumn(
                name: "Subject",
                schema: "gateway",
                table: "session_rule_firings");
        }
    }
}
