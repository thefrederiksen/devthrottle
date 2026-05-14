using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>
/// Pi coding agent (<c>pi.cmd</c> from <c>@earendil-works/pi-coding-agent</c>).
/// V1 behavior: spawn pi with optional user args. No session-id preassignment, no resume,
/// no Studio mode. Pi creates and manages its own session files under
/// <c>~/.pi/agent/sessions/</c>; Director treats each Pi session as a fresh ephemeral
/// launch for now. Resume-from-Director and crash recovery can be added in a follow-up
/// once the basic flow is proven.
/// </summary>
public sealed class PiAgent : IAgent
{
    private readonly AgentOptions _options;

    public PiAgent(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public AgentKind Kind => AgentKind.Pi;

    public string ExecutablePath => _options.PiPath;

    public bool SupportsPreassignedSessionId => false;

    public bool SupportsStudioMode => false;

    public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode)
    {
        FileLog.Write($"[PiAgent] BuildLaunchSpec: userArgs={userArgs ?? "(null)"}, resume={resumeSessionId ?? "(null)"}, studio={studioMode}");

        if (!string.IsNullOrEmpty(resumeSessionId))
            FileLog.Write($"[PiAgent] BuildLaunchSpec: ignoring resume={resumeSessionId} (Pi v1 does not support Director-initiated resume; use pi's own /resume inside the TUI)");
        if (studioMode)
            FileLog.Write("[PiAgent] BuildLaunchSpec: ignoring studioMode (Pi v1 does not support Studio stream-json wrapper)");

        // Pi v1: pass through user args verbatim. Pi has no equivalent of --session-id;
        // it generates its own session ID and writes to ~/.pi/agent/sessions/<cwd-slug>/<uuid>.jsonl.
        var args = (userArgs ?? string.Empty).Trim();
        FileLog.Write($"[PiAgent] BuildLaunchSpec result: argsLen={args.Length}");
        return new AgentLaunchSpec(args, PreassignedSessionId: null);
    }
}
