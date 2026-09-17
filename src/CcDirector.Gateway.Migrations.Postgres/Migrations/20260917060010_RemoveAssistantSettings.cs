using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <summary>
    /// The Fleet Manager mission, step 9: the Assistant was removed, and with it the two per-account settings
    /// only it used - the model its fleet brain ran on (<c>car_mode_model</c>) and the Car Mode end phrase its
    /// spoken help once quoted (<c>car_mode_end_phrase</c>). Neither key is a setting any more, so their stored
    /// rows are deleted for every account. The schema does not change. Its twin for the other database provider
    /// carries the same name and deletes the same rows.
    /// </summary>
    public partial class RemoveAssistantSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM "gateway"."tenant_settings"
                WHERE "Key" IN ('car_mode_model', 'car_mode_end_phrase');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to put back: the deleted values belonged to a removed feature, and no code reads the keys.
        }
    }
}
