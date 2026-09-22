using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Claude;

/// <summary>
/// Computes a session's token usage from its Claude Code JSONL transcript. Every assistant
/// line carries a usage block (input_tokens, output_tokens, cache_read_input_tokens,
/// cache_creation_input_tokens); this walks the file and sums them - purely mechanical, no
/// interpretation. Turn grouping: a turn starts at each real (non-meta, non-tool-result)
/// user message and collects the assistant lines until the next one.
///
/// A transcript is append-only, so the file is read ONCE and then only what was appended since.
/// The context gauge asks every four seconds for as long as a session is on screen; until
/// 22 September 2026 each ask re-opened the whole file and re-parsed every line, and these files
/// reach tens of megabytes. Now a cursor per transcript remembers how far it has read and the
/// running sums, and an ask with nothing new to read costs one file open and one length check.
/// A file that shrank (rewritten, or a different session at the same path) is read from the start.
/// </summary>
public static class SessionTokenUsage
{
    /// <summary>Per-turn entries returned to the UI are capped; older turns still count in
    /// the session totals.</summary>
    public const int MaxTurnsReturned = 60;

    /// <summary>How many transcripts keep a cursor. Sessions on a Director are few; beyond this the
    /// least recently asked-about cursor is dropped and that transcript is simply read whole again.</summary>
    internal const int MaxCursors = 32;

    private static readonly ConcurrentDictionary<string, TranscriptCursor> Cursors = new(StringComparer.Ordinal);

    /// <summary>Compute usage for the transcript file. Reads with FileShare.ReadWrite so the
    /// live session can keep writing. Throws when the file does not exist - callers decide
    /// what a missing transcript means for them.</summary>
    public static SessionUsageDto ComputeFromFile(string jsonlPath, string sessionId)
        => ComputeFromFileTracked(jsonlPath, sessionId).Usage;

