using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryEmailReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_email_reports",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SlotKey = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    BoundaryUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComposedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Detail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Html = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    FactoriesJson = table.Column<string>(type: "text", nullable: false),
                    ProblemCount = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProviderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_email_reports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_email_reports_tenant_id",
                schema: "gateway",
                table: "factory_email_reports",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_email_reports_tenant_id_BoundaryUtc",
                schema: "gateway",
                table: "factory_email_reports",
                columns: new[] { "tenant_id", "BoundaryUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_email_reports_tenant_id_SlotKey",
                schema: "gateway",
                table: "factory_email_reports",
                columns: new[] { "tenant_id", "SlotKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_email_reports",
                schema: "gateway");
        }
    }
}
