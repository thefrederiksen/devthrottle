using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// REFUSED AND UNMODIFIED ARE TWO CLAIMS, AND THIS FILE ASSERTS THE SECOND ONE.
///
/// A guard that rejects an input has two obligations: to DECLINE it, and to LEAVE IT UNTOUCHED. Almost every
/// test written for such a guard asserts only the first, and that is not a hypothetical worry here - the
/// adoption step once certified a FOREIGN database as fresh and then wrote sixteen statistics tables and a
/// baseline row INTO IT. The harm was the SIDE EFFECT and not the verdict, and a test asserting only that it
/// said no would have passed throughout.
///
/// WHAT THIS FILE ADDS THAT THE ADOPTION TESTS DO NOT. Those drive <c>GatewayStatsSqliteAdoption.Adopt</c>
/// DIRECTLY. The real startup path is <see cref="GatewayStatsStore"/>, which does strictly more before and
/// after that call: it builds a provider, creates the storage directory, opens a POOLED connection, runs the
/// adoption step, and then disposes the provider on refusal. Every one of those is an opportunity to touch a
/// file that the direct tests cannot see, so the whole path is exercised here and the store is measured
/// before and after.
///
/// WHAT MAKES THESE FIXTURES ABLE TO SHOW THE FAILURE:
///
///  1. Both stores are built by RUNNING REAL CODE or by writing a real database - never by describing what
///     one is believed to look like.
///  2. Each asserts its OWN PREMISE before the refusal: the foreign table is really there, the version stamp
///     is really the one being rejected. A store that had never contained the thing cannot demonstrate that
///     the thing survived.
///  3. The survival assertions are POSITIVE about what must remain and NEGATIVE about what must not appear.
///     Checking only "no migration history table" would pass against a refusal that dropped every table the
///     operator owned - which is the more destructive failure of the two.
/// </summary>
public sealed class GatewayStatsStoreRefusalLeavesTheStoreUntouchedTests : IDisposable
{
    // A clock that stands still. The store bounds its open by eight seconds on its clock; on a busy machine the open
    // of a fresh local file has taken longer than that, and the store then reports "did not answer in time" for a
    // test that is about something else. With this clock that deadline never runs out, so every test here is
    // decided by what the store did and not by how long the machine took.
    private static readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider StoodStill = new();

    private readonly ITestOutputHelper _out;
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-stats-refusal-" + Guid.NewGuid().ToString("N"));

    public GatewayStatsStoreRefusalLeavesTheStoreUntouchedTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private string SqlitePath => Path.Combine(_dir, "gateway-stats.db");

    // ================================================ a database that is not ours at all

    /// <summary>
    /// THE DEFECT'S OWN SCENARIO, DRIVEN THROUGH THE WHOLE STARTUP PATH. Somebody else's database carrying
    /// this store's version stamp is refused, and - the claim that actually matters - their table is still
    /// there afterwards, with no statistics table and no migration history written into it.
    /// </summary>
    [Fact]
    public void ForeignDatabase_IsRefused_AndIsLeftExactlyAsItWasFound()
    {
        BuildForeignDatabase();

        // THE PREMISE, asserted before the refusal. If their table were not really there, "it survived"
        // would be a statement about nothing.
        Assert.True(TableExists("somebody_elses_table"));
        Assert.Equal(1, CountRows("somebody_elses_table"));
        Assert.False(TableExists("stat_delta"));
        Assert.False(TableExists("__EFMigrationsHistory"));
        var tablesBefore = CountUserTables();
        // Captured, not assumed: the survival claim below is "the stamp is EXACTLY what it was before the
        // refusal", whatever the current build's version happens to be. This line used to be a literal 5
        // asserted after the fact - a copy of the constant in a second place, which went stale the day the
        // schema version moved and reported a phantom mutation of a file nothing had touched.
        var versionBefore = UserVersion();

        using (var store = new GatewayStatsStore(SelfHostChoice(), StoodStill))
        {
            // DECLINED - the first obligation.
            Assert.False(store.IsAvailable);
            Assert.Equal(StatsStoreUnavailableReason.NotAStatisticsStore, store.Availability.Reason);
            Assert.Equal("not_a_statistics_store", store.Availability.ReasonCode);
            Assert.Null(store.Factory);

            _out.WriteLine($"REFUSED: {store.Availability.ReasonCode}: {store.Availability.Detail}");
        }

        SqliteConnection.ClearAllPools();

        // UNMODIFIED - the second obligation, and the one the original defect broke.
        Assert.True(TableExists("somebody_elses_table"), "The refused database LOST the owner's table.");
        Assert.Equal(1, CountRows("somebody_elses_table"));
        Assert.False(TableExists("stat_delta"), "A statistics table was written into a foreign database.");
        Assert.False(
            TableExists("__EFMigrationsHistory"),
            "A migration history table was stamped into a database this store had just refused.");
        Assert.Equal(tablesBefore, CountUserTables());
        Assert.Equal(versionBefore, UserVersion());

        _out.WriteLine(
            $"UNTOUCHED: tables={CountUserTables()} user_version={UserVersion()} " +
            $"foreign_rows={CountRows("somebody_elses_table")} " +
            $"stat_delta_exists={TableExists("stat_delta")} " +
            $"history_exists={TableExists("__EFMigrationsHistory")}");
    }

