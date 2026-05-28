using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Storage;

/// <summary>
/// Reads back the per-turn review records written by <see cref="TurnReviewLog"/>, newest
/// first, for the desktop "Turn Reviews" page. Pure file read; safe to call off the UI thread.
/// </summary>
public static class TurnReviewReader
{
    /// <summary>
    /// Load up to <paramref name="max"/> of the most recent turn records across all days and
    /// sessions, ordered newest first. Unreadable files are skipped (logged), never thrown.
    /// </summary>
    public static IReadOnlyList<TurnReviewRecord> LoadRecent(int max = 1000)
    {
        var root = CcStorage.TurnReviewLogs();
        if (!Directory.Exists(root)) return Array.Empty<TurnReviewRecord>();

        // Cap by file mtime before deserializing so a huge backlog can't blow up the load,
        // then sort the loaded set by the record's own timestamp (authoritative).
        var files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(max);

        var records = new List<TurnReviewRecord>();
        foreach (var file in files)
        {
            try
            {
                var record = JsonSerializer.Deserialize<TurnReviewRecord>(File.ReadAllText(file));
                if (record is not null) records.Add(record);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TurnReviewReader] skip {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        records.Sort((a, b) => b.TsUtc.CompareTo(a.TsUtc));
        return records;
    }
}
