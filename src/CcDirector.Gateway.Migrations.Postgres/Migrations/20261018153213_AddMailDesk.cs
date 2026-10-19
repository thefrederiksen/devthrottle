using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddMailDesk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mail_desk_channels",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    MachinesJson = table.Column<string>(type: "text", nullable: false),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Seat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ChangedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RemovedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemovedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RemovedWhy = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_desk_channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mail_desk_decisions",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    ReceivedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SenderAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Layer = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    RouteFactory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RouteSeat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    CostMicros = table.Column<long>(type: "bigint", nullable: true),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    OutcomeDetail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DecidedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_desk_decisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mail_desk_list_changes",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: false),
                    VersionBefore = table.Column<int>(type: "integer", nullable: true),
                    VersionAfter = table.Column<int>(type: "integer", nullable: true),
                    WhoKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WhoId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    WhoName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Why = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_desk_list_changes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mail_desk_routed",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Seat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReturnNote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReturnedCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_desk_routed", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mail_desk_rules",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Document = table.Column<string>(type: "text", nullable: false),
                    SavedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SavedByKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SavedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_desk_rules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_channels_one_active_address",
                schema: "gateway",
                table: "mail_desk_channels",
                columns: new[] { "tenant_id", "Kind", "Address" },
                unique: true,
                filter: "\"RemovedUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_channels_tenant_id",
                schema: "gateway",
                table: "mail_desk_channels",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_decisions_tenant_id",
                schema: "gateway",
                table: "mail_desk_decisions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_decisions_tenant_id_ChannelId_DecidedUtc",
                schema: "gateway",
                table: "mail_desk_decisions",
                columns: new[] { "tenant_id", "ChannelId", "DecidedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_decisions_tenant_id_ChannelId_MessageKey",
                schema: "gateway",
                table: "mail_desk_decisions",
                columns: new[] { "tenant_id", "ChannelId", "MessageKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_decisions_tenant_id_DecidedUtc",
                schema: "gateway",
                table: "mail_desk_decisions",
                columns: new[] { "tenant_id", "DecidedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_list_changes_tenant_id",
                schema: "gateway",
                table: "mail_desk_list_changes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_list_changes_tenant_id_AtUtc",
                schema: "gateway",
                table: "mail_desk_list_changes",
                columns: new[] { "tenant_id", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_list_changes_tenant_id_Kind_AtUtc",
                schema: "gateway",
                table: "mail_desk_list_changes",
                columns: new[] { "tenant_id", "Kind", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_routed_tenant_id",
                schema: "gateway",
                table: "mail_desk_routed",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_routed_tenant_id_DecisionId",
                schema: "gateway",
                table: "mail_desk_routed",
                columns: new[] { "tenant_id", "DecisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_routed_tenant_id_State",
                schema: "gateway",
                table: "mail_desk_routed",
                columns: new[] { "tenant_id", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_rules_tenant_id",
                schema: "gateway",
                table: "mail_desk_rules",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_mail_desk_rules_tenant_id_Version",
                schema: "gateway",
                table: "mail_desk_rules",
                columns: new[] { "tenant_id", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mail_desk_channels",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "mail_desk_decisions",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "mail_desk_list_changes",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "mail_desk_routed",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "mail_desk_rules",
                schema: "gateway");
        }
    }
}
