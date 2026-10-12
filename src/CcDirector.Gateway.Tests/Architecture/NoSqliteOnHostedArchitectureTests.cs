using CcDirector.Gateway.Data;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace CcDirector.Gateway.Tests.Architecture;

/// <summary>
/// DT-SQL: the no-SQLite-on-hosted guard. The hosted Gateway must not keep a SQLite database.
///
/// WHY. On 2026-07-30 the hosted Gateway answered HTTP 500 to every client for 32 minutes: it keeps a SQLite
/// database on an Azure Files network share, a deploy ran two containers against that share, and the file was
/// corrupted. The remediation removes that database. THIS is what stops it coming back - the next hand that
/// adds an innocent-looking SQLite-backed store to the Gateway is stopped by a red test, not by somebody
/// remembering the incident.
///
/// WHY THE RULE IS STATIC RATHER THAN A STARTUP OR RUNTIME ASSERTION, which is the whole design decision here.
/// A runtime assertion - at startup, or inside a composed test host - only fires on a path that actually RUNS.
/// The thing being defended against is the store nobody has written yet, whose code will sit behind a lazy
/// initializer, a background sweep, or an endpoint no test calls. Such a guard passes GREEN for the exact
/// reason it should be red: nothing exercised the code that would open the connection. There is also no
/// supported process-wide interception point for <c>new SqliteConnection(...).Open()</c> - Microsoft.Data.Sqlite
/// exposes no global hook - so a runtime guard can only ever bind the sites that remember to CALL it, which is
/// precisely the "only checks the call site we already know about" weakness that makes a guard worth little.
/// A metadata scan has neither problem: it reads what the compiled assembly CAN do, so it does not care
/// whether any path ran, and it sees every site whether or not that site opted in.
///
/// AND THE POLARITY IS AN ALLOWLIST, NOT A DENYLIST. A denylist catches the call sites already known. An
/// allowlist catches every OTHER one - so a type nobody has written yet is caught by construction, because it
/// is not on the list. Adding a type to <see cref="SqliteTouchingTypes"/> is a deliberate, review-visible act
/// carrying a written reason, exactly as <c>TenantGateArchitectureTests.GlobalUnscopedTables</c> works for
/// un-scoped tables.
///
/// The rules:
///  - DT-SQL-1: no type in the hosted Gateway assemblies touches SQLite unless it is named on
///    <see cref="SqliteTouchingTypes"/>.
///  - DT-SQL-5: no exemption outlives its justification. Every allowlist entry carries a MACHINE-CHECKABLE
///    condition rather than only a reason, and the guard fails when a transitional one expires or a
///    structural one's property stops holding. A reason nobody checks is how a temporary accommodation
///    becomes a permanent hole.
///  - DT-SQL-2: every name on <see cref="SqliteTouchingTypes"/> still exists AND still touches SQLite, so the
///    allowlist cannot rot into ghosts that permit things nobody checked. It tightens itself: when the
///    remediation takes SQLite out of the statistics store, that entry goes RED and must be DELETED.
///  - DT-SQL-3: <see cref="HostedSqliteGuard"/>'s notion of "hosted" agrees with the provider
///    <see cref="GatewayDatabase"/> actually selects, across all three states of the variable.
///  - DT-SQL-4: <see cref="HostedSqliteGuard.EnsureNotHosted"/> refuses on hosted and names the path, and
///    does nothing on self-host.
///
/// SELF-HOST KEEPS SQLITE AND THAT IS CORRECT. The rule is no SQLite on the HOSTED Gateway. Nothing here
/// forbids the SQLite code from existing or from running on a desktop install.
///
/// This class joins the <c>DirectorRoot</c> collection: DT-SQL-3 and DT-SQL-4 SET a process-wide variable,
/// <c>CC_GATEWAY_DB_CONNECTION</c>, and anything constructing a GatewayDatabase in parallel would silently be
/// handed the other test's provider. The collection serialises its members, which is what makes that
/// impossible while the variable is still process-wide.
/// </summary>
[Collection("DirectorRoot")]
public sealed class NoSqliteOnHostedArchitectureTests
{
    /// <summary>
    /// The assemblies that ARE the hosted Gateway service: the Gateway itself and the container entry point
    /// that wraps it. Both are scanned, and a missing one is a RED test rather than a smaller silent scope -
    /// see <see cref="LoadHostedGatewayModules"/>.
    /// </summary>
    private static readonly string[] HostedGatewayAssemblyFiles =
    {
        "CcDirector.Gateway.dll",
        "CcDirector.Gateway.Host.dll",
    };

