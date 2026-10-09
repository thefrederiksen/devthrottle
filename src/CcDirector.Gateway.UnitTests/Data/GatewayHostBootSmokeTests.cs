using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// Boot smoke proof for the hosted Gateway (Step 4b): the Postgres migration set must be findable and
/// applicable through the RUNTIME assembly resolution the hosted host uses, not only when a test project
/// references the migrations project directly. The hosted container publishes CcDirector.Gateway.Host, which
/// references CcDirector.Gateway.Migrations.Postgres so its DLL ships in the image; at boot the Gateway calls
/// Database.Migrate() with MigrationsAssembly "CcDirector.Gateway.Migrations.Postgres", which loads that
/// assembly BY NAME. These facts prove that resolution works.
///
/// The primary "the container carries the DLL" evidence is the publish-output check recorded in the QA doc
/// (the host's publish output contains CcDirector.Gateway.Migrations.Postgres.dll and lists it in the host
/// deps.json). These tests cover the other half: the migration set actually resolves by name (no database),
/// and, when a real Postgres is configured, the real GatewayDatabase startup path applies it.
/// </summary>
public sealed class GatewayHostBootSmokeTests
{
    private const string PostgresMigrationsAssembly = "CcDirector.Gateway.Migrations.Postgres";
    private const string InitialPostgresMigration = "20260718120027_InitialPostgres";

    // The Fleet Manager mission's tables (step 3), named literally so deleting either migration fails here.
    private const string FleetManagerOutcomesPostgresMigration = "20260917090009_AddFleetManagerOutcomes";
    private const string FleetManagerMarkHistoryPostgresMigration = "20260917090109_AddFleetManagerMarkHistory";
    private const string FleetManagerOutcomesSqliteMigration = "20260917090000_AddFleetManagerOutcomes";
    private const string FleetManagerMarkHistorySqliteMigration = "20260917090100_AddFleetManagerMarkHistory";

