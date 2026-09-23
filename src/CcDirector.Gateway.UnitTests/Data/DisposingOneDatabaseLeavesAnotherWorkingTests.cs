using System.Reflection;
using CcDirector.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// Disposing one <see cref="GatewayDatabase"/> must not break another one that is in use in the same process.
///
/// The SQLite connection pool is process-wide. Dispose used to call <c>SqliteConnection.ClearAllPools()</c>,
/// which disposed the native handle of a connection ANOTHER database was opening at that moment, so a query
/// on a database nobody had closed failed with "Cannot access a disposed object. Object name:
/// 'SQLitePCL.sqlite3'". That is how the v2.9.2 release gate lost <c>HostedEntitlementGateTests</c>: another
/// test class, running in parallel, disposed its own database.
///
/// HOW THE CLEAR KILLED A CONNECTION IT DID NOT OWN (Microsoft.Data.Sqlite 9.0.2). When a connection is
/// rented, <c>SqliteConnectionInternal.Activate</c> sets <c>_active = true</c> and only THEN binds the owning
/// <c>SqliteConnection</c> into a weak reference. In between, the pool's <c>Leaked</c> check (active, owner not
/// reachable) is true, and a clear of that pool reclaims the connection as leaked and disposes its handle.
/// ClearAllPools clears every pool, so another database's disposal could land in that gap.
///
/// The gap is a couple of instructions wide, so a test that races threads against it only samples it (the
/// first version of this test went red on 2 of 8 runs against the old code). This test does not race: it
/// rents a connection on database A, puts it into exactly the mid-open state, disposes database B while it
/// is there, and then uses the connection. Revert-prove: put <c>ClearAllPools()</c> back in
/// <c>GatewayDatabase.Dispose</c> and this goes red on every run with that exception.
/// </summary>
public sealed class DisposingOneDatabaseLeavesAnotherWorkingTests : IDisposable
{
    private readonly GatewayDbTestHarness _inUse = new();
    private readonly GatewayDbTestHarness _disposed = new();

    public void Dispose()
    {
        _inUse.Dispose();
        _disposed.Dispose();
    }

    [Fact]
    public void Disposing_one_database_does_not_dispose_a_connection_another_database_is_opening()
    {
        var inUse = _inUse.Open();
        using var ctx = inUse.CreateUnscopedContext();
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        connection.Open();
        try
        {
            var owner = OwnerReferenceOf(connection);
            Assert.True(owner.TryGetTarget(out var bound) && ReferenceEquals(bound, connection),
                "the rented connection is not bound to its owner, so the mid-open state below would not be the one Activate leaves");

            // The instant inside Activate: rented and active, owner not yet bound.
            owner.SetTarget(null!);
            try
            {
                var other = _disposed.Open();
                using (var otherCtx = other.CreateUnscopedContext())
                    _ = otherCtx.Tenants.Count();
                other.Dispose();
            }
            finally
            {
                owner.SetTarget(connection);
            }

            var failure = Record.Exception(() => ctx.Tenants.Count());
            Assert.True(failure is null,
                $"database A's open connection was broken by disposing database B: {failure}");
        }
        finally
        {
            connection.Close();
        }
    }

    /// <summary>
    /// The weak reference from the pooled <c>SqliteConnectionInternal</c> behind <paramref name="connection"/>
    /// to its owner. Both fields are private to Microsoft.Data.Sqlite; if an upgrade renames them this fails
    /// by name rather than letting the test pass without reaching the state it exists to create.
    /// </summary>
    private static WeakReference<SqliteConnection> OwnerReferenceOf(SqliteConnection connection)
    {
        var innerField = typeof(SqliteConnection).GetField("_innerConnection", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(innerField is not null, "SqliteConnection._innerConnection not found - Microsoft.Data.Sqlite changed; re-derive this test's mid-open state");
        var inner = innerField!.GetValue(connection);
        Assert.True(inner is not null, "the open connection has no inner connection");

        // A pool clear can only reach a connection that belongs to a pool; an unpooled one would make this
        // test pass against the old code too.
        var poolField = inner!.GetType().GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(poolField is not null, "SqliteConnectionInternal._pool not found - Microsoft.Data.Sqlite changed; re-derive this test's mid-open state");
        Assert.True(poolField!.GetValue(inner) is not null, "database A's connection is not pooled, so no pool clear could ever reach it");

        var ownerField = inner!.GetType().GetField("_outerConnection", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(ownerField is not null, "SqliteConnectionInternal._outerConnection not found - Microsoft.Data.Sqlite changed; re-derive this test's mid-open state");
        return (WeakReference<SqliteConnection>)ownerField!.GetValue(inner)!;
    }
}
