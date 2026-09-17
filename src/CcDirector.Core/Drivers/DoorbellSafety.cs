using CcDirector.Core.Agents;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Drivers;

/// <summary>What one look at the composer found.</summary>
public enum ComposerReading
{
    /// <summary>The composer is on screen and holds no text.</summary>
    Empty,

    /// <summary>The composer is on screen and holds text - typed, pasted, or a half-entered slash command.</summary>
    HoldsText,

    /// <summary>A menu or dialog is drawn where the composer would be (issue 2842).</summary>
    MenuOpen,

    /// <summary>No composer this check recognises is on screen. Never read as empty.</summary>
    NotFound,
}

/// <summary>One frame of the terminal as the Director resolves it: rows, cursor, and whether the cursor is shown.</summary>
/// <param name="Rows">The visible rows, top to bottom, trailing-trimmed.</param>
/// <param name="CursorRow">Zero-based cursor row, or -1 when there is no grid.</param>
/// <param name="CursorCol">Zero-based cursor column, or -1.</param>
/// <param name="CursorVisible">Whether the agent is showing the hardware cursor. A drawn menu hides it.</param>
public sealed record ScreenFrame(IReadOnlyList<string> Rows, int CursorRow, int CursorCol, bool CursorVisible);

/// <summary>What the Director knows about a session at the moment it is asked to ring.</summary>
/// <param name="Agent">Which agent runs in the terminal - the composer is drawn differently by each.</param>
/// <param name="Exited">The session has exited or failed.</param>
/// <param name="DirectorSaysWorking">The Director's own activity state is Working or Starting.</param>
/// <param name="HasTerminalGrid">The session has a terminal the Director renders (not an embedded or piped one).</param>
/// <param name="ProductMayHaveLeftText">An earlier send by the product gave up without proving the composer
/// clear (<see cref="ComposerRetention"/>). The next send would press Escape first, which could land on the
/// owner's words, so a doorbell is never that send.</param>
/// <param name="Frames">Two frames taken a moment apart. Both must agree before anything is typed.</param>
public sealed record DoorbellFacts(
    AgentKind Agent,
    bool Exited,
    bool DirectorSaysWorking,
    bool HasTerminalGrid,
    bool ProductMayHaveLeftText,
    IReadOnlyList<ScreenFrame> Frames);

/// <summary>The safety check's answer.</summary>
/// <param name="Ring">True when the doorbell line may be typed now.</param>
/// <param name="Reason">One of <see cref="FleetRingDeferReasons"/> when <paramref name="Ring"/> is false; empty otherwise.</param>
/// <param name="Detail">The reason as a sentence.</param>
public readonly record struct DoorbellVerdict(bool Ring, string Reason, string Detail)
{
    /// <summary>Safe to type.</summary>
    public static DoorbellVerdict Safe { get; } = new(true, "", "not working, composer empty");

    /// <summary>Not safe; nothing is typed.</summary>
    public static DoorbellVerdict Defer(string reason, string detail) => new(false, reason, detail);
}

