using System.Text.Json;

namespace CcDirectorClient.Voice;

/// <summary>
/// Phone-side view of one wingman turn brief - the strong model's interpretation of a
/// completed turn, fetched verbatim from the Gateway (GET /sessions/{sid}/turnbriefs/latest)
/// and rendered by the Wingman tab. Mirrors the subset of the Gateway's TurnBriefDto the
/// phone shows (docs/architecture/wingman/TURN_BRIEFING.md). The phone NEVER interprets or
/// post-processes this - interpretation happened once, on the Director, with the best model.
/// </summary>
public sealed class TurnBrief
{
    public string SessionId { get; set; } = "";

    /// <summary>Transcript widget count when this brief was generated - the staleness key and the feedback selector.</summary>
    public int TurnNumber { get; set; }

    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>Generator identity ("wingman:opus", "stub", "condenser:gpt-4.1-mini").</summary>
    public string Model { get; set; } = "stub";

    /// <summary>True when this brief came from a degrade tier, not the wingman.</summary>
    public bool Degraded { get; set; }

    /// <summary>The current chapter's title: &lt;= 6 words naming WHAT the session works on. Empty falls back to Intent.</summary>
    public string Headline { get; set; } = "";

    /// <summary>True when THIS turn started a new chapter (a genuinely different piece of work).</summary>
    public bool NewChapter { get; set; }

    /// <summary>Rolling intent: what the user is trying to get done, carried across turns.</summary>
    public string Intent { get; set; } = "";

    /// <summary>The user prompt that started this turn, as the wingman saw it (dictated @file prompts arrive resolved).</summary>
    public string? YouAsked { get; set; }

    /// <summary>What the agent concretely did this turn. Proportional: small turn, few bullets.</summary>
    public List<string> Did { get; set; } = new();

    /// <summary>Null when the turn needs nothing from the user.</summary>
    public TurnBriefNeedsYou? NeedsYou { get; set; }

    /// <summary>Concrete "nothing needed" verdict when <see cref="NeedsYou"/> is null. Null on pre-v3 briefs.</summary>
    public string? AllClear { get; set; }

    /// <summary>Mission-complete suggestion: set when the wingman judges the goal delivered. Suggestion only.</summary>
    public TurnBriefSuggestedAction? SuggestedAction { get; set; }
}

/// <summary>The needs-you block of a turn brief. See TURN_BRIEFING.md section 4.</summary>
public sealed class TurnBriefNeedsYou
{
    /// <summary>Synthesized, crisp: leads with whether anything is broken/blocking, then the action(s).</summary>
    public string Statement { get; set; } = "";

    /// <summary>"reply" (typed message -> append Enter) | "keys" (on-screen menu answered via raw key sends).</summary>
    public string AnswerVia { get; set; } = "reply";

    /// <summary>"single" | "multiple". Multiple = pick-any checklist: an option sends a TOGGLE and <see cref="Submit"/> completes.</summary>
    public string SelectionMode { get; set; } = "single";

    /// <summary>The completing send for SelectionMode "multiple" (e.g. "\r"); null for single.</summary>
    public string? Submit { get; set; }

    /// <summary>Real choices the wingman decided exist. May be empty (the composer always works).</summary>
    public List<TurnBriefOption> Options { get; set; } = new();

    /// <summary>VERBATIM quote from the reply or screen. Empty when validation failed (UI hides it).</summary>
    public string Evidence { get; set; } = "";

    /// <summary>"blocking" | "review" | "fyi". fyi does NOT paint red.</summary>
    public string Urgency { get; set; } = "review";

    /// <summary>"high" | "ambiguous". Ambiguous statements say so honestly.</summary>
    public string Confidence { get; set; } = "high";

    /// <summary>"If you do nothing" - the single most decision-relevant fact. Null on pre-v3 briefs.</summary>
    public string? IfIgnored { get; set; }
}

/// <summary>One answer option: the visible label and the exact send that answers it.</summary>
public sealed class TurnBriefOption
{
    /// <summary>Short visible label ("1 Terse", "Looks good - commit it").</summary>
    public string Key { get; set; } = "";

    /// <summary>What one tap transmits: reply text, or a raw key sequence for answerVia "keys".</summary>
    public string Send { get; set; } = "";

    /// <summary>Consequence + risk of choosing this option. Shown under the button.</summary>
    public string? Note { get; set; }

    /// <summary>At most ONE option carries this - the wingman's pick, with the reason in <see cref="Note"/>.</summary>
    public bool Recommended { get; set; }
}

/// <summary>A wingman-suggested session-level action.</summary>
public sealed class TurnBriefSuggestedAction
{
    /// <summary>"close_session" (the only type today). Consumers ignore types they do not know.</summary>
    public string Type { get; set; } = "";

    /// <summary>&lt;= 12 words: why the session is finished ("Bug filed as #198; nothing pending").</summary>
    public string Reason { get; set; } = "";
}

/// <summary>
/// Parses the Gateway's turn-brief JSON into the phone model. The /latest endpoint returns
/// a bare TurnBriefDto (camelCase) on 200, or a { error, briefingState } object on 404. Kept
/// pure (no MAUI/HTTP dependency) so it is unit tested off-device.
/// </summary>
public static class TurnBriefParser
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Parse a single brief (the 200 body of /turnbriefs/latest). Returns null for an empty
    /// body or a payload with no session id (not a brief). Throws on malformed JSON so the
    /// caller's entry-point handler surfaces the real reason rather than showing a blank tab.
    /// </summary>
    public static TurnBrief? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var brief = JsonSerializer.Deserialize<TurnBrief>(json, Json);
        if (brief is null || string.IsNullOrWhiteSpace(brief.SessionId)) return null;
        return brief;
    }

    /// <summary>
    /// Pull the briefingState out of the 404 body ({ "error": "no brief yet", "briefingState":
    /// "Briefing" }) so the tab can say "wingman is reading this turn" instead of a flat "no
    /// brief". Returns "" when the field is absent. Trusts the Gateway's own JSON contract.
    /// </summary>
    public static string ParseBriefingState(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("briefingState", out var s)
            && s.ValueKind == JsonValueKind.String)
            return s.GetString() ?? "";
        return "";
    }
}
