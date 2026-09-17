using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDevReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dev_report_items",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    ClientItemId = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    AnchorJson = table.Column<string>(type: "text", nullable: true),
                    QuestionId = table.Column<string>(type: "text", nullable: false),
                    Question = table.Column<string>(type: "text", nullable: false),
                    OptionValue = table.Column<string>(type: "text", nullable: false),
                    OptionLabel = table.Column<string>(type: "text", nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StatusLabel = table.Column<string>(type: "text", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    SenderKind = table.Column<string>(type: "text", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReplacedBy = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_report_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dev_report_replies",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_report_replies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dev_report_versions",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Html = table.Column<string>(type: "text", nullable: false),
                    ByteHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_report_versions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dev_reports",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Key = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_reports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_items_tenant_id",
                schema: "gateway",
                table: "dev_report_items",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_items_tenant_id_ReportId_ClientItemId",
                schema: "gateway",
                table: "dev_report_items",
                columns: new[] { "tenant_id", "ReportId", "ClientItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_items_tenant_id_SessionId_Status",
                schema: "gateway",
                table: "dev_report_items",
                columns: new[] { "tenant_id", "SessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_replies_tenant_id",
                schema: "gateway",
                table: "dev_report_replies",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_replies_tenant_id_ReportId_AtUtc",
                schema: "gateway",
                table: "dev_report_replies",
                columns: new[] { "tenant_id", "ReportId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_versions_tenant_id",
                schema: "gateway",
                table: "dev_report_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_versions_tenant_id_ReportId_Version",
                schema: "gateway",
                table: "dev_report_versions",
                columns: new[] { "tenant_id", "ReportId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_reports_tenant_id",
                schema: "gateway",
                table: "dev_reports",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_dev_reports_tenant_id_SessionId_Key",
                schema: "gateway",
                table: "dev_reports",
                columns: new[] { "tenant_id", "SessionId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dev_report_items",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "dev_report_replies",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "dev_report_versions",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "dev_reports",
                schema: "gateway");
        }
    }
}
