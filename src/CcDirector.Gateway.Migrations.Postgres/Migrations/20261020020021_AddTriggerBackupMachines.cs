using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTriggerBackupMachines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackupMachinesJson",
                schema: "gateway",
                table: "triggers",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ClaimedByMachine",
                schema: "gateway",
                table: "triggers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            // Every claim held before backup machines existed is by a Director on the trigger's own machine.
            migrationBuilder.Sql("UPDATE gateway.triggers SET \"ClaimedByMachine\" = \"Machine\" WHERE \"ClaimedByDirectorId\" <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackupMachinesJson",
                schema: "gateway",
                table: "triggers");

            migrationBuilder.DropColumn(
                name: "ClaimedByMachine",
                schema: "gateway",
                table: "triggers");
        }
    }
}