    /// <summary>As <see cref="ComputeFromFile"/>, and also says how many bytes this call had to read.
    /// Zero means the transcript had not grown since the last ask.</summary>
    internal static (SessionUsageDto Usage, long BytesRead) ComputeFromFileTracked(string jsonlPath, string sessionId)
    {
        var key = Path.GetFullPath(jsonlPath);
        var cursor = Cursors.GetOrAdd(key, _ => new TranscriptCursor(sessionId));
        EvictIfCrowded(key);

        lock (cursor)
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (fs.Length < cursor.Consumed || !string.Equals(cursor.SessionId, sessionId, StringComparison.Ordinal))
            {
                FileLog.Write($"[SessionTokenUsage] ComputeFromFile: {jsonlPath} shrank to {fs.Length} bytes " +
                              $"(had read {cursor.Consumed}) or changed session - reading it from the start");
                cursor.Reset(sessionId);
            }

            long read = 0;
            var pending = fs.Length - cursor.Consumed;
            if (pending > 0)
            {
                fs.Position = cursor.Consumed;
                var buffer = new byte[pending];
                var filled = 0;
                while (filled < buffer.Length)
                {
                    var n = fs.Read(buffer, filled, buffer.Length - filled);
                    if (n <= 0) break; // the writer has not finished what the length promised
                    filled += n;
                }

                // Complete lines end with a newline. Everything through the last newline is consumed
                // whether or not each line parsed - a line that is not JSON is skipped for good, as
                // it always was. The tail after the last newline is consumed only if it parses NOW;
                // otherwise it is a torn line still being written and is read again next time.
                var end = Array.LastIndexOf(buffer, (byte)'\n', filled - 1, filled) + 1;
                if (end > 0)
                {
                    foreach (var line in Encoding.UTF8.GetString(buffer, 0, end).Split('\n'))
                        cursor.Accumulator.TryFeed(line);
                    read = end;
                }
                if (end < filled && cursor.Accumulator.TryFeed(Encoding.UTF8.GetString(buffer, end, filled - end)))
                    read = filled;

                cursor.Consumed += read;
            }

            cursor.LastUsedUtc = DateTime.UtcNow;
            var usage = cursor.Accumulator.Snapshot(sessionId);
            if (read > 0)
                FileLog.Write($"[SessionTokenUsage] ComputeFromFile: {jsonlPath} read {read} new bytes (now {cursor.Consumed} of {fs.Length}), " +
                              $"out={usage.OutputTokens}, ctx={usage.ContextTokens}, turns={usage.Turns.Count}");
            return (usage, read);
        }
    }

    /// <summary>Forget every cursor. For tests, which reuse paths across cases.</summary>
    internal static void ForgetAllCursors() => Cursors.Clear();

    private static void EvictIfCrowded(string keep)
    {
        if (Cursors.Count <= MaxCursors) return;
        var oldest = Cursors
            .Where(kv => !string.Equals(kv.Key, keep, StringComparison.Ordinal))
            .OrderBy(kv => kv.Value.LastUsedUtc)
            .FirstOrDefault();
        if (oldest.Key is not null && Cursors.TryRemove(oldest.Key, out _))
            FileLog.Write($"[SessionTokenUsage] dropped the cursor for {oldest.Key} - more than {MaxCursors} transcripts asked about");
    }

    /// <summary>Pure core - testable on raw JSONL lines.</summary>
    public static SessionUsageDto Compute(IEnumerable<string> jsonlLines, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(jsonlLines);
        var accumulator = new UsageAccumulator();
        foreach (var line in jsonlLines)
            accumulator.TryFeed(line);
        return accumulator.Snapshot(sessionId);
    }

    /// <summary>What one transcript has told us so far, and how far into it we have read.</summary>
    private sealed class TranscriptCursor
    {
        public string SessionId { get; private set; }
        public long Consumed { get; set; }
        public UsageAccumulator Accumulator { get; private set; } = new();
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

        public TranscriptCursor(string sessionId) => SessionId = sessionId;

        public void Reset(string sessionId)
        {
            SessionId = sessionId;
            Consumed = 0;
            Accumulator = new UsageAccumulator();
        }
    }

    /// <summary>The running sums and turn grouping. Feed lines in file order; take a snapshot at any
    /// point. A snapshot is a fresh object every time, so what a caller holds never changes under it.</summary>
    private sealed class UsageAccumulator
    {
        private long _input, _output, _cacheRead, _cacheCreate, _context;
        private string? _contextModel;
        private int _assistantMessages;
        private DateTime? _lastMessageUtc;
        private readonly List<TurnUsageDto> _turns = new();
        private TurnUsageDto? _turn;

        /// <summary>Returns true when the line was JSON and was taken in (whether or not it counted
        /// for anything); false for a blank or torn line.</summary>
        public bool TryFeed(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { return false; } // torn tail line while claude writes

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return true;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (type == "user" && IsRealUserPrompt(root))
                {
                    _turn = new TurnUsageDto { Index = _turns.Count + 1 };
                    _turns.Add(_turn);
                    return true;
                }

                if (type != "assistant") return true;
                if (!root.TryGetProperty("message", out var msg) ||
                    !msg.TryGetProperty("usage", out var u) ||
                    u.ValueKind != JsonValueKind.Object)
                    return true;

                var input = Long(u, "input_tokens");
                var output = Long(u, "output_tokens");
                var cacheRead = Long(u, "cache_read_input_tokens");
                var cacheCreate = Long(u, "cache_creation_input_tokens");

                _input += input;
                _output += output;
                _cacheRead += cacheRead;
                _cacheCreate += cacheCreate;
                _context = input + cacheRead + cacheCreate;
                // The latest line wins, mirroring ContextTokens: the window is sized to whichever
                // model produced the most recent reply (a session can switch models mid-run).
                if (msg.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                {
                    var model = modelEl.GetString();
                    if (!string.IsNullOrWhiteSpace(model))
                        _contextModel = model;
                }
                _assistantMessages++;

                var ts = root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                         && DateTime.TryParse(tsEl.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed
                    : (DateTime?)null;
                if (ts is not null) _lastMessageUtc = ts;

                // Assistant lines before the first user prompt (boot/system) count in the
                // totals but belong to no turn.
                if (_turn is not null)
                {
                    _turn.NewTokens += output + input + cacheCreate;
                    _turn.OutputTokens += output;
                    if (ts is not null) _turn.EndedAtUtc = ts.Value;
                }
                return true;
            }
        }

        public SessionUsageDto Snapshot(string sessionId)
        {
            // Drop prompt-only turns (no assistant line yet) and cap what the UI receives.
            var turns = _turns
                .Where(x => x.OutputTokens > 0 || x.NewTokens > 0)
                .Select(x => new TurnUsageDto { Index = x.Index, EndedAtUtc = x.EndedAtUtc, NewTokens = x.NewTokens, OutputTokens = x.OutputTokens })
                .ToList();
            if (turns.Count > MaxTurnsReturned)
                turns = turns.Skip(turns.Count - MaxTurnsReturned).ToList();

            return new SessionUsageDto
            {
                SessionId = sessionId,
                InputTokens = _input,
                OutputTokens = _output,
                CacheReadTokens = _cacheRead,
                CacheCreationTokens = _cacheCreate,
                ContextTokens = _context,
                ContextModel = _contextModel,
                AssistantMessageCount = _assistantMessages,
                LastMessageUtc = _lastMessageUtc,
                Turns = turns,
            };
        }
    }

    /// <summary>A REAL user prompt: not meta, and its content is a plain string or text
    /// blocks - never a tool_result array (those are the harness feeding tool output back).</summary>
    private static bool IsRealUserPrompt(JsonElement root)
    {
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
            return false;
        if (!root.TryGetProperty("message", out var msg)) return false;
        if (msg.ValueKind == JsonValueKind.String) return true;
        if (!msg.TryGetProperty("content", out var content)) return false;
        if (content.ValueKind == JsonValueKind.String) return true;
        if (content.ValueKind != JsonValueKind.Array) return false;

        var hasText = false;
        foreach (var block in content.EnumerateArray())
        {
            var bt = block.TryGetProperty("type", out var btEl) ? btEl.GetString() : null;
            if (bt == "tool_result") return false;
            if (bt == "text") hasText = true;
        }
        return hasText;
    }

    private static long Long(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