/// <summary>
/// THE DOORBELL'S SAFETY CHECK (the Message Load mission, slice 2, ruling 7). The Gateway asks the Director to
/// ring; this decides whether one line may be typed into the terminal RIGHT NOW. It is pure - the Director hands
/// it the facts and two screen frames - so every rule is proven against real captured screens.
///
/// THE RULES, IN ORDER. The first that fails defers the ring, and nothing is typed:
///  1. The session has not exited. Nothing is ever typed into a dead terminal.
///  2. The session has a rendered terminal. Without a screen, nothing below can be known.
///  3. The product has not left text of its own in the composer (see <see cref="DoorbellFacts.ProductMayHaveLeftText"/>).
///  4. The Director does not think the session is working.
///  5. In BOTH frames: the screen shows no working marker, no menu, and a composer that is recognised and empty.
///
/// Unknown is never empty. A screen this check does not recognise defers the ring as "screen unreadable"; that
/// costs a late doorbell, where the opposite mistake costs the owner's words or an answered menu.
///
/// HOW "COMPOSER EMPTY" IS DECIDED FROM THE ROWS - Claude Code (captured from Claude Code 2.1.274 on 17 September
/// 2026; the fixtures are in the Core tests under TestData/doorbell):
///  - The composer is a block of rows between two horizontal rules: a row made only of '─' characters, then a
///    row that starts with the prompt glyph '❯', then zero or more continuation rows, then another all-'─' row.
///    Only the LOWEST such block counts, and at most <see cref="MaxFooterRows"/> rows may follow it (the status
///    footer). A '❯' anywhere else - the selection arrow of a picker ("❯ 2. Opus"), a folder-trust dialog
///    ("❯ No, exit"), a past prompt in the transcript - is not a composer, because it is not framed that way.
///  - The composer is EMPTY when the prompt row holds nothing after the glyph and every continuation row is blank.
///    Any character at all means it holds text: a typed sentence, a second line of a draft, a half-typed
///    "/model", and a collapsed paste, which Claude Code draws as "[Pasted text #1 +29 lines]" with "paste again
///    to expand" in the footer. That last one is the case issue 2845 was afraid of - a collapsed paste reading
///    as empty - and it does not: the placeholder is text on the prompt row, and the capture proves it.
///  - Working: the footer (the rows after the composer) says "esc to interrupt". Mid-turn, Claude Code draws the
///    composer EMPTY - the capture shows "❯" alone while the numbers are still streaming - so an empty composer
///    on its own is never permission to type.
///
/// Codex (captured from Codex 0.154.0 on the same day):
///  - Codex draws no rules. The composer is the row the visible cursor is on, and it starts with '›'. A row
///    "› 1. ..." is a menu option, not a composer; so is any '›' row while the cursor is hidden.
///  - Codex shows a dim placeholder in an empty composer ("Ask Codex to do anything"). The rows carry no colour,
///    so the placeholder is recognised by the cursor sitting straight after the glyph AND the row being one of
///    <see cref="CodexPlaceholders"/>. Anything else on the row is treated as text.
///
/// WHAT IS NOT COVERED - read before relying on this:
///  - Agents other than Claude Code and Codex: every ring is deferred as "screen unreadable", so those sessions
///    are never rung (their messages wait until they read the inbox on their own, and never go stuck).
///  - A Codex placeholder not in <see cref="CodexPlaceholders"/> reads as text, so the ring is deferred until the
///    placeholder changes. Safe, but late.
///  - A Claude Code placeholder suggestion (dim text on an empty prompt row) would read as text, likewise.
///  - Text the owner has typed but the agent has not yet repainted, and a turn that starts after the last look.
///    The ringer (<see cref="Sessions.FleetDoorbellRinger"/>) takes a third frame and re-reads the Director's
///    state immediately before the first byte and defers if anything moved; a keystroke or a self-started turn
///    inside the remaining interval is not seen. The race is narrowed to that interval, not closed.
///  - Composer text scrolled out of the visible rows (a very long draft) - the prompt row still shows text, so
///    this defers; but a draft whose visible window is blank would not be seen.
///  - Codex "working": the marker is the same "esc to interrupt" footer; no mid-turn Codex screen was captured
///    (the account hit its usage limit during the capture), so that rule is tested on a written row only.
/// </summary>
public static class DoorbellSafety
{
    /// <summary>How many rows may follow Claude Code's composer block before it is not the live composer.</summary>
    public const int MaxFooterRows = 4;

    /// <summary>How many rows from the bottom are searched for a working marker or a menu hint.</summary>
    public const int BottomRows = 8;

    /// <summary>The placeholder texts Codex draws in an empty composer, as captured.</summary>
    public static readonly IReadOnlyList<string> CodexPlaceholders =
    [
        "Ask Codex to do anything",
    ];

    /// <summary>The footer text both agents show while a turn is running.</summary>
    public const string WorkingMarker = "esc to interrupt";

