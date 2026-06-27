using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Codex;

/// <summary>
/// Computes how full a Codex session's context window is, from its rollout JSONL. Codex writes a
/// <c>token_count</c> event after every turn whose payload carries both the live usage and the model's
/// window, so - unlike Claude - the denominator is given to us and nothing is guessed:
///
///   {"type":"event_msg","payload":{"type":"token_count","info":{
///       "last_token_usage":{"input_tokens":159314, ...},   // the input the model last saw = context fullness
///       "model_context_window":258400 }}}                    // the window size
///
/// UsedTokens = the latest <c>last_token_usage.input_tokens</c> (the whole conversation as the model
/// last ingested it); WindowTokens = <c>model_context_window</c>; percent = used / window. Null until a
/// token_count event exists (no turn has completed). The rollout for a repo is located by
/// <see cref="CodexRolloutLocator"/>.
/// </summary>
public static class CodexContextUsage
{
    /// <summary>Context usage for the newest Codex rollout matching <paramref name="repoPath"/>, or
    /// null when no rollout matches or it carries no token_count event yet.</summary>
    public static ContextUsageDto? ReadForRepo(string repoPath)
    {
        var rollout = CodexRolloutLocator.ResolveByRepo(repoPath);
        if (rollout is null)
            return null;
        return ReadFromFile(rollout);
    }

    /// <summary>Context usage from one rollout file. Reads with FileShare.ReadWrite so the live Codex
    /// session can keep writing. Null when the file is missing or has no token_count event.</summary>
    public static ContextUsageDto? ReadFromFile(string rolloutPath)
    {
        if (!File.Exists(rolloutPath))
            return null;
        FileLog.Write($"[CodexContextUsage] ReadFromFile: {rolloutPath}");
        using var fs = new FileStream(rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return Compute(ReadLines(reader));
    }

    /// <summary>Pure core - testable on raw rollout lines. Returns the LAST token_count event's usage.</summary>
    public static ContextUsageDto? Compute(IEnumerable<string> rolloutLines)
    {
        ArgumentNullException.ThrowIfNull(rolloutLines);
        ContextUsageDto? latest = null;

        foreach (var line in rolloutLines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // torn tail line while codex writes

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!(root.TryGetProperty("type", out var t) && t.GetString() == "event_msg")) continue;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                if (!(payload.TryGetProperty("type", out var pt) && pt.GetString() == "token_count")) continue;
                if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) continue;
                if (info.ValueKind == JsonValueKind.Null) continue;

                if (!info.TryGetProperty("last_token_usage", out var last) || last.ValueKind != JsonValueKind.Object)
                    continue;
                var used = Long(last, "input_tokens");
                if (used <= 0)
                    continue;

                var window = Long(info, "model_context_window");
                var percent = window > 0
                    ? Math.Round((double)used / window * 100.0, 1)
                    : (double?)null;

                var asOf = root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                           && DateTime.TryParse(tsEl.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed
                    : (DateTime?)null;

                latest = new ContextUsageDto
                {
                    UsedTokens = used,
                    WindowTokens = window > 0 ? window : null,
                    PercentUsed = percent,
                    AsOfUtc = asOf,
                };
            }
        }

        if (latest is not null)
            FileLog.Write($"[CodexContextUsage] used={latest.UsedTokens}, window={latest.WindowTokens}, pct={latest.PercentUsed}");
        return latest;
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
