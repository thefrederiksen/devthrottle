using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamShowcase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "showcase_tag",
                table: "team_requests",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "showcase_tag",
                table: "team_request_changes",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShowcaseTag",
                table: "team_mentor_blocks",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "display_email",
                table: "team_members",
                type: "TEXT",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "team_members",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "showcase_tag",
                table: "team_members",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShowcaseTag",
                table: "dev_reports",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShowcaseTag",
                table: "dev_report_versions",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShowcaseTag",
                table: "dev_report_recipients",
                type: "TEXT",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "showcase_tag",
                table: "team_requests");

            migrationBuilder.DropColumn(
                name: "showcase_tag",
                table: "team_request_changes");

            migrationBuilder.DropColumn(
                name: "ShowcaseTag",
                table: "team_mentor_blocks");

            migrationBuilder.DropColumn(
                name: "display_email",
                table: "team_members");

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "team_members");

            migrationBuilder.DropColumn(
                name: "showcase_tag",
                table: "team_members");

            migrationBuilder.DropColumn(
                name: "ShowcaseTag",
                table: "dev_reports");

            migrationBuilder.DropColumn(
                name: "ShowcaseTag",
                table: "dev_report_versions");

            migrationBuilder.DropColumn(
                name: "ShowcaseTag",
                table: "dev_report_recipients");
        }
    }
}
