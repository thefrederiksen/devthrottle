using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRaisedSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "raised_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RaisedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RaisedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raised_sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_raised_sessions_tenant_id",
                table: "raised_sessions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_raised_sessions_tenant_id_SessionId",
                table: "raised_sessions",
                columns: new[] { "tenant_id", "SessionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "raised_sessions");
        }
    }
}
