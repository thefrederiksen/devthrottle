using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>
/// xAI Grok CLI (<c>grok</c> binary installed to <c>~/.grok/bin/</c>).
/// V1 behavior: spawn grok with optional user args. No session-id preassignment, no
/// Director-initiated resume, no Studio mode. Grok CLI manages its own session state;
/// Director treats each launch as a fresh ephemeral session for now.
/// </summary>
public sealed class GrokAgent : IAgent
{
    private readonly AgentOptions _options;

    public GrokAgent(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public AgentKind Kind => AgentKind.Grok;

    public string ExecutablePath => _options.GrokPath;

    public bool SupportsPreassignedSessionId => false;

    public bool SupportsStudioMode => false;

    public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode)
    {
        FileLog.Write($"[GrokAgent] BuildLaunchSpec: userArgs={userArgs ?? "(null)"}, resume={resumeSessionId ?? "(null)"}, studio={studioMode}");

        if (!string.IsNullOrEmpty(resumeSessionId))
            FileLog.Write($"[GrokAgent] BuildLaunchSpec: ignoring resume={resumeSessionId} (Grok v1 does not support Director-initiated resume)");
        if (studioMode)
            FileLog.Write("[GrokAgent] BuildLaunchSpec: ignoring studioMode (Grok v1 does not support Studio stream-json wrapper)");

        var args = (userArgs ?? string.Empty).Trim();
        FileLog.Write($"[GrokAgent] BuildLaunchSpec result: argsLen={args.Length}");
        return new AgentLaunchSpec(args, PreassignedSessionId: null);
    }
}
