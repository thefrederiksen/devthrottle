using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class IndexFactoryActivityReads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_factory_activity_tenant_id_OccurredUtc",
                schema: "gateway",
                table: "factory_activity",
                columns: new[] { "tenant_id", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_activity_tenant_id_SessionId_Outcome_OccurredUtc",
                schema: "gateway",
                table: "factory_activity",
                columns: new[] { "tenant_id", "SessionId", "Outcome", "OccurredUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_factory_activity_tenant_id_OccurredUtc",
                schema: "gateway",
                table: "factory_activity");

            migrationBuilder.DropIndex(
                name: "IX_factory_activity_tenant_id_SessionId_Outcome_OccurredUtc",
                schema: "gateway",
                table: "factory_activity");
        }
    }
}
