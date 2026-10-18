using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddApiModeConversationItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_mode_conversation_items",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    ItemId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false, collation: "C"),
                    Ordinal = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    Tone = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TurnId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    FirstGenerationStartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstSequence = table.Column<long>(type: "bigint", nullable: false),
                    LastGeneration = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastGenerationStartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    Characters = table.Column<int>(type: "integer", nullable: false),
                    EarlierDropped = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_mode_conversation_items", x => new { x.tenant_id, x.SessionId, x.ItemId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_mode_conversation_items_tenant_id",
                schema: "gateway",
                table: "api_mode_conversation_items",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_mode_conversation_items_tenant_id_SessionId_Ordinal",
                schema: "gateway",
                table: "api_mode_conversation_items",
                columns: new[] { "tenant_id", "SessionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_mode_conversation_items_tenant_id_UpdatedAtUtc",
                schema: "gateway",
                table: "api_mode_conversation_items",
                columns: new[] { "tenant_id", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_mode_conversation_items",
                schema: "gateway");
        }
    }
}
