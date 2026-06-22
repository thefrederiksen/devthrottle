using System.Text.Json.Serialization;

namespace CcDirector.Core.Agents;

/// <summary>
/// Identifies which agent CLI a session is running.
/// Persisted in sessions.json so restored sessions can be relaunched with the right binary.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentKind
{
    /// <summary>Claude Code (claude.exe). Default.</summary>
    ClaudeCode = 0,

    /// <summary>Pi coding agent (pi.cmd from @earendil-works/pi-coding-agent npm package).</summary>
    Pi = 1,

    /// <summary>OpenAI Codex CLI (codex.cmd from @openai/codex npm package).</summary>
    Codex = 2,

    /// <summary>Google Gemini CLI (gemini.cmd from @google/gemini-cli npm package).</summary>
    Gemini = 3,

    /// <summary>opencode CLI (the <c>opencode</c> binary from opencode.ai).</summary>
    OpenCode = 4,

    /// <summary>
    /// User-supplied arbitrary CLI (aider, plain pwsh, or any other command).
    /// The session runs in raw terminal mode; no Claude-specific interpretation
    /// (turn detection, wingman, transcript) is applied. The actual executable
    /// and arguments are carried on the session, not on this enum value.
    /// </summary>
    RawCli = 5,

    /// <summary>Cursor CLI agent (the <c>cursor-agent</c> binary from cursor.com, marketed as "Agent").</summary>
    Cursor = 6,

    /// <summary>xAI Grok CLI (the <c>grok</c> binary installed via <c>irm https://x.ai/cli/install.ps1 | iex</c>).</summary>
    Grok = 7,

    /// <summary>
    /// GitHub Copilot CLI (the <c>copilot</c> binary from <c>@github/copilot</c>). On Windows the
    /// npm global install drops <c>copilot</c>, <c>copilot.cmd</c>, and <c>copilot.ps1</c>; the
    /// launchable shim for a process spawner is <c>copilot.cmd</c> (issue #625).
    /// </summary>
    Copilot = 8
}
