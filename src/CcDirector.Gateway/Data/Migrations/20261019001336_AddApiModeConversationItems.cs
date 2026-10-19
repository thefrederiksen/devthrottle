using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiModeConversationItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_mode_conversation_items",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ItemId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Ordinal = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    Tone = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TurnId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FirstGenerationStartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FirstSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    LastGeneration = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastGenerationStartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Characters = table.Column<int>(type: "INTEGER", nullable: false),
                    EarlierDropped = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_mode_conversation_items", x => new { x.tenant_id, x.SessionId, x.ItemId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_mode_conversation_items_tenant_id",
                table: "api_mode_conversation_items",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_mode_conversation_items_tenant_id_SessionId_Ordinal",
                table: "api_mode_conversation_items",
                columns: new[] { "tenant_id", "SessionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_mode_conversation_items_tenant_id_UpdatedAtUtc",
                table: "api_mode_conversation_items",
                columns: new[] { "tenant_id", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_mode_conversation_items");
        }
    }
}
