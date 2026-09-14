using Npgsql;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// WHETHER THIS RUN WAS PROMISED A POSTGRESQL, AND WHAT TO DO WHEN THE PROMISE IS BROKEN (issue #2834).
///
/// THE FALSE GREEN THIS CLOSES. Every Postgres-backed proof in this repository decides whether to run by
/// looking at a connection-string variable, and skips when it is blank. A skip is the right answer for a
/// developer on a laptop with no database - but it is indistinguishable from a pass in the console
/// summary, in the TRX counters and in every report built from them. So a release gate that lost its
/// database reported a clean run over tests that never executed, and nobody could tell from the output.
///
/// The gate script now BUILDS the database it needs and then says so, by setting
/// <see cref="RequiredEnvVar"/>. That turns the question from "is a database configured" - which nobody
/// can answer for the run as a whole - into "did this run provide what it said it would", which is
/// checkable and is checked, once, by PostgresRigIsPresentWhenRequiredTests.
///
/// The two states stay clearly separated, and neither is a guess:
///   - the variable is NOT set: nobody promised a database, so the proofs skip and say why. That is the
///     laptop case, and it is honest.
///   - the variable IS set: the run promised a database. If one is not reachable, that is a fault in the
///     run, reported as a red test with a sentence - never as a skip, and never as eight socket errors
///     spread across unrelated test classes.
///
/// Linked into CcDirector.Gateway.UnitTests rather than copied, because both assemblies hold Postgres
/// proofs and two copies of a rule this consequential drift.
/// </summary>
internal static class PostgresRigGate
{
    /// <summary>
    /// Set to "1" by scripts\test-local.ps1 when it has provisioned a throwaway PostgreSQL for the run.
    /// It is the run's own assertion about itself, which is why a test may hold it to it.
    /// </summary>
    internal const string RequiredEnvVar = "CC_TEST_REQUIRE_POSTGRES";

    /// <summary>Whether this run declared that it provided a database.</summary>
    internal static bool IsRequired =>
        string.Equals(Environment.GetEnvironmentVariable(RequiredEnvVar), "1", StringComparison.Ordinal);

    /// <summary>
    /// The Skip reason for a proof gated on <paramref name="connectionEnvVar"/>, or null when it must run.
    ///
    /// It NEVER returns a skip reason while <see cref="IsRequired"/> holds. A run that promised a database
    /// and then skipped its database tests is the exact false green this type exists to remove, so in that
    /// state the proofs run and fail honestly against whatever is - or is not - listening.
    /// </summary>
    internal static string? SkipReason(string connectionEnvVar, string whatIsProved)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(connectionEnvVar))) return null;
        if (IsRequired) return null;

        return $"No PostgreSQL for this run, so {whatIsProved} was not proved. "
            + "Run it through the gate (scripts\\test-local.ps1 -Parked), which builds a throwaway "
            + $"database and sets {connectionEnvVar} itself.";
    }

    /// <summary>
    /// Try to open the connection named by <paramref name="connectionEnvVar"/>. Returns null on success,
    /// or the reason it could not be reached - the message a person has to read, so it names the variable,
    /// the server and the underlying error rather than restating that something went wrong.
    /// </summary>
    internal static string? WhyUnreachable(string connectionEnvVar)
    {
        var connectionString = Environment.GetEnvironmentVariable(connectionEnvVar);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return $"{connectionEnvVar} is not set at all.";
        }

        // The host and port are worth naming even on success paths elsewhere, so they are read before the
        // attempt: a connection string that cannot be parsed is its own distinct fault.
        string where;
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            where = $"{builder.Host}:{builder.Port}/{builder.Database}";
        }
        catch (Exception ex)
        {
            return $"{connectionEnvVar} is not a connection string this can parse: {ex.Message}";
        }

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = new NpgsqlCommand("SELECT 1", connection);
            command.ExecuteScalar();
            return null;
        }
        catch (Exception ex)
        {
            return $"{connectionEnvVar} names {where}, and nothing there answered: {ex.Message}";
        }
    }
}
