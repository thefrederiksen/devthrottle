using CcDirector.Core.History;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Core.Tests.Copilot;

/// <summary>
/// Validates that SqliteSnapshotReader reads a database WITHOUT locking or contending with a
/// live writer: it copies the file (and its -wal / -shm sidecars) and opens the copy. The key
/// test keeps a writer connection open in write-ahead-log mode - with rows resident in the
/// -wal - while the snapshot reader reads, and proves those rows come back.
/// </summary>
public class SqliteSnapshotReaderTests
{
    [Fact]
    public void Read_SeesRowsWhileAnotherHandleHoldsTheDatabaseOpenInWalMode()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "snap-src-" + Guid.NewGuid().ToString("N") + ".db");

        // A live writer: WAL mode, rows inserted, connection LEFT OPEN for the whole read so the
        // database (and its -wal) are actively held. This mirrors a running Copilot process.
        var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        writer.Open();
        try
        {
            Execute(writer, "PRAGMA journal_mode=WAL;");
            Execute(writer, "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT);");
            Execute(writer, "INSERT INTO items (id, name) VALUES (1, 'alpha'), (2, 'beta');");

            // -wal should exist now (rows live in the log, not yet checkpointed into the db file).
            Assert.True(File.Exists(dbPath + "-wal"), "expected a -wal sidecar for the live writer");

            var names = SqliteSnapshotReader.Read(dbPath, conn =>
            {
                var result = new List<string>();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM items ORDER BY id";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    result.Add(reader.GetString(0));
                return result;
            });

            // The writer is still open and unblocked; the snapshot saw the WAL-resident rows.
            Assert.Equal(new[] { "alpha", "beta" }, names);

            // Prove the writer was never blocked: it can still write after the snapshot read.
            Execute(writer, "INSERT INTO items (id, name) VALUES (3, 'gamma');");
        }
        finally
        {
            writer.Close();
            writer.Dispose();
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void Read_MissingDatabase_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), "snap-missing-" + Guid.NewGuid().ToString("N") + ".db");
        Assert.Throws<FileNotFoundException>(() => SqliteSnapshotReader.Read(missing, _ => 0));
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
