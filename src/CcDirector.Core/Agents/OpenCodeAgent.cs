using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>
/// opencode CLI (the <c>opencode</c> binary from opencode.ai).
/// V1 behavior: spawn opencode with optional user args; bare launch opens opencode's
/// interactive TUI in the working directory. No session-id preassignment, no
/// Director-initiated resume, no Studio mode. opencode manages its own session state;
/// Director treats each launch as a fresh ephemeral session for now.
/// </summary>
public sealed class OpenCodeAgent : IAgent
{
    private readonly AgentOptions _options;

    public OpenCodeAgent(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public AgentKind Kind => AgentKind.OpenCode;

    public string ExecutablePath => _options.OpenCodePath;

    public bool SupportsPreassignedSessionId => false;

    public bool SupportsStudioMode => false;

    public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode)
    {
        FileLog.Write($"[OpenCodeAgent] BuildLaunchSpec: userArgs={userArgs ?? "(null)"}, resume={resumeSessionId ?? "(null)"}, studio={studioMode}");

        if (!string.IsNullOrEmpty(resumeSessionId))
            FileLog.Write($"[OpenCodeAgent] BuildLaunchSpec: ignoring resume={resumeSessionId} (opencode v1 does not support Director-initiated resume; use opencode's own session UI)");
        if (studioMode)
            FileLog.Write("[OpenCodeAgent] BuildLaunchSpec: ignoring studioMode (opencode v1 does not support Studio stream-json wrapper)");

        var args = (userArgs ?? string.Empty).Trim();
        FileLog.Write($"[OpenCodeAgent] BuildLaunchSpec result: argsLen={args.Length}");
        return new AgentLaunchSpec(args, PreassignedSessionId: null);
    }
}
