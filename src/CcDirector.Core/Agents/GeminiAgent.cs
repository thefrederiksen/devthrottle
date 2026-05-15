using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>
/// Google Gemini CLI (<c>gemini.cmd</c> from <c>@google/gemini-cli</c>).
/// V1 behavior: spawn gemini with optional user args. No session-id preassignment, no
/// Director-initiated resume, no Studio mode. Gemini CLI manages its own session state;
/// Director treats each launch as a fresh ephemeral session for now.
/// </summary>
public sealed class GeminiAgent : IAgent
{
    private readonly AgentOptions _options;

    public GeminiAgent(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public AgentKind Kind => AgentKind.Gemini;

    public string ExecutablePath => _options.GeminiPath;

    public bool SupportsPreassignedSessionId => false;

    public bool SupportsStudioMode => false;

    public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode)
    {
        FileLog.Write($"[GeminiAgent] BuildLaunchSpec: userArgs={userArgs ?? "(null)"}, resume={resumeSessionId ?? "(null)"}, studio={studioMode}");

        if (!string.IsNullOrEmpty(resumeSessionId))
            FileLog.Write($"[GeminiAgent] BuildLaunchSpec: ignoring resume={resumeSessionId} (Gemini v1 does not support Director-initiated resume)");
        if (studioMode)
            FileLog.Write("[GeminiAgent] BuildLaunchSpec: ignoring studioMode (Gemini v1 does not support Studio stream-json wrapper)");

        var args = (userArgs ?? string.Empty).Trim();
        FileLog.Write($"[GeminiAgent] BuildLaunchSpec result: argsLen={args.Length}");
        return new AgentLaunchSpec(args, PreassignedSessionId: null);
    }
}
