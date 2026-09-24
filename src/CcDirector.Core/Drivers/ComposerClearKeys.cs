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
        // by one Backspace), so the count covers it with room to spare.
        AgentKind.ClaudeCode => [CtrlE, .. Enumerable.Repeat(Backspace, Math.Clamp(retainedLength + 16, 64, 2048))],
        AgentKind.Codex => [CtrlU],
        AgentKind.Pi => [CtrlU],
        _ => null,
    };
}
