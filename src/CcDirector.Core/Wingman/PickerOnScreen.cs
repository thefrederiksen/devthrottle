using System.Text.RegularExpressions;

namespace CcDirector.Core.Wingman;

/// <summary>
/// WHETHER AN INTERACTIVE PICKER IS DRAWN ON A SESSION'S SCREEN - decided by code, never by the judge model.
///
/// WHY CODE AND NOT THE MODEL (owner ruling, 2026-09-20). The judge was asked whether a picker was on the screen, and
/// on the eighty-five screens whose answer is known it invented a menu on 19 of the 57 with no picker drawn. Asked
/// outright, in a field of its own, it still invented 18 - and never once contradicted itself, because once it had
/// read the conversation it sincerely believed a picker was there. This rule gets all eighty-five right bar the one
/// case excluded below on purpose, and on a held-out read of the corpus it caught every real picker found by eye (13)
/// and fired on none of the look-alikes (12).
///
/// A PICKER ANNOUNCES ITSELF. The rule looks only for POSITIVE evidence: a picker's own footer, its own fixed text, or
/// its selected option row. It never tries to prove a picker is absent, because the composer's status line survives a
/// repaint underneath a real prompt - a "Dangerous rm operation ... Do you want to proceed?" permission prompt was
/// captured with "bypass permissions on (shift+tab to cycle)" still on its last row. Proving "the composer is active"
/// would have stripped the buttons off a prompt asking permission to delete a directory.
///
/// EVERY SIGN IS CASE-SENSITIVE, because case is what separated a drawn control from prose each time they collided:
/// the usage-limit notice writes "esc to cancel" in lower case and offers no choice, and the agent's own prose "How do
/// you want to proceed?" contains "do you want to proceed".
///
/// NOT A SIGN, ON PURPOSE:
///   - "Esc to cancel" ON ITS OWN. The sign-in screen ("Paste code here if prompted") carries it and offers nothing to
///     choose. A real picker pairs it with an Enter footer or draws option rows, and those are the signs.
///   - The feedback survey ("How is Claude doing this session? (optional)"). It is optional and does not block - a
///     person types an ordinary message beneath it - and it sat on 313 of 14,073 unseen screens. Counting it would put
///     rating buttons on one stop in fifty.
///
/// PROBABLY SUBOPTIMAL FOR EVERY AGENT BUT CLAUDE CODE, AND APPLIED TO THEM ANYWAY (owner ruling, 2026-09-20: "it's
/// better to have something than nothing ... then we log it"). The signs were written from Claude Code screens, plus
/// the Codex and Copilot wording seen on a held-out read: Codex writes "Press enter to continue" and marks its selected
/// row with U+203A. Pi, Gemini, Grok, OpenCode and any agent added later are judged by the SAME list, unverified. The
/// failure that list can cause is a picker it does not recognise, which then gets no buttons; it cannot press a key,
/// because the send path guards a live picker separately (WaitingScreenReader). <see cref="Calibrated"/> says which
/// agents the list was checked against, and every caller logs every decision with it, so the misses can be found and
/// this list adjusted - it is expected to need adjusting as agents change their screens.
///
/// The reference implementation, and the screens it was measured on, are in the private repository:
/// docs/missions/wingman-picker-flag-2026-09-19/footer_rule.py. Keep the two in step.
/// </summary>
public static class PickerOnScreen
{
    /// <summary>How many rows from the bottom a footer is looked for in. A picker is drawn at the foot of the screen.</summary>
    public const int BottomRows = 12;

    /// <summary>How many rows from the bottom a selected option row is looked for in.</summary>
    public const int OptionSignRows = 8;

    /// <summary>How far from a column-0 selected row a second numbered option may sit and still count as its neighbour.</summary>
    public const int OptionRowsNear = 3;

    /// <summary>The agents whose screens the sign list was checked against. Every other agent is judged by it unverified.</summary>
    public static readonly IReadOnlyList<string> CalibratedAgents = new[] { "ClaudeCode", "Codex", "Copilot" };

    private static readonly (string Name, Regex Pattern)[] Signs =
    {
        ("enter-to-confirm", new Regex("Enter to (confirm|select|continue)", RegexOptions.CultureInvariant)),
        ("tab-to-amend", new Regex("Tab to amend", RegexOptions.CultureInvariant)),
        ("to-navigate", new Regex("to navigate", RegexOptions.CultureInvariant)),
        ("folder-trust", new Regex("Yes, I trust this folder", RegexOptions.CultureInvariant)),
        ("proceed-prompt", new Regex(@"^\s*Do you want to proceed\?", RegexOptions.CultureInvariant | RegexOptions.Multiline)),
        ("submit-answers", new Regex("Ready to submit your answers", RegexOptions.CultureInvariant)),
        ("type-something", new Regex(@"^\s*\d+\.\s+Type something\.", RegexOptions.CultureInvariant | RegexOptions.Multiline)),
        // Codex writes its footer in lower case after "Press". Seen on 32 unseen Codex screens - an update prompt, a
        // folder-trust prompt, a switch-model prompt - and on no Claude Code screen at all.
        ("press-enter", new Regex("Press enter to (continue|confirm)", RegexOptions.CultureInvariant)),
    };

