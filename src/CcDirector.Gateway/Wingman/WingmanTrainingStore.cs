using System.Text;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// Captures wingman training data (issue #531 follow-up). When the "wingman_training_capture"
/// setting is on, every wingman summary appends one JSON-lines record holding up to
/// <see cref="MaxTerminalChars"/> characters of the session terminal, the agent reply + recent
/// context the wingman actually saw, and the wingman's spoken response. The pairs are a labeled
/// dataset for testing and improving the wingman.
///
/// Best-effort and off the hot path: a capture failure (setting read, terminal fetch, disk) is
/// logged and swallowed - it must never break or slow a voice turn. The setting is read at the
/// moment of capture, so turning it on/off takes effect immediately with no Gateway restart.
/// </summary>
public sealed class WingmanTrainingStore
{
    /// <summary>How many characters of the session terminal to keep per record (the most recent tail).</summary>
    public const int MaxTerminalChars = 20_000;

    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private readonly Func<bool> _isEnabled;
    private readonly string _dir;

    /// <param name="isEnabled">Reads the live setting; defaults to <see cref="WingmanTrainingCaptureConfig.Get"/>.</param>
    /// <param name="dir">Where records are written; defaults to <see cref="CcStorage.WingmanTrainingData"/>. Tests pass a temp dir.</param>
    public WingmanTrainingStore(Func<bool>? isEnabled = null, string? dir = null)
    {
        _isEnabled = isEnabled ?? WingmanTrainingCaptureConfig.Get;
        _dir = dir ?? CcStorage.WingmanTrainingData();
    }

    /// <summary>Whether capture is currently turned on (read live each call).</summary>
    public bool Enabled
    {
        get
        {
            try { return _isEnabled(); }
            catch (Exception ex) { FileLog.Write($"[WingmanTrainingStore] reading setting FAILED: {ex.Message}"); return false; }
        }
    }

