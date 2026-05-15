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
    Gemini = 3
}
