using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <summary>
    /// A reading takes TWO model calls, and until now only the first was recorded (the owner's ruling of
    /// 2026-09-18). These six nullable columns hold the second: the exact prompt sent to the narrator, its answer
    /// as received, how long it took, the two cut flags the judge's own texts already have, and the sentence
    /// saying why a call produced no words. There is no second PACKAGE column because there is no second package:
    /// the narration is given the same one the judge was, which PackageJson already holds.
    ///
    /// Every existing row keeps its data and reads as a stop whose narration was not recorded, which is the truth
    /// about it.
    /// </summary>
    public partial class AddWingmanNarrationCallTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NarrationFailureDetail",
                schema: "gateway",
                table: "turn_verdict_traces",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NarrationPrompt",
                schema: "gateway",
                table: "turn_verdict_traces",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NarrationPromptTruncated",
                schema: "gateway",
                table: "turn_verdict_traces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "NarrationRawReply",
                schema: "gateway",
                table: "turn_verdict_traces",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NarrationRawReplyTruncated",
                schema: "gateway",
                table: "turn_verdict_traces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "NarrationSeconds",
                schema: "gateway",
                table: "turn_verdict_traces",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NarrationFailureDetail",
                schema: "gateway",
                table: "turn_verdict_traces");

            migrationBuilder.DropColumn(
                name: "NarrationPrompt",
                schema: "gateway",
                table: "turn_verdict_traces");

            migrationBuilder.DropColumn(
                name: "NarrationPromptTruncated",
                schema: "gateway",
                table: "turn_verdict_traces");

            migrationBuilder.DropColumn(
                name: "NarrationRawReply",
                schema: "gateway",
                table: "turn_verdict_traces");

            migrationBuilder.DropColumn(
                name: "NarrationRawReplyTruncated",
                schema: "gateway",
                table: "turn_verdict_traces");

            migrationBuilder.DropColumn(
                name: "NarrationSeconds",
                schema: "gateway",
                table: "turn_verdict_traces");
        }
    }
}
