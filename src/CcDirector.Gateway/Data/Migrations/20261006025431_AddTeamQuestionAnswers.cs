using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamQuestionAnswers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnswererSubject",
                table: "dev_report_items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuestionId",
                table: "dev_report_comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_items_tenant_id_AnswererSubject",
                table: "dev_report_items",
                columns: new[] { "tenant_id", "AnswererSubject" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_dev_report_items_tenant_id_AnswererSubject",
                table: "dev_report_items");

            migrationBuilder.DropColumn(
                name: "AnswererSubject",
                table: "dev_report_items");

            migrationBuilder.DropColumn(
                name: "QuestionId",
                table: "dev_report_comments");
        }
    }
}
