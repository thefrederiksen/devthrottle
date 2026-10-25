using CcDirector.Core.Agents;

namespace CcDirector.Core.Drivers;

/// <summary>
/// THE KEYSTROKE THAT PUTS A NEW LINE INTO EACH AGENT'S COMPOSER - MEASURED, NOT ASSUMED (issue #4379).
///
/// The desktop terminal sent a plain Enter for Shift+Enter, so a person who made a new line submitted the first line on
/// its own and the rest became a second prompt (the parity baseline, 23 October 2026: every multi-line text on the
/// desktop-newline route split, on all three agents). A terminal has no single Shift+Enter byte - what it sends depends
/// on the keyboard protocol the program asked for - so the qualification rig (`--newline-keys`) typed a first line,
/// pressed each candidate, typed a second line and pressed Enter, and read the agent's own records, on 23 October 2026:
///
/// | key                              | Claude Code 2.1.309 | Codex 0.163.0 | Pi 1.2.0     |
/// |----------------------------------|---------------------|---------------|--------------|
/// | Ctrl+J (LF)                      | new line            | new line      | new line     |
/// | Alt+Enter (ESC CR)               | new line            | new line      | SUBMITS      |
/// | kitty Shift+Enter (CSI 13;2u)    | new line            | new line      | new line     |
/// | xterm Shift+Enter (CSI 27;2;13~) | new line            | break LOST    | new line     |
/// | backslash, then Enter            | new line            | keeps the \   | new line     |
///
/// (Codex and Pi ask the terminal for the kitty keyboard protocol, <c>CSI &gt; 7 u</c>; Claude Code only asks whether it is
/// there. The Director's terminal does not speak that protocol, so a real terminal that does would send CSI 13;2u.)
///
/// Ctrl+J is the one key every agent takes, and it is ONE byte: Alt+Enter is two, and when an agent reads its input
/// slowly the Escape and the Enter can arrive apart - an Escape and then an Enter, which submits.
/// An agent that has not been measured returns null, and the terminal keeps sending what it always sent.
/// </summary>
public static class ComposerNewlineKey
{
    private static readonly byte[] CtrlJ = [0x0A];

    /// <summary>The keystroke that inserts a new line in this agent's composer, or null when none has been measured.</summary>
    public static byte[]? For(AgentKind agent) => agent switch
    {
        AgentKind.ClaudeCode or AgentKind.Codex or AgentKind.Pi => CtrlJ,
        _ => null,
    };
}
