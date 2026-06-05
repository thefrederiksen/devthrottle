using System.Text;
using CcDirector.Core.Claude;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Everything the wingman reads to brief ONE completed turn (TURN_BRIEFING.md section 3,
/// box [1]). Assembled mechanically - no LLM, no parsing heuristics; raw material only.
/// </summary>
public sealed record TurnPackage(
    Guid SessionId,
    int TurnCount,
    string? FirstUserPrompt,
    string? LastUserPrompt,
    string? LastAssistantText,
    bool ReplyPending,
    string TranscriptDelta,
    string ScreenTail,
    string? RollingIntent,
    IReadOnlyList<string> PriorRailLines,
    string? CurrentHeadline = null);

/// <summary>
/// Builds a <see cref="TurnPackage"/> from the parsed transcript widgets, the current screen
/// grid, and the prior briefs. Pure and testable; the orchestrator feeds it live data.
/// </summary>
public static class TurnPackageBuilder
{
    /// <summary>Widgets included in the delta text (the current turn plus a little context).</summary>
    public const int DeltaWidgetBudget = 40;

    /// <summary>Character caps keep the wingman prompt bounded on monster turns.</summary>
    public const int DeltaMaxChars = 14_000;
    public const int ScreenTailMaxChars = 4_000;

    public static TurnPackage Build(
        Guid sessionId,
        IReadOnlyList<TurnWidgetDto> widgets,
        string screenTail,
        TurnBriefDto? priorBrief,
        IReadOnlyList<TurnBriefDto>? recentBriefs = null)
    {
        ArgumentNullException.ThrowIfNull(widgets);

        var extract = BriefBuilder.Extract(widgets);

        // The delta: widgets from the last briefed turn onward (or the tail on first brief).
        var from = Math.Max(0, Math.Max(priorBrief?.TurnNumber ?? 0, widgets.Count - DeltaWidgetBudget));
        var sb = new StringBuilder();
        for (var i = from; i < widgets.Count; i++)
        {
            var w = widgets[i];
            var body = w.Kind is "Text" or "UserMessage" ? w.Content : (w.Subheader ?? w.Content);
            sb.Append(i).Append(". ").Append(w.Kind);
            if (w.IsPending) sb.Append(" (pending)");
            sb.Append(": ").AppendLine(Truncate(body?.Replace("\r\n", "\n") ?? "", w.Kind is "Text" or "UserMessage" ? 4000 : 160));
        }
        var delta = sb.ToString();
        if (delta.Length > DeltaMaxChars) delta = delta[^DeltaMaxChars..];

        var tail = screenTail ?? "";
        if (tail.Length > ScreenTailMaxChars) tail = tail[^ScreenTailMaxChars..];

        var priorLines = (recentBriefs ?? Array.Empty<TurnBriefDto>())
            .Where(b => !string.IsNullOrWhiteSpace(b.NeedsYou?.RailLine))
            .Take(5)
            .Select(b => b.NeedsYou is { } n ? n.RailLine : "")
            .ToList();

        // The session's standing headline (v2.2): the newest stored brief that carries one.
        // Stub/degrade briefs may have an empty headline - skip past them so a single failed
        // wingman read does not amnesia the headline.
        var headline = (recentBriefs ?? Array.Empty<TurnBriefDto>())
            .Select(b => b.Headline)
            .FirstOrDefault(h => !string.IsNullOrWhiteSpace(h));

        return new TurnPackage(
            sessionId,
            extract.TurnCount,
            extract.FirstUserPrompt,
            extract.LastUserPrompt,
            extract.LastAssistantText,
            extract.ReplyPending,
            delta,
            tail,
            priorBrief?.Intent,
            priorLines,
            headline);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";
}
