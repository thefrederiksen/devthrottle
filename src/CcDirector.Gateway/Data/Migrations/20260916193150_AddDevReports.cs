using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDevReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dev_report_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReportId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClientItemId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    AnchorJson = table.Column<string>(type: "TEXT", nullable: true),
                    QuestionId = table.Column<string>(type: "TEXT", nullable: false),
                    Question = table.Column<string>(type: "TEXT", nullable: false),
                    OptionValue = table.Column<string>(type: "TEXT", nullable: false),
                    OptionLabel = table.Column<string>(type: "TEXT", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StatusLabel = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    SenderKind = table.Column<string>(type: "TEXT", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReplacedBy = table.Column<string>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_report_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dev_report_replies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReportId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_report_replies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dev_report_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReportId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Html = table.Column<string>(type: "TEXT", nullable: false),
                    ByteHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_report_versions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dev_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_reports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_items_tenant_id",
                table: "dev_report_items",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_items_tenant_id_ReportId_ClientItemId",
                table: "dev_report_items",
                columns: new[] { "tenant_id", "ReportId", "ClientItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_items_tenant_id_SessionId_Status",
                table: "dev_report_items",
                columns: new[] { "tenant_id", "SessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_replies_tenant_id",
                table: "dev_report_replies",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_replies_tenant_id_ReportId_AtUtc",
                table: "dev_report_replies",
                columns: new[] { "tenant_id", "ReportId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_versions_tenant_id",
                table: "dev_report_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_versions_tenant_id_ReportId_Version",
                table: "dev_report_versions",
                columns: new[] { "tenant_id", "ReportId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_reports_tenant_id",
                table: "dev_reports",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_reports_tenant_id_SessionId_Key",
                table: "dev_reports",
                columns: new[] { "tenant_id", "SessionId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dev_report_items");

            migrationBuilder.DropTable(
                name: "dev_report_replies");

            migrationBuilder.DropTable(
                name: "dev_report_versions");

            migrationBuilder.DropTable(
                name: "dev_reports");
        }
    }
}
