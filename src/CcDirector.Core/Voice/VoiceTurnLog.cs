using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Voice;

/// <summary>
/// Short-retention, disk-backed log of each voice turn, so a meaning-level
/// divergence between what the agent actually said and what the wingman spoke can
/// be flagged and compared after the fact.
///
/// One directory per turn under <see cref="CcStorage.VoiceTurnLogs"/>:
///   audio.&lt;ext&gt;   the reassembled utterance bytes (the raw speech)
///   inbound.json    user side: raw transcript, cleaned transcript, cleanup reason
///   outbound.json   wingman side: agent reply (DisplayText) vs spoken reply (Summary)
///
/// The two halves are written by two different requests (/voice/utterance/complete
/// and /chat). They are correlated server-side by sessionId + recency: per session
/// the voice orchestrator is strictly sequential (transcribe, wait, send, follow to
/// completion), so each inbound is immediately followed by exactly one outbound for
/// the same session. The endpoints construct their services per request, so this
/// type holds NO state - it scans the (tiny) set of recent turn dirs on disk.
///
/// Everything is best-effort: a logging failure must never break a voice turn, so
/// all public methods swallow and log their own exceptions.
/// </summary>
public static class VoiceTurnLog
{
    /// <summary>How long turn records are kept before the purge deletes them.</summary>
    public const int RetentionDays = 5;

