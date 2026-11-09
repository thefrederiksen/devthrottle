using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContributorNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contributor_notice_choices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DailyHour = table.Column<int>(type: "INTEGER", nullable: false),
                    DailyHandledLocalDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contributor_notice_choices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "contributor_notices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientSubject = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SettledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    FailedTries = table.Column<int>(type: "INTEGER", nullable: false),
                    NextTryAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contributor_notices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contributor_notice_choices_tenant_id",
                table: "contributor_notice_choices",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_contributor_notices_tenant_id",
                table: "contributor_notices",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_contributor_notices_tenant_id_SettledAtUtc_CreatedAtUtc",
                table: "contributor_notices",
                columns: new[] { "tenant_id", "SettledAtUtc", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contributor_notice_choices");

            migrationBuilder.DropTable(
                name: "contributor_notices");
        }
    }
}
