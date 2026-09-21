using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_activity",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Factory = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FactoryAgent = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FactoryAgentVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    What = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Link = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CorrectsId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_activity", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_activity_tenant_id",
                schema: "gateway",
                table: "factory_activity",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_activity_tenant_id_Factory_OccurredUtc",
                schema: "gateway",
                table: "factory_activity",
                columns: new[] { "tenant_id", "Factory", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_activity_tenant_id_FactoryAgent_OccurredUtc",
                schema: "gateway",
                table: "factory_activity",
                columns: new[] { "tenant_id", "FactoryAgent", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_activity_tenant_id_Outcome_OccurredUtc",
                schema: "gateway",
                table: "factory_activity",
                columns: new[] { "tenant_id", "Outcome", "OccurredUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_activity",
                schema: "gateway");
        }
    }
}
