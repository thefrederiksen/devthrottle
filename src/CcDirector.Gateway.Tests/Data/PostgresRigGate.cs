using System.Runtime.CompilerServices;
using Npgsql;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// WHETHER THIS RUN WAS PROMISED A POSTGRESQL, WHICH ONE, AND WHAT TO DO WHEN THE PROMISE IS BROKEN
/// (issue #2834).
///
/// THE FALSE GREEN THIS CLOSES. Every Postgres-backed proof in this repository decides whether to run by
/// looking at a connection-string variable, and skips when it is blank. A skip is the right answer for a
/// developer on a laptop with no database - but it is indistinguishable from a pass in the console
/// summary, in the TRX counters and in every report built from them. So a release gate that lost its
/// database reported a clean run over tests that never executed, and nobody could tell from the output.
///
/// The gate script now BUILDS the database and says WHICH ONE it built, by putting the rig's instance
/// name in <see cref="RequiredEnvVar"/>. That turns an unanswerable question - "is a database available"
/// - into a checkable one: "is the database this run built the database these tests are pointed at".
///
/// IT CHECKS IDENTITY, NOT A HEARTBEAT, and that distinction was a review finding. The first version
/// opened each connection and ran SELECT 1. That passes against ANY reachable PostgreSQL: reproduced in
/// review with both variables aimed at the same proof database under the superuser, which is a
/// configuration in which the statistics proofs are meaningless - they exist to prove what a RESTRICTED
/// role can and cannot do - and the guard still reported one of one passed. So each connection is now
/// held to the database name AND, for the statistics connection, the login role that the rig built.
///
/// IT IS ENFORCED WHERE A FILTER CANNOT REACH. Also a review finding: the guard was one [Fact], and
/// `-Filter` is a documented option on the gate, so a filtered run could exclude the very test that
/// proves the run is sound and still exit zero. The check therefore runs from a [ModuleInitializer] -
/// on assembly load, before any test, whatever filter was passed - and throws. The test below remains
/// as the readable report in an ordinary run, and as the thing that fails if the initializer ever stops
/// running.
///
/// Linked into CcDirector.Gateway.UnitTests rather than copied, because both assemblies hold Postgres
/// proofs and two copies of a rule this consequential drift.
/// </summary>
internal static class PostgresRigGate
{
    /// <summary>
    /// Set by scripts\test-local.ps1 to the INSTANCE NAME of the throwaway rig it built for this run -
    /// not a bare "1". The name is what makes the promise checkable: every database and role the rig
    /// creates is derived from it, so the tests can be held to the exact one.
    /// </summary>
    internal const string RequiredEnvVar = "CC_TEST_REQUIRE_POSTGRES";

    internal const string ProofConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";
    internal const string StatsConnectionEnvVar = "CC_GATEWAY_TEST_PG_STATS_CONNECTION";

    /// <summary>The rig instance this run was promised, or null when it was promised nothing.</summary>
    internal static string? PromisedInstance
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(RequiredEnvVar);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    /// <summary>Whether this run declared that it provided a database.</summary>
    internal static bool IsRequired => PromisedInstance is not null;

    /// <summary>
    /// Everything wrong with the databases this run promised, in the order a reader would want them.
    /// Empty when the promise holds, and empty when nothing was promised - there is nobody to hold to
    /// anything in that case, which is the laptop run and is deliberately left alone.
    ///
    /// The expected names mirror how scripts\pg-stats-proof-rig.ps1 derives them from the instance. If
    /// that derivation ever changes, this fails loudly on the next run rather than silently accepting a
    /// database it did not build - which is the whole point.
    /// </summary>
    internal static IReadOnlyList<string> Faults()
    {
        var instance = PromisedInstance;
        if (instance is null) return Array.Empty<string>();

        var identifier = instance.Replace("-", "_");
        var faults = new List<string>();

        Check(faults, ProofConnectionEnvVar, $"ccpgproof_{identifier}", expectedRole: null);
        Check(faults, StatsConnectionEnvVar, $"ccpgstats_{identifier}", $"gateway_app_{identifier}");
        return faults;
    }

    /// <summary>
    /// Hold one connection to the database - and where it matters the login role - that the rig built.
    ///
    /// The role is checked ONLY for the statistics connection, and it is not pedantry: those proofs
    /// exist to show what a role holding exactly the hosted Gateway's measured grants can and cannot do.
    /// Run them as the superuser and every one of them passes while proving nothing at all.
    /// </summary>
    private static void Check(List<string> faults, string variable, string expectedDatabase, string? expectedRole)
    {
        var connectionString = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            faults.Add($"{variable} is not set at all.");
            return;
        }

        string where;
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            where = $"{builder.Host}:{builder.Port}";
        }
        catch (Exception ex)
        {
            faults.Add($"{variable} is not a connection string this can parse: {ex.Message}");
            return;
        }

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();

            using var command = new NpgsqlCommand("SELECT current_database(), current_user", connection);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                faults.Add($"{variable} names {where}, which answered nothing when asked which database it is.");
                return;
            }

            var actualDatabase = reader.GetString(0);
            var actualRole = reader.GetString(1);

            if (!string.Equals(actualDatabase, expectedDatabase, StringComparison.Ordinal))
            {
                faults.Add(
                    $"{variable} names {where}, and something answered - but it is the database "
                    + $"'{actualDatabase}', not '{expectedDatabase}' which this run built. These tests "
                    + "would be running against a database nobody here created.");
            }

            if (expectedRole is not null && !string.Equals(actualRole, expectedRole, StringComparison.Ordinal))
            {
                faults.Add(
                    $"{variable} is connected as '{actualRole}', not the restricted role '{expectedRole}'. "
                    + "The statistics proofs exist to show what a role with the hosted Gateway's measured "
                    + "grants can and cannot do; as any other role they pass while proving nothing.");
            }
        }
        catch (Exception ex)
        {
            faults.Add($"{variable} names {where}, and nothing there answered: {ex.Message}");
        }
    }

    /// <summary>The sentence a person has to read. Built once so the test and the fail-fast agree.</summary>
    internal static string Explain(IReadOnlyList<string> faults) =>
        $"This run set {RequiredEnvVar}={PromisedInstance}, which says it built a throwaway PostgreSQL "
        + "for the Postgres-backed proofs - but the databases it named are not the ones it built:"
        + Environment.NewLine + Environment.NewLine
        + string.Join(Environment.NewLine, faults)
        + Environment.NewLine + Environment.NewLine
        + "The database is created and destroyed by scripts\\test-local.ps1 for the run that needs it. "
        + "Read that run's own output, not the other Postgres failures - they are all this same fault "
        + "repeated.";

    /// <summary>
    /// THE ENFORCEMENT A FILTER CANNOT EXCLUDE. Runs on assembly load, before any test is selected, so
    /// `-Filter` cannot leave it out the way it can leave out a [Fact]. It throws rather than reporting,
    /// because there is no test result to report into at this point - and a test host that refuses to
    /// start writes no TRX, which the gate already treats as a failed suite.
    ///
    /// It costs nothing on a run that promised nothing: <see cref="Faults"/> returns immediately and no
    /// connection is opened.
    /// </summary>
    [ModuleInitializer]
    internal static void RefuseToRunWithoutThePromisedDatabase()
    {
        var faults = Faults();
        if (faults.Count == 0) return;
        throw new InvalidOperationException(Explain(faults));
    }
}
