using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionAndScheduleFactory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Factory",
                table: "session_history",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Factory",
                table: "cron_jobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Factory",
                table: "session_history");

            migrationBuilder.DropColumn(
                name: "Factory",
                table: "cron_jobs");
        }
    }
}
