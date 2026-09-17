using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetMessageReplyMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RepliedAtUtc",
                table: "fleet_messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReplyOverdueAtUtc",
                table: "fleet_messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_fleet_messages_tenant_id_ReplyByUtc",
                table: "fleet_messages",
                columns: new[] { "tenant_id", "ReplyByUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_fleet_messages_tenant_id_ReplyByUtc",
                table: "fleet_messages");

            migrationBuilder.DropColumn(
                name: "RepliedAtUtc",
                table: "fleet_messages");

            migrationBuilder.DropColumn(
                name: "ReplyOverdueAtUtc",
                table: "fleet_messages");
        }
    }
}
