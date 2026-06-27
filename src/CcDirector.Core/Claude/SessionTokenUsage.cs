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
/// </summary>
public static class SessionTokenUsage
{
    /// <summary>Per-turn entries returned to the UI are capped; older turns still count in
    /// the session totals.</summary>
    public const int MaxTurnsReturned = 60;

    /// <summary>Compute usage for the transcript file. Reads with FileShare.ReadWrite so the
    /// live session can keep writing. Throws when the file does not exist - callers decide
    /// what a missing transcript means for them.</summary>
    public static SessionUsageDto ComputeFromFile(string jsonlPath, string sessionId)
    {
        FileLog.Write($"[SessionTokenUsage] ComputeFromFile: {jsonlPath}");
        using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        var usage = Compute(ReadLines(reader), sessionId);
        FileLog.Write($"[SessionTokenUsage] ComputeFromFile: out={usage.OutputTokens}, ctx={usage.ContextTokens}, turns={usage.Turns.Count}");
        return usage;
    }

    /// <summary>Pure core - testable on raw JSONL lines.</summary>
    public static SessionUsageDto Compute(IEnumerable<string> jsonlLines, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(jsonlLines);
        var dto = new SessionUsageDto { SessionId = sessionId };
        var turns = new List<TurnUsageDto>();
        TurnUsageDto? turn = null;

        foreach (var line in jsonlLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // torn tail line while claude writes

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (type == "user" && IsRealUserPrompt(root))
                {
                    turn = new TurnUsageDto { Index = turns.Count + 1 };
                    turns.Add(turn);
                    continue;
                }

                if (type != "assistant") continue;
                if (!root.TryGetProperty("message", out var msg) ||
                    !msg.TryGetProperty("usage", out var u) ||
                    u.ValueKind != JsonValueKind.Object)
                    continue;

                var input = Long(u, "input_tokens");
                var output = Long(u, "output_tokens");
                var cacheRead = Long(u, "cache_read_input_tokens");
                var cacheCreate = Long(u, "cache_creation_input_tokens");

                dto.InputTokens += input;
                dto.OutputTokens += output;
                dto.CacheReadTokens += cacheRead;
                dto.CacheCreationTokens += cacheCreate;
                dto.ContextTokens = input + cacheRead + cacheCreate;
                // The latest line wins, mirroring ContextTokens: the window is sized to whichever
                // model produced the most recent reply (a session can switch models mid-run).
                if (msg.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                {
                    var model = modelEl.GetString();
                    if (!string.IsNullOrWhiteSpace(model))
                        dto.ContextModel = model;
                }
                dto.AssistantMessageCount++;

                var ts = root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                         && DateTime.TryParse(tsEl.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed
                    : (DateTime?)null;
                if (ts is not null) dto.LastMessageUtc = ts;

                // Assistant lines before the first user prompt (boot/system) count in the
                // totals but belong to no turn.
                if (turn is not null)
                {
                    turn.NewTokens += output + input + cacheCreate;
                    turn.OutputTokens += output;
                    if (ts is not null) turn.EndedAtUtc = ts.Value;
                }
            }
        }

        // Drop prompt-only turns (no assistant line yet) and cap what the UI receives.
        dto.Turns = turns.Where(x => x.OutputTokens > 0 || x.NewTokens > 0).ToList();
        if (dto.Turns.Count > MaxTurnsReturned)
            dto.Turns = dto.Turns.Skip(dto.Turns.Count - MaxTurnsReturned).ToList();
        return dto;
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

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
            yield return line;
    }
}
