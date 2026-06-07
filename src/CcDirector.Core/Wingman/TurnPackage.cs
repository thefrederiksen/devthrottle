using System.Text;
using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;
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
    string? CurrentHeadline = null,
    string? ParkedComposerText = null,
    // Issue #236: the session's declared purpose, so the brief contract can apply a
    // per-type mission clause (a BugReport session whose issue is filed is DONE -> suggest
    // close). Defaults to Implement, the back-compat no-op.
    SessionType SessionType = SessionType.Implement);

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
        IReadOnlyList<TurnBriefDto>? recentBriefs = null,
        SessionType sessionType = SessionType.Implement)
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
            headline,
            ExtractParkedComposerText(tail),
            sessionType);
    }

    /// <summary>
    /// Mechanical extraction of typed-but-unsubmitted composer text from the screen tail
    /// (issue #208, review rounds 1-3: in 3 of 5 red turns the user's reply was already
    /// parked and briefs that ignore it re-ask a decided question; whether text was parked
    /// vs submitted is unknowable to the model from pixels alone, so the package states it).
    ///
    /// Heuristic, deliberately CONSERVATIVE (a false null is harmless; a false positive
    /// would reject good briefs via the v3.2 validation invariant): at turn end the idle
    /// footer renders "... · &lt;- for agents"; anything after the LAST such marker that is
    /// not known chrome (tips, usage banners, separators, prompt markers) is the composer's
    /// parked content.
    /// </summary>
    public static string? ExtractParkedComposerText(string screenTail)
    {
        if (string.IsNullOrEmpty(screenTail)) return null;

        // INVARIANT: this marker is Claude Code's idle-footer chrome ("... · ← for
        // agents"). If a CLI update renames it, extraction degrades to null fleet-wide
        // (conservative, no false positives) - briefs stop quoting parked replies. The
        // ParkedComposerTextTests pin the current chrome; revisit on CLI footer changes.
        const string marker = "← for agents";
        var at = screenTail.LastIndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;

        var rest = screenTail[(at + marker.Length)..];
        var kept = new StringBuilder();
        foreach (var raw in rest.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("Tip:", StringComparison.Ordinal)) continue;
            if (line.StartsWith("You've used", StringComparison.Ordinal)) continue;
            if (line.StartsWith("⏵⏵", StringComparison.Ordinal)) continue;
            if (line.StartsWith('─') || line.StartsWith('❯')) continue;
            if (line.Contains("session limit", StringComparison.Ordinal)) continue;
            if (line.Contains("Enter to select", StringComparison.Ordinal)) continue;
            if (kept.Length > 0) kept.Append('\n');
            kept.Append(line);
        }

        var text = kept.ToString().Trim();
        // Length cap: real parked replies are short; a long blob means the heuristic
        // grabbed rendering debris - return null rather than poison the invariant.
        return text.Length is > 0 and <= 500 ? text : null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";
}
