using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionRuleChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_rule_changes",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Agent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Before = table.Column<string>(type: "text", nullable: false),
                    After = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_rule_changes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_changes_tenant_id",
                schema: "gateway",
                table: "session_rule_changes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_changes_tenant_id_RuleId_Sequence",
                schema: "gateway",
                table: "session_rule_changes",
                columns: new[] { "tenant_id", "RuleId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_changes_tenant_id_Sequence",
                schema: "gateway",
                table: "session_rule_changes",
                columns: new[] { "tenant_id", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_changes_tenant_id_SessionId_Sequence",
                schema: "gateway",
                table: "session_rule_changes",
                columns: new[] { "tenant_id", "SessionId", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_rule_changes",
                schema: "gateway");
        }
    }
}
