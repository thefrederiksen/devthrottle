using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddContributorNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contributor_notice_choices",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DailyHour = table.Column<int>(type: "integer", nullable: false),
                    DailyHandledLocalDate = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contributor_notice_choices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "contributor_notices",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientSubject = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SettledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FailedTries = table.Column<int>(type: "integer", nullable: false),
                    NextTryAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contributor_notices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contributor_notice_choices_tenant_id",
                schema: "gateway",
                table: "contributor_notice_choices",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_contributor_notices_tenant_id",
                schema: "gateway",
                table: "contributor_notices",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_contributor_notices_tenant_id_SettledAtUtc_CreatedAtUtc",
                schema: "gateway",
                table: "contributor_notices",
                columns: new[] { "tenant_id", "SettledAtUtc", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contributor_notice_choices",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "contributor_notices",
                schema: "gateway");
        }
    }
}