    // The Message Load mission's inbox, which landed on main first; the step 3 pair must sort after it.
    private const string FleetMessagesPostgresMigration = "20260916195948_AddFleetMessages";
    private const string FleetMessagesSqliteMigration = "20260916195943_AddFleetMessages";
    // The dev reports and the trace row and clock, which landed on main after step 3; step 4's pair must sort after them.
    private const string DevReportsPostgresMigration = "20260917101851_AddDevReports";
    private const string DevReportsSqliteMigration = "20260917101833_AddDevReports";
    private const string TraceRowAndClockPostgresMigration = "20260917103105_AddTurnVerdictTraceRowAndClock";
    private const string TraceRowAndClockSqliteMigration = "20260917103039_AddTurnVerdictTraceRowAndClock";
    // Step 4's events, sorting after those, then what their delivery needs: the stop stored before it is read, how a
    // death was learned, and the owned sessions the Gateway last knew alive.
    private const string FleetManagerEventsPostgresMigration = "20260917110009_AddFleetManagerEvents";
    private const string FleetManagerEventsSqliteMigration = "20260917110000_AddFleetManagerEvents";
    private const string FleetManagerEventDeliveryPostgresMigration = "20260917110109_AddFleetManagerEventDelivery";
    private const string FleetManagerEventDeliverySqliteMigration = "20260917110100_AddFleetManagerEventDelivery";
    // Step 7: the Fleet Manager's advice and pick on a record, and the owner's note (a snooze from the walkthrough).
    private const string FleetOutcomeAdvicePostgresMigration = "20260917110209_AddFleetOutcomeAdvice";
    private const string FleetOutcomeAdviceSqliteMigration = "20260917110200_AddFleetOutcomeAdvice";
    // Step 9: the Assistant's two settings rows are deleted on both databases.
    private const string RemoveAssistantSettingsPostgresMigration = "20260917110309_RemoveAssistantSettings";
    private const string RemoveAssistantSettingsSqliteMigration = "20260917110300_RemoveAssistantSettings";
    // Steps 5 and 6 fixes: an owner's answer to a card is carried to the Fleet Manager as an event.
    private const string FleetManagerEventOutcomeAnswerPostgresMigration = "20260917110409_AddFleetManagerEventOutcomeAnswer";
    private const string FleetManagerEventOutcomeAnswerSqliteMigration = "20260917110400_AddFleetManagerEventOutcomeAnswer";
    // Step 7 fixes: a verdict's answer is stored with what was sent.
    private const string TurnVerdictAnswerChoicePostgresMigration = "20260917110509_AddTurnVerdictAnswerChoice";
    private const string TurnVerdictAnswerChoiceSqliteMigration = "20260917110500_AddTurnVerdictAnswerChoice";
    // Steps 7 to 9, round 2 fixes: an outcome record stores the stop it is about.
    private const string FleetOutcomeStopIdentityPostgresMigration = "20260917110609_AddFleetOutcomeStopIdentity";
    private const string FleetOutcomeStopIdentitySqliteMigration = "20260917110600_AddFleetOutcomeStopIdentity";
    // Contract v3: the NARRATION call is traced beside the judge call, so the debug view can show both halves of
    // one reading. Six nullable columns on turn_verdict_traces; no second package, because the narration call is
    // given the judge's.
    private const string WingmanNarrationCallTracePostgresMigration = "20260918181205_AddWingmanNarrationCallTrace";
    private const string WingmanNarrationCallTraceSqliteMigration = "20260918171353_AddWingmanNarrationCallTrace";
    // The one-repository-list mission, phase 2: the known-repositories catalog holds the found-but-never-
    // opened half, so its last-used time becomes nullable and it gains the reporting Director and a
    // last-seen stamp.
    private const string DiscoveredRepositoriesPostgresMigration = "20260920021806_AddDiscoveredRepositories";
    private const string DiscoveredRepositoriesSqliteMigration = "20260920021757_AddDiscoveredRepositories";
    private const string RaisedSessionsPostgresMigration = "20260920053001_AddRaisedSessions";
    private const string RaisedSessionsSqliteMigration = "20260920052924_AddRaisedSessions";
    // Website Business Factory: the append-only factory activity record.
    private const string FactoryActivityPostgresMigration = "20260921084515_AddFactoryActivity";
    private const string FactoryActivitySqliteMigration = "20260921081600_AddFactoryActivity";
    // The Website Business Factory mission, product track: the factory triggers and their run history.
    private const string FactoryTriggersPostgresMigration = "20260921105238_AddFactoryTriggers";
    private const string FactoryTriggersSqliteMigration = "20260921105211_AddFactoryTriggers";
    // The Factory Agents pages: indexes for the Sessions chip's read and the unfiltered window read.
    private const string FactoryActivityIndexesPostgresMigration = "20260921131114_IndexFactoryActivityReads";
    private const string FactoryActivityIndexesSqliteMigration = "20260921131049_IndexFactoryActivityReads";
    // The trigger's live check: the exact name a pending start used, stored with its lock.
    private const string TriggerStartNamePostgresMigration = "20260921203258_AddTriggerStartName";
    private const string TriggerStartNameSqliteMigration = "20260921203243_AddTriggerStartName";
    // The fleet message's unreachable-notice mark (issue 3289): a sender is told once when its doorbell cannot ring.
    private const string UnreachableNoticePostgresMigration = "20260927212206_AddFleetMessageUnreachableNotice";
    private const string UnreachableNoticeSqliteMigration = "20260927212143_AddFleetMessageUnreachableNotice";
    // Factory Memory mission, phase 1: which factory a session belongs to, and the factory a schedule's
    // sessions are born into.
    private const string SessionAndScheduleFactoryPostgresMigration = "20260928123825_AddSessionAndScheduleFactory";
    private const string SessionAndScheduleFactorySqliteMigration = "20260928123819_AddSessionAndScheduleFactory";
    // Factory Memory mission, phase 2: the memory table itself - every version of every note, keyed so two
    // Gateway replicas cannot both mint one version.
    private const string FactoryMemoryNotesPostgresMigration = "20260928124407_AddFactoryMemoryNotes";
    private const string FactoryMemoryNotesSqliteMigration = "20260928124401_AddFactoryMemoryNotes";
    // Teams, first version (devthrottle_internal#2300): the teams and who belongs to each, in which role.
    private const string TeamsPostgresMigration = "20261003182436_AddTeams";
    private const string TeamsSqliteMigration = "20261003182410_AddTeams";
    // Team invitations by email (devthrottle_internal#2301).
    private const string TeamInvitationsPostgresMigration = "20261003233544_AddTeamInvitations";
    private const string TeamInvitationsSqliteMigration = "20261003233525_AddTeamInvitations";
    // Message links between sessions (issue #3548).
    private const string MessageLinksPostgresMigration = "20261005031815_AddFleetMessageLinks";
    private const string MessageLinksSqliteMigration = "20261005031703_AddFleetMessageLinks";
    // The Mentor's weekly page (devthrottle_internal#2305): its blocks, outcomes and run marker, and the person on a
    // team session.
    private const string TeamMentorPostgresMigration = "20261004214416_AddTeamMentor";
    private const string TeamMentorSqliteMigration = "20261004214334_AddTeamMentor";
    // A dev report sent to a member of a team, and that member's comments (devthrottle_internal#2309).
    private const string DevReportSharingPostgresMigration = "20261005234644_AddDevReportSharing";
    private const string DevReportSharingSqliteMigration = "20261005234625_AddDevReportSharing";
    // The factory registry and goal numbers (Factories screen mission, phase A) - the newest.
    private const string FactoryRegistryPostgresMigration = "20261006140813_AddFactoryRegistry";
    private const string FactoryRegistrySqliteMigration = "20261006140613_AddFactoryRegistry";

