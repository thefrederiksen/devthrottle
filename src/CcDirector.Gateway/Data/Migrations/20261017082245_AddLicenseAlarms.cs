using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseAlarms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "license_alarms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MachineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AccountEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Problem = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    OpenedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EmailState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    EmailSentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_alarms", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_license_alarms_tenant_id",
                table: "license_alarms",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_license_alarms_tenant_id_ClosedAtUtc",
                table: "license_alarms",
                columns: new[] { "tenant_id", "ClosedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "license_alarms");
        }
    }
}
