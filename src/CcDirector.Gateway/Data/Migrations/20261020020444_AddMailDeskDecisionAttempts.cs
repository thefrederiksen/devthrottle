using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMailDeskDecisionAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Mail Desk phase 3, section 8: the rules version a decision was made by, and how many times its email was
            // screened. A row written before this was screened once, so its attempt count is 1.
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "mail_desk_decisions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "RulesVersion",
                table: "mail_desk_decisions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "mail_desk_decisions");

            migrationBuilder.DropColumn(
                name: "RulesVersion",
                table: "mail_desk_decisions");
        }
    }
}
