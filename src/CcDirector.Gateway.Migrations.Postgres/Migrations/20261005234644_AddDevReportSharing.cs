using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDevReportSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthorSubject",
                schema: "gateway",
                table: "dev_reports",
                type: "text",
                nullable: true,
                collation: "C");

            migrationBuilder.CreateTable(
                name: "dev_report_comments",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromSubject = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    ToSubject = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Text = table.Column<string>(type: "text", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_report_comments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dev_report_recipients",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientSubject = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    SentBySubject = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    SentVersion = table.Column<int>(type: "integer", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_report_recipients", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dev_reports_tenant_id_AuthorSubject",
                schema: "gateway",
                table: "dev_reports",
                columns: new[] { "tenant_id", "AuthorSubject" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_comments_tenant_id",
                schema: "gateway",
                table: "dev_report_comments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_comments_tenant_id_ReportId_AtUtc",
                schema: "gateway",
                table: "dev_report_comments",
                columns: new[] { "tenant_id", "ReportId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_recipients_tenant_id",
                schema: "gateway",
                table: "dev_report_recipients",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_recipients_tenant_id_RecipientSubject_SentAtUtc",
                schema: "gateway",
                table: "dev_report_recipients",
                columns: new[] { "tenant_id", "RecipientSubject", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_recipients_tenant_id_ReportId_RecipientSubject",
                schema: "gateway",
                table: "dev_report_recipients",
                columns: new[] { "tenant_id", "ReportId", "RecipientSubject" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dev_report_comments",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "dev_report_recipients",
                schema: "gateway");

            migrationBuilder.DropIndex(
                name: "IX_dev_reports_tenant_id_AuthorSubject",
                schema: "gateway",
                table: "dev_reports");

            migrationBuilder.DropColumn(
                name: "AuthorSubject",
                schema: "gateway",
                table: "dev_reports");
        }
    }
}