    /// <summary>
    /// How an exemption is bound to a fact a machine can check, instead of to prose.
    /// </summary>
    private enum ConditionKind
    {
        /// <summary>TRANSITIONAL. The exemption is a temporary accommodation and the condition describes the
        /// world in which it is no longer justified. The guard FAILS when the condition becomes TRUE.</summary>
        ExpiresWhenTrue,

        /// <summary>STRUCTURAL. The exemption rests on a property of the code that must keep holding. The
        /// condition IS that property, and the guard FAILS when it stops being true.</summary>
        RequiresToStayTrue,
    }

    /// <summary>An allowlist entry. The condition is not optional and there is no constructor without one, so
    /// an entry justified only in words cannot be written.</summary>
    private sealed record Exemption(string Reason, ConditionKind Kind, string Condition, Func<ScanFacts, bool> Evaluate);

    /// <summary>
    /// The types permitted to touch SQLite inside the hosted Gateway assemblies. This list is the guard: a type
    /// that is not here and touches SQLite fails DT-SQL-1 by name.
    ///
    /// EVERY ENTRY CARRIES A MACHINE-CHECKABLE CONDITION, NOT ONLY A REASON. A written reason is unfalsifiable
    /// - nothing checks whether it is true, and this list already proves it can be false: GatewayStatsDatabase
    /// sits here today with a reason that is NOT true, accepted so the guard could land ahead of the port. An
    /// exemption that cannot expire is a permanent hole wearing a temporary label, and it would be a permanent
    /// hole in the exact place this mission is about. So each entry also names a condition the scan evaluates,
    /// and DT-SQL-5 fails the moment a transitional exemption's justification lapses or a structural one's
    /// property stops holding. Nobody has to remember; the failure arrives when the justification does.
    ///
    /// ADDING A NAME HERE IS THE ONLY WAY PAST THE GUARD AND IS MEANT TO BE UNCOMFORTABLE. The question is not
    /// "does my store work" but "what makes it unreachable on hosted, and what fact would show that has
    /// stopped being true".
    /// </summary>
    private static readonly Dictionary<string, Exemption> SqliteTouchingTypes = new(StringComparer.Ordinal)
    {
        [nameof(GatewayDatabase)] = new(
            "BRANCH-GATED on the hosted marker itself: every SQLite statement in it sits behind the same " +
            "CC_GATEWAY_DB_CONNECTION test that means hosted, so a hosted Gateway takes the PostgreSQL branch " +
            "and cannot reach the file. This is the shape every other entry is measured against.",
            ConditionKind.RequiresToStayTrue,
            "GatewayDatabase still consults CC_GATEWAY_DB_CONNECTION - its own code carries that literal AND " +
            "calls Environment.GetEnvironmentVariable. Strip the provider branch and this stops holding.",
            f => f.Literals(nameof(GatewayDatabase)).Contains(HostedSqliteGuard.HostedMarkerEnvVar)
                 && f.Calls(nameof(GatewayDatabase)).Any(c => c.Contains("GetEnvironmentVariable", StringComparison.Ordinal))),

        [nameof(GatewayDbContextDesignTimeFactory)] = new(
            "DESIGN-TIME ONLY: constructed by the 'dotnet ef' tooling to scaffold migrations, never by the " +
            "Gateway process. It serves no request and holds no data.",
            ConditionKind.RequiresToStayTrue,
            "It still implements IDesignTimeDbContextFactory, which is what makes it tooling-only. Give it any " +
            "other role - a runtime factory, a store - and that interface goes, and with it the justification.",
            f => f.Interfaces(nameof(GatewayDbContextDesignTimeFactory))
                  .Any(i => i.Contains("IDesignTimeDbContextFactory", StringComparison.Ordinal))),

        // ---- The self-host statistics family. Self-host KEEPS SQLite and that is correct - the rule is no
        // SQLite on the HOSTED Gateway. These entries replaced the two transitional "the port has not
        // happened yet" accommodations the day the port landed (DT-SQL-5 expired them, exactly as designed);
        // each now carries the STRUCTURAL property that keeps its type unreachable-or-refusing on hosted.

        ["GatewayStatsDatabase"] = new(
            "THE SELF-HOST STATISTICS FILE'S OPENER, and the one type here that opens it. Runtime-guarded at " +
            "that exact line: HostedSqliteGuard.EnsureNotHosted refuses on hosted before the directory is " +
            "even created, which is the refusal added for the 2026-07-30 outage's own file.",
            ConditionKind.RequiresToStayTrue,
            "GatewayStatsDatabase still calls HostedSqliteGuard.EnsureNotHosted before opening. Remove that " +
            "call and the runtime half of the rule is gone from the only site that opens the statistics file.",
            f => f.Calls("GatewayStatsDatabase")
                  .Any(c => c.Contains("HostedSqliteGuard::EnsureNotHosted", StringComparison.Ordinal))),

        ["GatewayInputStatsAggregator"] = new(
            "Two constructors, one SQLite reach. The SELF-HOST constructor reads and writes through the " +
            "connection GatewayStatsDatabase already opened and opens none of its own, so it is gated " +
            "exactly as well as that store is: the store's constructor refuses on hosted, so this type can " +
            "never be handed a connection there. The HOSTED constructor (issue #1174) takes " +
            "GatewayStatsStore's context factory and touches no connection at all - which is why " +
            "GatewayHost.OpenInputStats DOES now construct this type on hosted, over PostgreSQL. Note that " +
            "this entry's earlier reason said the opposite - that it was never constructed on hosted - and " +
            "that clause was retired WITH the wiring rather than left to be read as still true.",
            ConditionKind.RequiresToStayTrue,
            "It still obtains its connection FROM GatewayStatsDatabase (the runtime-guarded opener) and " +
            "still opens none of its own.",
            f => f.Calls("GatewayInputStatsAggregator")
                     .Any(c => c.Contains("GatewayStatsDatabase::get_Connection", StringComparison.Ordinal))
                 && !f.Calls("GatewayInputStatsAggregator")
                     .Any(c => c.Contains("SqliteConnection::Open", StringComparison.Ordinal))),

        ["GatewayStatsDbContextDesignTimeFactory"] = new(
            "DESIGN-TIME ONLY: constructed by the 'dotnet ef' tooling to scaffold the statistics context's " +
            "SQLITE migration chain, never by the Gateway process. Same shape as " +
            "GatewayDbContextDesignTimeFactory above.",
            ConditionKind.RequiresToStayTrue,
            "It still implements IDesignTimeDbContextFactory, which is what makes it tooling-only. Give it " +
            "any other role and that interface goes, and with it the justification.",
            f => f.Interfaces("GatewayStatsDbContextDesignTimeFactory")
                  .Any(i => i.Contains("IDesignTimeDbContextFactory", StringComparison.Ordinal))),

        ["StatsConnectionSelection"] = new(
            "THE SELECTOR that decides which statistics store exists at all, and where the no-file-on-hosted " +
            "law is enforced for the store proper: on hosted it answers Postgres or NOT CONFIGURED, never " +
            "the file. Its only SQLite reach is building the self-host connection STRING; it opens nothing.",
            ConditionKind.RequiresToStayTrue,
            "Its hosted branch still refuses a statistics file in so many words, and its only SQLite reach " +
            "is still the connection-string builder.",
            f => f.Literals("StatsConnectionSelection")
                     .Any(l => l.Contains("NEVER opens a local statistics file", StringComparison.Ordinal))
                 && f.Calls("StatsConnectionSelection")
                     .Where(c => c.Contains("Sqlite", StringComparison.Ordinal))
                     .All(c => c.Contains("SqliteConnectionStringBuilder", StringComparison.Ordinal))),

        ["GatewayStatsStore"] = new(
            "THE FAILURE-DOMAIN BOUNDARY around the statistics store. Its SQLite arm serves self-host only: " +
            "the only connection string it ever opens is the one StatsConnectionSelection chose, and that " +
            "selector never chooses a file on hosted.",
            ConditionKind.RequiresToStayTrue,
            "It still routes its provider choice through StatsConnectionSelection.Resolve rather than " +
            "deciding for itself.",
            f => f.Calls("GatewayStatsStore")
                  .Any(c => c.Contains("StatsConnectionSelection::Resolve", StringComparison.Ordinal))),

        ["GatewayStatsSqliteAdoption"] = new(
            "THE SELF-HOST ADOPTION STEP: takes an existing version 5 file into the migration chain. It " +
            "opens no file of its own - it inspects the caller's connection, refuses any context that is " +
            "not on SQLite as a caller error, and its scratch reference database is in-memory.",
            ConditionKind.RequiresToStayTrue,
            "Its only production caller is still GatewayStatsStore, the boundary whose selector never " +
            "chooses SQLite on hosted - so on hosted there is no path that reaches it.",
            f => f.Calls("GatewayStatsStore")
                  .Any(c => c.Contains("GatewayStatsSqliteAdoption::Adopt", StringComparison.Ordinal))),

        ["GatewayStatsSqliteContextFactory"] = new(
            "A context factory over an ALREADY-OPEN self-host connection - the one GatewayStatsDatabase " +
            "opened, behind its runtime guard. It opens nothing itself, so it can only ever serve a " +
            "connection that got past that guard.",
            ConditionKind.RequiresToStayTrue,
            "It still opens nothing: no SqliteConnection is constructed or opened here and no connection " +
            "string is built.",
            f => !f.Calls("GatewayStatsSqliteContextFactory").Any(c =>
                     c.Contains("SqliteConnection::Open", StringComparison.Ordinal)
                     || c.Contains("SqliteConnection::.ctor", StringComparison.Ordinal)
                     || c.Contains("SqliteConnectionStringBuilder", StringComparison.Ordinal))),

        ["GatewaySessionConcurrencyStore"] = new(
            "Provider-agnostic concurrency store driven through whatever context factory GatewayStatsStore " +
            "publishes - Npgsql on hosted. Its ONLY SQLite reach is asking which provider a context is on " +
            "(IsSqlite) to choose the GREATEST/MAX spelling; it holds and opens no connection.",
            ConditionKind.RequiresToStayTrue,
            "Its only SQLite reach is still the read-only provider probe.",
            f => f.Calls("GatewaySessionConcurrencyStore")
                  .Where(c => c.Contains("Sqlite", StringComparison.Ordinal))
                  .All(c => c.Contains("IsSqlite", StringComparison.Ordinal))),
    };

