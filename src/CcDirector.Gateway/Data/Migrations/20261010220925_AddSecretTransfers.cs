using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecretTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "secret_transfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TransferId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    EntryName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FromMachine = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ToMachine = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Replace = table.Column<bool>(type: "INTEGER", nullable: false),
                    AskedBySessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AskedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AnsweredWhere = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    AnsweredBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ApprovalWords = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AnsweredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AcceptedReceiverFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_transfers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_secret_transfers_tenant_id",
                table: "secret_transfers",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_secret_transfers_tenant_id_CreatedAtUtc",
                table: "secret_transfers",
                columns: new[] { "tenant_id", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_secret_transfers_tenant_id_State",
                table: "secret_transfers",
                columns: new[] { "tenant_id", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_secret_transfers_tenant_id_TransferId",
                table: "secret_transfers",
                columns: new[] { "tenant_id", "TransferId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "secret_transfers");
        }
    }
}