    // ================================================ our store, at a version this build cannot adopt

    /// <summary>
    /// A REAL statistics store at a version this build cannot adopt, driven through the whole startup path.
    /// Refused, and the operator's sixteen tables and their rows are still there.
    ///
    /// Version 99 stands for the dangerous direction - a file written by a NEWER build that knows something
    /// this one does not. Opening it would be a downgrade against a shape this build cannot see, and the
    /// fastest way to lose the owner's numbers is to touch it anyway.
    /// </summary>
    [Fact]
    public void StoreAtAnUnadoptableVersion_IsRefused_AndKeepsEveryTableAndRow()
    {
        BuildRealHandRolledStore();
        SeedOneStatDeltaRow();
        SetUserVersion(99);

        Assert.Equal(99, UserVersion());
        Assert.Equal(1, CountRows("stat_delta"));
        var tablesBefore = CountUserTables();
        Assert.True(tablesBefore >= 16);

        using (var store = new GatewayStatsStore(SelfHostChoice(), StoodStill))
        {
            Assert.False(store.IsAvailable);
            Assert.Equal(StatsStoreUnavailableReason.IncompatibleSchemaVersion, store.Availability.Reason);
            Assert.Equal("incompatible_schema_version", store.Availability.ReasonCode);
            Assert.Null(store.Factory);

            _out.WriteLine($"REFUSED: {store.Availability.ReasonCode}: {store.Availability.Detail}");
        }

        SqliteConnection.ClearAllPools();

        // The rows are the point. A refusal that kept the tables and emptied one of them would satisfy a
        // table-count check and would still have destroyed what the operator came for.
        Assert.Equal(tablesBefore, CountUserTables());
        Assert.Equal(1, CountRows("stat_delta"));
        Assert.Equal(99, UserVersion());
        Assert.False(
            TableExists("__EFMigrationsHistory"),
            "A baseline was stamped into a store this build had just refused to adopt.");

        _out.WriteLine(
            $"UNTOUCHED: tables={CountUserTables()} user_version={UserVersion()} " +
            $"stat_delta_rows={CountRows("stat_delta")} history_exists={TableExists("__EFMigrationsHistory")}");
    }

    // ================================================ fixtures

    /// <summary>Somebody else's database, carrying THIS store's version stamp so that the version gate alone
    /// cannot be what refuses it - the table check has to be doing the work.</summary>
    private void BuildForeignDatabase()
    {
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = SqlitePath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE somebody_elses_table (id INTEGER PRIMARY KEY, note TEXT); " +
                "INSERT INTO somebody_elses_table(note) VALUES ('do not touch'); " +
                $"PRAGMA user_version={GatewayStatsDatabase.SchemaVersion}";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>A REAL version 5 store, built by running the shipped hand-rolled creation code rather than
    /// by describing what such a file is believed to contain.</summary>
    private void BuildRealHandRolledStore()
    {
        using (var db = new GatewayStatsDatabase(SqlitePath))
        {
            // The fixture is the FILE this block just created by running the real shipped code; what it
            // must look like is pinned by the assertions that follow. There used to be an
            // Assert.Equal(5, GatewayStatsDatabase.SchemaVersion) here - a literal copy of a constant,
            // which can only ever fail when somebody legitimately moves the constant. Raising the
            // schema version to 7 did exactly that and reddened five files at once for no defect
            // (issue #1156). A pin that fires on correct changes is noise, not a guard.
        }

        SqliteConnection.ClearAllPools();
    }

    private void SeedOneStatDeltaRow()
    {
        using (var db = new GatewayStatsDatabase(SqlitePath))
        {
            using var command = db.Connection.CreateCommand();
            command.CommandText =
                "INSERT INTO stat_delta(hour_utc, session_id, modality, surface, is_voice, repo_id, wingman, " +
                "turns, chars, model_id, checkout_id, tenant) " +
                "VALUES ('2026-07-30T09', 'session-a', 'typed', 'terminal', 0, 1, 0, 7, 42, NULL, NULL, 'local')";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    private void SetUserVersion(int version)
    {
        Execute($"PRAGMA user_version={version}");
        SqliteConnection.ClearAllPools();
    }

    private StatsConnectionChoice SelfHostChoice() =>
        StatsConnectionSelection.Resolve(
            statsOverride: null, gatewayConnection: null, hosted: false, sqlitePath: SqlitePath);

    // ================================================ reading the store without going through the product

    private bool TableExists(string name) => ScalarInt(
        $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{name}'") == 1;

    private int CountUserTables() => ScalarInt(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'");

    private int CountRows(string table) =>
        TableExists(table) ? ScalarInt($"SELECT COUNT(*) FROM {table}") : -1;

    private int UserVersion() => ScalarInt("PRAGMA user_version");

    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private int ScalarInt(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = SqlitePath }.ToString());
        connection.Open();
        return connection;
    }
}
