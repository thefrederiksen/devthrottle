using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A MIGRATION THAT DIED PART-WAY THROUGH THE CHAIN, AND WHAT THE CONTAINMENT DOES ABOUT IT.
///
/// THE QUESTION THIS FILE EXISTS TO SETTLE. Ledger row 18 records a state nobody designed and everybody can
/// reach: a migration history table sitting there recording NOTHING as applied, next to tables this store
/// already owns. The chain reads an empty history, decides the baseline is pending, tries to create sixteen
/// tables that exist, and dies on the first one. The Architect kept that row OUT of Step 2 conditionally -
/// conditional on the containment being able to cover it. This file is that condition, discharged by running
/// rather than by reading the code and believing it.
///
/// WHAT THE CONTAINMENT CAN AND CANNOT DO, because the difference decides the row. It CANNOT prevent the
/// state: that state is made by a process that DIES mid-migration, and a try-catch catches nothing once the
/// process is gone. What it buys is that the NEXT startup over that state is survivable - the Gateway starts,
/// serves its roster, and says what is wrong with a reason that points at the right place.
///
/// WHY THE REASON IS ITS OWN NAME AND NOT "UNREACHABLE". This half-built store is REACHED perfectly well. The
/// database is up, the network is fine, the settings are right. Reporting it as unreachable would send the
/// first responder to check three healthy things, and the fault would be sitting on the store's own disk the
/// whole time. So INCOMPLETE SCHEMA is a distinct reason on the same grounds that NOT CONFIGURED and
/// UNREACHABLE are distinct: a named reason exists to separate causes that are FIXED IN DIFFERENT PLACES.
///
/// HOW THESE FIXTURES CAN FAIL, which is what makes a green here worth anything:
///
///  1. The half-built store is built by RUNNING THE REAL OLD CODE - a genuine GatewayStatsDatabase creates
///     the sixteen tables - and then adding the empty history table with ENTITY FRAMEWORK'S OWN create
///     script. Nothing here is a hand-written guess at what a broken store looks like; a fixture synthesised
///     from the new code's own understanding would be a guard supplying its own evidence.
///  2. <see cref="TheHalfBuiltStore_IsFatal_WhenItIsNotContained"/> runs that same file through the ordinary
///     migration path with nothing containing it and watches it THROW, naming the table. Without that arm
///     every assertion below could pass against a store that was never in a broken state at all.
///  3. <see cref="ContainedOpen_ChangesNothingOnDisk"/> seeds a row first. "It did not repair anything" is
///     otherwise indistinguishable from "there was nothing there to lose".
///  4. <see cref="TheThreeReasons_AreAllDifferentFromEachOther"/> produces all three states side by side and
///     asserts they differ PAIRWISE. Asserting each equals its own expected value would pass just as happily
///     against a build that had collapsed two of them, which is the exact defect the ruling exists to stop.
/// </summary>
public sealed class GatewayStatsStoreMidChainContainmentTests : IDisposable
{
    // A clock that stands still. The store bounds its open by eight seconds on its clock; on a busy machine the open
    // of a fresh local file has taken longer than that, and the store then reports "did not answer in time" for a
    // test that is about something else. With this clock that deadline never runs out, so every test here is
    // decided by what the store did and not by how long the machine took.
    private static readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider StoodStill = new();

    /// <summary>
    /// A PostgreSQL endpoint that is not there - port 1 on the loopback interface, refused immediately. Used
    /// only to produce a genuine UNREACHABLE beside the other two reasons for the pairwise comparison.
    /// </summary>
    private const string DeadPostgres =
        "Host=127.0.0.1;Port=1;Database=gateway_live;Username=gateway_app;Password=s3cret;" +
        "Timeout=2;Command Timeout=2";

    private readonly ITestOutputHelper _out;
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-stats-midchain-" + Guid.NewGuid().ToString("N"));

    public GatewayStatsStoreMidChainContainmentTests(ITestOutputHelper output)
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

    // ============================================================ the fixture, and its own premises pinned

    /// <summary>
    /// Build the exact state ledger row 18 describes: this store's tables present, a migration history table
    /// present, and NOTHING recorded in it.
    ///
    /// The tables come from RUNNING the shipped hand-rolled code, so they are the real on-disk shape rather
    /// than the new model's belief about it. The history table comes from Entity Framework's OWN create
    /// script, so it is byte-identical to the one an interrupted migration would have left and cannot drift
    /// from it when the framework version moves. The baseline row is then deliberately not inserted - which
    /// is precisely the window the adoption step's own comment warns about, one statement wide.
    /// </summary>
    private void BuildHalfBuiltStore()
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

        using (var context = OpenContext())
        {
            var history = context.GetService<IHistoryRepository>();
            context.Database.ExecuteSqlRaw(history.GetCreateScript());
        }

