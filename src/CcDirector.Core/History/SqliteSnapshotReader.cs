using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;

namespace CcDirector.Core.History;

/// <summary>
/// Reads a live SQLite database without contending with its writer. The database is copied
/// (together with its write-ahead-log sidecars, the <c>-wal</c> and <c>-shm</c> files) to a
/// private temporary location, and that copy is what we open. The live store is never opened
/// by us for writing and is never locked by us, so a running agent (GitHub Copilot today,
/// OpenCode next) can keep writing while we read a consistent recent snapshot.
///
/// This helper is deliberately agent-agnostic: it knows nothing about any particular store's
/// tables. A caller passes a read callback that runs whatever queries it needs against the
/// opened snapshot connection.
/// </summary>
public static class SqliteSnapshotReader
{
    /// <summary>
    /// Copy <paramref name="databasePath"/> and any of its <c>-wal</c>/<c>-shm</c> sidecars to
    /// a private temporary file, open the copy, hand the open connection to
    /// <paramref name="read"/>, and return its result. The temporary copy is always deleted
    /// before returning, whether the read succeeds or throws.
    /// </summary>
    /// <param name="databasePath">Path to the live SQLite database file.</param>
    /// <param name="read">Runs queries against the opened snapshot and produces the result.</param>
    /// <exception cref="FileNotFoundException">The database file does not exist.</exception>
    public static T Read<T>(string databasePath, Func<SqliteConnection, T> read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(read);

        if (!File.Exists(databasePath))
            throw new FileNotFoundException("SQLite database not found.", databasePath);

        var tempDirectory = Path.Combine(Path.GetTempPath(), "ccdir-sqlite-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var snapshotPath = Path.Combine(tempDirectory, "snapshot.db");

        try
        {
            // Copy the main file first, then the sidecars. The writer keeps the file open with
            // shared read access, so a plain file copy succeeds without locking it.
            File.Copy(databasePath, snapshotPath, overwrite: true);
            CopyIfPresent(databasePath + "-wal", snapshotPath + "-wal");
            CopyIfPresent(databasePath + "-shm", snapshotPath + "-shm");

            // Open the PRIVATE copy. We allow read-write so SQLite can replay the write-ahead
            // log into the snapshot on open (a read-only handle cannot recover a -wal). The live
            // store is untouched - this is our throwaway copy. Pooling is off so the file handle
            // is released on Dispose and the temporary directory can be deleted immediately.
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = snapshotPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            return read(connection);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SqliteSnapshotReader] Temp cleanup failed for {tempDirectory}: {ex.Message}");
            }
        }
    }

    private static void CopyIfPresent(string source, string destination)
    {
        if (File.Exists(source))
            File.Copy(source, destination, overwrite: true);
    }
}
