using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Everything the wingman reads for one on-demand session deep dive (issue #217):
/// the session's own per-turn story (from the stored briefs), the first user prompt,
/// a recent transcript span, and the current screen. Assembled mechanically - no LLM,
/// no parsing heuristics; raw material only (same philosophy as <see cref="TurnPackage"/>).
/// </summary>
public sealed record ExplainPackage(
    Guid SessionId,
    int TurnCount,
    string? FirstUserPrompt,
    IReadOnlyList<string> StoryLines,
    string TranscriptDelta,
    string ScreenTail);

/// <summary>
/// The "I am lost - explain" contract (issue #217): the prompt that asks the model for a
/// SESSION-LEVEL deep dive - what happened, what we did, what next - and the mechanical
/// validation of its JSON answer. Lives in the one prompt home next to
/// <see cref="TurnBriefContract"/>; a change here reaches the fleet via the Gateway.
///
/// Everything here is pure: no model calls, no I/O beyond logging. Validation is
/// mechanical (presence, length caps), never interpretation.
/// </summary>
public static class ExplainContract
{
    /// <summary>Caps keep the deep-dive prompt bounded on long sessions.</summary>
    public const int MaxStoryLines = 150;
    public const int StoryLineMaxChars = 300;

    /// <summary>
    /// The session's own story, one compact line per briefed turn, OLDEST FIRST -
    /// built mechanically from the stored briefs (headline = chapter, turnTitle, did).
    /// </summary>
    public static IReadOnlyList<string> BuildStoryLines(IReadOnlyList<TurnBriefDto> briefsNewestFirst)
    {
        ArgumentNullException.ThrowIfNull(briefsNewestFirst);

        var lines = new List<string>(briefsNewestFirst.Count);
        for (var i = briefsNewestFirst.Count - 1; i >= 0; i--)
        {
            var b = briefsNewestFirst[i];
            if (b.Degraded) continue; // stubs carry no story
            var sb = new StringBuilder();
            sb.Append('t').Append(b.TurnNumber);
            if (b.NewChapter && !string.IsNullOrWhiteSpace(b.Headline))
                sb.Append(" CHAPTER[").Append(b.Headline).Append(']');
            if (!string.IsNullOrWhiteSpace(b.TurnTitle))
                sb.Append(' ').Append(b.TurnTitle);
            if (b.Did.Count > 0)
                sb.Append(" - ").Append(string.Join("; ", b.Did));
            var line = sb.ToString();
            lines.Add(line.Length <= StoryLineMaxChars ? line : line[..StoryLineMaxChars]);
        }

        // On monster sessions keep the most RECENT story; the opening chapter context
        // survives through the first user prompt, which the prompt always carries.
        return lines.Count <= MaxStoryLines ? lines : lines.Skip(lines.Count - MaxStoryLines).ToList();
    }

    public static string BuildPrompt(ExplainPackage p)
    {
        ArgumentNullException.ThrowIfNull(p);

        var sb = new StringBuilder();
        sb.AppendLine("You are the WINGMAN. The user just came back to one of their AI coding agent");
        sb.AppendLine("sessions and is LOST - they pressed an 'I am lost - explain' button. Answer the");
        sb.AppendLine("three questions they actually have, in plain language for someone with ZERO");
        sb.AppendLine("context. This is about the WHOLE session, not the last turn.");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object (no fences, no prose) in exactly this shape:");
        sb.AppendLine("""
{
  "whatHappened": "2-4 sentences: the session's story - what it was for and where it ended up.",
  "whatWeDid": ["3-8 bullets, past tense, concrete deliverables: commits, releases, deploys, files, decisions. <=18 words each."],
  "whatNext": "1-3 sentences: the single recommendation. If the goal is delivered, say closing the session is the real action; if something is pending, name THE next step."
}
""");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Never invent. Everything must be grounded in the material below.");
        sb.AppendLine("- If the story is genuinely unclear from the material, SAY SO in whatHappened.");
        sb.AppendLine("- Plain language: no jargon the first user prompt does not itself use.");
        sb.AppendLine("- whatNext is a recommendation, not a list - one next step, or 'close this session'.");
        sb.AppendLine();
        sb.AppendLine("=== FIRST USER PROMPT (what this session was started for) ===");
        sb.AppendLine(Truncate(p.FirstUserPrompt, 800));
        sb.AppendLine();
        sb.AppendLine("=== SESSION STORY (the wingman's own per-turn notes, oldest first) ===");
        sb.AppendLine(p.StoryLines.Count > 0 ? string.Join(Environment.NewLine, p.StoryLines) : "(no per-turn notes stored yet)");
        sb.AppendLine();
        sb.AppendLine($"=== RECENT TRANSCRIPT (turn count: {p.TurnCount}) ===");
        sb.AppendLine(p.TranscriptDelta);
        sb.AppendLine();
        sb.AppendLine("=== CURRENT SCREEN (bottom of the terminal) ===");
        sb.AppendLine(p.ScreenTail);
        return sb.ToString();
    }

    /// <summary>Mechanical validation: all three sections present, bullets bounded, caps applied.</summary>
    public static ExplainReportDto? ParseAndValidate(string raw, ExplainPackage package, string generatorId, DateTime requestedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Models sometimes wrap JSON in fences despite instructions; unwrap mechanically.
        var json = raw.Trim();
        if (json.StartsWith("```"))
        {
            var first = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (first >= 0 && lastFence > first) json = json[(first + 1)..lastFence].Trim();
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            FileLog.Write($"[ExplainContract] validation: not JSON ({ex.Message})");
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var report = new ExplainReportDto
            {
                SessionId = package.SessionId.ToString(),
                TurnNumber = package.TurnCount,
                RequestedAtUtc = requestedAtUtc,
                GeneratedAtUtc = DateTime.UtcNow,
                Model = generatorId,
                Degraded = false,
                WhatHappened = Str(root, "whatHappened"),
                WhatNext = Str(root, "whatNext"),
            };

            if (root.TryGetProperty("whatWeDid", out var did) && did.ValueKind == JsonValueKind.Array)
                report.WhatWeDid = did.EnumerateArray()
                    .Select(b => (b.GetString() ?? "").Trim())
                    .Where(s => s.Length > 0)
                    .Take(10)
                    .ToList();

            if (string.IsNullOrWhiteSpace(report.WhatHappened)
                || string.IsNullOrWhiteSpace(report.WhatNext)
                || report.WhatWeDid.Count == 0)
            {
                FileLog.Write("[ExplainContract] validation: missing whatHappened/whatWeDid/whatNext");
                return null;
            }

            if (report.WhatHappened.Length > 1200) report.WhatHappened = report.WhatHappened[..1200];
            if (report.WhatNext.Length > 700) report.WhatNext = report.WhatNext[..700];
            report.WhatWeDid = report.WhatWeDid.Select(b => b.Length <= 250 ? b : b[..250]).ToList();

            return report;
        }
    }

    private static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "...";
}