    // ----------------------------------------------------------------------------------------------------
    // DT-SQL-1: the allowlist is total.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// DT-SQL-1: every type in the hosted Gateway assemblies that touches SQLite is named on
    /// <see cref="SqliteTouchingTypes"/>. "Touches" is read off the compiled metadata (Mono.Cecil), not off
    /// the source text, so it cannot be walked around by an alias, a <c>using static</c>, a differently-named
    /// helper, a partial class, a new file, or generated code: whatever the source looked like, the IL names
    /// the type it calls.
    ///
    /// Revert-proof: add a type anywhere in the Gateway that opens, builds or holds a SQLite connection and
    /// this test goes red naming that type and the SQLite member it reached for. Watched tripping - see
    /// docs/step2-nosqlite-guard-proof.md.
    /// </summary>
    [Fact]
    public void DT_SQL_1_no_unlisted_type_in_the_hosted_gateway_touches_sqlite()
    {
        var offenders = new List<string>();

        foreach (var (file, module) in LoadHostedGatewayModules())
        {
            using (module)
            {
                foreach (var type in AllTypes(module))
                {
                    var touch = FirstSqliteTouch(type);
                    if (touch is null) continue;

                    var owner = TopLevel(type).Name;
                    if (!SqliteTouchingTypes.ContainsKey(owner))
                        offenders.Add($"{file}: {type.FullName} touches {touch}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "DT-SQL-1: the hosted Gateway must keep NO SQLite database - its file lives on a network share, " +
            "where a deploy running two containers corrupts it (the 2026-07-30 outage, 32 minutes of HTTP 500 " +
            "to every client). These types touch SQLite and are not named on SqliteTouchingTypes:" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders.Distinct()) +
            Environment.NewLine +
            "Put the data in PostgreSQL (the hosted Gateway already has one), or - if this really cannot reach " +
            "a hosted Gateway - add the type to SqliteTouchingTypes with the reason it cannot, and call " +
            "HostedSqliteGuard.EnsureNotHosted before opening anything. Self-host keeps SQLite and is unaffected.");
    }

    // ----------------------------------------------------------------------------------------------------
    // DT-SQL-2: the allowlist cannot rot.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// DT-SQL-2: every name on <see cref="SqliteTouchingTypes"/> still exists in the scanned assemblies AND
    /// still touches SQLite. A known-failure allowance goes stale: an entry whose type was renamed, deleted,
    /// or cleaned of SQLite is an allowance nobody is checking, and the next type to take that name inherits
    /// a permission it was never granted.
    ///
    /// This is also what makes the guard TIGHTEN as the remediation lands: the moment the statistics store
    /// stops opening SQLite, its entry here goes red and must be deleted, and the allowlist shrinks to the two
    /// entries that are structurally safe.
    /// </summary>
    [Fact]
    public void DT_SQL_2_every_allowlisted_type_still_exists_and_still_touches_sqlite()
    {
        var stillTouching = new HashSet<string>(StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (_, module) in LoadHostedGatewayModules())
        {
            using (module)
            {
                foreach (var type in AllTypes(module))
                {
                    var owner = TopLevel(type).Name;
                    present.Add(owner);
                    if (FirstSqliteTouch(type) is not null) stillTouching.Add(owner);
                }
            }
        }

        var gone = SqliteTouchingTypes.Keys.Where(n => !present.Contains(n)).ToList();
        var cleaned = SqliteTouchingTypes.Keys.Where(n => present.Contains(n) && !stillTouching.Contains(n)).ToList();

        Assert.True(gone.Count == 0,
            "DT-SQL-2: these names are on SqliteTouchingTypes but no such type exists in the hosted Gateway " +
            "assemblies any more. A stale allowance is a permission nobody is checking, and the next type to " +
            "take the name inherits it. Delete them: " + string.Join(", ", gone));

        Assert.True(cleaned.Count == 0,
            "DT-SQL-2: these types are allowlisted for SQLite but no longer touch it - which is GOOD NEWS. " +
            "Delete their entries from SqliteTouchingTypes so the allowlist shrinks to what is really still " +
            "there, and the guard tightens around it: " + string.Join(", ", cleaned));
    }

    // ----------------------------------------------------------------------------------------------------
    // DT-SQL-5: every exemption expires MECHANICALLY.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// DT-SQL-5: no exemption outlives its justification. Each entry on <see cref="SqliteTouchingTypes"/> names
    /// a condition the scan EVALUATES rather than a reason a reader has to believe:
    ///
    ///  - a TRANSITIONAL entry (<see cref="ConditionKind.ExpiresWhenTrue"/>) names the world in which it is no
    ///    longer justified, and this test fails the moment that world arrives;
    ///  - a STRUCTURAL entry (<see cref="ConditionKind.RequiresToStayTrue"/>) names the property it rests on,
    ///    and this test fails the moment that property stops holding.
    ///
    /// WHY THIS RULE EXISTS RATHER THAN A NOTE IN A DOCUMENT. GatewayStatsDatabase is on the allowlist today
    /// with a reason that is FALSE - it really does open SQLite on hosted - accepted so the guard could land
    /// ahead of the port. Everything else that keeps such an accommodation honest is keyed to somebody
    /// REMEMBERING to remove it, guarding the exact hole this mission exists to close. A memory is not a
    /// mechanism. The presence of a statistics DbContext is a fact a machine can check, and it is the fact
    /// that means the port has happened - so the entry dies on the day it stops being true, named, whether or
    /// not anyone is reading this file.
    ///
    /// Revert-proof: land the statistics context and this goes red naming the stale exemption. Watched
    /// tripping - see docs/step2-nosqlite-guard-proof.md.
    /// </summary>
    [Fact]
    public void DT_SQL_5_no_exemption_outlives_the_condition_that_justifies_it()
    {
        var facts = GatherScanFacts();
        var stale = new List<string>();

        foreach (var (name, exemption) in SqliteTouchingTypes)
        {
            var holds = exemption.Evaluate(facts);

            if (exemption.Kind == ConditionKind.ExpiresWhenTrue && holds)
                stale.Add($"{name}: EXPIRED. Its accommodation was temporary and the condition that ends it is " +
                          $"now TRUE - \"{exemption.Condition}\" Delete the entry; if the type still touches " +
                          "SQLite after the port, that is the finding, not a reason to keep the exemption.");

            if (exemption.Kind == ConditionKind.RequiresToStayTrue && !holds)
                stale.Add($"{name}: JUSTIFICATION BROKEN. It is exempt because \"{exemption.Condition}\" and " +
                          "that is no longer true, so the reason it was allowed SQLite has gone. Restore the " +
                          "property or remove the exemption and move the data to PostgreSQL.");
        }

        Assert.True(stale.Count == 0,
            "DT-SQL-5: an exemption has outlived its justification. An exemption that cannot expire is a " +
            "permanent hole wearing a temporary label, so every entry is bound to a fact a machine checks " +
            "rather than to a reason someone wrote down:" + Environment.NewLine + "  " +
            string.Join(Environment.NewLine + "  ", stale));
    }

    // ----------------------------------------------------------------------------------------------------
    // DT-SQL-3 / DT-SQL-4: the runtime refusal.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// DT-SQL-3: <see cref="HostedSqliteGuard.IsHosted"/> agrees with the provider <see cref="GatewayDatabase"/>
    /// ACTUALLY selects, in all three states of the variable. Asserting the two read the same variable NAME
    /// would prove nothing - they share one compile-time constant, so that comparison cannot fail and a check
    /// that cannot fail is not a check. This drives the real selection instead and observes which provider came
    /// out: SQLite opens a file, PostgreSQL is selected without being reached (the database is built with its
    /// open deferred, and its path is the redacted PostgreSQL target, not the file it was handed), blank is
    /// rejected as a misconfiguration. If the guard's predicate is ever loosened away from the Gateway's, this
    /// goes red.
    ///
    /// The PostgreSQL case used to OPEN against a refusing port and wait out the open's whole 170-second retry
    /// window, which made this one test three minutes of every Gateway.Tests run and put a real network
    /// connection in the gate. The selection is decided in the constructor; the open adds nothing to what this
    /// test proves.
    /// </summary>
    [Fact]
    public void DT_SQL_3_the_guards_notion_of_hosted_matches_the_provider_the_gateway_selects()
    {
        var original = Environment.GetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar);
        var dir = Path.Combine(Path.GetTempPath(), "dt-sql-3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // UNSET: self-host. The guard says not hosted, and the Gateway really does open a SQLite file.
            Environment.SetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar, null);
            Assert.False(HostedSqliteGuard.IsHosted);
            var dbPath = Path.Combine(dir, "gateway.db");
            using (var db = new GatewayDatabase(new CcDirector.Core.Tenancy.SingleTenantContext(), dbPath))
                Assert.Equal(dbPath, db.Path);
            Assert.True(File.Exists(dbPath), "the unset case must really have opened a SQLite FILE, not merely reported one");

            // SET: hosted. The guard says hosted, and the Gateway selects PostgreSQL - proven by the database
            // describing itself as the PostgreSQL target rather than the SQLite file it was handed. Nothing is
            // reached: the open is deferred, and the selection is made before it.
            Environment.SetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar,
                "Host=127.0.0.1;Port=1;Database=dt_sql_3;Username=u;Password=p;Timeout=1;Command Timeout=1");
            Assert.True(HostedSqliteGuard.IsHosted);
            var pgPath = Path.Combine(dir, "must-not-be-created.db");
            using (var pg = new GatewayDatabase(new CcDirector.Core.Tenancy.SingleTenantContext(), pgPath, deferOpen: true))
                Assert.Equal("postgres host=127.0.0.1 database=dt_sql_3", pg.Path);
            Assert.False(File.Exists(pgPath),
                "the hosted case must not have created a SQLite file even though a path was supplied");

            // SET BUT BLANK: hosted, failing CLOSED. A misconfiguration must never be what hands a hosted
            // Gateway a SQLite file, so the guard reads blank as hosted and the Gateway refuses outright.
            Environment.SetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar, "   ");
            Assert.True(HostedSqliteGuard.IsHosted);
            var blankFailure = Assert.Throws<InvalidOperationException>(
                () => new GatewayDatabase(new CcDirector.Core.Tenancy.SingleTenantContext(), pgPath));
            Assert.Contains("blank", blankFailure.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(pgPath),
                "the blank case must not have fallen back to a SQLite file");
        }
        finally
        {
            Environment.SetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar, original);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// DT-SQL-4: the runtime refusal does what it says - throws on hosted with the offending path NAMED in the
    /// message (so the next incident reads the answer instead of hunting for it), and does nothing at all on
    /// self-host, which keeps SQLite and must be untouched.
    /// </summary>
    [Fact]
    public void DT_SQL_4_the_runtime_guard_refuses_on_hosted_and_names_the_path()
    {
        var original = Environment.GetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar,
                "Host=example.invalid;Database=d;Username=u;Password=p");

            var ex = Assert.Throws<InvalidOperationException>(
                () => HostedSqliteGuard.EnsureNotHosted("/home/data/gateway-stats.db (GatewayStatsDatabase)"));
            Assert.Contains("/home/data/gateway-stats.db", ex.Message, StringComparison.Ordinal);
            Assert.Contains("GatewayStatsDatabase", ex.Message, StringComparison.Ordinal);
            Assert.Contains(HostedSqliteGuard.HostedMarkerEnvVar, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=p", ex.Message, StringComparison.Ordinal);

            // A refusal that cannot say what it refused is not worth throwing.
            Assert.Throws<ArgumentException>(() => HostedSqliteGuard.EnsureNotHosted("  "));

            // Self-host: the guard is inert. This is the half that protects the desktop install.
            Environment.SetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar, null);
            Assert.False(HostedSqliteGuard.IsHosted);
            HostedSqliteGuard.EnsureNotHosted("C:/Users/someone/gateway-stats.db (self-host)");
        }
        finally
        {
            Environment.SetEnvironmentVariable(HostedSqliteGuard.HostedMarkerEnvVar, original);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Helpers.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Open every assembly that makes up the hosted Gateway service. A file that is not there is a LOUD
    /// failure, never a quietly smaller scan: the whole value of an allowlist is that it covers everything it
    /// claims to, and an assembly silently skipped would make DT-SQL-1 pass by not having looked.
    /// </summary>
    /// <summary>
    /// The facts DT-SQL-5's conditions are evaluated against, read off the same compiled assemblies the rest of
    /// the guard scans - so a condition is checked against the real build output, not against a description of
    /// it. Per top-level type: the string literals its code carries, the methods it calls, and the interfaces
    /// it implements. Plus the one whole-assembly fact the transitional entries turn on.
    /// </summary>
    private sealed class ScanFacts
    {
        private readonly Dictionary<string, HashSet<string>> _literals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _calls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _interfaces = new(StringComparer.Ordinal);

        /// <summary>
        /// True when the hosted Gateway assemblies contain a STATISTICS Entity Framework context - the
        /// machine-checkable signal that the SQLite statistics store has been ported. Deliberately not pinned
        /// to one exact spelling: any direct DbContext subclass whose name mentions statistics counts, so the
        /// signal does not miss because the port chose "StatisticsDbContext" over "GatewayStatsDbContext".
        /// </summary>
        public bool StatisticsDbContextExists { get; private set; }

        public IReadOnlyCollection<string> Literals(string type) => Get(_literals, type);
        public IReadOnlyCollection<string> Calls(string type) => Get(_calls, type);
        public IReadOnlyCollection<string> Interfaces(string type) => Get(_interfaces, type);

        private static IReadOnlyCollection<string> Get(Dictionary<string, HashSet<string>> map, string type)
            => map.TryGetValue(type, out var set) ? set : Array.Empty<string>();

        public void Observe(TypeDefinition type)
        {
            var owner = TopLevel(type).Name;

            if (type.BaseType?.FullName == "Microsoft.EntityFrameworkCore.DbContext"
                && type.Name.Contains("Stat", StringComparison.OrdinalIgnoreCase))
                StatisticsDbContextExists = true;

            foreach (var i in type.Interfaces)
                Add(_interfaces, owner, i.InterfaceType.FullName);

            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.OpCode.Code == Mono.Cecil.Cil.Code.Ldstr && instr.Operand is string literal)
                        Add(_literals, owner, literal);
                    else if (instr.Operand is MethodReference called)
                        Add(_calls, owner, called.FullName);
                }
            }
        }

        private static void Add(Dictionary<string, HashSet<string>> map, string owner, string value)
        {
            if (!map.TryGetValue(owner, out var set))
                map[owner] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(value);
        }
    }

    /// <summary>Read <see cref="ScanFacts"/> off every hosted Gateway assembly.</summary>
    private static ScanFacts GatherScanFacts()
    {
        var facts = new ScanFacts();
        foreach (var (_, module) in LoadHostedGatewayModules())
        {
            using (module)
            {
                foreach (var type in AllTypes(module))
                    facts.Observe(type);
            }
        }
        return facts;
    }

    private static IEnumerable<(string File, ModuleDefinition Module)> LoadHostedGatewayModules()
    {
        var baseDir = Path.GetDirectoryName(typeof(GatewayDatabase).Assembly.Location)!;
        foreach (var file in HostedGatewayAssemblyFiles)
        {
            var path = Path.Combine(baseDir, file);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"DT-SQL: '{file}' is not in the test output directory ('{baseDir}'), so the no-SQLite scan " +
                    "would silently cover less than it claims. Restore the project reference that brings it here.",
                    path);

            yield return (file, ModuleDefinition.ReadModule(path));
        }
    }

    /// <summary>
    /// The first SQLite thing this type touches, or null. Everything a type can reach SQLite THROUGH is
    /// checked - the members its code calls, the fields and locals it holds, and its own signatures - because
    /// a rule that only looked at <c>newobj</c> would miss a store handed its connection by someone else.
    /// </summary>
    private static string? FirstSqliteTouch(TypeDefinition type)
    {
        foreach (var field in type.Fields)
            if (IsSqlite(field.FieldType)) return Describe(field.FieldType);

        foreach (var method in type.Methods)
        {
            if (IsSqlite(method.ReturnType)) return Describe(method.ReturnType);
            foreach (var p in method.Parameters)
                if (IsSqlite(p.ParameterType)) return Describe(p.ParameterType);

            if (!method.HasBody) continue;

            foreach (var local in method.Body.Variables)
                if (IsSqlite(local.VariableType)) return Describe(local.VariableType);

            foreach (var instr in method.Body.Instructions)
            {
                switch (instr.Operand)
                {
                    case MethodReference m when IsSqlite(m.DeclaringType) || IsSqlite(m.ReturnType):
                        return $"{Describe(IsSqlite(m.DeclaringType) ? m.DeclaringType : m.ReturnType)}.{m.Name}";
                    case FieldReference f when IsSqlite(f.DeclaringType) || IsSqlite(f.FieldType):
                        return Describe(IsSqlite(f.DeclaringType) ? f.DeclaringType : f.FieldType);
                    case TypeReference t when IsSqlite(t):
                        return Describe(t);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True when this type is a SQLite type: the Microsoft.Data.Sqlite client, the Entity Framework SQLite
    /// provider (including the <c>UseSqlite</c> extension class), or the raw SQLitePCL layer underneath both -
    /// so going one level lower does not get past the rule. Generic arguments, arrays and by-ref wrappers are
    /// unwrapped, so a <c>List&lt;SqliteConnection&gt;</c> counts exactly as the connection does.
    /// </summary>
    private static bool IsSqlite(TypeReference? type)
    {
        if (type is null) return false;

        if (type is GenericInstanceType generic)
        {
            if (IsSqlite(generic.ElementType)) return true;
            foreach (var arg in generic.GenericArguments)
                if (IsSqlite(arg)) return true;
            return false;
        }

        if (type is TypeSpecification spec) return IsSqlite(spec.ElementType);

        var name = type.FullName;
        if (name.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal)) return true;
        if (name.StartsWith("SQLitePCL", StringComparison.Ordinal)) return true;
        if (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            && name.Contains("Sqlite", StringComparison.Ordinal)) return true;

        return false;
    }

    private static string Describe(TypeReference type) => type.FullName;

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var t in module.Types)
            foreach (var nested in Flatten(t))
                yield return nested;
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
            foreach (var inner in Flatten(nested))
                yield return inner;
    }

    private static TypeDefinition TopLevel(TypeDefinition type)
    {
        while (type.DeclaringType is not null) type = type.DeclaringType;
        return type;
    }
}
