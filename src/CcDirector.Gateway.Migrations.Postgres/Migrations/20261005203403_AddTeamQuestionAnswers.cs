using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamQuestionAnswers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnswererSubject",
                schema: "gateway",
                table: "dev_report_items",
                type: "text",
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "QuestionId",
                schema: "gateway",
                table: "dev_report_comments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_report_items_tenant_id_AnswererSubject",
                schema: "gateway",
                table: "dev_report_items",
                columns: new[] { "tenant_id", "AnswererSubject" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_dev_report_items_tenant_id_AnswererSubject",
                schema: "gateway",
                table: "dev_report_items");

            migrationBuilder.DropColumn(
                name: "AnswererSubject",
                schema: "gateway",
                table: "dev_report_items");

            migrationBuilder.DropColumn(
                name: "QuestionId",
                schema: "gateway",
                table: "dev_report_comments");
        }
    }
}
