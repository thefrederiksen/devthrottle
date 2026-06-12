using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>
/// Raw-CLI agent: runs an arbitrary user-supplied executable (aider, plain pwsh, etc.)
/// in raw terminal mode. No Claude-specific interpretation is applied: no session-id
/// preassignment, no resume, no Studio mode. The executable path and optional extra
/// arguments are supplied at construction time by the caller.
/// </summary>
/// <remarks>
/// Custody code (ConPty, buffers, keystrokes, git, cc-* tools) is fully agent-agnostic;
/// this agent just plugs an arbitrary CLI into the same terminal session lifecycle.
/// Claude-specific features (turn-detection heuristics, wingman briefing, transcript
/// linking) degrade gracefully: they simply never fire for a raw session (no
/// claude-output patterns to detect), so no errors or false NEEDS-YOU storms occur.
/// </remarks>
public sealed class RawCliAgent : IAgent
{
    private readonly string _executablePath;
    private readonly string? _extraArgs;

    /// <summary>
    /// Creates a raw-CLI agent that will launch <paramref name="executablePath"/> with
    /// optional <paramref name="extraArgs"/> appended.
    /// </summary>
    /// <param name="executablePath">
    /// Path or bare command name of the executable to launch (e.g. "pwsh", "aider",
    /// "C:\Tools\mytool.exe"). Resolved against PATH+PATHEXT by
    /// <see cref="ExecutableResolver"/> before spawning, exactly as the other agents.
    /// A path that cannot be resolved fails loudly at launch.
    /// </param>
    /// <param name="extraArgs">
    /// Optional extra arguments appended after <paramref name="executablePath"/>
    /// in the command line. May be null or empty.
    /// </param>
    public RawCliAgent(string executablePath, string? extraArgs = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("executablePath is required for a raw-CLI agent.", nameof(executablePath));

        _executablePath = executablePath.Trim();
        _extraArgs = extraArgs;
    }

    /// <inheritdoc />
    public AgentKind Kind => AgentKind.RawCli;

    /// <inheritdoc />
    public string ExecutablePath => _executablePath;

    /// <inheritdoc />
    /// <remarks>Raw-CLI agents have no concept of Director-assigned session IDs.</remarks>
    public bool SupportsPreassignedSessionId => false;

    /// <inheritdoc />
    /// <remarks>Raw-CLI agents do not support Studio mode (stream-json card UI).</remarks>
    public bool SupportsStudioMode => false;

    /// <summary>
    /// Passes through <paramref name="userArgs"/> verbatim, appended after any
    /// <see cref="_extraArgs"/> set at construction. Resume and Studio mode are
    /// silently ignored (raw CLIs have no equivalent).
    /// </summary>
    public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode)
    {
        FileLog.Write($"[RawCliAgent] BuildLaunchSpec: exe={_executablePath}, extraArgs={_extraArgs ?? "(null)"}, userArgs={userArgs ?? "(null)"}");

        if (!string.IsNullOrEmpty(resumeSessionId))
            FileLog.Write($"[RawCliAgent] BuildLaunchSpec: ignoring resume={resumeSessionId} (raw-CLI sessions do not support Director-initiated resume)");
        if (studioMode)
            FileLog.Write("[RawCliAgent] BuildLaunchSpec: ignoring studioMode (raw-CLI sessions do not support Studio stream-json wrapper)");

        // Combine construction-time extra args with caller-supplied user args.
        // Neither is required; produce a clean empty string rather than a trailing space.
        var parts = new[] { _extraArgs, userArgs }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim());
        var args = string.Join(" ", parts);

        FileLog.Write($"[RawCliAgent] BuildLaunchSpec result: argsLen={args.Length}");
        return new AgentLaunchSpec(args, PreassignedSessionId: null);
    }
}