    // Requests to a team's Owner and Managers (devthrottle_internal#2308), after the message links.
    private const string TeamRequestsPostgresMigration = "20261005124444_AddTeamRequests";
    // The requests for a message link (issue #3548).
    private const string MessageLinkRequestsPostgresMigration = "20261005160425_AddFleetMessageLinkRequests";
    private const string TeamRequestsSqliteMigration = "20261005124413_AddTeamRequests";
    private const string MessageLinkRequestsSqliteMigration = "20261005160401_AddFleetMessageLinkRequests";

    // The Fleet Manager's lessons (issue #3559), after the message link requests - the newest.
    private const string LessonsPostgresMigration = "20261005184813_AddFleetManagerLessons";
    private const string LessonsSqliteMigration = "20261005184745_AddFleetManagerLessons";
    // Who answered a question on a team member's Questions page, and which question a comment is about
    // (devthrottle_internal#2307).
    private const string TeamQuestionAnswersPostgresMigration = "20261006171258_AddTeamQuestionAnswers";
    private const string TeamQuestionAnswersSqliteMigration = "20261006171223_AddTeamQuestionAnswers";
    // Archiving a factory (Factories screen mission, round 2).
    private const string ArchiveFactoriesPostgresMigration = "20261007052952_ArchiveFactories";
    private const string ArchiveFactoriesSqliteMigration = "20261007035034_ArchiveFactories";
    // The newest migration: the team showcase's tag and a made-up member's name and email (8 Oct 2026). It moves each
    // time one is added.
    private const string NewestPostgresMigration = "20261008220319_RenameCeoSeatToBossSeat";
    private const string NewestSqliteMigration = "20261008220200_RenameCeoSeatToBossSeat";

