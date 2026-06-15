using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Voice;

/// <summary>
/// Durable, disk-backed store of completed voice-turn replies (issue: guaranteed audio-turn).
///
/// The in-memory <see cref="GatewayTurnJobStore"/> is the hot path for a turn in flight, but it
/// has a 10-minute TTL and is lost on a Gateway restart. This archive is the durability layer:
/// when a turn finishes, its result (summary text, transcript, and the reply MP3) is written
/// here keyed by <c>turnId</c>, so the reply "sits in the session" and is retrievable hours
/// later and across restarts. It also records the originating <c>uploadId</c> so a retried
/// completion finds the already-finished turn instead of starting a duplicate.
///
/// Layout: <c>{CcStorage.VoiceTurnArchive()}/&lt;turnId&gt;/meta.json</c> plus <c>reply.mp3</c>
/// (present only when TTS produced audio). One directory per turn, purged after
/// <see cref="RetentionHours"/>.
///
/// Best-effort: a persistence failure must never break a live turn, so the writers swallow and
/// log their own exceptions.
/// </summary>
public sealed class VoiceTurnArchive
{
    /// <summary>How long a completed turn's result stays retrievable.</summary>
    public const int RetentionHours = 24;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _root;

    public VoiceTurnArchive() : this(CcStorage.VoiceTurnArchive()) { }

    /// <summary>Test seam: archive under an explicit root instead of the shared storage dir.</summary>
    public VoiceTurnArchive(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Persist a completed turn's reply. Writes meta.json (always) and reply.mp3 (when audio is
    /// present), then purges aged turns. Best-effort: failures are logged, never thrown.
    /// </summary>
    public void Save(VoiceTurnArchiveRecord record, byte[]? replyAudio)
    {
        try
        {
            Purge();

            var dir = DirFor(record.TurnId);
            if (dir is null)
            {
                FileLog.Write($"[VoiceTurnArchive] Save: rejected non-GUID turnId={record.TurnId}");
                return;
            }
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(record, JsonOpts));
            if (replyAudio is { Length: > 0 })
                File.WriteAllBytes(Path.Combine(dir, "reply.mp3"), replyAudio);

            FileLog.Write($"[VoiceTurnArchive] Save: turnId={record.TurnId} sid={record.SessionId} " +
                          $"uploadId={record.UploadId} audioBytes={replyAudio?.Length ?? 0}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnArchive] Save FAILED turnId={record.TurnId}: {ex.Message}");
        }
    }

    /// <summary>The archived record for <paramref name="turnId"/>, or null when absent/aged-out.</summary>
    public VoiceTurnArchiveRecord? Get(string turnId)
    {
        var dir = DirFor(turnId);
        if (dir is null) return null;
        return ReadMeta(dir);
    }

    /// <summary>The reply MP3 bytes for <paramref name="turnId"/>, or null when there is no
    /// archived audio (no key configured at turn time, or turn absent/aged-out).</summary>
    public byte[]? GetAudio(string turnId)
    {
        var dir = DirFor(turnId);
        if (dir is null) return null;
        var path = Path.Combine(dir, "reply.mp3");
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnArchive] GetAudio FAILED turnId={turnId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Completed turns for a session, newest first, optionally only those at/after
    /// <paramref name="sinceUtc"/>. Scans the (bounded, retention-limited) archive dirs.
    /// </summary>
    public IReadOnlyList<VoiceTurnArchiveRecord> ListForSession(string sessionId, DateTime? sinceUtc = null)
    {
        var results = new List<VoiceTurnArchiveRecord>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                var rec = ReadMeta(dir);
                if (rec is null) continue;
                if (!string.Equals(rec.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) continue;
                if (sinceUtc is { } since && rec.CreatedAtUtc < since) continue;
                results.Add(rec);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnArchive] ListForSession FAILED sid={sessionId}: {ex.Message}");
        }
        return results.OrderByDescending(r => r.CreatedAtUtc).ToList();
    }

    /// <summary>
    /// The completed turn that originated from <paramref name="uploadId"/>, or null. Used to make a
    /// retried completion idempotent even after the in-memory job has expired.
    /// </summary>
    public VoiceTurnArchiveRecord? FindByUpload(string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId)) return null;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                var rec = ReadMeta(dir);
                if (rec is not null && string.Equals(rec.UploadId, uploadId, StringComparison.OrdinalIgnoreCase))
                    return rec;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnArchive] FindByUpload FAILED uploadId={uploadId}: {ex.Message}");
        }
        return null;
    }

    // ====== internals ===============================================================

    private VoiceTurnArchiveRecord? ReadMeta(string dir)
    {
        var path = Path.Combine(dir, "meta.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<VoiceTurnArchiveRecord>(File.ReadAllText(path)); }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnArchive] ReadMeta skip {Path.GetFileName(dir)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Delete turn dirs older than the retention window. Best-effort.</summary>
    private void Purge()
    {
        var cutoff = DateTime.UtcNow.AddHours(-RetentionHours);
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[VoiceTurnArchive] Purge skip {Path.GetFileName(dir)}: {ex.Message}");
            }
        }
    }

    /// <summary>Per-turn dir, or null when the turn id is not GUID-shaped (so it can never
    /// escape the archive root).</summary>
    private string? DirFor(string turnId)
        => Guid.TryParse(turnId, out var g) ? Path.Combine(_root, g.ToString("N")) : null;
}

/// <summary>
/// The persisted form of one completed voice turn. <see cref="HasAudio"/> mirrors the presence of
/// reply.mp3 so a list view can show "has a spoken reply" without opening the audio file.
/// </summary>
public sealed class VoiceTurnArchiveRecord
{
    public string TurnId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string UploadId { get; set; } = "";
    public string Stage { get; set; } = "reply";
    public string Transcript { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool HasAudio { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
