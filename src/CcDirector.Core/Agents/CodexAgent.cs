using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
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
    private readonly IAgentDriver _driver;

    public CodexAgent(AgentOptions options, IAgentDriver? driver = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _driver = driver ?? AgentDrivers.For(AgentKind.Codex);
    }

    public AgentKind Kind => AgentKind.Codex;

    public string ExecutablePath => _options.CodexPath;

    public bool SupportsPreassignedSessionId => false;

    public bool SupportsStudioMode => false;

    public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode)
    {
        FileLog.Write($"[CodexAgent] BuildLaunchSpec: userArgs={userArgs ?? "(null)"}, resume={resumeSessionId ?? "(null)"}, studio={studioMode}");

        if (studioMode)
            FileLog.Write("[CodexAgent] BuildLaunchSpec: ignoring studioMode (Codex v1 does not support Studio stream-json wrapper)");

        var spec = _driver.BuildLaunchSpec(userArgs, resumeSessionId);
        FileLog.Write($"[CodexAgent] BuildLaunchSpec result: argsLen={spec.Arguments.Length}");
        return spec;
    }
}
