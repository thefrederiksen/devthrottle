using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>
/// Pi coding agent (<c>pi.cmd</c> from <c>@earendil-works/pi-coding-agent</c>).
/// Every launch passes <c>--session-id &lt;id&gt;</c>: pi "uses the exact project session id, creating it
/// if missing" (pi 0.80.10), and names the session file after it
/// (<c>~/.pi/agent/sessions/&lt;cwd-slug&gt;/&lt;timestamp&gt;_&lt;id&gt;.jsonl</c>). So the Director knows a Pi
/// session's transcript from birth and never has to guess it from the newest file in the repo - which
/// guessed wrong (issue #2670). A new session gets a Director-minted id; a reopened one gets the id it
/// had, which is also how pi resumes it. No Studio mode.
/// <para>
/// Every launch also passes <c>--approve</c> (issue #4010). Since pi 0.79.0 a folder holding project
/// resources (<c>.agents/skills</c>, <c>.pi/settings.json</c>, ...) makes pi stop on a full-screen "Trust
/// project folder?" question before the composer exists, so the Director's first prompt never lands.
/// The person picked the repository when they opened the session; <c>--approve</c> trusts it for this
/// process only and saves nothing. It goes FIRST: pi keeps the last trust flag it reads, so a
/// <c>--no-approve</c> in the session's own arguments - however it is quoted or spaced - still wins.
/// </para>
/// </summary>
public sealed class PiAgent : IAgent
{
    public const string ApproveFlag = "--approve";

    private readonly AgentOptions _options;

    public PiAgent(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public AgentKind Kind => AgentKind.Pi;

    public string ExecutablePath => _options.PiPath;

    public bool SupportsPreassignedSessionId => true;

    public bool SupportsStudioMode => false;

    public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode)
    {
        FileLog.Write($"[PiAgent] BuildLaunchSpec: userArgs={userArgs ?? "(null)"}, resume={resumeSessionId ?? "(null)"}, studio={studioMode}");

        if (studioMode)
            FileLog.Write("[PiAgent] BuildLaunchSpec: ignoring studioMode (Pi does not support the Studio stream-json wrapper)");

        // One flag serves both cases. A fresh id makes pi create that session; an existing id makes pi
        // load it (verified against pi 0.80.10: a second launch with the same id recalled the first
        // launch's conversation). Either way the file on disk is named by the id.
        var sessionId = string.IsNullOrEmpty(resumeSessionId) ? Guid.NewGuid().ToString() : resumeSessionId;
        // No parsing of the user's arguments to look for a trust flag: --approve leads and pi's last trust
        // flag wins, so whatever the user wrote overrides it without the Director having to tokenise it.
        var trimmedUserArgs = (userArgs ?? string.Empty).Trim();
        var args = trimmedUserArgs.Length == 0
            ? $"{ApproveFlag} --session-id {sessionId}"
            : $"{ApproveFlag} {trimmedUserArgs} --session-id {sessionId}";

        FileLog.Write($"[PiAgent] BuildLaunchSpec result: argsLen={args.Length}, sessionId={sessionId}, resumed={!string.IsNullOrEmpty(resumeSessionId)}");
        return new AgentLaunchSpec(args, sessionId);
    }
}