    /// <summary>Hints only an interactive menu or dialog shows. Compared case-insensitively.</summary>
    public static readonly IReadOnlyList<string> MenuHints =
    [
        "enter to select",
        "enter to confirm",
        "press enter to confirm",
        "press enter to continue",
        "esc to go back",
        "tab/arrow to navigate",
        "enter to set as default",
        "do you want to proceed",
    ];

    /// <summary>Decide whether the doorbell may be typed.</summary>
    public static DoorbellVerdict Check(DoorbellFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Exited)
            return DoorbellVerdict.Defer(FleetRingDeferReasons.Exited, "the session has exited; nothing is typed into it");
        if (!facts.HasTerminalGrid)
            return DoorbellVerdict.Defer(FleetRingDeferReasons.ScreenUnreadable, "the session has no rendered terminal to check");
        if (facts.ProductMayHaveLeftText)
            return DoorbellVerdict.Defer(FleetRingDeferReasons.ComposerHoldsText,
                "an earlier send could not prove the composer clear; typing now would clear it first");
        if (facts.DirectorSaysWorking)
            return DoorbellVerdict.Defer(FleetRingDeferReasons.Working, "the Director's activity state is working");
        if (facts.Frames is null || facts.Frames.Count < 2)
            return DoorbellVerdict.Defer(FleetRingDeferReasons.ScreenUnreadable, "two screen frames are needed and were not taken");

