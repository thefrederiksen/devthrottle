using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDevReportSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthorSubject",
                table: "dev_reports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "dev_report_comments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReportId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromSubject = table.Column<string>(type: "TEXT", nullable: false),
                    ToSubject = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_report_comments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dev_report_recipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReportId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientSubject = table.Column<string>(type: "TEXT", nullable: false),
                    SentBySubject = table.Column<string>(type: "TEXT", nullable: false),
                    SentVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_report_recipients", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dev_reports_tenant_id_AuthorSubject",
                table: "dev_reports",
                columns: new[] { "tenant_id", "AuthorSubject" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_comments_tenant_id",
                table: "dev_report_comments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_comments_tenant_id_ReportId_AtUtc",
                table: "dev_report_comments",
                columns: new[] { "tenant_id", "ReportId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_recipients_tenant_id",
                table: "dev_report_recipients",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_recipients_tenant_id_RecipientSubject_SentAtUtc",
                table: "dev_report_recipients",
                columns: new[] { "tenant_id", "RecipientSubject", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_recipients_tenant_id_ReportId_RecipientSubject",
                table: "dev_report_recipients",
                columns: new[] { "tenant_id", "ReportId", "RecipientSubject" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dev_report_comments");

            migrationBuilder.DropTable(
                name: "dev_report_recipients");

            migrationBuilder.DropIndex(
                name: "IX_dev_reports_tenant_id_AuthorSubject",
                table: "dev_reports");

            migrationBuilder.DropColumn(
                name: "AuthorSubject",
                table: "dev_reports");
        }
    }
}
