// Scratch edit, for the path-filter proof only. This branch exists to show that a change to a
// migration STARTS the PostgreSQL proofs job. It is never merged.
﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "gateway");

            migrationBuilder.CreateTable(
                name: "account_hosted_ai_spend",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountMicros = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    TransactionCreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ObservedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_hosted_ai_spend", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cron_jobs",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ScheduleKind = table.Column<string>(type: "text", nullable: false),
                    CronExpression = table.Column<string>(type: "text", nullable: true),
                    RunAt = table.Column<string>(type: "text", nullable: true),
                    TimeZoneId = table.Column<string>(type: "text", nullable: false),
                    PreventOverlap = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyOn = table.Column<string>(type: "text", nullable: false),
                    NotifyWebhookUrl = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastFiredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRunUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStatus = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "jsonb", nullable: false),
                    Target = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cron_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cron_runs",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<string>(type: "text", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    ScheduledUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FiredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Machine = table.Column<string>(type: "text", nullable: false),
                    TargetDirectorId = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: true),
                    InfraStatus = table.Column<string>(type: "text", nullable: false),
                    TaskStatus = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cron_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "governance_audit_events",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Category = table.Column<string>(type: "text", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: true),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_governance_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "governance_events",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectKind = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: true),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_governance_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mission_notes",
                schema: "gateway",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Mission = table.Column<string>(type: "text", nullable: false),
                    Why = table.Column<string>(type: "text", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mission_notes", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "push_subscriptions",
                schema: "gateway",
                columns: table => new
                {
                    Endpoint = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    P256dh = table.Column<string>(type: "text", nullable: false),
                    Auth = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_push_subscriptions", x => x.Endpoint);
                });

            migrationBuilder.CreateTable(
                name: "session_spend",
                schema: "gateway",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    AgentKind = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: true),
                    RepoPath = table.Column<string>(type: "text", nullable: true),
                    TokensCaptured = table.Column<bool>(type: "boolean", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheCreationTokens = table.Column<long>(type: "bigint", nullable: false),
                    BillingMode = table.Column<string>(type: "text", nullable: false),
                    MeteredCostMicros = table.Column<long>(type: "bigint", nullable: true),
                    FirstObservedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastObservedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_spend", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "snoozes",
                schema: "gateway",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    SnoozeUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DirectorId = table.Column<string>(type: "text", nullable: true),
                    PendingMinutes = table.Column<int>(type: "integer", nullable: true),
                    OwnerTurnBaselineUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snoozes", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "wingman_instructions",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActiveVersionId = table.Column<string>(type: "text", nullable: true),
                    AckDefaultVersion = table.Column<string>(type: "text", nullable: false),
                    AckDefaultContent = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    Versions = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wingman_instructions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_files",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_files", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_runs",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<string>(type: "text", nullable: false),
                    WorkflowVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowVersion = table.Column<int>(type: "integer", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AcceptanceStatus = table.Column<string>(type: "text", nullable: false),
                    AcceptedBy = table.Column<string>(type: "text", nullable: true),
                    AcceptedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "text", nullable: true),
                    MissionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    RepoPath = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    CriteriaResults = table.Column<string>(type: "jsonb", nullable: true),
                    Participants = table.Column<string>(type: "jsonb", nullable: true),
                    ProofLinks = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_versions",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    WhenToUse = table.Column<string>(type: "text", nullable: false),
                    HumanCheckpoint = table.Column<string>(type: "text", nullable: false),
                    InstructionsMarkdown = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    AuthoredBy = table.Column<string>(type: "text", nullable: false),
                    ChangeNote = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    OutcomeCriteria = table.Column<string>(type: "jsonb", nullable: true),
                    Steps = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_versions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workflows",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    LatestVersion = table.Column<int>(type: "integer", nullable: false),
                    PublishedVersion = table.Column<int>(type: "integer", nullable: true),
                    ShippedContentHash = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "worklists",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Consumer = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worklists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "worklist_items",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkListId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    ItemId = table.Column<string>(type: "text", nullable: false),
                    Area = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worklist_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_worklist_items_worklists_WorkListId",
                        column: x => x.WorkListId,
                        principalSchema: "gateway",
                        principalTable: "worklists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_account_hosted_ai_spend_tenant_id",
                schema: "gateway",
                table: "account_hosted_ai_spend",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_account_hosted_ai_spend_tenant_id_Kind_AmountMicros_Transac~",
                schema: "gateway",
                table: "account_hosted_ai_spend",
                columns: new[] { "tenant_id", "Kind", "AmountMicros", "TransactionCreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_account_hosted_ai_spend_tenant_id_TransactionCreatedUtc",
                schema: "gateway",
                table: "account_hosted_ai_spend",
                columns: new[] { "tenant_id", "TransactionCreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_cron_jobs_tenant_id",
                schema: "gateway",
                table: "cron_jobs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_cron_runs_JobId_Sequence",
                schema: "gateway",
                table: "cron_runs",
                columns: new[] { "JobId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_cron_runs_tenant_id",
                schema: "gateway",
                table: "cron_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_governance_audit_events_tenant_id",
                schema: "gateway",
                table: "governance_audit_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_governance_audit_events_tenant_id_Category_OccurredUtc",
                schema: "gateway",
                table: "governance_audit_events",
                columns: new[] { "tenant_id", "Category", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_governance_audit_events_tenant_id_RunId_OccurredUtc",
                schema: "gateway",
                table: "governance_audit_events",
                columns: new[] { "tenant_id", "RunId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_governance_audit_events_tenant_id_SessionId_OccurredUtc",
                schema: "gateway",
                table: "governance_audit_events",
                columns: new[] { "tenant_id", "SessionId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_governance_events_tenant_id",
                schema: "gateway",
                table: "governance_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_governance_events_tenant_id_OccurredUtc",
                schema: "gateway",
                table: "governance_events",
                columns: new[] { "tenant_id", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_governance_events_tenant_id_RunId_OccurredUtc",
                schema: "gateway",
                table: "governance_events",
                columns: new[] { "tenant_id", "RunId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_governance_events_tenant_id_SessionId_OccurredUtc",
                schema: "gateway",
                table: "governance_events",
                columns: new[] { "tenant_id", "SessionId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_mission_notes_tenant_id",
                schema: "gateway",
                table: "mission_notes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_push_subscriptions_tenant_id",
                schema: "gateway",
                table: "push_subscriptions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_spend_tenant_id",
                schema: "gateway",
                table: "session_spend",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_spend_tenant_id_LastObservedUtc",
                schema: "gateway",
                table: "session_spend",
                columns: new[] { "tenant_id", "LastObservedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_snoozes_tenant_id",
                schema: "gateway",
                table: "snoozes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_wingman_instructions_tenant_id",
                schema: "gateway",
                table: "wingman_instructions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_files_tenant_id",
                schema: "gateway",
                table: "workflow_files",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_files_VersionId",
                schema: "gateway",
                table: "workflow_files",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_MissionId",
                schema: "gateway",
                table: "workflow_runs",
                column: "MissionId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_tenant_id",
                schema: "gateway",
                table: "workflow_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_WorkflowId",
                schema: "gateway",
                table: "workflow_runs",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_tenant_id",
                schema: "gateway",
                table: "workflow_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_WorkflowId_Version",
                schema: "gateway",
                table: "workflow_versions",
                columns: new[] { "WorkflowId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflows_tenant_id",
                schema: "gateway",
                table: "workflows",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_worklist_items_tenant_id",
                schema: "gateway",
                table: "worklist_items",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_worklist_items_WorkListId_Position",
                schema: "gateway",
                table: "worklist_items",
                columns: new[] { "WorkListId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_worklists_tenant_id",
                schema: "gateway",
                table: "worklists",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_hosted_ai_spend",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "cron_jobs",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "cron_runs",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "governance_audit_events",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "governance_events",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "mission_notes",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "push_subscriptions",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "session_spend",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "snoozes",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "wingman_instructions",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "workflow_files",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "workflow_runs",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "workflow_versions",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "workflows",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "worklist_items",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "worklists",
                schema: "gateway");
        }
    }
}
