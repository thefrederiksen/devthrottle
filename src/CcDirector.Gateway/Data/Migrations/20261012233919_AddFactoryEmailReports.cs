using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryEmailReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_email_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SlotKey = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    BoundaryUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ComposedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Html = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    FactoriesJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProblemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_email_reports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_email_reports_tenant_id",
                table: "factory_email_reports",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_email_reports_tenant_id_BoundaryUtc",
                table: "factory_email_reports",
                columns: new[] { "tenant_id", "BoundaryUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_email_reports_tenant_id_SlotKey",
                table: "factory_email_reports",
                columns: new[] { "tenant_id", "SlotKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_email_reports");
        }
    }
}
