using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleFailureNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NotifyDefaultedUtc",
                schema: "gateway",
                table: "cron_jobs",
                type: "timestamp with time zone",
                nullable: true);

            // A FAILED SCHEDULE TELLS ITS OWNER BY DEFAULT (issue #4436). Every schedule still on the old default - "none",
            // or no policy at all - moves to "failure", and when is recorded on it, so its page can say the default changed
            // and how to turn it off.
            migrationBuilder.Sql(
                "UPDATE gateway.cron_jobs SET \"NotifyOn\" = 'failure', \"NotifyDefaultedUtc\" = CURRENT_TIMESTAMP " +
                "WHERE \"NotifyOn\" = 'none' OR \"NotifyOn\" = '' OR \"NotifyOn\" IS NULL;");

            migrationBuilder.CreateTable(
                name: "cron_failure_notices",
                schema: "gateway",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    JobId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Cause = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    LastSentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HeldBack = table.Column<int>(type: "integer", nullable: false),
                    LastCountedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastCountedRunId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cron_failure_notices", x => new { x.tenant_id, x.JobId, x.Cause });
                });

            migrationBuilder.CreateIndex(
                name: "IX_cron_failure_notices_tenant_id",
                schema: "gateway",
                table: "cron_failure_notices",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cron_failure_notices",
                schema: "gateway");

            // Going back puts the schedules this moved back on "none".
            migrationBuilder.Sql(
                "UPDATE gateway.cron_jobs SET \"NotifyOn\" = 'none' WHERE \"NotifyDefaultedUtc\" IS NOT NULL AND \"NotifyOn\" = 'failure';");

            migrationBuilder.DropColumn(
                name: "NotifyDefaultedUtc",
                schema: "gateway",
                table: "cron_jobs");
        }
    }
}
