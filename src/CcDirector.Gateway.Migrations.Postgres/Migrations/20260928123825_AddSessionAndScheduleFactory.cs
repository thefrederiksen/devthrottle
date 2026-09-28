using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionAndScheduleFactory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Factory",
                schema: "gateway",
                table: "session_history",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Factory",
                schema: "gateway",
                table: "cron_jobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Factory",
                schema: "gateway",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "Factory",
                schema: "gateway",
                table: "cron_jobs");
        }
    }
}