        SqliteConnection.ClearAllPools();

        // THE FIXTURE-SHAPE GUARD. Every claim in this file is about a store that is HALF BUILT, and each of
        // these three premises is a way the fixture could quietly stop being that - leaving tests that pass
        // for reasons unconnected to the thing they name. A fixture that cannot show the failure is refused
        // here rather than run.
        Assert.True(File.Exists(SqlitePath), "The fixture did not create a statistics file at all.");
        Assert.True(
            CountTables() >= 16,
            $"The fixture holds only {CountTables()} tables, so there is no half-built schema to find: the " +
            "chain would simply create everything and every assertion below would pass for the wrong reason.");
        Assert.True(
            HistoryTableExists(),
            "The fixture has no migration history table, which is the ORDINARY adoptable store worker 2's " +
            "adoption step handles - not the half-built state this file is about.");
        Assert.Equal(0, CountHistoryRows());
    }

    /// <summary>One row through the OLD code's own connection, so "nothing was lost" is a claim with
    /// something behind it rather than a statement about an empty file.</summary>
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

    // ============================================================ the fault is real: watched, uncontained

    /// <summary>
    /// THE FAILING DIRECTION, WATCHED. The same half-built store, migrated the ordinary way with nothing
    /// containing it, THROWS - and names the table it could not create. This is what a Gateway that ran the
    /// statistics migration inside its startup gate would do over a store left in this state: refuse to start.
    ///
    /// It is a PERMANENT test and not a run somebody once did, because every containment assertion below
    /// rests on this fault being real, and a claim resting on a fixture nobody re-checks goes stale the first
    /// time the fixture quietly stops being broken.
    /// </summary>
    [Fact]
    public void TheHalfBuiltStore_IsFatal_WhenItIsNotContained()
    {
        BuildHalfBuiltStore();

        using var context = OpenContext();

        var thrown = Assert.ThrowsAny<SqliteException>(() => context.Database.Migrate());

        _out.WriteLine($"UNCONTAINED: {thrown.GetType().FullName}: {thrown.Message}");

        // Pinned to the SUBSTANCE: a table this store already owns cannot be created again. Asserting only
        // "it threw" would pass for a connection error, a locked file or a build fault.
        Assert.Contains("already exists", thrown.Message, StringComparison.OrdinalIgnoreCase);

        // The table it names must be one of THIS STORE'S OWN, read from the model rather than written out
        // here. This assertion used to name agent_delta, because the baseline created the sixteen tables
        // alphabetically and died on the first - and that went red the moment worker 2 changed the order,
        // reporting 'table stat_delta already exists'. The test was RIGHT about the substance and WRONG to
        // pin the order: which table happens to be created first is incidental, and a fixture pinned to an
        // incidental detail goes red for a reason unconnected to what it measures, which teaches people to
        // re-run until green.
        using var model = OpenContext();
        var ours = model.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            ours.Any(t => thrown.Message.Contains(t, StringComparison.Ordinal)),
            "The failure did not name any table this store owns, so it is not the collision this test is " +
            "about: " + thrown.Message);
    }

    // ============================================================ the same fault, contained and named

    /// <summary>
    /// THE ANSWER TO ROW 18. The Gateway's statistics store opens that same half-built file, does NOT throw,
    /// and reports INCOMPLETE SCHEMA - the state named for what it is.
    /// </summary>
    [Fact]
    public void HalfBuiltStore_IsContained_AndReportsAHalfBuiltSchema()
    {
        BuildHalfBuiltStore();

        using var store = new GatewayStatsStore(SelfHostChoice(), StoodStill);

        Assert.False(store.IsAvailable);
        Assert.Equal(StatsStoreUnavailableReason.StoreSchemaIncomplete, store.Availability.Reason);
        Assert.Equal("store_schema_incomplete", store.Availability.ReasonCode);

        // No substitute store. Not a second file, not an empty in-memory one, not a context over something
        // else - the surface is off, and reaching for it fails explicitly.
        Assert.Null(store.Factory);
        Assert.Throws<InvalidOperationException>(() => store.CreateContext());

        // The failure is on the health surface in Step 1's shape.
        Assert.Equal(GatewayStatsStore.ObserverName, store.Health.Observer);
        Assert.Equal(1, store.Health.FailureCount);
        Assert.NotNull(store.Health.LastError);
        Assert.Null(store.Health.LastSuccessfulWrite);

        // THE SENTENCE MUST NOT SEND THE READER TO THE NETWORK. That is the defect this reason exists for,
        // and it is asserted as the ABSENCE of the misleading claim rather than the presence of one
        // particular phrasing.
        //
        // The wording used to be pinned positively ("NOT a network"), which was this branch's own sentence.
        // After the collapse the sentence comes from the adoption step, which points at the store on disk and
        // says what to do about it without that explicit clause. Pinning my phrase would have meant either a
        // permanent red or rewriting another seat's operator text to satisfy my test - and the ruling was
        // about WHERE THE READER IS SENT, not about a form of words. So the guard is: it must name the store,
        // it must say the store was not changed, and it must NOT carry the network claim that UNREACHABLE
        // carries. That holds whoever writes the sentence.
        Assert.DoesNotContain(
            "database or network problem", store.Availability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("statistics store", store.Availability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("has NOT been changed", store.Availability.Detail, StringComparison.Ordinal);

        _out.WriteLine($"CONTAINED: reason={store.Availability.ReasonCode}: {store.Availability.Detail}");
    }

    /// <summary>
    /// THE STORE IS LEFT EXACTLY AS FOUND. A contained open is not a quiet repair: the seeded row is still
    /// there, the history is still empty, and the version stamp has not moved.
    ///
    /// The seeded row is the point. Without it, "nothing was lost" would be a statement about an empty file
    /// and would hold just as well against a startup path that had dropped and recreated every table.
    /// </summary>
    [Fact]
    public void ContainedOpen_ChangesNothingOnDisk()
    {
        BuildHalfBuiltStore();
        SeedOneStatDeltaRow();

        var tablesBefore = CountTables();
        var versionBefore = UserVersion();
        Assert.Equal(1, CountStatDeltaRows());

        using (var store = new GatewayStatsStore(SelfHostChoice(), StoodStill))
        {
            Assert.False(store.IsAvailable);
            Assert.Equal(StatsStoreUnavailableReason.StoreSchemaIncomplete, store.Availability.Reason);
        }

        SqliteConnection.ClearAllPools();

        Assert.Equal(1, CountStatDeltaRows());
        Assert.Equal(tablesBefore, CountTables());
        Assert.Equal(versionBefore, UserVersion());
        Assert.Equal(0, CountHistoryRows());

        _out.WriteLine(
            $"UNCHANGED: tables={CountTables()} user_version={UserVersion()} " +
            $"history_rows={CountHistoryRows()} stat_delta_rows={CountStatDeltaRows()}");
    }

    // ============================================================ THE RULING, now across three reasons

    /// <summary>
    /// NOT CONFIGURED, UNREACHABLE and INCOMPLETE SCHEMA, produced side by side and compared PAIRWISE.
    ///
    /// The comparison is the test. Each state asserted only against its own expected value would pass against
    /// a build that had collapsed any two of them into one generic "statistics unavailable" - and that
    /// collapse is exactly the defect the ruling exists to prevent, because the three are fixed in three
    /// different places: a setting, a database, and the store's own disk.
    /// </summary>
    [Fact]
    public void TheThreeReasons_AreAllDifferentFromEachOther()
    {
        BuildHalfBuiltStore();

        // NOT CONFIGURED: the override is SET BUT BLANK - a real operator error, never read as unset.
        using var notConfigured = new GatewayStatsStore(
            StatsConnectionSelection.Resolve("", null, hosted: true, sqlitePath: SqlitePath), StoodStill);

        // UNREACHABLE: the settings are right and the database is not there.
        using var unreachable = new GatewayStatsStore(
            StatsConnectionSelection.Resolve(DeadPostgres, null, hosted: true, sqlitePath: SqlitePath), StoodStill);

        // INCOMPLETE SCHEMA: the store is right there and its schema is half built.
        using var incomplete = new GatewayStatsStore(SelfHostChoice(), StoodStill);

        // All three are unavailable, so "is it available" cannot tell any of them apart. That is the premise
        // the rest of the test rests on, and it is asserted rather than assumed.
        Assert.False(notConfigured.IsAvailable);
        Assert.False(unreachable.IsAvailable);
        Assert.False(incomplete.IsAvailable);

        Assert.Equal(StatsStoreUnavailableReason.NotConfigured, notConfigured.Availability.Reason);
        Assert.Equal(StatsStoreUnavailableReason.Unreachable, unreachable.Availability.Reason);
        Assert.Equal(StatsStoreUnavailableReason.StoreSchemaIncomplete, incomplete.Availability.Reason);

        var reasons = new[]
        {
            notConfigured.Availability.Reason,
            unreachable.Availability.Reason,
            incomplete.Availability.Reason,
        };
        Assert.Equal(3, reasons.Distinct().Count());

        // The stable code an operator greps for, and a surface keys off.
        var codes = new[]
        {
            notConfigured.Availability.ReasonCode,
            unreachable.Availability.ReasonCode,
            incomplete.Availability.ReasonCode,
        };
        Assert.Equal(new[] { "not_configured", "unreachable", "store_schema_incomplete" }, codes);
        Assert.Equal(3, codes.Distinct(StringComparer.Ordinal).Count());

        // The operator-facing sentences, which are what a human actually acts on.
        var details = new[]
        {
            notConfigured.Availability.Detail,
            unreachable.Availability.Detail,
            incomplete.Availability.Detail,
        };
        Assert.Equal(3, details.Distinct(StringComparer.Ordinal).Count());

        // And each sends the reader somewhere DIFFERENT, which is the entire justification for three members
        // rather than one. One names the setting to fix; one says the setting is already right and the
        // database is not answering; one says the database IS answering and the fault is on its disk.
        Assert.Contains(
            StatsConnectionSelection.StatsConnectionEnvVar,
            notConfigured.Availability.Detail,
            StringComparison.Ordinal);
        Assert.Contains(
            "rather than a missing setting", unreachable.Availability.Detail, StringComparison.Ordinal);

        // THE CONTRAST IS THE ASSERTION. The unreachable sentence DOES send the reader to the database or
        // the network; the half-built one must NOT, because there the database is answering and the fault is
        // on its disk. Asserting the pair together is what makes this meaningful - checking only that the
        // half-built sentence lacks a phrase would also pass if no sentence anywhere used it.
        Assert.Contains(
            "database or network problem", unreachable.Availability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "database or network problem", incomplete.Availability.Detail, StringComparison.OrdinalIgnoreCase);

        _out.WriteLine($"NOT CONFIGURED:   {notConfigured.Availability.ReasonCode}: {notConfigured.Availability.Detail}");
        _out.WriteLine($"UNREACHABLE:      {unreachable.Availability.ReasonCode}: {unreachable.Availability.Detail}");
        _out.WriteLine($"INCOMPLETE SCHEMA: {incomplete.Availability.ReasonCode}: {incomplete.Availability.Detail}");
    }

    // ============================================================ the diagnosis does not fire on healthy stores

    /// <summary>
    /// THE OTHER FAILURE DIRECTION OF THE SAME GUARD. A guard that reports a half-built schema has two ways
    /// to be wrong, and only one of them is a missed detection: this is the other one, where it condemns a
    /// perfectly good store. A fresh machine has no file at all and a returning one has a fully migrated
    /// file, and neither may be reported as half built - a guard that failed this way would take the
    /// statistics surface down on every healthy self-host Gateway.
    /// </summary>
    [Fact]
    public void HealthyStores_AreNotReportedAsIncomplete()
    {
        // A fresh machine: no file at all. The chain creates the schema.
        using (var fresh = new GatewayStatsStore(SelfHostChoice(), StoodStill))
        {
            Assert.True(
                fresh.IsAvailable,
                $"A fresh store was refused: {fresh.Availability.ReasonCode}: {fresh.Availability.Detail}");
            Assert.Equal(StatsStoreUnavailableReason.None, fresh.Availability.Reason);
        }

        SqliteConnection.ClearAllPools();

        // The steady state: the same file, fully migrated, opened again. Its history is non-empty and its
        // tables are there, which is the pair of facts the diagnosis keys off - so this is the case a
        // careless check would condemn.
        using var reopened = new GatewayStatsStore(SelfHostChoice(), StoodStill);
        Assert.True(
            reopened.IsAvailable,
            $"A migrated store was refused on reopen: {reopened.Availability.ReasonCode}: " +
            $"{reopened.Availability.Detail}");
        Assert.Equal(StatsStoreUnavailableReason.None, reopened.Availability.Reason);

        _out.WriteLine(
            $"HEALTHY: reopened available={reopened.IsAvailable} history_rows={CountHistoryRows()} " +
            $"tables={CountTables()}");
    }

    // ============================================================ helpers

    private StatsConnectionChoice SelfHostChoice() =>
        StatsConnectionSelection.Resolve(
            statsOverride: null, gatewayConnection: null, hosted: false, sqlitePath: SqlitePath);

    private GatewayStatsDbContext OpenContext()
    {
        var options = new DbContextOptionsBuilder<GatewayStatsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = SqlitePath }.ToString())
            .Options;
        return new GatewayStatsDbContext(options);
    }

    private int CountTables() => ScalarInt(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' " +
        "AND name <> '__EFMigrationsHistory'");

    private bool HistoryTableExists() => ScalarInt(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'") == 1;

    private int CountHistoryRows() =>
        HistoryTableExists() ? ScalarInt("SELECT COUNT(*) FROM __EFMigrationsHistory") : 0;

    private int CountStatDeltaRows() => ScalarInt("SELECT COUNT(*) FROM stat_delta");

    private int UserVersion() => ScalarInt("PRAGMA user_version");

    private int ScalarInt(string sql)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = SqlitePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }
}
