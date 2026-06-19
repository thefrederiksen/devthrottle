using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Wingman;

/// <summary>One choice in an on-screen menu the coding agent is presenting (issue #531 menu
/// handling). Mirrors the proven brief option shape: a visible label, the EXACT keystrokes that
/// pick it (for a picker that confirms with Enter the send already includes "\r"), an optional
/// consequence note, and whether it is the recommended/default pick.</summary>
public sealed class WingmanMenuOption
{
    /// <summary>Short visible label, e.g. "1. Yes" or "Yes, and don't ask again".</summary>
    public string Key { get; set; } = "";

    /// <summary>Raw keystrokes that choose this option (e.g. "1\r" for a picker; "1" toggle for multi-select).</summary>
    public string Send { get; set; } = "";

    /// <summary>Consequence/risk of choosing this option (a permission scope, a destructive effect).</summary>
    public string? Note { get; set; }

    /// <summary>At most one option is the wingman's recommended/default pick.</summary>
    public bool Recommended { get; set; }
}

/// <summary>The structured menu the agent is showing on screen, extracted by the warm brain, plus
/// a ready-to-speak reading of it. <see cref="IsMenu"/> is false when the terminal is not an
/// interactive choice (a free-text prompt or just idle), in which case the rest is empty.</summary>
public sealed class WingmanMenu
{
    public bool IsMenu { get; set; }

    /// <summary>The choice the agent is asking, in plain speakable words.</summary>
    public string Question { get; set; } = "";

    /// <summary>"single" (pick one) | "multiple" (toggle any that apply, then <see cref="Submit"/>).</summary>
    public string SelectionMode { get; set; } = "single";

    /// <summary>The completing keystroke for "multiple" (e.g. "\r"); empty for single (each send self-submits).</summary>
    public string Submit { get; set; } = "";

    public List<WingmanMenuOption> Options { get; set; } = new();

    /// <summary>The full speakable reading: the question, each option, and how to answer. Built by the gateway.</summary>
    public string Spoken { get; set; } = "";
}

/// <summary>
/// Pure (no-brain) helpers for menu handling: a cheap heuristic to decide whether the terminal is
/// even worth a brain look, and local mapping of a spoken/typed answer to an option so the common
/// cases ("two", "the recommended one", "yes") never need a second model call.
/// </summary>
public static class WingmanMenuLogic
{
    // A line like "1. Yes", "  2) No", "a. Cancel", "> 1. Proceed", "❯ 3. ...". Leading non-word run
    // swallows arrows/markers/whitespace; then a number or single letter, a . or ), a space, content.
    private static readonly Regex OptionLine = new(@"^\W*(?:\d{1,2}|[A-Za-z])[.)]\s+\S", RegexOptions.Compiled);
    private static readonly Regex LeadingKeyNum = new(@"^\W*(\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex DigitWord = new(@"\b(\d{1,2})\b", RegexOptions.Compiled);

    /// <summary>
    /// Cheap gate: does the BOTTOM of the screen (where an active prompt lives) look like an
    /// interactive menu? True when the last ~40 lines hold 2+ numbered/lettered option lines, OR
    /// they carry a Claude-Code permission-prompt fingerprint (its "❯ 1" selection cursor, or the
    /// stock "do you want to proceed" / "don't ask again" / "yes, and" phrasing). These fingerprints
    /// are menu-specific, so a normal turn does not trip them. When false, skip the brain
    /// menu-detection entirely and treat the input as a normal prompt - that keeps non-menu turns
    /// from paying for a brain call, and (correctly) ignores a numbered list sitting in scrollback.
    /// </summary>
    public static bool LooksLikeMenu(string? terminal)
    {
        if (string.IsNullOrWhiteSpace(terminal)) return false;
        var lines = terminal.Replace("\r", "").Split('\n');
        var tailStart = Math.Max(0, lines.Length - 40);
        var count = 0;
        for (var i = tailStart; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (OptionLine.IsMatch(t)) count++;
        }
        if (count >= 2) return true;

        // Permission-prompt fingerprints (for boxed/wrapped menus the per-line regex can miss).
        var tail = string.Join("\n", lines.Skip(tailStart)).ToLowerInvariant();
        return tail.Contains("❯ 1") || tail.Contains("❯1")          // "❯ 1" selection cursor on option 1
            || tail.Contains("do you want to proceed")
            || tail.Contains("don't ask again") || tail.Contains("dont ask again")
            || tail.Contains("yes, and");
    }

    private static readonly Dictionary<string, int> NumberWords = new()
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
    };

