using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The database is opened SEPARATELY from construction, so the hosted Gateway can bind its listener first.
///
/// WHY THIS ORDER IS A PROPERTY AND NOT A PREFERENCE. Connecting and migrating used to run inside the
/// constructor, which runs long before the listener binds. On a deploy the new container therefore did its
/// database work while the platform was still waiting for it to listen; a slow open pushed the bind past the
/// container-start deadline, the platform concluded no port would ever appear, and it stopped the SITE -
/// which tore down the healthy container that was serving traffic beside it. That stop, not the swap, is the
/// 38.5 seconds of 2 August 2026 (#2383) and the 46.7 seconds of 12 August (#2585).
///
/// Shortening the wait only ever made it less likely. Splitting the open out is what makes it impossible.
///
/// In the process-environment collection because one test blanks the provider selection for a moment, and the
/// statistics database does not take the environment gate the Gateway database takes: TeamDirectorTunnelTests failed
/// in its constructor with "The hosted Gateway tried to open a SQLite database" while that test ran beside it.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DatabaseOpensAfterTheBindTests : IDisposable
{
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var d in _temp)
        {
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    private string TempDbPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dt-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _temp.Add(dir);
        return Path.Combine(dir, "gateway.db");
    }

    [Fact]
    public void Deferred_construction_does_not_open_the_database()
    {
        var path = TempDbPath();

        using var db = new GatewayDatabase(new SingleTenantContext(), path, deferOpen: true);

        Assert.False(db.IsOpen);
        // The file is the observable: a database that was opened and migrated has one, and this is the whole
        // claim - construction did no database work at all.
        Assert.False(File.Exists(path), "constructing with deferOpen must not create or migrate the database");
    }

    [Fact]
    public void Open_connects_and_migrates()
    {
        var path = TempDbPath();
        using var db = new GatewayDatabase(new SingleTenantContext(), path, deferOpen: true);

        db.Open();

        Assert.True(db.IsOpen);
        Assert.True(File.Exists(path));
        using var ctx = db.CreateContext();
        Assert.NotNull(ctx);
    }

    [Fact]
    public void A_query_before_Open_says_so_rather_than_dereferencing_null()
    {
        // A request that lands in the window between the bind and the open must get an explanation, not a
        // NullReferenceException raised somewhere far from the cause.
        using var db = new GatewayDatabase(new SingleTenantContext(), TempDbPath(), deferOpen: true);

        var ex = Assert.Throws<InvalidOperationException>(() => db.CreateContext());

        Assert.Contains("not open yet", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_is_idempotent()
    {
        var path = TempDbPath();
        using var db = new GatewayDatabase(new SingleTenantContext(), path, deferOpen: true);

        db.Open();
        db.Open();      // must not rebuild the provider or re-run migrations

        Assert.True(db.IsOpen);
    }

    [Fact]
    public void THE_DEFAULT_IS_UNCHANGED_construction_still_opens_eagerly()
    {
        // Everything that is not the hosted Gateway - the desktop pairing store, and every test that asserts
        // this constructor throws on a database it cannot open - keeps the historical behaviour exactly.
        var path = TempDbPath();

        using var db = new GatewayDatabase(new SingleTenantContext(), path);

        Assert.True(db.IsOpen);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Disposing_a_never_opened_database_does_not_throw()
    {
        // The deferred path can be disposed without Open() ever running - a failed start does exactly that.
        var db = new GatewayDatabase(new SingleTenantContext(), TempDbPath(), deferOpen: true);

        db.Dispose();   // must not dereference the provider it never built
    }

    [Fact]
    public void A_configuration_fault_still_fails_in_the_CONSTRUCTOR_even_when_deferred()
    {
        // The split defers only the part that can succeed later - reaching the server. A connection string
        // that is set but blank is misconfiguration: waiting cannot fix it, and a Gateway must not bind a
        // port and serve errors forever because of one. So it must still fail at construction.
        // ALONE. The variable is process-global and the suite runs in parallel, so a database opened by any
        // other test inside this window fails with a message about THIS test's fault - see
        // GatewayDbEnvironmentGate for the shape and why it is worth a lock rather than a comment.
        CcDirector.Gateway.Tests.Data.GatewayDbEnvironmentGate.WhileNobodyIsOpeningADatabase(() =>
        {
            var prior = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, "   ");

                Assert.Throws<InvalidOperationException>(
                    () => new GatewayDatabase(new SingleTenantContext(), TempDbPath(), deferOpen: true));
            }
            finally
            {
                Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, prior);
            }
        });
    }
}
