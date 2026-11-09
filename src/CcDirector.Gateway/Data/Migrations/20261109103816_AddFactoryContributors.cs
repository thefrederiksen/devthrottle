using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryContributors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_contributors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Factory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContributorSubject = table.Column<string>(type: "TEXT", nullable: false),
                    SharedBySubject = table.Column<string>(type: "TEXT", nullable: false),
                    SharedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_contributors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_factory_contributors_tenant_id",
                table: "factory_contributors",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_factory_contributors_tenant_id_ContributorSubject",
                table: "factory_contributors",
                columns: new[] { "tenant_id", "ContributorSubject" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_contributors_tenant_id_Factory_ContributorSubject",
                table: "factory_contributors",
                columns: new[] { "tenant_id", "Factory", "ContributorSubject" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_contributors");
        }
    }
}
