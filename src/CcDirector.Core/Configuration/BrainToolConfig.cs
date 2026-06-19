using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Agents;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The agent tool the Gateway's warm brain - the fleet's wingman - runs as (issue #393).
/// Previously the brain was a hardcoded claude.exe; this makes the tool an explicit,
/// configured choice at the Gateway level alongside the model (<see cref="BrainModelConfig"/>).
///
/// Persisted in config.json as "brain_tool" (an <see cref="AgentKind"/> name, e.g.
/// "ClaudeCode"). Read once at Gateway start - the BrainSupervisor's driver is fixed when
/// the host is constructed, so a change applies on the next Gateway restart.
///
/// Only tools whose driver can HOST a brain are valid (the hosted-agent path requires a
/// preassigned session id and transcript reads - see <see cref="BrainHostableTools"/>).
/// The default is <see cref="Default"/> (Claude Code) so existing fleets are unchanged.
///
/// No-fallback rule: a key that is present but not a brain-hostable tool name THROWS with
/// the fix. The brain must never silently run as a tool nobody chose, or one that cannot
/// be hosted.
/// </summary>
public static class BrainToolConfig
{
    /// <summary>The default brain tool: Claude Code, the original hardcoded behavior.</summary>
    public const AgentKind Default = AgentKind.ClaudeCode;

    /// <summary>
    /// The agent tools that can run as the Gateway brain, in display order. A tool is
    /// brain-hostable only when its driver supports the hosted-agent contract: a
    /// preassigned session id (so the transcript path is known from birth) and transcript
    /// reads (the answer channel). Today only Claude Code's driver satisfies this; Pi's
    /// driver explicitly throws for executable resolution, launch specs, and transcript
    /// reads, so Pi cannot be hosted as a brain. This list is the single source of truth
    /// for the Cockpit's tool selector and for validation, and grows automatically when a
    /// new hostable driver lands.
    /// </summary>
    public static IReadOnlyList<AgentKind> BrainHostableTools { get; } = new[]
    {
        AgentKind.ClaudeCode,
    };

    /// <summary>True when the tool can run as the Gateway brain.</summary>
    public static bool IsHostable(AgentKind tool)
    {
        foreach (var t in BrainHostableTools)
            if (t == tool)
                return true;
        return false;
    }

    /// <summary>
    /// Resolve the brain tool: config.json "brain_tool" when set, else <see cref="Default"/>.
    ///
    /// As of issue #510 the wingman agent is chosen from the machine's registered agents (any
    /// <see cref="AgentKind"/>), not the Claude-only brain-hostable list, because the driver-level
    /// hostability work landed in issue #509 - so the value must be a valid agent-kind name, but it
    /// no longer has to be in <see cref="BrainHostableTools"/>. The no-fallback rule still holds: a
    /// key that is present but not a recognised, non-empty agent-kind name THROWS with the fix, so
    /// the brain never silently runs as a tool nobody chose.
    /// </summary>
    public static AgentKind Get()
    {
        var node = CcDirectorConfigService.ReadRaw()["brain_tool"];
        if (node is null)
            return Default;

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            var raw = v.GetValue<string>().Trim();
            if (raw.Length > 0 && Enum.TryParse<AgentKind>(raw, ignoreCase: true, out var tool))
                return tool;
        }

        throw new InvalidOperationException(
            "config.json key 'brain_tool' must be a recognised agent-kind name " +
            $"(e.g. \"{Default}\"). Fix the value or remove the key to use the default " +
            $"(\"{Default}\").");
    }
}
