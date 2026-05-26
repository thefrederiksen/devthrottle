using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Durable, append-only record of every activity-state transition (blue&lt;-&gt;red) a
/// session goes through. One JSONL file per session at
/// <c>%LOCALAPPDATA%/cc-director/state-changes/&lt;sessionId&gt;.jsonl</c>.
///
/// This is the persistent twin of the in-memory ring
/// (<see cref="Sessions.Session.RecentStateChanges"/>) the Wingman tab renders live: the
/// ring is fast but capped and lost on restart, this log survives so the full history of
/// when a session needed the user is recoverable.
///
/// Write-only and infrequent (one record per transition), so a single process-wide lock is
/// plenty. Append failures are logged and swallowed -- the log must never affect the
/// session it observes.
/// </summary>
public static class StateChangeLog
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cc-director", "state-changes");

    /// <summary>Disabled by setting <c>CC_DIRECTOR_STATE_LOG=0</c> at startup; the in-memory
    /// ring and the Wingman tab still work without the durable log.</summary>
    public static bool Enabled { get; set; } =
        Environment.GetEnvironmentVariable("CC_DIRECTOR_STATE_LOG") != "0";

    /// <summary>One persisted transition: ISO-8601 timestamp, the state moved from -&gt; to,
    /// and the colour it mapped to ("blue" / "red" / ...).</summary>
    public sealed record Record(
        string T,
        string From,
        string To,
        string Color);

    public static void Append(Guid sessionId, Record record)
    {
        if (!Enabled || record is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Root);
                var path = Path.Combine(Root, sessionId.ToString("N") + ".jsonl");
                File.AppendAllText(path, JsonSerializer.Serialize(record, Json) + "\n", Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[StateChangeLog] append failed for {sessionId}: {ex.Message}");
        }
    }
}
