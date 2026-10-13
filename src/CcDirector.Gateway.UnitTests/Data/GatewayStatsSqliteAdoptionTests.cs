using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The self-host adoption step: an existing gateway-stats.db written by the hand-rolled
/// <see cref="GatewayStatsDatabase"/> is taken into the Entity Framework migration chain without losing a
/// row, and anything that is NOT such a store is refused with a named reason and a Gateway that keeps
/// running.
///
/// THE FIXTURE IS BUILT BY RUNNING THE OLD CODE, and that is the single point on which these tests are worth
/// something or worthless. Every version 5 file here is produced by constructing a real
/// <see cref="GatewayStatsDatabase"/> and letting it create and migrate the file - never by hand-writing what
/// a version 5 file is believed to look like, and never by generating one from the new model's understanding
/// of the old schema. A fixture synthesised from the new code's own understanding is a guard supplying its
/// own evidence: it would pass happily against a file shape that has never existed on any real machine, and
/// prove nothing about the installs this step exists to protect.
///
/// THE FAILURE IS WATCHED FIRST. <see cref="Migrate_Version5Store_WithoutAdoption_FailsOnTablesThatAlreadyExist"/>
/// runs the chain against such a file with NO adoption and pins the exact error it produces. Without it, the
/// adoption test could pass for a reason that has nothing to do with adoption - it is what proves the step is
/// load-bearing, and it turns red the day somebody deletes it.
/// </summary>
public sealed class GatewayStatsSqliteAdoptionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public GatewayStatsSqliteAdoptionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-adopt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "gateway-stats.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// Build a REAL version 5 statistics store at <see cref="_path"/> by running the shipped
    /// <see cref="GatewayStatsDatabase"/> creation and migration code, stopped at version 5 through its
    /// internal target-version seam, then close it so the file is free. This is the state every self-host
    /// user who predates the Entity Framework chain is in: the sixteen tables, PRAGMA user_version 5 stamped
    /// by the hand-rolled migrator, and no __EFMigrationsHistory table.
    ///
    /// WHY THE SEAM EXISTS AND WHY IT IS THE RIGHT FIXTURE. Adoption recognises version 5 files ONLY
    /// (<see cref="GatewayStatsSqliteAdoption.LegacyBaselineSchemaVersion"/>, frozen forever), but the public
    /// constructor migrates every file to the CURRENT <see cref="GatewayStatsDatabase.SchemaVersion"/> - so
    /// once that moved past 5 no public code path could produce the file these tests are about. An interim
    /// fix had this fixture build a current-version file instead, which adoption then correctly refused, and
    /// twelve tests failed for a fixture reason. The seam runs the REAL migration steps and stops at 5, which
    /// keeps this class's own hard rule: fixtures are built by running shipped code, never hand-written and
    /// never synthesised from the new model's idea of the old schema.
    ///
    /// The version is asserted against the adoption path's own frozen constant - the number that DEFINES
    /// "a legacy file" - never against the moving build version and never as a bare literal copied here.
    /// </summary>
    private void BuildRealVersion5Store()
    {
        using (var db = new GatewayStatsDatabase(_path, GatewayStatsSqliteAdoption.LegacyBaselineSchemaVersion))
        {
            // Pin the fixture's own premises. If the seam ever stops producing this shape, these tests must
            // say so here rather than silently going on to prove something about a file that no longer
            // exists in the field.
            Assert.Equal(GatewayStatsSqliteAdoption.LegacyBaselineSchemaVersion,
                ScalarInt(db.Connection, "PRAGMA user_version"));
            Assert.Equal(0, ScalarInt(db.Connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'"));
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Write rows into the real version 5 store, so "no row was lost" is a claim with something behind it.
    /// Written through the OLD code's own connection, in the old code's own shape.
    ///
    /// EVERY VALUE IS DISTINGUISHABLE FROM EVERY OTHER, and that is the point rather than fussiness. This
    /// fixture's job is to catch a column mapped to the wrong column, and it can only do that if no two
    /// columns carry values a swap would leave looking identical. An earlier version of this row set
    /// <c>is_voice</c> and <c>wingman</c> BOTH to 0 and <c>model_id</c> and <c>checkout_id</c> BOTH to null -
    /// so a mapping that crossed either pair would have read back correct and every assertion below would
    /// have passed with the defect sitting in the model.
    ///
    /// So: the two booleans differ from each other, the two nullable surrogates carry different non-null
    /// numbers, the two counts differ, and every string differs. The second row is the one that keeps the
    /// nullable columns' NULL case covered, since row one no longer does.
    /// </summary>
    private void SeedDistinguishableStatDeltaRows()
    {
        // Reopened through the same version 5 seam the fixture was built with: the public constructor would
        // migrate the file to the current version on open, and the rows must land in a version 5 store.
        using var db = new GatewayStatsDatabase(_path, GatewayStatsSqliteAdoption.LegacyBaselineSchemaVersion);

        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO stat_delta(hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, " +
                "turns, chars, model_id, checkout_id, tenant) " +
                "VALUES ('2026-07-30T09', 'session-a', 'typed', 'terminal', 1, 3, 0, 7, 42, 11, 22, 'local')";
            command.ExecuteNonQuery();
        }

        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO stat_delta(hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, " +
                "turns, chars, model_id, checkout_id, tenant) " +
                "VALUES ('2026-07-30T10', 'session-b', 'voice', 'mobile', 0, 5, 1, 13, 64, NULL, NULL, 'local')";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    private static int ScalarInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private GatewayStatsDbContext OpenContext()
    {
        var options = new DbContextOptionsBuilder<GatewayStatsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = _path }.ToString())
            .Options;
        return new GatewayStatsDbContext(options);
    }

    // ---- the failure, watched on purpose --------------------------------------------------------------

    /// <summary>
    /// THE PROOF THAT THE ADOPTION STEP IS LOAD-BEARING. Run the migration chain against a real version 5
    /// store with no adoption and it fails, because the baseline migration creates sixteen tables that are
    /// already there. This is the exact breakage every self-host user would have hit.
    /// </summary>
    [Fact]
    public void Migrate_Version5Store_WithoutAdoption_FailsOnTablesThatAlreadyExist()
    {
        BuildRealVersion5Store();

        using var context = OpenContext();

        var ex = Assert.ThrowsAny<SqliteException>(() => context.Database.Migrate());

        // Pinned to the substance of the failure, not to a whole message: a table this store already owns
        // cannot be created again. Asserting only "it threw" would pass for a connection error too.
        //
        // It names stat_delta because the baseline creates the sixteen tables in the order schema version 5's
        // own migration steps introduced them, and stat_delta is the first. It dies there and never reaches
        // the fifteen behind it - which is the point.
        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stat_delta", ex.Message, StringComparison.Ordinal);
    }

    // ---- and the same thing passing, because of adoption -----------------------------------------------

    [Fact]
    public void Adopt_RealVersion5Store_StampsBaselineAndLetsTheChainRun()
    {
        BuildRealVersion5Store();
        SeedDistinguishableStatDeltaRows();

        using var context = OpenContext();

        var result = GatewayStatsSqliteAdoption.Adopt(context);

        Assert.Equal(StatsStoreAdoptionOutcome.Adopted, result.Outcome);
        Assert.Equal(StatsStoreUnavailableReason.None, result.Reason);
        Assert.True(result.IsUsable);

        // The whole point: the chain now runs against the adopted file instead of failing on it.
        context.Database.Migrate();

        // Nothing is pending afterwards - the baseline is genuinely recorded as applied, not merely survived.
        Assert.Empty(context.Database.GetPendingMigrations());

        // The history records the chain's own baseline FIRST - the row adoption stamped, not something
        // invented here - and then every later migration Migrate() just ran. Asserted as the whole chain,
        // in order: when this test was written the chain WAS only the baseline, and pinning applied to
        // exactly one row went stale the day the chain grew its second migration.
        Assert.Equal(context.Database.GetMigrations().ToArray(),
            context.Database.GetAppliedMigrations().ToArray());
        Assert.Equal(context.Database.GetMigrations().First(),
            context.Database.GetAppliedMigrations().First());
    }

    [Fact]
    public void Adopt_RealVersion5Store_KeepsEveryRowItAlreadyHeld()
    {
        BuildRealVersion5Store();
        SeedDistinguishableStatDeltaRows();

        using var context = OpenContext();
        Assert.Equal(StatsStoreAdoptionOutcome.Adopted, GatewayStatsSqliteAdoption.Adopt(context).Outcome);
        context.Database.Migrate();

        // Read the pre-existing rows back THROUGH THE NEW MODEL. This is the port's real claim in one place:
        // the entity mapping lands on the columns the old code wrote, so the rows already on self-host disks
        // are readable without being copied or reshaped. It is currently the ONLY place the entity mapping
        // meets the real on-disk shape, so a wrong ToTable or HasColumnName throws here rather than passing
        // quietly.
        var rows = context.StatDeltas.OrderBy(r => r.HourUtc).ToList();
        Assert.Equal(2, rows.Count);

        var first = rows[0];
        Assert.Equal("2026-07-30T09", first.HourUtc);
        Assert.Equal("session-a", first.SessionId);
        Assert.Equal("typed", first.Modality);
        Assert.Equal("terminal", first.Surface);
        Assert.True(first.IsVoice);
        Assert.Equal(3, first.RepoId);
        Assert.False(first.Wingman);
        Assert.Equal(7, first.Turns);
        Assert.Equal(42, first.Chars);
        Assert.Equal(11, first.ModelId);
        Assert.Equal(22, first.CheckoutId);
        Assert.Equal("local", first.Tenant);

        // The second row carries the opposite of every distinguishable value, and the NULL case for the two
        // nullable surrogates.
        var second = rows[1];
        Assert.Equal("2026-07-30T10", second.HourUtc);
        Assert.Equal("session-b", second.SessionId);
        Assert.Equal("voice", second.Modality);
        Assert.Equal("mobile", second.Surface);
        Assert.False(second.IsVoice);
        Assert.Equal(5, second.RepoId);
        Assert.True(second.Wingman);
        Assert.Equal(13, second.Turns);
        Assert.Equal(64, second.Chars);
        Assert.Null(second.ModelId);
        Assert.Null(second.CheckoutId);
        Assert.Equal("local", second.Tenant);
    }

    [Fact]
    public void Adopt_AlreadyAdoptedStore_IsTrackedAndDoesNothingTwice()
    {
        BuildRealVersion5Store();

        using (var first = OpenContext())
        {
            Assert.Equal(StatsStoreAdoptionOutcome.Adopted, GatewayStatsSqliteAdoption.Adopt(first).Outcome);
            first.Database.Migrate();
        }

        SqliteConnection.ClearAllPools();

        // The next startup - and every startup after it - finds a tracked store and leaves it alone.
        using var second = OpenContext();
        var result = GatewayStatsSqliteAdoption.Adopt(second);

        Assert.Equal(StatsStoreAdoptionOutcome.AlreadyTracked, result.Outcome);
        Assert.True(result.IsUsable);
        second.Database.Migrate();
        Assert.Empty(second.Database.GetPendingMigrations());
    }

    // ---- what is NOT adopted, and what that must cost --------------------------------------------------

    /// <summary>
    /// A refused store must be left EXACTLY as it was found, and that is TWO claims, not one: nothing was
    /// ADDED, and nothing was LOST. Asserting only the first passes a refusal that dropped a table on its way
    /// out - the same half-checked shape that let a foreign database be written into.
    ///
    /// The row count is what makes the second claim able to fail. A refusal path that dropped and recreated a
    /// table would keep every table NAME and lose every row, and a names-only check would call that untouched.
    /// </summary>
    /// <summary>
    /// A fingerprint of the whole store on disk - the database file and any write-ahead log or shared-memory
    /// file beside it.
    ///
    /// THE STRONGEST FORM OF "UNMODIFIED", and the one the refusal tests actually need. Counting tables and
    /// rows proves a lot, but it still only proves what somebody thought to count: a refusal that rewrote a
    /// value, moved the version stamp, or dropped an index nobody enumerated would pass every count and still
    /// have changed the operator's file. A hash cannot be fooled by an omission in the checklist, because it
    /// has no checklist.
    ///
    /// The sidecar files are included because a change parked in the write-ahead log is still a change to the
    /// store, and hashing only the main file would miss it.
    /// </summary>
    private string FingerprintStore()
    {
        SqliteConnection.ClearAllPools();

        using var sha = System.Security.Cryptography.SHA256.Create();
        var parts = new List<string>();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (!File.Exists(file)) { parts.Add($"{Path.GetFileName(file)}=<absent>"); continue; }
            using var stream = File.OpenRead(file);
            parts.Add($"{Path.GetFileName(file)}={Convert.ToHexString(sha.ComputeHash(stream))}");
        }
        return string.Join(" ", parts);
    }

    private void AssertStoreSurvivedUntouched(int expectedStatDeltaRows)
    {
        using var check = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        check.Open();

        // Nothing ADDED - no history table, so nothing was stamped.
        Assert.Equal(0, ScalarInt(check,
            "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('__EFMigrationsHistory', '__EFMigrationsLock')"));

        // Nothing LOST - all sixteen tables and all four named indexes still there.
        Assert.Equal(16, ScalarInt(check,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"));
        Assert.Equal(4, ScalarInt(check,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'ix_%'"));

        // And the operator's DATA, which is the thing that actually matters to them.
        Assert.Equal(expectedStatDeltaRows, ScalarInt(check, "SELECT COUNT(*) FROM stat_delta"));
    }

    [Theory]
    [InlineData(4)]  // older than the baseline was ported from
    [InlineData(6)]  // past the adoptable version: only the version 5 no-history population is adopted;
                     // a hand-rolled file the CURRENT build migrated further is refused by the same gate
    public void Adopt_StatisticsStoreAtAnotherVersion_IsRefusedWithANamedReasonAndLeftUntouched(int version)
    {
        BuildRealVersion5Store();
        SeedDistinguishableStatDeltaRows();

        // Move the REAL store's version stamp. The file keeps its genuine version 5 table shape, so this
        // tests the version gate itself rather than a hand-made file that differs in other ways too.
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version={version}";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using (var context = OpenContext())
        {
            var result = GatewayStatsSqliteAdoption.Adopt(context);

            Assert.Equal(StatsStoreAdoptionOutcome.NotAdoptable, result.Outcome);
            Assert.Equal(StatsStoreUnavailableReason.IncompatibleSchemaVersion, result.Reason);
            Assert.False(result.IsUsable);
            Assert.Contains(version.ToString(), result.Detail, StringComparison.Ordinal);
        }

        SqliteConnection.ClearAllPools();
        AssertStoreSurvivedUntouched(expectedStatDeltaRows: 2);
    }

    [Fact]
    public void Adopt_DatabaseThatIsNotAStatisticsStore_IsRefusedWithANamedReason()
    {
        // A database carrying the right version stamp but somebody else's tables. The version alone must not
        // be enough to stamp a baseline over it.
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE somebody_elses_table (id INTEGER PRIMARY KEY); " +
                                  "PRAGMA user_version=5";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using var context = OpenContext();
        var result = GatewayStatsSqliteAdoption.Adopt(context);

        Assert.Equal(StatsStoreAdoptionOutcome.NotAdoptable, result.Outcome);
        Assert.Equal(StatsStoreUnavailableReason.NotAStatisticsStore, result.Reason);
        Assert.False(result.IsUsable);

        // The refusal names the FOREIGN object it found, not the statistics tables it did not find. That is
        // the more useful message and it is also the safer check: this database is refused because it holds
        // something that is not ours, which is decided before the version stamp is even read - so a foreign
        // database can never reach the adoption path by carrying a version 5 stamp.
        Assert.Contains("somebody_elses_table", result.Detail, StringComparison.Ordinal);

        // And it is left exactly as it was found - this is the case that previously had sixteen statistics
        // tables and a baseline row written into it.
        using var check = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        check.Open();
        Assert.Equal(0, ScalarInt(check,
            "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('stat_delta', '__EFMigrationsHistory')"));
    }

    /// <summary>
    /// A REFUSAL IS NOT AN EXCEPTION. Nothing about a user's file may throw out of the adoption step: the
    /// Gateway has to start and serve its roster with the statistics surface off and the reason named. This
    /// is the containment rule itself, so it is asserted directly rather than left implied by the other
    /// tests happening not to throw.
    /// </summary>
    [Fact]
    public void Adopt_NeverThrowsOnAnyStateAUsersFileCanBeIn()
    {
        BuildRealVersion5Store();
        SeedDistinguishableStatDeltaRows();

        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=99";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using (var context = OpenContext())
        {
            var result = GatewayStatsSqliteAdoption.Adopt(context);

            Assert.False(result.IsUsable);
            Assert.NotEqual(StatsStoreUnavailableReason.None, result.Reason);
            Assert.False(string.IsNullOrWhiteSpace(result.Detail));
        }

        // Containment is not only about not throwing - the operator's data must survive the refusal too.
        SqliteConnection.ClearAllPools();
        AssertStoreSurvivedUntouched(expectedStatDeltaRows: 2);
    }

    /// <summary>
    /// AN INTERRUPTED MIGRATION IS NOT A TRACKED STORE. Entity Framework creates the migration history table
    /// BEFORE it records what it has done, so a first migration that died partway leaves an EMPTY history
    /// beside tables that already exist.
    ///
    /// The naive check - "does a history table exist?" - reports that store as usable, and the chain then
    /// tries to build tables that are already there, throwing from Migrate() OUTSIDE this step and therefore
    /// outside its containment. "The store has a history table" and "the store is at the baseline" are two
    /// different claims, and only the second is the one the chain depends on.
    /// </summary>
    [Fact]
    public void Adopt_HistoryTableWithoutTheBaseline_IsRefusedRatherThanCertifiedAsTracked()
    {
        BuildRealVersion5Store();

        // The state an interrupted first migration leaves: the history table exists and records nothing,
        // while the sixteen tables are already there. Created with Entity Framework's OWN create script, so
        // this is the table the framework would have made rather than a hand-rolled lookalike.
        using (var context = OpenContext())
        {
            var history = context.GetService<IHistoryRepository>();
            context.Database.OpenConnection();
            context.Database.ExecuteSqlRaw(history.GetCreateScript());
        }

        SqliteConnection.ClearAllPools();

        var before = FingerprintStore();

        using (var check = OpenContext())
        {
            var result = GatewayStatsSqliteAdoption.Adopt(check);

            Assert.Equal(StatsStoreAdoptionOutcome.NotAdoptable, result.Outcome);
            Assert.Equal(StatsStoreUnavailableReason.StoreSchemaIncomplete, result.Reason);
            Assert.False(result.IsUsable);
            Assert.Contains("interrupted", result.Detail, StringComparison.OrdinalIgnoreCase);
        }

        // The empty history and the tables are exactly as they were - refused AND unmodified.
        Assert.Equal(before, FingerprintStore());
    }

    // ---- the refusal states, each proved to leave the file BYTE-IDENTICAL ------------------------------
    //
    // Mutation was the harmful half of the original defect - a foreign database had sixteen tables and a
    // baseline row written into it - so "refused" without "unmodified" is only half the guarantee. Each of
    // these fingerprints the whole store before the call and again after, so nothing can change without the
    // test seeing it, including things nobody thought to count.

    [Fact]
    public void Adopt_TrackedStoreWithATableDropped_IsRefusedAndLeavesTheFileByteIdentical()
    {
        BuildRealVersion5Store();

        using (var context = OpenContext())
        {
            Assert.Equal(StatsStoreAdoptionOutcome.Adopted, GatewayStatsSqliteAdoption.Adopt(context).Outcome);
            context.Database.Migrate();
        }
        SqliteConnection.ClearAllPools();

        // The store records the baseline, so a history-only check would call it healthy - and it would then
        // report nothing pending and die on the first query.
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE stat_delta";
            command.ExecuteNonQuery();
        }

        var before = FingerprintStore();

        using (var context = OpenContext())
        {
            var result = GatewayStatsSqliteAdoption.Adopt(context);
            Assert.False(result.IsUsable);
            Assert.Equal(StatsStoreUnavailableReason.StoreSchemaIncomplete, result.Reason);
            Assert.Contains("stat_delta", result.Detail, StringComparison.Ordinal);
        }

        Assert.Equal(before, FingerprintStore());
    }

    [Fact]
    public void Adopt_StoreWhoseTableHasTheRightNamesAndNothingElse_IsRefusedAndLeavesTheFileByteIdentical()
    {
        BuildRealVersion5Store();

        // The exact column names, and no primary key, no NOT NULL, no default and no indexes. A names-only
        // check adopted this and stamped it.
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "DROP TABLE stat_delta; " +
                "CREATE TABLE stat_delta (id, hour_utc, session_id, modality, surface, is_voice, repo_id, " +
                "wingman, turns, chars, model_id, checkout_id, tenant); PRAGMA user_version=5";
            command.ExecuteNonQuery();
        }

        var before = FingerprintStore();

        using (var context = OpenContext())
        {
            var result = GatewayStatsSqliteAdoption.Adopt(context);
            Assert.False(result.IsUsable);
            Assert.Equal(StatsStoreUnavailableReason.StoreSchemaIncomplete, result.Reason);
            Assert.Contains("primary key", result.Detail, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(before, FingerprintStore());
    }

    [Fact]
    public void Adopt_ViewWearingATableName_IsRefusedAndLeavesTheFileByteIdentical()
    {
        // A database whose only stat_delta is a VIEW holds no tables at all, so a tables-only emptiness check
        // called it fresh and the chain then wrote sixteen tables into it.
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE somebody_elses_data (id INTEGER PRIMARY KEY, value TEXT); " +
                "INSERT INTO somebody_elses_data VALUES (1, 'precious'); " +
                "CREATE VIEW stat_delta AS SELECT id, value FROM somebody_elses_data";
            command.ExecuteNonQuery();
        }

        var before = FingerprintStore();

        using (var context = OpenContext())
        {
            var result = GatewayStatsSqliteAdoption.Adopt(context);
            Assert.False(result.IsUsable);
            Assert.Equal(StatsStoreUnavailableReason.NotAStatisticsStore, result.Reason);
        }

        Assert.Equal(before, FingerprintStore());
    }

    [Fact]
    public void Adopt_ForeignDatabaseWithItsOwnMigrationHistory_IsRefusedAndLeavesTheFileByteIdentical()
    {
        // The ORIGINAL defect's exact shape: somebody else's database, tracked by their OWN Entity Framework
        // history. This was certified FreshStore and then had sixteen statistics tables and a baseline row
        // written into it.
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE somebody_elses_table (id INTEGER PRIMARY KEY, value TEXT); " +
                "INSERT INTO somebody_elses_table VALUES (1, 'precious'); " +
                "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL CONSTRAINT " +
                "\"PK___EFMigrationsHistory\" PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL); " +
                "INSERT INTO \"__EFMigrationsHistory\" VALUES ('20260101000000_SomebodyElsesBaseline','9.0.2'); " +
                "PRAGMA user_version=3";
            command.ExecuteNonQuery();
        }

        var before = FingerprintStore();

        using (var context = OpenContext())
        {
            var result = GatewayStatsSqliteAdoption.Adopt(context);
            Assert.False(result.IsUsable);
            Assert.Equal(StatsStoreUnavailableReason.NotAStatisticsStore, result.Reason);
        }

        // Their table, their row, their history and their version stamp all exactly as they were.
        Assert.Equal(before, FingerprintStore());

        using var check = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        check.Open();
        Assert.Equal(1, ScalarInt(check, "SELECT COUNT(*) FROM somebody_elses_table WHERE value='precious'"));
        Assert.Equal(0, ScalarInt(check, "SELECT COUNT(*) FROM sqlite_master WHERE name='stat_delta'"));
        Assert.Equal(3, ScalarInt(check, "PRAGMA user_version"));
    }

    /// <summary>
    /// THE DESKTOP-ROLLBACK PATH, and the one the version discipline exists for.
    ///
    /// The version gate used to run only on stores with NO history table, so once a store was tracked a file
    /// from a LATER build sailed through: the baseline was in its applied list, the current model's shape was
    /// still present, and it came back usable. Migrate() was then a no-op - this older chain has nothing
    /// pending against a newer database - and the first write meeting a constraint the newer build added
    /// failed OUTSIDE the containment boundary.
    ///
    /// That silently removed the protection the per-migration stamp rule exists to preserve, and it is the
    /// path a user takes when they roll a desktop build back.
    /// </summary>
    [Fact]
    public void Adopt_TrackedStoreWrittenByANewerBuild_IsRefusedAndLeavesTheFileByteIdentical()
    {
        BuildRealVersion5Store();

        using (var context = OpenContext())
        {
            Assert.Equal(StatsStoreAdoptionOutcome.Adopted, GatewayStatsSqliteAdoption.Adopt(context).Outcome);
            context.Database.Migrate();
        }
        SqliteConnection.ClearAllPools();

        // What a NEWER build leaves behind: a migration this chain does not have, plus the schema changes
        // that migration made. The model's own shape is all still present, which is why a shape-only check
        // called this healthy.
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO \"__EFMigrationsHistory\" VALUES ('20990101000000_FutureStats','9.0.2'); " +
                "CREATE TABLE future_table (id INTEGER PRIMARY KEY); " +
                "ALTER TABLE stat_delta ADD COLUMN future_column TEXT; " +
                "CREATE UNIQUE INDEX ux_future ON repo_identity(tenant, repo_display); " +
                "PRAGMA user_version=6";
            command.ExecuteNonQuery();
        }

        var before = FingerprintStore();

        using (var context = OpenContext())
        {
            var result = GatewayStatsSqliteAdoption.Adopt(context);

            Assert.False(result.IsUsable);
            Assert.Equal(StatsStoreUnavailableReason.StoreIsNewerThanThisBuild, result.Reason);
            // It NAMES the migration, which is what makes the message actionable - the operator learns the
            // store is ahead rather than broken, and that the answer is to upgrade rather than to repair.
            Assert.Contains("20990101000000_FutureStats", result.Detail, StringComparison.Ordinal);
        }

        Assert.Equal(before, FingerprintStore());
    }

    // ---- THE INVERSE DIRECTION: a healthy store must never be condemned --------------------------------
    //
    // This direction matters MORE than the false-accept direction, and the reason is the containment design
    // itself. A statistics failure is quiet BY CONSTRUCTION: the Gateway starts, serves its roster, and
    // reports statistics unavailable with a named reason. So a healthy store that is wrongly refused produces
    // a Gateway that works perfectly well, with statistics off, and a named reason that is a LIE - and
    // nothing pages anyone. It would sit unnoticed for months.
    //
    // A false ACCEPT eventually breaks loudly on the chain. A false REFUSE never breaks at all. Every guard
    // added to this step tightened the accept direction, so each one is a chance to have quietly narrowed
    // what counts as healthy.

    /// <summary>
    /// The plain case, restated as its own assertion rather than left implied by the adoption tests: a real
    /// version 5 store WITH ROWS IN IT is adopted, not refused. If a tightening ever makes this red, the
    /// tightening is wrong - it has started condemning the exact population this step exists to rescue.
    /// </summary>
    [Fact]
    public void Adopt_AHealthyVersion5StoreWithRows_IsNeverRefused()
    {
        BuildRealVersion5Store();
        SeedDistinguishableStatDeltaRows();

        using var context = OpenContext();
        var result = GatewayStatsSqliteAdoption.Adopt(context);

        Assert.True(result.IsUsable,
            $"A healthy version 5 store was refused as {result.Reason}: {result.Detail}");
        Assert.Equal(StatsStoreAdoptionOutcome.Adopted, result.Outcome);
        Assert.Equal(StatsStoreUnavailableReason.None, result.Reason);
    }

    /// <summary>
    /// A store carrying rows in EVERY one of the sixteen tables, not just the one the other tests seed.
    /// The column check walks all sixteen, so a mistake in the expected shape of a table nothing else
    /// populates would only show here - and would show as a healthy store being condemned.
    /// </summary>
    [Fact]
    public void Adopt_AHealthyVersion5StoreWithEveryTablePopulated_IsNeverRefused()
    {
        BuildRealVersion5Store();

        using (var db = new GatewayStatsDatabase(_path, GatewayStatsSqliteAdoption.LegacyBaselineSchemaVersion))
        {
            void Run(string sql)
            {
                using var command = db.Connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            Run("INSERT INTO stat_delta(hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, " +
                "turns, chars, model_id, checkout_id, tenant) " +
                "VALUES ('2026-07-30T09', 's1', 'typed', 'terminal', 1, 3, 0, 7, 42, 11, 22, 'local')");
            Run("INSERT INTO token_delta(hour_utc, model_id, input_tokens, output_tokens, cache_read_tokens, " +
                "cache_creation_tokens, tenant) VALUES ('2026-07-30T09', 11, 1, 2, 3, 4, 'local')");
            Run("INSERT INTO agent_delta(agent_id, is_voice, turns, chars, tenant) VALUES (1, 1, 5, 6, 'local')");
            Run("INSERT INTO agent_driven_delta(agent_id, turns, chars, tenant) VALUES (1, 7, 8, 'local')");
            Run("INSERT INTO repo_identity(repo_display, tenant) VALUES ('owner/repo', 'local')");
            Run("INSERT INTO agent_identity(agent_display, tenant) VALUES ('claude', 'local')");
            Run("INSERT INTO model_identity(model_display, tenant) VALUES ('opus', 'local')");
            Run("INSERT INTO checkout_identity(checkout_display, tenant) VALUES ('D:/repo', 'local')");
            Run("INSERT INTO session_highwater(tenant, session_id, modality, surface, turns, chars) " +
                "VALUES ('local', 's1', 'typed', 'terminal', 7, 42)");
            Run("INSERT INTO token_highwater(tenant, session_id, input_tokens, output_tokens, " +
                "cache_read_tokens, cache_creation_tokens) VALUES ('local', 's1', 1, 2, 3, 4)");
            Run("INSERT INTO agent_driven_highwater(tenant, session_id, turns, chars) VALUES ('local', 's1', 7, 8)");
            Run("INSERT INTO wingman_session(tenant, session_id) VALUES ('local', 's1')");
            Run("INSERT INTO agents_seeded(tenant, session_id) VALUES ('local', 's1')");
            Run("INSERT INTO repo_session(repo_id, session_id) VALUES (1, 's1')");
            Run("INSERT INTO agent_session(agent_id, session_id) VALUES (1, 's1')");
            Run("INSERT OR IGNORE INTO meta(tenant, name, value) VALUES ('local', 'agents_since_utc', '2026-07-30T00:00:00Z')");
        }

        SqliteConnection.ClearAllPools();

        using var context = OpenContext();
        var result = GatewayStatsSqliteAdoption.Adopt(context);

        Assert.True(result.IsUsable,
            $"A healthy fully-populated version 5 store was refused as {result.Reason}: {result.Detail}");
        Assert.Equal(StatsStoreAdoptionOutcome.Adopted, result.Outcome);

        // And it is genuinely usable afterwards, not merely blessed.
        context.Database.Migrate();
        Assert.Empty(context.Database.GetPendingMigrations());
        Assert.Single(context.StatDeltas.ToList());

        // meta carries TWO rows, not the one this test seeds. models_since_utc is written by the store's own
        // version 2 migration when the file is created, so a real version 5 store is never empty of meta -
        // asserting a single row was this test being wrong about its own fixture's starting state, which is
        // exactly the thing a fixture built by RUNNING the old code is able to correct.
        //
        // Asserted by NAME rather than by count, so it says which rows it means and does not go quietly green
        // again if some future migration adds a third.
        var meta = context.Meta.ToList();
        Assert.Contains(meta, m => m.Name == GatewayStatsDatabase.ModelsSinceKey && m.Tenant == "local");
        Assert.Contains(meta, m => m.Name == "agents_since_utc" && m.Value == "2026-07-30T00:00:00Z");
    }

    /// <summary>
    /// A store the CHAIN built and has been running on for a while - the state every machine is in after the
    /// first adopted startup. It must come back tracked and usable, not refused by one of the guards added
    /// for damaged stores.
    /// </summary>
    [Fact]
    public void Adopt_AHealthyChainBuiltStoreWithRows_IsTrackedAndNeverRefused()
    {
        using (var context = OpenContext())
        {
            context.Database.Migrate();
            // Fully qualified from global:: on purpose. The relative form binds BY PROXIMITY: it resolved
            // to CcDirector.Gateway.Stats only because no CcDirector.Gateway.Tests.Stats namespace existed,
            // and the moment one does - the read-port tests add exactly that - this stops compiling with
            // CS0234. Nothing is wrong on either branch alone, which is why it has to be pinned here.
            context.StatDeltas.Add(new global::CcDirector.Gateway.Stats.Data.Entities.StatDeltaEntity
            {
                HourUtc = "2026-07-30T09", SessionId = "s1", Modality = "typed", Surface = "terminal",
                IsVoice = true, RepoId = 3, Wingman = false, Turns = 7, Chars = 42, Tenant = "local",
            });
            context.SaveChanges();
        }

        SqliteConnection.ClearAllPools();

        using var reopened = OpenContext();
        var result = GatewayStatsSqliteAdoption.Adopt(reopened);

        Assert.True(result.IsUsable,
            $"A healthy chain-built store was refused as {result.Reason}: {result.Detail}");
        Assert.Equal(StatsStoreAdoptionOutcome.AlreadyTracked, result.Outcome);
        Assert.Single(reopened.StatDeltas.ToList());
    }

    /// <summary>
    /// REFUSE ON MISSING, TOLERATE EXTRA - the asymmetry, pinned on the tolerant side.
    ///
    /// A missing column breaks queries loudly and immediately, so refusing is the only safe answer, and
    /// <see cref="Adopt_AVersion5StoreMissingAColumn_IsRefused"/> holds that line. An extra column is
    /// harmless to every query this store runs, because all sixteen tables are read by an explicit column
    /// list - measured, not assumed - so refusing on it buys nothing and costs the worse failure: condemning
    /// a healthy store, which here is silent and permanent.
    ///
    /// Strictness would also be redundant. The realistic way a store gains a column is a newer build adding
    /// one and the user rolling back, and that store's version stamp is HIGHER - so the version check refuses
    /// it first, more precisely, and with a message about versions rather than columns.
    /// </summary>
    [Fact]
    public void Adopt_AVersion5StoreWithAnExtraColumn_IsStillAdopted()
    {
        BuildRealVersion5Store();
        SeedDistinguishableStatDeltaRows();

        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // Added WITHOUT moving the version stamp, so this exercises the column check rather than the
            // version check - the state the version stamp would otherwise have caught first.
            command.CommandText = "ALTER TABLE stat_delta ADD COLUMN something_extra TEXT";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using var context = OpenContext();
        var result = GatewayStatsSqliteAdoption.Adopt(context);

        Assert.True(result.IsUsable,
            $"A version 5 store with a harmless extra column was refused as {result.Reason}: {result.Detail}");
        Assert.Equal(StatsStoreAdoptionOutcome.Adopted, result.Outcome);

        // And it genuinely works - the extra column does not disturb reads, because every read names its
        // columns. That is the measured fact the tolerance rests on, asserted rather than trusted.
        context.Database.Migrate();
        Assert.Equal(2, context.StatDeltas.Count());
    }

    /// <summary>The other side of the same ruling: a MISSING column must still refuse, because that is the
    /// one that breaks queries. Without this, tolerating extras could quietly become tolerating anything.
    /// </summary>
    [Fact]
    public void Adopt_AVersion5StoreMissingAColumn_IsRefused()
    {
        BuildRealVersion5Store();

        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE stat_delta DROP COLUMN chars";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        using var context = OpenContext();
        var result = GatewayStatsSqliteAdoption.Adopt(context);

        Assert.False(result.IsUsable);
        Assert.Equal(StatsStoreUnavailableReason.StoreSchemaIncomplete, result.Reason);
        Assert.Contains("chars", result.Detail, StringComparison.Ordinal);
    }

    // ---- the ordinary new-machine paths ----------------------------------------------------------------

    [Fact]
    public void Adopt_NoFile_IsAFreshStoreAndTheChainCreatesTheSchema()
    {
        using var context = OpenContext();

        var result = GatewayStatsSqliteAdoption.Adopt(context);

        Assert.Equal(StatsStoreAdoptionOutcome.FreshStore, result.Outcome);
        Assert.True(result.IsUsable);

        context.Database.Migrate();
        Assert.Empty(context.Database.GetPendingMigrations());
    }

    /// <summary>An empty file reports PRAGMA user_version 0. It must read as a fresh store, not as an
    /// incompatible version - otherwise a zero-byte file takes the statistics surface down.</summary>
    [Fact]
    public void Adopt_EmptyFile_IsAFreshStoreRatherThanAnIncompatibleVersion()
    {
        File.WriteAllBytes(_path, Array.Empty<byte>());

        using var context = OpenContext();
        var result = GatewayStatsSqliteAdoption.Adopt(context);

        Assert.Equal(StatsStoreAdoptionOutcome.FreshStore, result.Outcome);
        Assert.True(result.IsUsable);

        context.Database.Migrate();
        Assert.Empty(context.Database.GetPendingMigrations());
    }

    // ---- the caller contract ---------------------------------------------------------------------------

    /// <summary>A context that is not on SQLite is a CALLER error, not a user state, so it throws rather than
    /// being reported as an unavailable store. The two are deliberately handled differently and that split
    /// is pinned here.</summary>
    [Fact]
    public void Adopt_NonSqliteContext_ThrowsBecauseThatIsACallerErrorNotAUserState()
    {
        var options = new DbContextOptionsBuilder<GatewayStatsDbContext>()
            .UseNpgsql("Host=localhost;Database=design;Username=design;Password=design")
            .Options;
        using var context = new GatewayStatsDbContext(options);

        Assert.Throws<ArgumentException>(() => GatewayStatsSqliteAdoption.Adopt(context));
    }
}
