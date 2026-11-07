using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerDealtWithToFactoryInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OwnerDealtWithAtUtc",
                table: "factory_inbox_messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerDealtWithBy",
                table: "factory_inbox_messages",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerDealtWithAtUtc",
                table: "factory_inbox_messages");

            migrationBuilder.DropColumn(
                name: "OwnerDealtWithBy",
                table: "factory_inbox_messages");
        }
    }
}