    /// <summary>The selection marker Claude Code draws (U+276F) and the one Codex draws (U+203A).</summary>
    private static readonly string Markers = new string(new[] { (char)0x276F, (char)0x203A });

    // THE SELECTED OPTION OF A LIVE PICKER: a marker, an ORDINARY space, a number and a dot. The composer draws the
    // marker followed by a NO-BREAK space (U+00A0), so a reply typed or suggested in the input box - even one that
    // starts "2." - never matches. That no-break space is the whole difference between the two on screen.
    private static readonly Regex SelectedRow =
        new(@"^\s*[" + Markers + @"] (\d+)\.\s+\S", RegexOptions.CultureInvariant);

    private static readonly Regex OptionRow =
        new(@"^\s*(?:[" + Markers + @"] )?\s*(\d+)[.)]\s+\S", RegexOptions.CultureInvariant);

    /// <summary>Read one screen.</summary>
    /// <param name="rows">The screen grid, top to bottom. Null or empty means the screen was not read.</param>
    /// <param name="agentKind">The agent the session runs, as the package names it; null when unknown.</param>
    public static PickerReading Read(IReadOnlyList<string>? rows, string? agentKind)
    {
        var agent = string.IsNullOrWhiteSpace(agentKind) ? "unknown" : agentKind.Trim();
        var calibrated = CalibratedAgents.Contains(agent, StringComparer.Ordinal);

        // AN UNREAD SCREEN IS NOT A SCREEN WITH NO PICKER. The two never share an answer: a real picker whose grid did
        // not arrive must keep whatever the judge offered for it.
        if (rows is null || rows.Count == 0)
            return new PickerReading(false, false, Array.Empty<string>(), agent, calibrated);

        var trimmed = new List<string>(rows.Count);
        foreach (var row in rows) trimmed.Add((row ?? "").TrimEnd());
        while (trimmed.Count > 0 && trimmed[^1].Trim().Length == 0) trimmed.RemoveAt(trimmed.Count - 1);
        if (trimmed.Count == 0)
            return new PickerReading(false, false, Array.Empty<string>(), agent, calibrated);

        var text = string.Join("\n", trimmed.Skip(Math.Max(0, trimmed.Count - BottomRows)));
        var found = new List<string>();
        foreach (var (name, pattern) in Signs)
            if (pattern.IsMatch(text)) found.Add(name);
        if (HasSelectedOptionRow(trimmed)) found.Add("selected-option-row");

        return new PickerReading(found.Count > 0, true, found, agent, calibrated);
    }

    /// <summary>
    /// A live picker's selected row, near the bottom. INDENTED, it stands alone: a picker draws its options indented
    /// under its question, and a repaint tear can overwrite every option but the selected one - the rate-limit menu and
    /// the /model selector both reach the screen that way. At column 0 it needs a numbered neighbour, because an echoed
    /// earlier message in the history is also drawn at column 0 with the marker and an ordinary space, and can start
    /// with a number.
    /// </summary>
    private static bool HasSelectedOptionRow(IReadOnlyList<string> rows)
    {
        var start = Math.Max(0, rows.Count - OptionSignRows);
        for (var i = start; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!SelectedRow.IsMatch(row)) continue;
            if (row.Length > 0 && row[0] == ' ') return true;

            var low = Math.Max(start, i - OptionRowsNear);
            var high = Math.Min(rows.Count - 1, i + OptionRowsNear);
            for (var j = low; j <= high; j++)
                if (j != i && OptionRow.IsMatch(rows[j])) return true;
        }
        return false;
    }
}

/// <summary>What <see cref="PickerOnScreen.Read"/> found on one screen.</summary>
/// <param name="Drawn">True when a picker is drawn. False both when there is none and when the screen was not read -
/// <paramref name="ScreenRead"/> tells the two apart, and a caller must never correct a menu on an unread screen.</param>
/// <param name="ScreenRead">False when there were no rows to read.</param>
/// <param name="Signs">The names of the signs that fired, for the log.</param>
/// <param name="Agent">The agent, as named in the package, or "unknown".</param>
/// <param name="Calibrated">True when the sign list was checked against this agent's screens. False means the answer
/// is the Claude Code list applied unverified, by the owner's ruling, and is logged so its misses can be found.</param>
public sealed record PickerReading(bool Drawn, bool ScreenRead, IReadOnlyList<string> Signs, string Agent, bool Calibrated)
{
    /// <summary>One line for the log: the decision, the evidence, the agent and whether the list was checked against it.</summary>
    public string Describe() =>
        !ScreenRead
            ? $"screen not read (agent={Agent})"
            : $"picker {(Drawn ? "DRAWN" : "not drawn")} signs=[{string.Join(",", Signs)}] agent={Agent} "
              + (Calibrated ? "calibrated" : "UNCALIBRATED - Claude Code list applied unverified");
}
