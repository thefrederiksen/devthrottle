using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// Two connections that are open at the same time never share one SQLite handle.
///
/// HOW THEY DID (Microsoft.Data.Sqlite 9.0.2 through 10.0.12, dotnet/efcore#39008). When a pooled connection is
/// rented, <c>SqliteConnectionInternal.Activate</c> set <c>_active = true</c> and only THEN bound its owning
/// <c>SqliteConnection</c> into a weak reference. In between, the pool's <c>Leaked</c> check (active, owner not
/// reachable) is true. A second opener that finds the pool empty with an even count reclaims "leaked"
/// connections, puts this one back on the warm stack, and pops it again for itself: two openers, one handle.
/// The second opener then re-registers the functions EF Core adds on open while the first has a statement
/// running, and the open fails with "SQLite Error 5: 'unable to delete/modify user-function due to active
/// statements'". When it does not fail, one opener's statements run inside the other's transaction.
///
/// That is what crashed the Gateway unit test host in issue #4352: eight callers answering one Fleet outcome
/// at once on two store instances grew the pool concurrently. The store was right and the test was right; the
/// pool was wrong. 10.0.13 publishes the owner before marking the connection active.
///
/// The race itself is a window of a couple of instructions: a test that samples it (32 openers growing an empty
/// pool, 300 rounds) went red on 4 of 5 runs against 9.0.2, and 5 seconds is too much to spend on a sample in
/// the default gate. So this guards the floor, which turns red on every run if the package is pinned back.
/// </summary>
public sealed class SqlitePoolGivesEachOpenerItsOwnHandleTests
{
    [Fact]
    public void MicrosoftDataSqlite_IsAtLeastTheReleaseThatFixedThePoolRace()
    {
        var version = typeof(SqliteConnection).Assembly.GetName().Version!;

        Assert.True(version >= new Version(10, 0, 13),
            $"Microsoft.Data.Sqlite {version} is loaded. Every release before 10.0.13 can hand one pooled SQLite " +
            "handle to two connections opened at the same moment (dotnet/efcore#39008, issue #4352). Do not pin it back.");
    }
}
