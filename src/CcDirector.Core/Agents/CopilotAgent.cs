using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>
/// GitHub Copilot CLI agent (the <c>copilot</c> binary from <c>@github/copilot</c>, issue #625).
///
/// Behavior (verified live against Copilot CLI v1.0.63):
/// <list type="bullet">
/// <item>Copilot preassigns the session UUID via <c>--session-id &lt;uuid&gt;</c> (like Claude's
/// <c>--session-id</c>), so <see cref="SupportsPreassignedSessionId"/> is true and the Director
/// mints the id for a new session.</item>
/// <item>Resume by id/prefix/name is <c>--resume &lt;id&gt;</c>; <c>--continue</c> resumes the most
/// recent session. The Director-initiated resume path uses <c>--resume</c> with the supplied id.</item>
/// <item>The <c>Automatic (yolo)</c> preset contributes <c>--allow-all</c> (carried in via the
/// preset args), so no permission-bypass flag is added here; it flows through <paramref name="userArgs"/>.</item>
/// </list>
///
/// Studio mode (the Director's stream-json card UI) is not wired for Copilot in this issue
/// (<see cref="SupportsStudioMode"/> is false); the phase-2 <c>CopilotDriver</c> owns JSONL
/// transcript parsing instead.
/// </summary>
public sealed class CopilotAgent : IAgent
{
    private readonly AgentOptions _options;

    public CopilotAgent(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public AgentKind Kind => AgentKind.Copilot;

    public string ExecutablePath => _options.CopilotPath;

    /// <summary>
    /// True: Copilot accepts a caller-chosen session UUID at launch via <c>--session-id</c>
    /// (verified live - the preassigned id appears in the <c>--output-format json</c> stream).
    /// </summary>
    public bool SupportsPreassignedSessionId => true;

    /// <summary>
    /// False: the Director's stream-json Studio wrapper is not wired for Copilot in issue #625.
    /// The phase-2 <c>CopilotDriver</c> parses Copilot's own <c>--output-format json</c> JSONL.
    /// </summary>
    public bool SupportsStudioMode => false;

    public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode)
    {
        FileLog.Write($"[CopilotAgent] BuildLaunchSpec: userArgs={userArgs ?? "(null)"}, resume={resumeSessionId ?? "(null)"}, studio={studioMode}");

        var args = (userArgs ?? string.Empty).Trim();
        string? preassignedSessionId = null;

        if (!string.IsNullOrEmpty(resumeSessionId))
        {
            // Copilot resumes a prior session by id/prefix/name with --resume.
            args = $"{args} --resume {resumeSessionId}".Trim();
        }
        else
        {
            // New session: mint the UUID and pass it in so the transcript id is known from birth.
            preassignedSessionId = Guid.NewGuid().ToString();
            args = $"{args} --session-id {preassignedSessionId}".Trim();
        }

        if (studioMode)
            FileLog.Write("[CopilotAgent] BuildLaunchSpec: ignoring studioMode (Copilot uses its own --output-format json via CopilotDriver, not the Studio wrapper)");

        FileLog.Write($"[CopilotAgent] BuildLaunchSpec result: argsLen={args.Length}, preassignedId={preassignedSessionId ?? "(null)"}");
        return new AgentLaunchSpec(args, preassignedSessionId);
    }
}
