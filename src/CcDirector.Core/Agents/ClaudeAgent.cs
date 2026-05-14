using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>
/// Default agent: Claude Code (<c>claude.exe</c>).
/// Preserves the exact argument-building behavior that <c>SessionManager.CreateSession</c>
/// used before the IAgent refactor: a Director-generated UUID is passed via
/// <c>--session-id</c> for new sessions, or <c>--resume</c> for resumes; Studio mode
/// prepends <c>-p --output-format stream-json --verbose</c>.
/// </summary>
public sealed class ClaudeAgent : IAgent
{
    private readonly AgentOptions _options;

    public ClaudeAgent(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public AgentKind Kind => AgentKind.ClaudeCode;

    public string ExecutablePath => _options.ClaudePath;

    public bool SupportsPreassignedSessionId => true;

    public bool SupportsStudioMode => true;

    public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode)
    {
        FileLog.Write($"[ClaudeAgent] BuildLaunchSpec: userArgs={userArgs ?? "(null)"}, resume={resumeSessionId ?? "(null)"}, studio={studioMode}");

        string args = userArgs ?? _options.DefaultClaudeArgs ?? string.Empty;
        string? preassignedSessionId = null;

        if (!string.IsNullOrEmpty(resumeSessionId))
        {
            args = $"{args} --resume {resumeSessionId}".Trim();
        }
        else
        {
            preassignedSessionId = Guid.NewGuid().ToString();
            args = $"{args} --session-id {preassignedSessionId}".Trim();
        }

        if (studioMode)
        {
            args = $"-p --output-format stream-json --verbose {args}".Trim();
        }

        FileLog.Write($"[ClaudeAgent] BuildLaunchSpec result: argsLen={args.Length}, preassignedId={preassignedSessionId ?? "(null)"}");
        return new AgentLaunchSpec(args, preassignedSessionId);
    }
}
