using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Sessions;

namespace CcDirector.Core.Rules;

/// <summary>
/// Session Rules part 2, phase 1: the per-session rules file (wire contract W2) - its exact shape, and
/// the one compare-before-write rule that keeps a timer from rewriting it for nothing.
///
/// A session with no hook rules gets a file whose <c>rules</c> is an empty list, never no file: an absent
/// file means "not a Director session, or not written yet", and the hook's evaluation log says which.
/// </summary>
public static class SessionRulesFile
{
    /// <summary>The shape version the hook checks before it reads anything else.</summary>
    public const int Version = 1;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Render the file's text. Pure, so the shape is pinned by test.</summary>
    public static string Render(
        Guid sessionId, string machine, string repo, IReadOnlyList<HookRuleEntry> rules, DateTime generatedUtc)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var body = new FileBody
        {
            Version = Version,
            SessionId = sessionId.ToString(),
            Machine = machine,
            Repo = repo,
            GeneratedUtc = FormatUtc(generatedUtc),
            Rules = rules,
        };
        return JsonSerializer.Serialize(body, JsonOpts);
    }

    /// <summary>
    /// Write the file only when what it SAYS has changed. The generation time is not part of what it
    /// says: a rewrite carrying the same rules keeps the file's existing time and leaves it untouched,
    /// so the time reads as "when these rules arrived", and a quiet Director touches nothing.
    /// </summary>
    /// <returns>True when the file was written (it changed, or did not exist yet).</returns>
    public static bool WriteIfChanged(
        string path, Guid sessionId, string machine, string repo, IReadOnlyList<HookRuleEntry> rules, DateTime nowUtc)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            var existingTime = ReadGeneratedUtc(existing);
            if (existingTime is not null)
            {
                var same = Render(sessionId, machine, repo, rules, existingTime.Value);
                if (string.Equals(existing, same, StringComparison.Ordinal))
                    return false;
            }
        }

        SessionHookFiles.WriteAtomic(path, Render(sessionId, machine, repo, rules, nowUtc));
        return true;
    }

    /// <summary>
    /// Delete a session's rules file and the hook's evaluation log beside it (W4), so neither outlives
    /// the session. The log is the proof a negative control was evaluated, so a live check reads it
    /// before it closes the session. A file that is already gone is not an error.
    /// </summary>
    public static void DeleteFor(Guid sessionId, string? directory)
    {
        foreach (var path in new[] { SessionHookFiles.RulesPathFor(sessionId, directory), SessionHookFiles.EvaluationLogPathFor(sessionId, directory) })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>Round-trip UTC with seven fractional digits and a Z, e.g. 2026-10-18T12:00:00.0000000Z.</summary>
    private static string FormatUtc(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    /// <summary>The generation time an existing file carries, or null when it is not a file of this shape
    /// (which simply means it is rewritten).</summary>
    private static DateTime? ReadGeneratedUtc(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("generatedUtc", out var value)
                && value.ValueKind == JsonValueKind.String
                && DateTime.TryParseExact(value.GetString(), "yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
        }
        catch (JsonException)
        {
            // not-an-error: a file that is not JSON is replaced by the caller
        }
        return null;
    }

    private sealed record FileBody
    {
        [JsonPropertyName("version")] public int Version { get; init; }
        [JsonPropertyName("sessionId")] public string SessionId { get; init; } = "";
        [JsonPropertyName("machine")] public string Machine { get; init; } = "";
        [JsonPropertyName("repo")] public string Repo { get; init; } = "";
        [JsonPropertyName("generatedUtc")] public string GeneratedUtc { get; init; } = "";
        [JsonPropertyName("rules")] public IReadOnlyList<HookRuleEntry> Rules { get; init; } = Array.Empty<HookRuleEntry>();
    }
}
