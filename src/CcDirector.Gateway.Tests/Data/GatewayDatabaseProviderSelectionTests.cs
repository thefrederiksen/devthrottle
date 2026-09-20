using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The collection that owns every test which mutates the process-global CC_GATEWAY_DB_CONNECTION environment
/// variable. It is marked DisableParallelization so it never runs alongside any other collection - otherwise a
/// concurrent test constructing a GatewayDatabase would read the env var mid-flight and pick the wrong
/// provider. Tests inside still save and restore the previous value in try/finally as a second guard.
/// </summary>
[CollectionDefinition("GatewayDatabase provider env var", DisableParallelization = true)]
public sealed class GatewayDatabaseEnvVarCollection
{
}

/// <summary>
/// Provider selection in <see cref="GatewayDatabase"/>: the env var UNSET selects the local SQLite file, and
/// the env var SET-but-blank fails loud (it must never silently fall through to SQLite - that would be the
/// hidden fallback the no-fallback rule forbids). The non-blank Postgres path is proved separately, against a
/// real server, in <c>PostgresProviderProofTests</c> (CcDirector.Gateway.Postgres.Tests).
/// </summary>
[Collection("GatewayDatabase provider env var")]
public sealed class GatewayDatabaseProviderSelectionTests
{
    private const string EnvVar = "CC_GATEWAY_DB_CONNECTION";

    [Fact]
    public void EnvVarUnset_SelectsSqlite()
    {
        var previous = Environment.GetEnvironmentVariable(EnvVar);
        var dir = Path.Combine(Path.GetTempPath(), "cc-gateway-provider-" + Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(dir, "gateway.db");
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, null);

            using var db = new GatewayDatabase(new SingleTenantContext(), dbPath);

            // SQLite was selected: the reported target is the file path we passed (not a redacted Postgres
            // target), and the file was actually created and migrated.
            Assert.Equal(dbPath, db.Path);
            Assert.True(File.Exists(dbPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, previous);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort - the OS may hold the file briefly after pool clear */ }
        }
    }

    // Note: an EMPTY string is not tested through the env var because Environment.SetEnvironmentVariable with
    // "" DELETES the variable on Windows (it would read back as unset, not blank). Whitespace values are
    // preserved and exercise the same IsNullOrWhiteSpace guard, which also covers empty.
    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    public void EnvVarSetButBlank_FailsLoud_NoSilentSqlite(string blank)
    {
        var previous = Environment.GetEnvironmentVariable(EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, blank);

            var ex = Assert.Throws<InvalidOperationException>(
                () => new GatewayDatabase(new SingleTenantContext()));
            Assert.Contains("is set but blank", ex.Message);
            Assert.Contains("will not fall back", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, previous);
        }
    }
}

/// <summary>
/// The connection-string redactor must never leak credentials into a log line, even when a password contains
/// the very delimiters (';' and '=') a naive split would choke on. This calls
/// <see cref="GatewayDatabase.RedactConnectionTarget"/> directly (internal, exposed via InternalsVisibleTo).
/// It touches no environment state, so it runs freely in parallel.
/// </summary>
public sealed class GatewayDatabaseRedactionTests
{
    [Fact]
    public void Redact_PasswordWithDelimiters_NeverAppearsInOutput()
    {
        // A password full of ';' and '=' - exactly what breaks a hand-rolled split - inside a quoted value.
        const string password = "a;b=c;=;secret";
        var connectionString =
            $"Host=db.example.com;Port=5432;Database=gatewayprod;Username=app;Password=\"{password}\"";

        var redacted = GatewayDatabase.RedactConnectionTarget(connectionString);

        Assert.Equal("postgres host=db.example.com database=gatewayprod", redacted);
        Assert.DoesNotContain("secret", redacted);
        Assert.DoesNotContain("Password", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("app", redacted);        // the username must not appear either
        Assert.DoesNotContain("a;b=c", redacted);      // no password fragment
    }

    [Fact]
    public void Redact_UnparseableConnectionString_ReturnsFixedLiteral_NoEcho()
    {
        // A malformed string the parser rejects. The output must be the fixed literal, never an echo of input.
        const string garbage = "this is not=a valid;;; Password=leakme";

        var redacted = GatewayDatabase.RedactConnectionTarget(garbage);

        Assert.Equal("postgres (target redacted)", redacted);
        Assert.DoesNotContain("leakme", redacted);
    }
}