    /// <summary>A Fact that skips itself unless the runtime Postgres selector CC_GATEWAY_DB_CONNECTION is set
    /// to a non-blank value, so CI never reaches out to the hosted database and never needs the secret.</summary>
    private sealed class RequiresConfiguredPostgresFactAttribute : FactAttribute
    {
        public RequiresConfiguredPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar)))
                Skip = $"Set {GatewayDatabase.PostgresConnectionEnvVar} to a real PostgreSQL connection " +
                       "string to run the live host-boot migration proof.";
        }
    }

    /// <summary>
    /// The Postgres migration set resolves BY ASSEMBLY NAME - the exact mechanism EF uses at runtime when the
    /// hosted host calls Migrate() with MigrationsAssembly "CcDirector.Gateway.Migrations.Postgres". This runs
    /// with NO database (a placeholder, never-connected connection string; GetMigrations reads the migrations
    /// assembly, it does not open a connection), so it always runs in CI and touches nothing. It proves the
    /// separately-assembled migration set is discoverable through the name the host wires - the gap Step 4b
    /// closes for the deployed image.
    /// </summary>
    [Fact]
    public void PostgresMigrationSet_ResolvesByAssemblyName_WithoutDatabase()
    {
        // A non-resolvable placeholder host (the reserved .invalid TLD never resolves) - GetMigrations does
        // not connect, and no connection must ever be attempted from this fact.
        using var ctx = new GatewayDbContext(
            new DbContextOptionsBuilder<GatewayDbContext>()
                .UseNpgsql("Host=pg.invalid;Database=none;Username=none;Password=none",
                    o => o.MigrationsAssembly(PostgresMigrationsAssembly))
                .Options);

        var migrations = ctx.Database.GetMigrations().ToList();

        Assert.Contains(InitialPostgresMigration, migrations);
        Assert.Contains(FleetManagerOutcomesPostgresMigration, migrations);
        Assert.Contains(FleetManagerMarkHistoryPostgresMigration, migrations);
        Assert.Contains(FleetManagerEventsPostgresMigration, migrations);
        Assert.Contains(FleetManagerEventDeliveryPostgresMigration, migrations);
        Assert.Contains(FleetOutcomeAdvicePostgresMigration, migrations);
        Assert.Contains(RemoveAssistantSettingsPostgresMigration, migrations);
        Assert.Contains(FleetManagerEventOutcomeAnswerPostgresMigration, migrations);
        Assert.Contains(TurnVerdictAnswerChoicePostgresMigration, migrations);
        Assert.Contains(FleetOutcomeStopIdentityPostgresMigration, migrations);
        Assert.Contains(WingmanNarrationCallTracePostgresMigration, migrations);
        Assert.Contains(DiscoveredRepositoriesPostgresMigration, migrations);
        Assert.Contains(RaisedSessionsPostgresMigration, migrations);
        Assert.Contains(FactoryActivityPostgresMigration, migrations);
        Assert.Contains(FactoryTriggersPostgresMigration, migrations);
        Assert.Contains(FactoryActivityIndexesPostgresMigration, migrations);
        Assert.Contains(TriggerStartNamePostgresMigration, migrations);
        Assert.Contains(UnreachableNoticePostgresMigration, migrations);
        Assert.Contains(SessionAndScheduleFactoryPostgresMigration, migrations);
        Assert.Contains(FactoryMemoryNotesPostgresMigration, migrations);
        Assert.Contains(TeamsPostgresMigration, migrations);
        Assert.Contains(TeamInvitationsPostgresMigration, migrations);
        Assert.Contains(MessageLinksPostgresMigration, migrations);
        Assert.Contains(TeamMentorPostgresMigration, migrations);
        Assert.Contains(TeamRequestsPostgresMigration, migrations);
        Assert.Contains(MessageLinkRequestsPostgresMigration, migrations);
        Assert.Contains(LessonsPostgresMigration, migrations);
        Assert.Contains(DevReportSharingPostgresMigration, migrations);
        Assert.Contains(FactoryRegistryPostgresMigration, migrations);
        Assert.Contains(TeamQuestionAnswersPostgresMigration, migrations);
        Assert.Contains(ArchiveFactoriesPostgresMigration, migrations);
        Assert.Contains(NewestPostgresMigration, migrations);
        Assert.Equal(NewestPostgresMigration, migrations[^1]);
    }

    /// <summary>
    /// EVERY SQLITE MIGRATION SINCE THE POSTGRES BASELINE HAS A POSTGRES TWIN OF THE SAME NAME, AND THE OTHER WAY
    /// ROUND. A table added for SQLite only boots on a self-hosted Gateway and fails on the hosted one the first
    /// time a row is written - and no SQLite test can see it. The two sets are read the way the host reads them
    /// (by assembly, with no database), compared by the name after the timestamp, and the SQLite migrations dated
    /// before the baseline are left out because the baseline folds them in.
    /// </summary>
    [Fact]
    public void EverySqliteMigrationSinceTheBaseline_HasAPostgresTwin_AndTheOtherWayRound()
    {
        using var postgres = new GatewayDbContext(
            new DbContextOptionsBuilder<GatewayDbContext>()
                .UseNpgsql("Host=pg.invalid;Database=none;Username=none;Password=none",
                    o => o.MigrationsAssembly(PostgresMigrationsAssembly))
                .Options);
        using var sqlite = new GatewayDbContext(
            new DbContextOptionsBuilder<GatewayDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);

        var sqliteAll = sqlite.Database.GetMigrations().ToList();
        var baseline = InitialPostgresMigration[..InitialPostgresMigration.IndexOf('_')];
        static string Name(string migration) => migration[(migration.IndexOf('_') + 1)..];

        var sqliteSince = sqliteAll
            .Where(m => string.CompareOrdinal(m[..m.IndexOf('_')], baseline) > 0)
            .Select(Name).ToList();
        var postgresSince = postgres.Database.GetMigrations()
            .Where(m => m != InitialPostgresMigration)
            .Select(Name).ToList();

        // A PRESENCE, so an empty read cannot pass: the step 3 pair is in both lists.
        Assert.Contains(FleetManagerOutcomesSqliteMigration, sqliteAll);
        Assert.Contains(FleetManagerMarkHistorySqliteMigration, sqliteAll);
        Assert.Contains(FleetManagerEventsSqliteMigration, sqliteAll);
        Assert.Contains(FleetManagerEventDeliverySqliteMigration, sqliteAll);
        Assert.Contains(FleetOutcomeAdviceSqliteMigration, sqliteAll);
        Assert.Contains(RemoveAssistantSettingsSqliteMigration, sqliteAll);
        Assert.Contains(FleetManagerEventOutcomeAnswerSqliteMigration, sqliteAll);
        Assert.Contains(TurnVerdictAnswerChoiceSqliteMigration, sqliteAll);
        Assert.Contains(FleetOutcomeStopIdentitySqliteMigration, sqliteAll);
        Assert.Contains(WingmanNarrationCallTraceSqliteMigration, sqliteAll);
        Assert.Contains(DiscoveredRepositoriesSqliteMigration, sqliteAll);
        Assert.Contains(RaisedSessionsSqliteMigration, sqliteAll);
        Assert.Contains(FactoryActivitySqliteMigration, sqliteAll);
        Assert.Contains(FactoryTriggersSqliteMigration, sqliteAll);
        Assert.Contains(FactoryActivityIndexesSqliteMigration, sqliteAll);
        Assert.Contains(TriggerStartNameSqliteMigration, sqliteAll);
        Assert.Contains(UnreachableNoticeSqliteMigration, sqliteAll);
        Assert.Contains(SessionAndScheduleFactorySqliteMigration, sqliteAll);
        Assert.Contains(FactoryMemoryNotesSqliteMigration, sqliteAll);
        Assert.Contains(TeamsSqliteMigration, sqliteAll);
        Assert.Contains(TeamInvitationsSqliteMigration, sqliteAll);
        Assert.Contains(MessageLinksSqliteMigration, sqliteAll);
        Assert.Contains(TeamMentorSqliteMigration, sqliteAll);
        Assert.Contains(TeamRequestsSqliteMigration, sqliteAll);
        Assert.Contains(MessageLinkRequestsSqliteMigration, sqliteAll);
        Assert.Contains(LessonsSqliteMigration, sqliteAll);
        Assert.Contains(DevReportSharingSqliteMigration, sqliteAll);
        Assert.Contains(FactoryRegistrySqliteMigration, sqliteAll);
        Assert.Contains(TeamQuestionAnswersSqliteMigration, sqliteAll);
        Assert.Contains(ArchiveFactoriesSqliteMigration, sqliteAll);
        Assert.Contains(NewestSqliteMigration, sqliteAll);
        Assert.Equal(NewestSqliteMigration, sqliteAll[^1]);
        Assert.Contains("AddFleetManagerMarkHistory", sqliteSince);

        Assert.Equal(sqliteSince.OrderBy(n => n, StringComparer.Ordinal), postgresSince.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// THE FLEET MANAGER'S TABLES ARE THE SAME ON BOTH DATABASES, COLUMN FOR COLUMN: each provider's model
    /// snapshot - what its migrations build - is read, and every column of <c>fleet_manager_events</c>,
    /// <c>fleet_manager_owned_sessions</c> and <c>fleet_outcomes</c> (with step 7's advice, pick and owner's note)
    /// must have the same name, type, nullability and length on both. A column
    /// added for one provider only makes the hosted Gateway fail the first time it writes that column.
    /// </summary>
    [Theory]
    [InlineData("fleet_manager_events", "ReadingPending")]
    [InlineData("fleet_manager_events", "OutcomeId")]
    [InlineData("fleet_manager_events", "Words")]
    [InlineData("fleet_manager_owned_sessions", "EndedAtUtc")]
    [InlineData("fleet_outcomes", "Advice")]
    [InlineData("fleet_outcomes", "FleetManagerPick")]
    [InlineData("fleet_outcomes", "OwnerNote")]
    public void FleetManagerTables_MatchColumnForColumn_OnSqliteAndPostgres(string table, string mustHave)
    {
        using var postgres = new GatewayDbContext(
            new DbContextOptionsBuilder<GatewayDbContext>()
                .UseNpgsql("Host=pg.invalid;Database=none;Username=none;Password=none",
                    o => o.MigrationsAssembly(PostgresMigrationsAssembly))
                .Options);
        using var sqlite = new GatewayDbContext(
            new DbContextOptionsBuilder<GatewayDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);

        var sqliteColumns = Columns(sqlite, table);
        var postgresColumns = Columns(postgres, table);

        // A PRESENCE, so an empty read cannot pass.
        Assert.Contains(sqliteColumns, c => c.Name == mustHave);
        Assert.Equal(sqliteColumns, postgresColumns);
    }

    private static List<(string Name, Type Type, bool Nullable, int? MaxLength)> Columns(GatewayDbContext ctx, string table)
    {
        var snapshot = ctx.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);
        var entity = snapshot!.Model.GetEntityTypes().Single(e => e.GetTableName() == table);
        return entity.GetProperties()
            .Select(p => (p.GetColumnName(), p.ClrType, p.IsNullable, p.GetMaxLength()))
            .OrderBy(c => c.Item1, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// THE SQLITE MIGRATIONS APPLY FROM AN EMPTY DATABASE AND LEAVE NOTHING FOR THE MODEL TO ADD. Two missions that
    /// each add a table each regenerate the model snapshot; merged by hand, the snapshot can silently drop one
    /// side's table, and the next migration anyone generates would then try to create it a second time. This
    /// applies every migration to a fresh in-memory database, proves the step 3 pair ran after the fleet message
    /// inbox, step 4's pair ran after step 3's and steps 5 to 9's three ran after step 4's, and asks EF whether the model still differs from the snapshot.
    /// </summary>
    [Fact]
    public void SqliteMigrations_ApplyFromEmpty_LeaveNoPendingModelChange()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var ctx = new GatewayDbContext(
            new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options);

        ctx.Database.Migrate();

        var applied = ctx.Database.GetAppliedMigrations().ToList();
        Assert.Contains(FleetManagerOutcomesSqliteMigration, applied);
        Assert.Contains(FleetManagerMarkHistorySqliteMigration, applied);
        Assert.True(
            applied.IndexOf(FleetMessagesSqliteMigration) >= 0 &&
            applied.IndexOf(FleetMessagesSqliteMigration) < applied.IndexOf(FleetManagerOutcomesSqliteMigration),
            "The Fleet Manager migrations must sort after the fleet message inbox migration.");
        AssertInOrder(applied,
            FleetMessagesSqliteMigration,
            FleetManagerOutcomesSqliteMigration,
            FleetManagerMarkHistorySqliteMigration,
            DevReportsSqliteMigration,
            TraceRowAndClockSqliteMigration,
            FleetManagerEventsSqliteMigration,
            FleetManagerEventDeliverySqliteMigration,
            FleetOutcomeAdviceSqliteMigration,
            RemoveAssistantSettingsSqliteMigration,
            FleetManagerEventOutcomeAnswerSqliteMigration,
            TurnVerdictAnswerChoiceSqliteMigration,
            FleetOutcomeStopIdentitySqliteMigration,
            WingmanNarrationCallTraceSqliteMigration,
            DiscoveredRepositoriesSqliteMigration,
            RaisedSessionsSqliteMigration,
            FactoryActivitySqliteMigration,
            FactoryTriggersSqliteMigration,
            FactoryActivityIndexesSqliteMigration,
            TriggerStartNameSqliteMigration,
            UnreachableNoticeSqliteMigration,
            SessionAndScheduleFactorySqliteMigration,
            FactoryMemoryNotesSqliteMigration,
            TeamsSqliteMigration,
            TeamInvitationsSqliteMigration,
            TeamMentorSqliteMigration,
            MessageLinksSqliteMigration,
            TeamRequestsSqliteMigration,
            MessageLinkRequestsSqliteMigration,
            LessonsSqliteMigration,
            DevReportSharingSqliteMigration,
            FactoryRegistrySqliteMigration,
            TeamQuestionAnswersSqliteMigration,
            ArchiveFactoriesSqliteMigration,
            NewestSqliteMigration);
        Assert.Equal(NewestSqliteMigration, applied[^1]);
        Assert.Empty(ctx.Database.GetPendingMigrations());
        Assert.False(ctx.Database.HasPendingModelChanges(),
            "The SQLite model snapshot does not match the model - a migration is missing or the snapshot was merged wrong.");
    }

    /// <summary>
    /// THE POSTGRESQL SNAPSHOT MATCHES THE MODEL, THE STEP 3 PAIR SORTS AFTER THE FLEET MESSAGE INBOX, AND STEP 4'S
    /// PAIR SORTS AFTER STEP 3'S, AND STEPS 5 TO 9'S SORT AFTER STEP 4'S, WITH THE ROUND 2 FIXES' STOP IDENTITY LAST. Asking
    /// whether the model has pending changes compares the compiled snapshot with the model and opens no
    /// connection, so this runs without a database. Applying the set to a real server is the Postgres-backed
    /// suite's job.
    /// </summary>
    [Fact]
    public void PostgresSnapshot_MatchesTheModel_AndStepThreeSortsAfterTheInbox_WithoutDatabase()
    {
        using var ctx = new GatewayDbContext(
            new DbContextOptionsBuilder<GatewayDbContext>()
                .UseNpgsql("Host=pg.invalid;Database=none;Username=none;Password=none",
                    o => o.MigrationsAssembly(PostgresMigrationsAssembly))
                .Options);

        var migrations = ctx.Database.GetMigrations().ToList();

        Assert.True(
            migrations.IndexOf(FleetMessagesPostgresMigration) >= 0 &&
            migrations.IndexOf(FleetMessagesPostgresMigration) < migrations.IndexOf(FleetManagerOutcomesPostgresMigration) &&
            migrations.IndexOf(FleetManagerOutcomesPostgresMigration) < migrations.IndexOf(FleetManagerMarkHistoryPostgresMigration),
            "The Fleet Manager migrations must sort after the fleet message inbox migration.");
        AssertInOrder(migrations,
            FleetMessagesPostgresMigration,
            FleetManagerOutcomesPostgresMigration,
            FleetManagerMarkHistoryPostgresMigration,
            DevReportsPostgresMigration,
            TraceRowAndClockPostgresMigration,
            FleetManagerEventsPostgresMigration,
            FleetManagerEventDeliveryPostgresMigration,
            FleetOutcomeAdvicePostgresMigration,
            RemoveAssistantSettingsPostgresMigration,
            FleetManagerEventOutcomeAnswerPostgresMigration,
            TurnVerdictAnswerChoicePostgresMigration,
            FleetOutcomeStopIdentityPostgresMigration,
            WingmanNarrationCallTracePostgresMigration,
            DiscoveredRepositoriesPostgresMigration,
            RaisedSessionsPostgresMigration,
            FactoryActivityPostgresMigration,
            FactoryTriggersPostgresMigration,
            FactoryActivityIndexesPostgresMigration,
            TriggerStartNamePostgresMigration,
            UnreachableNoticePostgresMigration,
            SessionAndScheduleFactoryPostgresMigration,
            FactoryMemoryNotesPostgresMigration,
            TeamsPostgresMigration,
            TeamInvitationsPostgresMigration,
            TeamMentorPostgresMigration,
            MessageLinksPostgresMigration,
            TeamRequestsPostgresMigration,
            MessageLinkRequestsPostgresMigration,
            LessonsPostgresMigration,
            DevReportSharingPostgresMigration,
            FactoryRegistryPostgresMigration,
            TeamQuestionAnswersPostgresMigration,
            ArchiveFactoriesPostgresMigration,
            NewestPostgresMigration);
        Assert.Equal(NewestPostgresMigration, migrations[^1]);
        Assert.False(ctx.Database.HasPendingModelChanges(),
            "The PostgreSQL model snapshot does not match the model - a migration is missing or the snapshot was merged wrong.");
    }

    /// <summary>Every name is present, and each sorts after the one before it.</summary>
    private static void AssertInOrder(List<string> migrations, params string[] expected)
    {
        var positions = expected.Select(m => migrations.IndexOf(m)).ToList();
        for (var i = 0; i < expected.Length; i++)
            Assert.True(positions[i] >= 0, $"Migration {expected[i]} is missing.");
        for (var i = 1; i < expected.Length; i++)
            Assert.True(positions[i - 1] < positions[i], $"Migration {expected[i]} must sort after {expected[i - 1]}.");
    }

    /// <summary>
    /// The real hosted startup path: constructing GatewayDatabase (the exact class the hosted host boots)
    /// with CC_GATEWAY_DB_CONNECTION set runs Database.Migrate() against the configured Postgres, resolving
    /// and applying the Postgres migration set. Asserting the applied-migrations list contains the
    /// InitialPostgres migration proves the set was found by name and applied - end to end on a real server.
    /// Env-gated: skips cleanly when unset, so CI connects to nothing.
    /// </summary>
    [RequiresConfiguredPostgresFact]
    public void HostStartupPath_ResolvesAndAppliesPostgresMigrations_OnConfiguredPostgres()
    {
        using var db = new GatewayDatabase(new SingleTenantContext());
        using var ctx = db.CreateContext();

        var applied = ctx.Database.GetAppliedMigrations().ToList();

        Assert.Contains(InitialPostgresMigration, applied);
    }
}
