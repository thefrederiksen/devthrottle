using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseAlarms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "license_alarms",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Problem = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Action = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    OpenedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EmailState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    EmailSentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_alarms", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_license_alarms_tenant_id",
                schema: "gateway",
                table: "license_alarms",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_license_alarms_tenant_id_ClosedAtUtc",
                schema: "gateway",
                table: "license_alarms",
                columns: new[] { "tenant_id", "ClosedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "license_alarms",
                schema: "gateway");
        }
    }
}
