using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryAttachmentRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RemovedAtUtc",
                schema: "gateway",
                table: "factory_attachments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemovedBy",
                schema: "gateway",
                table: "factory_attachments",
                type: "character varying(230)",
                maxLength: 230,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RemovedAtUtc",
                schema: "gateway",
                table: "factory_attachments");

            migrationBuilder.DropColumn(
                name: "RemovedBy",
                schema: "gateway",
                table: "factory_attachments");
        }
    }
}
