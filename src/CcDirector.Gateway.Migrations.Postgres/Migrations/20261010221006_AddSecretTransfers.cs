using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSecretTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "secret_transfers",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TransferId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    EntryName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FromMachine = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ToMachine = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Replace = table.Column<bool>(type: "boolean", nullable: false),
                    AskedBySessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AskedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnsweredWhere = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    AnsweredBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ApprovalWords = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AnsweredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcceptedReceiverFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_transfers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_secret_transfers_tenant_id",
                schema: "gateway",
                table: "secret_transfers",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_secret_transfers_tenant_id_CreatedAtUtc",
                schema: "gateway",
                table: "secret_transfers",
                columns: new[] { "tenant_id", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_secret_transfers_tenant_id_State",
                schema: "gateway",
                table: "secret_transfers",
                columns: new[] { "tenant_id", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_secret_transfers_tenant_id_TransferId",
                schema: "gateway",
                table: "secret_transfers",
                columns: new[] { "tenant_id", "TransferId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "secret_transfers",
                schema: "gateway");
        }
    }
}