    /// <summary>
    /// Map a spoken/typed answer to a 0-based option index, or -1 when there is no confident local
    /// match (the caller then asks the brain). Deliberately conservative - it returns a hit only when
    /// sure, deferring anything fuzzy to the model. Tries, in order: an explicit number (digit or
    /// word; the option's own key number, else its position), "recommended/default", ordinals
    /// ("first".."fifth"/"last"), a normalized label match, then yes/no shortcuts.
    /// </summary>
    public static int MatchOption(WingmanMenu menu, string userText)
    {
        if (menu?.Options is null || menu.Options.Count == 0 || string.IsNullOrWhiteSpace(userText)) return -1;
        var opts = menu.Options;
        var padded = " " + Norm(userText) + " ";

        // 1. "recommended" / "default" / "suggested" (most explicit intent; also beats the filler
        //    word "one" in "the recommended one").
        if (padded.Contains("recommend") || padded.Contains("default") || padded.Contains("suggest"))
        {
            var rec = opts.FindIndex(o => o.Recommended);
            if (rec >= 0) return rec;
        }

        // 2. Ordinals - BEFORE numbers, so "the last one"/"the first one" are not read as the
        //    number-word "one".
        var ord = Ordinal(padded, opts.Count);
        if (ord >= 0) return ord;

        // 3. Explicit number (digit "2" or word "two") -> option whose key starts with it, else position.
        foreach (var n in NumbersIn(padded))
        {
            for (var i = 0; i < opts.Count; i++)
                if (LeadingNumber(opts[i].Key) == n) return i;
            if (n >= 1 && n <= opts.Count) return n - 1;
        }

        // 4. Label match: the longest option label (punctuation-normalized) contained in the speech.
        var best = -1; var bestLen = 0;
        for (var i = 0; i < opts.Count; i++)
        {
            var label = Norm(StripLeadingKey(opts[i].Key));
            if (label.Length < 3) continue;
            if (padded.Contains(" " + label + " ") || padded.Contains(" " + label) || padded.Contains(label + " "))
            {
                if (label.Length > bestLen) { best = i; bestLen = label.Length; }
            }
        }
        if (best >= 0) return best;

        // 5. yes/no shortcuts -> first option whose label starts with yes / no. Negation wins so
        //    "not sure"/"no thanks" never reads as a yes.
        if (IsNegative(padded)) { var i = FindLabelStartsWith(opts, "no"); if (i >= 0) return i; }
        else if (IsAffirmative(padded)) { var i = FindLabelStartsWith(opts, "yes"); if (i >= 0) return i; }

        return -1;
    }

    /// <summary>Lowercase, strip punctuation to spaces, collapse runs - so "Yes, and don't" and
    /// "yes and dont" compare equal.</summary>
    private static string Norm(string s)
        => Regex.Replace(Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9 ]+", " "), @"\s+", " ").Trim();

    /// <summary>The numbers named in the padded, normalized text - digits and number-words.</summary>
    private static IEnumerable<int> NumbersIn(string padded)
    {
        foreach (Match m in DigitWord.Matches(padded))
            if (int.TryParse(m.Groups[1].Value, out var n)) yield return n;
        foreach (var kv in NumberWords)
            if (padded.Contains(" " + kv.Key + " ")) yield return kv.Value;
    }

    private static int LeadingNumber(string key)
    {
        var m = LeadingKeyNum.Match(key ?? "");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : -1;
    }

    /// <summary>Drop a leading "1." / "2)" / "a." marker from an option label, leaving the words.</summary>
    private static string StripLeadingKey(string key)
        => Regex.Replace(key ?? "", @"^\W*(?:\d{1,2}|[A-Za-z])[.)]\s*", "").Trim();

    private static int Ordinal(string text, int count)
    {
        if (text.Contains(" last ") && count > 0) return count - 1;
        string[] words = { "first", "second", "third", "fourth", "fifth" };
        for (var i = 0; i < words.Length && i < count; i++)
            if (text.Contains(" " + words[i] + " ")) return i;
        return -1;
    }

    private static int FindLabelStartsWith(IReadOnlyList<WingmanMenuOption> opts, string prefix)
    {
        for (var i = 0; i < opts.Count; i++)
            if (StripLeadingKey(opts[i].Key).TrimStart().ToLowerInvariant().StartsWith(prefix, StringComparison.Ordinal))
                return i;
        return -1;
    }

    // Conservative on purpose (the brain handles the fuzzy cases): only strong, unambiguous tokens.
    // Input is already normalized (lowercased, apostrophes removed) - "don't" arrives as "dont".
    private static bool IsAffirmative(string text)
        => text.Contains(" yes ") || text.Contains(" yeah ") || text.Contains(" yep ") || text.Contains(" yup ")
        || text.Contains(" proceed ") || text.Contains(" approve ");

    private static bool IsNegative(string text)
        => text.Contains(" no ") || text.Contains(" nope ") || text.Contains(" cancel ") || text.Contains(" deny ")
        || text.Contains(" dont ") || text.Contains(" do not ") || text.Contains(" reject ");
}
