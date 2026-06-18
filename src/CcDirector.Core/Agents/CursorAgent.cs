using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>
/// Cursor CLI agent (the <c>cursor-agent</c> binary from cursor.com, marketed as "Agent").
/// Phase 1+2 behavior (issue #517): spawn cursor-agent with the user/preset args and,
/// when requested, resume an existing Cursor chat.
///
/// Unlike Claude, Cursor mints its own session id and has no preassign flag
/// (<see cref="SupportsPreassignedSessionId"/> is false; assumption A3): the driver
/// captures the id from the stream-json <c>init</c> event instead of passing one in.
/// Resume is supported via <c>--resume="&lt;chat-id&gt;"</c>. Studio (stream-json) mode is
/// supported and adds Cursor's print/stream-json flags so the Director can parse the
/// agent's events into cards.
/// </summary>
public sealed class CursorAgent : IAgent
{
    /// <summary>The Cursor flags that switch the agent into machine-readable stream-json output.</summary>
    internal const string StreamJsonArgs = "-p --output-format stream-json";

    private readonly AgentOptions _options;

    public CursorAgent(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public AgentKind Kind => AgentKind.Cursor;

    public string ExecutablePath => _options.CursorPath;

    /// <summary>
    /// False: Cursor assigns its own session id and emits it in the stream-json
    /// <c>init</c> event. There is no <c>--session-id</c> preassign flag (assumption A3).
    /// </summary>
    public bool SupportsPreassignedSessionId => false;

    /// <summary>True: Cursor supports a stream-json print mode the Director can parse.</summary>
    public bool SupportsStudioMode => true;

    public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode)
    {
        FileLog.Write($"[CursorAgent] BuildLaunchSpec: userArgs={userArgs ?? "(null)"}, resume={resumeSessionId ?? "(null)"}, studio={studioMode}");

        var args = (userArgs ?? string.Empty).Trim();

        if (!string.IsNullOrEmpty(resumeSessionId))
        {
            // Cursor resumes a prior chat by id; it never preassigns one, so this is the
            // only id that flows in on the command line.
            args = $"{args} --resume=\"{resumeSessionId}\"".Trim();
        }

        if (studioMode)
            args = $"{StreamJsonArgs} {args}".Trim();

        FileLog.Write($"[CursorAgent] BuildLaunchSpec result: argsLen={args.Length} (no preassigned id - Cursor mints its own)");
        return new AgentLaunchSpec(args, PreassignedSessionId: null);
    }
}
