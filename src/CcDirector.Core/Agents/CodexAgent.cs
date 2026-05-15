using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>
/// OpenAI Codex CLI (<c>codex.cmd</c> from <c>@openai/codex</c>).
/// V1 behavior: spawn codex with optional user args. No session-id preassignment, no
/// Director-initiated resume, no Studio mode. Codex manages its own session state;
/// Director treats each launch as a fresh ephemeral session for now.
/// </summary>
public sealed class CodexAgent : IAgent
{
    private readonly AgentOptions _options;

    public CodexAgent(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public AgentKind Kind => AgentKind.Codex;

    public string ExecutablePath => _options.CodexPath;

    public bool SupportsPreassignedSessionId => false;

    public bool SupportsStudioMode => false;

    public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode)
    {
        FileLog.Write($"[CodexAgent] BuildLaunchSpec: userArgs={userArgs ?? "(null)"}, resume={resumeSessionId ?? "(null)"}, studio={studioMode}");

        if (!string.IsNullOrEmpty(resumeSessionId))
            FileLog.Write($"[CodexAgent] BuildLaunchSpec: ignoring resume={resumeSessionId} (Codex v1 does not support Director-initiated resume)");
        if (studioMode)
            FileLog.Write("[CodexAgent] BuildLaunchSpec: ignoring studioMode (Codex v1 does not support Studio stream-json wrapper)");

        var args = (userArgs ?? string.Empty).Trim();
        FileLog.Write($"[CodexAgent] BuildLaunchSpec result: argsLen={args.Length}");
        return new AgentLaunchSpec(args, PreassignedSessionId: null);
    }
}
