using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamShowcase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "showcase_tag",
                schema: "gateway",
                table: "team_requests",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "showcase_tag",
                schema: "gateway",
                table: "team_request_changes",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShowcaseTag",
                schema: "gateway",
                table: "team_mentor_blocks",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "display_email",
                schema: "gateway",
                table: "team_members",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                schema: "gateway",
                table: "team_members",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "showcase_tag",
                schema: "gateway",
                table: "team_members",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShowcaseTag",
                schema: "gateway",
                table: "dev_reports",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShowcaseTag",
                schema: "gateway",
                table: "dev_report_versions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShowcaseTag",
                schema: "gateway",
                table: "dev_report_recipients",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "showcase_tag",
                schema: "gateway",
                table: "team_requests");

            migrationBuilder.DropColumn(
                name: "showcase_tag",
                schema: "gateway",
                table: "team_request_changes");

            migrationBuilder.DropColumn(
                name: "ShowcaseTag",
                schema: "gateway",
                table: "team_mentor_blocks");

            migrationBuilder.DropColumn(
                name: "display_email",
                schema: "gateway",
                table: "team_members");

            migrationBuilder.DropColumn(
                name: "display_name",
                schema: "gateway",
                table: "team_members");

            migrationBuilder.DropColumn(
                name: "showcase_tag",
                schema: "gateway",
                table: "team_members");

            migrationBuilder.DropColumn(
                name: "ShowcaseTag",
                schema: "gateway",
                table: "dev_reports");

            migrationBuilder.DropColumn(
                name: "ShowcaseTag",
                schema: "gateway",
                table: "dev_report_versions");

            migrationBuilder.DropColumn(
                name: "ShowcaseTag",
                schema: "gateway",
                table: "dev_report_recipients");
        }
    }
}
