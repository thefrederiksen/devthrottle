using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionRuleChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_rule_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ChangedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Agent = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Before = table.Column<string>(type: "TEXT", nullable: false),
                    After = table.Column<string>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_rule_changes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_changes_tenant_id",
                table: "session_rule_changes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_changes_tenant_id_RuleId_Sequence",
                table: "session_rule_changes",
                columns: new[] { "tenant_id", "RuleId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_changes_tenant_id_Sequence",
                table: "session_rule_changes",
                columns: new[] { "tenant_id", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_session_rule_changes_tenant_id_SessionId_Sequence",
                table: "session_rule_changes",
                columns: new[] { "tenant_id", "SessionId", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_rule_changes");
        }
    }
}
