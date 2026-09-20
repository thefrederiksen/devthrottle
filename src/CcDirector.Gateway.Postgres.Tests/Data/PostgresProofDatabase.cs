using Npgsql;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The per-RUN Postgres database for the live proof classes (issue #1156).
///
/// THE COLLISION THIS CLOSES. The three Postgres proof classes read one connection string from
/// <c>CC_GATEWAY_TEST_PG_CONNECTION</c> and call <c>EnsureDeleted()</c> on it before migrating from nothing.
/// Two runs handed the SAME connection string therefore DROP EACH OTHER'S DATABASE while the other is still
/// executing against it. That is not a timing hazard or a scheduling artefact - it is one process destroying
/// another's data mid-test, and it is the only demonstrated cross-process corruption left in this assembly.
/// It defeats a file lock too: the lock serializes runs on ONE machine for ONE user, while a shared Postgres
/// server is reachable from anywhere.
///
/// THE FIX: the environment variable names a TEMPLATE, not the database to use. Every run derives its own
/// database name from it by appending a unique suffix, so two runs can point at the same server, share
/// credentials, and never touch the same database. Nothing about the server, host, port, or user changes -
/// only which database the run owns.
///
/// WHY THE SUFFIX IS PER PROCESS, NOT PER CLASS. All three proof classes must agree on the database within a
/// run: they migrate and assert against the same schema. A static computed once per process gives exactly
/// that - one database per test process, shared by the classes in it, distinct from every other run.
///
/// THE THROWAWAY GUARD IS KEPT AND STRENGTHENED. The original refused to drop a database whose name did not
/// begin with <c>ccpg</c>, so a mistyped variable could never nuke a real database. That check still runs,
/// against the SUPPLIED name, before any suffix is added - so the operator's intent is what is validated. The
/// derived name inherits the prefix, so the guard holds for the database actually dropped.
/// </summary>
internal static class PostgresProofDatabase
{
    internal const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";

    /// <summary>The throwaway prefix a supplied database name must carry before anything may drop it.</summary>
    private const string ThrowawayPrefix = "ccpg";

    /// <summary>
    /// Unique per test process. Short, lower-case and alphanumeric so it is a legal Postgres identifier
    /// without quoting, and so a leftover database is still recognisably one of ours.
    /// </summary>
    private static readonly string RunSuffix = Guid.NewGuid().ToString("N")[..12];

    private static readonly Lazy<string?> Derived = new(BuildConnection);

    /// <summary>True when the operator asked for the live Postgres proofs at all.</summary>
    internal static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar));

    /// <summary>
    /// This run's own connection string. Same server and credentials the operator supplied; a database name
    /// no other run will use.
    /// </summary>
    internal static string Connection =>
        Derived.Value ?? throw new InvalidOperationException($"{ConnectionEnvVar} is not set.");

    /// <summary>The database name this run owns, for messages and for the drop guard.</summary>
    internal static string DatabaseName =>
        new NpgsqlConnectionStringBuilder(Connection).Database ?? "";

    private static string? BuildConnection()
    {
        var supplied = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        if (string.IsNullOrWhiteSpace(supplied)) return null;

        var builder = new NpgsqlConnectionStringBuilder(supplied);
        var suppliedDatabase = builder.Database ?? "";

        // Validate what the OPERATOR wrote, before deriving anything from it. A name that was never safe to
        // drop must not become safe merely because a suffix was appended to it.
        if (!suppliedDatabase.StartsWith(ThrowawayPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to use the database '{suppliedDatabase}' for the Postgres proofs: its name must begin " +
                $"with the throwaway prefix '{ThrowawayPrefix}', because these tests DROP the database they run " +
                $"against. Point {ConnectionEnvVar} at a disposable database (e.g. 'ccpgproof').");
        }

        builder.Database = $"{suppliedDatabase}_{RunSuffix}";
        return builder.ConnectionString;
    }

    /// <summary>
    /// The drop guard, kept as an explicit call so the proof classes still state out loud that they are about
    /// to destroy a database. Re-checks the DERIVED name, which is what actually gets dropped.
    /// </summary>
    internal static void GuardThrowawayDatabase()
    {
        var database = DatabaseName;
        if (!database.StartsWith(ThrowawayPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to EnsureDeleted() the database '{database}': its name must begin with the throwaway " +
                $"prefix '{ThrowawayPrefix}'.");
        }
    }
}
