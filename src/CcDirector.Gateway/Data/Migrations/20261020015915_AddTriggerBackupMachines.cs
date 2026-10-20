using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTriggerBackupMachines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackupMachinesJson",
                table: "triggers",
                type: "TEXT",
                maxLength: 2048,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ClaimedByMachine",
                table: "triggers",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            // Every claim held before backup machines existed is by a Director on the trigger's own machine.
            migrationBuilder.Sql("UPDATE triggers SET ClaimedByMachine = Machine WHERE ClaimedByDirectorId <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackupMachinesJson",
                table: "triggers");

            migrationBuilder.DropColumn(
                name: "ClaimedByMachine",
                table: "triggers");
        }
    }
}
