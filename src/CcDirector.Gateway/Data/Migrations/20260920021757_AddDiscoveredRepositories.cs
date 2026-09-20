using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <summary>
    /// The one-repository-list mission, phase 2: the known-repositories catalog gains the FOUND-BUT-NEVER-
    /// OPENED half, in the same table rather than a second one.
    ///
    /// <c>LastUsedUtc</c> becomes NULLABLE, and null IS "found under a registered root folder and never
    /// opened" - already the Director dialog's own rule. <c>DiscoveredByDirectorId</c> records which
    /// Director reported the repository, which is the scope a root-folder removal reconciles against, and
    /// <c>LastSeenUtc</c> records when it last did.
    ///
    /// Every existing row keeps its data. It has a last-used time, so it reads as the used half - which is
    /// the truth about it - and the read side serves exactly what it served before.
    /// </summary>
    public partial class AddDiscoveredRepositories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "LastUsedUtc",
                table: "known_repositories",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "DiscoveredByDirectorId",
                table: "known_repositories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenUtc",
                table: "known_repositories",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscoveredByDirectorId",
                table: "known_repositories");

            migrationBuilder.DropColumn(
                name: "LastSeenUtc",
                table: "known_repositories");

            migrationBuilder.AlterColumn<DateTime>(
                name: "LastUsedUtc",
                table: "known_repositories",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