    /// <summary>How far back AttachOutbound will look for the matching inbound.</summary>
    private const int PairingWindowMinutes = 10;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// Record the user side of a turn: write the audio and the transcripts, and run
    /// the retention purge. Returns the turn directory, or "" on failure.
    /// </summary>
    public static string WriteInbound(
        string sessionId, string sessionName, byte[] audio, string fileName,
        string rawTranscript, string cleanedTranscript, string cleanupReason)
    {
        try
        {
            Purge();

            var turnId = Guid.NewGuid().ToString("N");
            var dir = Path.Combine(CcStorage.VoiceTurnLogs(), DirName(turnId));
            Directory.CreateDirectory(dir);

            if (audio is { Length: > 0 })
                File.WriteAllBytes(Path.Combine(dir, "audio" + AudioExt(fileName)), audio);

            var inbound = new InboundRecord
            {
                TurnId = turnId,
                SessionKey = SessionKey(sessionId),
                SessionId = sessionId ?? "",
                SessionName = sessionName ?? "",
                TsUtc = DateTime.UtcNow.ToString("o"),
                RawTranscript = rawTranscript ?? "",
                CleanedTranscript = cleanedTranscript ?? "",
                CleanupReason = cleanupReason ?? "",
            };
            File.WriteAllText(Path.Combine(dir, "inbound.json"), JsonSerializer.Serialize(inbound, JsonOpts));

            FileLog.Write($"[VoiceTurnLog] WriteInbound: session={SessionKey(sessionId)} turn={turnId} audioBytes={audio?.Length ?? 0}");
            return dir;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnLog] WriteInbound FAILED: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Record the wingman side of a turn: attach the agent's actual reply and the
    /// spoken reply to the newest unpaired inbound for this session. When there is
    /// no pending inbound (e.g. a conductor poll with no preceding utterance) a
    /// standalone outbound-only turn is written so the comparison pair is still kept.
    /// </summary>
    public static void AttachOutbound(
        string sessionId, string agentReply, string wingmanSpoken, string summarizerModel, string status)
    {
        try
        {
            var dir = FindPendingInbound(SessionKey(sessionId)) ?? CreateStandalone(sessionId);
            var outbound = new OutboundRecord
            {
                TsUtc = DateTime.UtcNow.ToString("o"),
                AgentReply = agentReply ?? "",
                WingmanSpoken = wingmanSpoken ?? "",
                SummarizerModel = summarizerModel ?? "",
                Status = status ?? "",
            };
            File.WriteAllText(Path.Combine(dir, "outbound.json"), JsonSerializer.Serialize(outbound, JsonOpts));
            FileLog.Write($"[VoiceTurnLog] AttachOutbound: session={SessionKey(sessionId)} dir={Path.GetFileName(dir)} agentLen={agentReply?.Length ?? 0} spokenLen={wingmanSpoken?.Length ?? 0}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnLog] AttachOutbound FAILED: {ex.Message}");
        }
    }

    // ====== internals ===============================================================

    /// <summary>
    /// Find the newest turn dir for this session that has an inbound record but no
    /// outbound yet, within the pairing window. Returns null when none qualifies.
    /// </summary>
    private static string? FindPendingInbound(string sessionKey)
    {
        var root = CcStorage.VoiceTurnLogs();
        var cutoff = DateTime.UtcNow.AddMinutes(-PairingWindowMinutes);

        string? best = null;
        DateTime bestTs = DateTime.MinValue;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var inboundPath = Path.Combine(dir, "inbound.json");
            if (!File.Exists(inboundPath)) continue;
            if (File.Exists(Path.Combine(dir, "outbound.json"))) continue;

            InboundRecord? rec;
            try { rec = JsonSerializer.Deserialize<InboundRecord>(File.ReadAllText(inboundPath)); }
            catch { continue; }
            if (rec is null || !string.Equals(rec.SessionKey, sessionKey, StringComparison.Ordinal)) continue;

            if (!DateTime.TryParse(rec.TsUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
                ts = Directory.GetLastWriteTimeUtc(dir);
            if (ts < cutoff) continue;

            if (ts > bestTs) { bestTs = ts; best = dir; }
        }
        return best;
    }

    private static string CreateStandalone(string sessionId)
    {
        var turnId = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(CcStorage.VoiceTurnLogs(), DirName(turnId));
        Directory.CreateDirectory(dir);
        var inbound = new InboundRecord
        {
            TurnId = turnId,
            SessionKey = SessionKey(sessionId),
            SessionId = sessionId ?? "",
            TsUtc = DateTime.UtcNow.ToString("o"),
            CleanupReason = "no inbound utterance (standalone outbound)",
        };
        File.WriteAllText(Path.Combine(dir, "inbound.json"), JsonSerializer.Serialize(inbound, JsonOpts));
        return dir;
    }

    /// <summary>Delete turn dirs older than the retention window. Best-effort.</summary>
    private static void Purge()
    {
        var root = CcStorage.VoiceTurnLogs();
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[VoiceTurnLog] Purge skip {Path.GetFileName(dir)}: {ex.Message}");
            }
        }
    }

    // A sortable, unique dir name: timestamp prefix so listings read chronologically.
    private static string DirName(string turnId)
        => $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}_{turnId[..Math.Min(8, turnId.Length)]}";

    /// <summary>
    /// Stable per-session key used to pair the two halves. A GUID session id (the
    /// normal case) collapses to its first 8 hex chars; anything else is sanitized.
    /// </summary>
    private static string SessionKey(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return "nosession";
        return Guid.TryParse(sessionId, out var g)
            ? g.ToString("N")[..8]
            : CcStorage.SafeFileName(sessionId.Trim());
    }

    private static string AudioExt(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return string.IsNullOrEmpty(ext) ? ".webm" : ext;
    }

    private sealed class InboundRecord
    {
        public string TurnId { get; set; } = "";
        public string SessionKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string SessionName { get; set; } = "";
        public string TsUtc { get; set; } = "";
        public string RawTranscript { get; set; } = "";
        public string CleanedTranscript { get; set; } = "";
        public string CleanupReason { get; set; } = "";
    }

    private sealed class OutboundRecord
    {
        public string TsUtc { get; set; } = "";
        public string AgentReply { get; set; } = "";
        public string WingmanSpoken { get; set; } = "";
        public string SummarizerModel { get; set; } = "";
        public string Status { get; set; } = "";
    }
}
