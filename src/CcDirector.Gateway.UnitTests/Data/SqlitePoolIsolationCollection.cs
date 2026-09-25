using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The SQLite connection pool is process-wide, and dozens of classes in this assembly call
/// <c>SqliteConnection.ClearAllPools()</c> to release their files. A test that deliberately holds a pooled
/// connection in the state a pool clear takes for "leaked" must therefore run with no other test class beside
/// it: any other class's ClearAllPools landing in that window reclaims the connection, and the test fails on
/// work that is not the code under test (issue #3393).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlitePoolIsolationCollection
{
    public const string Name = "SQLite pool isolation";
}