    /// <summary>
    /// Capture one wingman summary: fetch up to <see cref="MaxTerminalChars"/> of the session
    /// terminal from the owning Director, then append the record. No-op when capture is off.
    /// </summary>
    public async Task CaptureAsync(
        DirectorEndpointClient client, string endpoint, string sessionId, string source,
        string reply, string recentContext, string spoken, double replySeconds, CancellationToken ct = default)
    {
        if (!Enabled) return;
        string terminal = "";
        try
        {
            var buf = await client.GetBufferAsync(endpoint, sessionId, lines: null, raw: false, since: null, ct);
            if (!string.IsNullOrEmpty(buf?.Text)) terminal = buf!.Text;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanTrainingStore] terminal fetch FAILED (capturing without it): sid={sessionId}, error={ex.Message}");
        }
        await WriteAsync(sessionId, source, terminal, reply, recentContext, spoken, replySeconds, ct);
    }

    /// <summary>
    /// Append one record (terminal truncated to the most recent <see cref="MaxTerminalChars"/>).
    /// Internal so a test can write with a known terminal and assert truncation + the JSON line.
    /// </summary>
    internal async Task WriteAsync(
        string sessionId, string source, string terminal, string reply, string recentContext,
        string spoken, double replySeconds, CancellationToken ct = default)
    {
        if (!Enabled) return;
        try
        {
            var trimmed = terminal.Length > MaxTerminalChars ? terminal[^MaxTerminalChars..] : terminal;
            var record = new
            {
                atUtc = DateTime.UtcNow,
                sessionId,
                source,
                model = SafeModel(),
                terminalChars = trimmed.Length,
                terminalTruncated = terminal.Length > MaxTerminalChars,
                terminal = trimmed,
                reply,
                recentContext,
                spoken,
                replySeconds,
            };
            var line = JsonSerializer.Serialize(record) + "\n";
            var file = Path.Combine(_dir, $"wingman-training-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");

            await WriteLock.WaitAsync(ct);
            try { await File.AppendAllTextAsync(file, line, Encoding.UTF8, ct); }
            finally { WriteLock.Release(); }

            FileLog.Write($"[WingmanTrainingStore] captured: sid={sessionId}, source={source}, termChars={trimmed.Length}, replyLen={reply.Length}, spokenLen={spoken.Length}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanTrainingStore] write FAILED (best-effort): sid={sessionId}, error={ex.Message}");
        }
    }

    /// <summary>One captured record, summarized for a picker (issue #537 A/B testing).</summary>
    public sealed record TrainingRecordSummary(string Id, string Source, DateTime AtUtc, string SessionId, string ReplyPreview, string SpokenPreview);

    /// <summary>One captured record in full, to re-run draft instructions against.</summary>
    public sealed record TrainingRecord(string Id, string Source, DateTime AtUtc, string SessionId, string Terminal, string Reply, string RecentContext, string Spoken);

    /// <summary>Recent captured records, newest first (across daily files). Addressed positionally
    /// as "&lt;filename&gt;#&lt;lineindex&gt;" - stable because the files are append-only.</summary>
    public IReadOnlyList<TrainingRecordSummary> ListRecords(int limit = 30)
    {
        var outp = new List<TrainingRecordSummary>();
        try
        {
            if (!Directory.Exists(_dir)) return outp;
            foreach (var f in Directory.GetFiles(_dir, "wingman-training-*.jsonl").OrderByDescending(f => f))
            {
                var name = Path.GetFileName(f);
                string[] lines;
                try { lines = File.ReadAllLines(f); } catch { continue; }
                for (var i = lines.Length - 1; i >= 0; i--)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(lines[i]);
                        var r = doc.RootElement;
                        outp.Add(new TrainingRecordSummary(
                            Id: $"{name}#{i}",
                            Source: Str(r, "source"),
                            AtUtc: r.TryGetProperty("atUtc", out var a) && a.TryGetDateTime(out var dt) ? dt : default,
                            SessionId: Str(r, "sessionId"),
                            ReplyPreview: Preview(Str(r, "reply"), 140),
                            SpokenPreview: Preview(Str(r, "spoken"), 140)));
                        if (outp.Count >= limit) return outp;
                    }
                    catch (JsonException) { }
                }
            }
        }
        catch (Exception ex) { FileLog.Write($"[WingmanTrainingStore] ListRecords FAILED: {ex.Message}"); }
        return outp;
    }

    /// <summary>Load one full record by its positional id ("&lt;filename&gt;#&lt;lineindex&gt;").</summary>
    public TrainingRecord? GetRecord(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var hash = id.LastIndexOf('#');
        if (hash <= 0 || hash >= id.Length - 1) return null;
        var name = id[..hash];
        if (!int.TryParse(id[(hash + 1)..], out var index) || index < 0) return null;
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) return null;   // no path escape
        try
        {
            var file = Path.Combine(_dir, name);
            if (!File.Exists(file)) return null;
            var lines = File.ReadAllLines(file);
            if (index >= lines.Length) return null;
            using var doc = JsonDocument.Parse(lines[index]);
            var r = doc.RootElement;
            return new TrainingRecord(
                Id: id,
                Source: Str(r, "source"),
                AtUtc: r.TryGetProperty("atUtc", out var a) && a.TryGetDateTime(out var dt) ? dt : default,
                SessionId: Str(r, "sessionId"),
                Terminal: Str(r, "terminal"),
                Reply: Str(r, "reply"),
                RecentContext: Str(r, "recentContext"),
                Spoken: Str(r, "spoken"));
        }
        catch (Exception ex) { FileLog.Write($"[WingmanTrainingStore] GetRecord {id} FAILED: {ex.Message}"); return null; }
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static string Preview(string s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "...";
    }

    private static string SafeModel()
    {
        try { return BrainModelConfig.Get(); }
        catch { return ""; }
    }
}
