using System.Collections.Concurrent;
using CcDirector.Gateway.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// Disposing one <see cref="GatewayDatabase"/> must not break another one that is in use in the same process.
///
/// The SQLite connection pool is process-wide. Dispose used to call <c>SqliteConnection.ClearAllPools()</c>,
/// which disposed the native handle of a connection ANOTHER database was opening at that moment, so a query
/// on a database nobody had closed failed with "Cannot access a disposed object. Object name:
/// 'SQLitePCL.sqlite3'" inside <c>SqliteConnection.Open()</c>. That is how the v2.9.2 release gate lost
/// <c>HostedEntitlementGateTests</c>: another test class, running in parallel, disposed its own database.
///
/// This test forces the same collision on purpose: four threads keep querying database A while the test
/// opens and disposes database B over and over. Revert-prove: put <c>ClearAllPools()</c> back in
/// <c>GatewayDatabase.Dispose</c> and this goes red with that exception.
/// </summary>
public sealed class DisposingOneDatabaseLeavesAnotherWorkingTests : IDisposable
{
    private readonly GatewayDbTestHarness _inUse = new();
    private readonly GatewayDbTestHarness _disposedOverAndOver = new();

    public void Dispose()
    {
        _inUse.Dispose();
        _disposedOverAndOver.Dispose();
    }

    [Fact]
    public void Disposing_one_database_does_not_dispose_the_connections_of_another_in_use()
    {
        var inUse = _inUse.Open();
        var failures = new ConcurrentQueue<Exception>();
        var queries = 0;
        using var stop = new CancellationTokenSource();

        // Several readers, so a connection is being rented from A's pool at almost every instant a disposal of
        // B clears the pools - the window the defect needs is narrow, and one reader missed it too often.
        var readers = new List<Thread>();
        for (var r = 0; r < 4; r++)
        {
            var reader = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        using var ctx = inUse.CreateUnscopedContext();
                        _ = ctx.Tenants.Count();
                        Interlocked.Increment(ref queries);
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                }
            }) { IsBackground = true, Name = $"database-A-reader-{r}" };
            readers.Add(reader);
            reader.Start();
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        var disposals = 0;
        while (DateTime.UtcNow < deadline && failures.IsEmpty)
        {
            var other = _disposedOverAndOver.Open();
            using (var ctx = other.CreateUnscopedContext())
                _ = ctx.Tenants.Count();
            other.Dispose();
            disposals++;
        }

        stop.Cancel();
        foreach (var reader in readers)
            Assert.True(reader.Join(TimeSpan.FromSeconds(30)), $"{reader.Name} did not stop");

        Assert.True(failures.IsEmpty,
            $"database A failed {failures.Count} time(s) while database B was disposed {disposals} time(s); first: {failures.FirstOrDefault()}");
        // The collision only means something if both sides actually ran.
        Assert.True(disposals > 5, $"only {disposals} disposal(s) of database B ran");
        Assert.True(queries > 5, $"only {queries} query(ies) on database A ran");
    }
}
