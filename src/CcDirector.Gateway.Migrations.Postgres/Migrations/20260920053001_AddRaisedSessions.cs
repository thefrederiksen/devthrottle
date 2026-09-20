using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddRaisedSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "raised_sessions",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    RaisedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RaisedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raised_sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_raised_sessions_tenant_id",
                schema: "gateway",
                table: "raised_sessions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_raised_sessions_tenant_id_SessionId",
                schema: "gateway",
                table: "raised_sessions",
                columns: new[] { "tenant_id", "SessionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "raised_sessions",
                schema: "gateway");
        }
    }
}
