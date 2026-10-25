using CcDirector.Core.Agents;

namespace CcDirector.Core.Drivers;

/// <summary>
/// THE KEYSTROKES THAT EMPTY EACH AGENT'S COMPOSER - MEASURED, NOT ASSUMED (issue #3290).
///
/// The submit code cleared a composer with one Escape. The text-delivery qualification rig
/// (src/CcDirector.DeliveryQualification, `--keys`) typed a draft into real agents on 24 September 2026, pressed each
/// candidate key, and read the composer back through <see cref="DoorbellSafety"/>:
///
/// | key                 | Claude Code 2.1.281       | Codex 0.155.1 | Pi 0.85.1 |
/// |---------------------|---------------------------|---------------|-----------|
/// | Escape              | NOT cleared               | NOT cleared   | cleared   |
/// | Escape twice        | cleared                   | NOT cleared   | cleared   |
/// | Ctrl+U              | clears one line only      | cleared       | cleared   |
/// | Ctrl+C              | cleared, then "again to exit" | cleared   | cleared   |
/// | End + Backspaces    | cleared                   | partly        | cleared   |
///
/// Measured again on Codex 0.163.0 (23 October 2026, issue #4380, `--keys codex`): when Codex refuses a command it keeps
/// the line in its composer with the cursor at its START, and Ctrl+U - delete back to the start of the line - deletes
/// nothing there. So one refused command made every later send refuse, "the composer still holds text after it was
/// cleared". End then Ctrl+U emptied the refused line and a typed line, and did nothing to an empty composer. Neither
/// key emptied a two-line paste on 0.163.0: both left its first line.
///
/// Escape is also unsafe on its own terms: when the agent reads input slowly, the Escape and the next letter arrive in
/// one read and are taken as a single Alt keystroke, so the Escape vanishes and the letter is eaten (seen on Pi: a
/// retyped "Token" arrived as "oken").
///
/// So each agent gets the key that was measured to work and that is harmless on an empty composer. An agent that has
/// not been measured returns null, and callers keep their old behaviour for it until it is.
/// </summary>
public static class ComposerClearKeys
{
    private const byte CtrlE = 0x05;
    private const byte CtrlU = 0x15;
    private const byte Backspace = 0x7F;

    /// <summary>The keys that empty this agent's composer of up to <paramref name="retainedLength"/> characters, or
    /// null when none has been measured.</summary>
    public static byte[]? For(AgentKind agent, int retainedLength = 0) => agent switch
    {
        // Ctrl+U clears only the cursor's line in Claude Code, and a double Escape on an EMPTY composer opens its
        // rewind menu. End then Backspaces cleared both a typed and a pasted two-line draft and does nothing to an
        // empty one. A typed text is at most one line (longer or multi-line text is pasted, and a paste is removed
        // by one Backspace), so the count covers it with room to spare. A lead typed in front of a paste (issue #3967)
        // is counted with it: the send marks the composer with the lead and the paste together.
        AgentKind.ClaudeCode => [CtrlE, .. Enumerable.Repeat(Backspace, Math.Clamp(retainedLength + 16, 64, 2048))],
        // End first: Ctrl+U deletes only what is before the cursor, and a command Codex refused leaves it at the start.
        AgentKind.Codex => [CtrlE, CtrlU],
        AgentKind.Pi => [CtrlU],
        _ => null,
    };
}
