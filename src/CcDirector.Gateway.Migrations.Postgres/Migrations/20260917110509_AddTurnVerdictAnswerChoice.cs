using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <summary>
    /// The Fleet Manager mission, step 7 fixes: a verdict's answer is stored with WHAT was sent - the verdict it
    /// answered (id and turn end), the option positions and their words - so the walkthrough records the answer
    /// route's choice and never a client's. One nullable column; no existing row changes.
    /// </summary>
    public partial class AddTurnVerdictAnswerChoice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnswerJson",
                schema: "gateway",
                table: "turn_verdicts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnswerJson",
                schema: "gateway",
                table: "turn_verdicts");
        }
    }
}
