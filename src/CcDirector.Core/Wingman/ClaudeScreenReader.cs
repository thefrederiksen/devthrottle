using System.Text.RegularExpressions;

namespace CcDirector.Core.Wingman;

/// <summary>
/// What a resolved Claude Code screen tells us the session is doing, from POSITIVE
/// on-screen evidence (never inferred from silence).
/// </summary>
public enum ScreenParkState
{
    /// <summary>An active-work indicator is on screen (the "esc to interrupt" footer).
    /// The turn is NOT over.</summary>
    Working,

    /// <summary>The agent is parked at the idle input prompt, waiting for the user's next
    /// instruction (the "? for shortcuts" hint is up and nothing is working). The turn is
    /// over. Covers a clean finish, a finish-with-a-question, and a post-cancel prompt.</summary>
    ParkedForInput,

    /// <summary>The agent is parked on a real bordered numbered-choice confirmation (or a
    /// [y/n] gate) and cannot continue until the user picks. The turn is over and the user
    /// is needed.</summary>
    ParkedForPermission,

    /// <summary>The screen does not positively show any of the above (garbled, empty, or a
    /// transitional frame). Callers must treat this as "not finished" so a turn-end is
    /// never fabricated from absence of evidence.</summary>
    Unknown,
}

/// <summary>
/// Deterministic, no-LLM reader of a resolved Claude Code screen. This is the
/// terminal-confirmation half of finish detection (the Stop hook is the other half - see
/// docs/wingman/REDESIGN.md section 2). It replaces the per-turn LLM classify for the job
/// of "is the agent working, or parked waiting on the user?", deciding ONLY from positive
/// evidence so it can never invent a turn-end from silence.
///
/// The ordering matters and resolves the one genuinely ambiguous case: a plan's numbered
/// list ("1. ... 2. ... 3. ...") looks like a permission box's numbered choices. We check
/// the idle hint FIRST - plan mode and ordinary idle always show "? for shortcuts"; a real
/// permission box never does - so a plan list is correctly read as ParkedForInput, not a
/// permission gate.
/// </summary>
public static class ClaudeScreenReader
{
    /// <summary>The footer Claude Code shows while a turn or tool is running. Reliable,
    /// ASCII, specific to Claude Code. Its presence is positive evidence of working.</summary>
    internal const string WorkingFooter = "esc to interrupt";

    /// <summary>An older hint shown beneath the input box when waiting for input. Some
    /// Claude Code versions show this; v2.1.150 does NOT (it shows only the mode footer).
    /// Kept as a cross-version idle marker.</summary>
    internal const string IdleHint = "? for shortcuts";

    /// <summary>The PERSISTENT mode footer Claude Code always shows ("bypass permissions on
    /// (shift+tab to cycle)" / "accept edits on ..." / "plan mode on ..."). It is on screen
    /// whether working or idle, so it is NOT a discriminator by itself - but when it is the
    /// bottom-most footer with NO <see cref="WorkingFooter"/> after it, the agent is parked at
    /// the prompt. Captured real from Claude Code v2.1.150, where it is the only idle marker
    /// (there is no "? for shortcuts").</summary>
    internal const string ModeFooter = "shift+tab to cycle";

    // A numbered choice with the selector arrow in front (e.g. "> 1. Yes" or "❯ 1. Yes").
    // The arrow is what distinguishes a permission box's options from a plan's prose-numbered
    // list (whose "1." items have no leading arrow), so a plan is never misread as a gate.
    private static readonly Regex SelectedChoice = new(@"^\s*[>❯]\s*\d+\.\s+\S", RegexOptions.Compiled);

    // A [y/n] style inline gate.
    private static readonly Regex YesNoGate = new(@"\[y/n\]|\(y/n\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True for the two states in which the turn is over and the agent is waiting
    /// on the user (so the Wingman should be woken).</summary>
    public static bool IsParked(ScreenParkState state)
        => state is ScreenParkState.ParkedForInput or ScreenParkState.ParkedForPermission;

    /// <summary>
    /// Classify the resolved on-screen grid. <paramref name="rows"/> are ANSI-stripped
    /// screen lines (e.g. from Session.SnapshotScreenRows). Null/empty -> Unknown.
    /// </summary>
    public static ScreenParkState Read(IReadOnlyList<string>? rows)
    {
        if (rows is null || rows.Count == 0) return ScreenParkState.Unknown;

        // Track the LOWEST (bottom-most) row at which each footer marker appears. The
        // working footer and the idle hint are both bottom-of-screen status lines, so the
        // one nearer the bottom reflects the CURRENT state - this is what lets us ignore a
        // stale "esc to interrupt" left in scrollback above a now-idle prompt (the
        // post-cancel case) and, symmetrically, a stale "? for shortcuts" above a spinner.
        var workingIdx = -1;
        var idleIdx = -1;
        var hasPermissionGate = false;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i] ?? "";
            if (row.IndexOf(WorkingFooter, StringComparison.OrdinalIgnoreCase) >= 0) workingIdx = i;
            if (row.IndexOf(IdleHint, StringComparison.OrdinalIgnoreCase) >= 0
                || row.IndexOf(ModeFooter, StringComparison.OrdinalIgnoreCase) >= 0) idleIdx = i;
            if (SelectedChoice.IsMatch(row) || YesNoGate.IsMatch(row)) hasPermissionGate = true;
        }

        // 1. Working: the working footer ("esc to interrupt") is the bottom-most status line
        //    (>= covers the v2.1.150 case where the working footer and the mode footer are the
        //    SAME line). Beats a stale working footer left higher in scrollback when an idle
        //    footer appears below it (post-cancel).
        if (workingIdx >= 0 && workingIdx >= idleIdx) return ScreenParkState.Working;

        // 2. Permission gate: a real numbered-choice box with the selector arrow ("> 1." /
        //    "❯ 1.") or a [y/n] prompt. Checked before the generic parked-for-input because the
        //    mode footer is present in a permission box too. The arrow requirement is what keeps
        //    a plan's prose-numbered "1. 2. 3." list from being misread as a gate.
        if (hasPermissionGate) return ScreenParkState.ParkedForPermission;

        // 3. Parked for input: an idle footer (the mode footer "shift+tab to cycle", or the
        //    older "? for shortcuts") is present with no working footer after it. The agent is
        //    waiting at the prompt - the turn is over.
        if (idleIdx >= 0) return ScreenParkState.ParkedForInput;

        // 4. No positive evidence either way: do NOT claim finished.
        return ScreenParkState.Unknown;
    }
}
