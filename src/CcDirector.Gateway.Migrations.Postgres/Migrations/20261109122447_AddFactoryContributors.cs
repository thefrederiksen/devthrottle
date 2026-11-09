using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryContributors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_contributors",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Factory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContributorSubject = table.Column<string>(type: "text", nullable: false),
                    SharedBySubject = table.Column<string>(type: "text", nullable: false),
                    SharedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_contributors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_contributors_tenant_id",
                schema: "gateway",
                table: "factory_contributors",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_contributors_tenant_id_ContributorSubject",
                schema: "gateway",
                table: "factory_contributors",
                columns: new[] { "tenant_id", "ContributorSubject" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_contributors_tenant_id_Factory_ContributorSubject",
                schema: "gateway",
                table: "factory_contributors",
                columns: new[] { "tenant_id", "Factory", "ContributorSubject" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_contributors",
                schema: "gateway");
        }
    }
}