        foreach (var frame in facts.Frames)
        {
            var verdict = CheckFrame(facts.Agent, frame);
            if (!verdict.Ring) return verdict;
        }
        return DoorbellVerdict.Safe;
    }

    /// <summary>The screen rules for one frame.</summary>
    public static DoorbellVerdict CheckFrame(AgentKind agent, ScreenFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var rows = frame.Rows ?? [];
        if (rows.Count == 0 || rows.All(string.IsNullOrWhiteSpace))
            return DoorbellVerdict.Defer(FleetRingDeferReasons.ScreenUnreadable, "the screen rendered nothing");

        if (ShowsWorking(rows))
            return DoorbellVerdict.Defer(FleetRingDeferReasons.Working, $"the screen shows \"{WorkingMarker}\"");

        return ReadComposer(agent, frame) switch
        {
            ComposerReading.Empty => DoorbellVerdict.Safe,
            ComposerReading.HoldsText => DoorbellVerdict.Defer(FleetRingDeferReasons.ComposerHoldsText, "the composer holds text"),
            ComposerReading.MenuOpen => DoorbellVerdict.Defer(FleetRingDeferReasons.MenuOpen, "an interactive menu or dialog is open"),
            _ => DoorbellVerdict.Defer(FleetRingDeferReasons.ScreenUnreadable,
                $"no {agent} composer was recognised on the screen"),
        };
    }

    /// <summary>Does the bottom of the screen show the working marker?</summary>
    public static bool ShowsWorking(IReadOnlyList<string> rows) =>
        Bottom(rows).Any(r => r.Contains(WorkingMarker, StringComparison.OrdinalIgnoreCase));

    /// <summary>Read the composer for this agent. Every agent without a reader answers <see cref="ComposerReading.NotFound"/>.</summary>
    public static ComposerReading ReadComposer(AgentKind agent, ScreenFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return agent switch
        {
            AgentKind.ClaudeCode => ReadClaudeComposer(frame.Rows ?? []),
            AgentKind.Codex => ReadCodexComposer(frame),
            _ => ComposerReading.NotFound,
        };
    }

    private static ComposerReading ReadClaudeComposer(IReadOnlyList<string> rows)
    {
        // Search upward for the lowest closing rule that has a prompt row framed above it.
        var last = LastNonBlank(rows);
        for (var close = last; close >= 2 && last - close <= MaxFooterRows; close--)
        {
            if (!IsRule(rows[close])) continue;

            // Walk up from the closing rule to the prompt row; the rows in between are continuation rows.
            for (var prompt = close - 1; prompt >= 1; prompt--)
            {
                var row = rows[prompt];
                if (IsRule(row)) break; // reached another rule without a prompt row: not a composer block
                if (!row.StartsWith('❯')) continue;
                if (!IsRule(rows[prompt - 1])) break; // a '❯' that is not framed from above: a picker or a transcript line

                var footer = rows.Skip(close + 1).ToList();
                if (footer.Any(IsMenuHint)) return ComposerReading.MenuOpen;

                var onPrompt = row[1..].Trim();
                var continuation = rows.Skip(prompt + 1).Take(close - prompt - 1);
                return onPrompt.Length == 0 && continuation.All(string.IsNullOrWhiteSpace)
                    ? ComposerReading.Empty
                    : ComposerReading.HoldsText;
            }
        }
        return Bottom(rows).Any(IsMenuHint) || rows.Any(IsSelectedOption)
            ? ComposerReading.MenuOpen
            : ComposerReading.NotFound;
    }

    private static ComposerReading ReadCodexComposer(ScreenFrame frame)
    {
        var rows = frame.Rows ?? [];
        if (Bottom(rows).Any(IsMenuHint) || rows.Any(IsSelectedOption))
            return ComposerReading.MenuOpen;
        if (!frame.CursorVisible || frame.CursorRow < 0 || frame.CursorRow >= rows.Count)
            return ComposerReading.NotFound;

        var row = rows[frame.CursorRow];
        if (!row.StartsWith('›')) return ComposerReading.NotFound;
        var text = row[1..].Trim();
        if (text.Length == 0) return ComposerReading.Empty;
        // A placeholder is drawn to the RIGHT of a cursor that sits straight after the glyph and its space.
        var cursorAtStart = frame.CursorCol <= 2;
        return cursorAtStart && CodexPlaceholders.Contains(text, StringComparer.Ordinal)
            ? ComposerReading.Empty
            : ComposerReading.HoldsText;
    }

    private static bool IsRule(string? row)
    {
        if (string.IsNullOrEmpty(row)) return false;
        var t = row.Trim();
        return t.Length >= 10 && t.All(c => c == '─');
    }

    private static bool IsMenuHint(string row) =>
        MenuHints.Any(h => row.Contains(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>A drawn selection arrow in front of a numbered option: "❯ 2. Opus", "› 1. Yes, continue".</summary>
    private static bool IsSelectedOption(string row)
    {
        var t = row.TrimStart(' ', '│', '|');
        if (t.Length < 3 || (t[0] != '❯' && t[0] != '›')) return false;
        var rest = t[1..].TrimStart();
        var digits = 0;
        while (digits < rest.Length && char.IsDigit(rest[digits])) digits++;
        return digits is > 0 and <= 2 && digits < rest.Length && rest[digits] is '.' or ')';
    }

    private static int LastNonBlank(IReadOnlyList<string> rows)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!string.IsNullOrWhiteSpace(rows[i])) return i;
        return -1;
    }

    private static IEnumerable<string> Bottom(IReadOnlyList<string> rows)
    {
        var last = LastNonBlank(rows);
        if (last < 0) return [];
        var start = Math.Max(0, last - BottomRows + 1);
        return rows.Skip(start).Take(last - start + 1).Where(r => r is not null);
    }
}

/// <summary>The one line the doorbell types (ruling 8). Fixed except for the count; message text is never typed.</summary>
public static class FleetDoorbellLine
{
    /// <summary>The command a session runs to read its messages.</summary>
    public const string ReadCommand = "cc-devthrottle message inbox";

    /// <summary>The line for this many waiting messages.</summary>
    public static string For(int unreadCount)
    {
        var n = Math.Max(1, unreadCount);
        var noun = n == 1 ? "fleet message is" : "fleet messages are";
        return $"[DevThrottle doorbell] {n} {noun} waiting for you. To read, run: {ReadCommand}";
    }
}
