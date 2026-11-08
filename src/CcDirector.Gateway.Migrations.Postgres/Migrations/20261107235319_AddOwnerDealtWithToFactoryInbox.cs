using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerDealtWithToFactoryInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OwnerDealtWithAtUtc",
                schema: "gateway",
                table: "factory_inbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerDealtWithBy",
                schema: "gateway",
                table: "factory_inbox_messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                collation: "C");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerDealtWithAtUtc",
                schema: "gateway",
                table: "factory_inbox_messages");

            migrationBuilder.DropColumn(
                name: "OwnerDealtWithBy",
                schema: "gateway",
                table: "factory_inbox_messages");
        }
    }
}
