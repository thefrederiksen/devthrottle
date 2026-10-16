namespace CcDirector.Core.ApiMode;

/// <summary>
/// How to start an API-mode agent process: one long-lived child per session, spoken to over its standard
/// input and output. Agent-neutral - the agent's wrapper fills it in.
///
/// The environment is the caller's to decide. The process starts from the Director's own environment with
/// the variables no session may inherit already removed (<see cref="Sessions.InheritedSessionEnvironment"/>),
/// then <see cref="RemoveVariables"/> and <see cref="RemoveVariablePrefixes"/> are removed, then
/// <see cref="SetVariables"/> are applied. Removal exists because an agent can need a variable ABSENT, not
/// empty: Pi is started with no <c>CC_*</c> variable at all.
/// </summary>
public sealed record ApiModeProcessStartInfo
{
    /// <summary>The executable: a bare name looked up on the search path, or a path to the file.</summary>
    public required string Executable { get; init; }

    /// <summary>The arguments, one element each. Never joined into one command string.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The folder the process starts in. Must exist.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Variables set for the process, applied after every removal.</summary>
    public IReadOnlyDictionary<string, string> SetVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>Variable names removed from the inherited environment.</summary>
    public IReadOnlyList<string> RemoveVariables { get; init; } = [];

    /// <summary>Every inherited variable whose name starts with one of these is removed (for example <c>CC_</c>).</summary>
    public IReadOnlyList<string> RemoveVariablePrefixes { get; init; } = [];

    /// <summary>A one-line description for logs: the executable, its arguments and its folder. Never the environment.</summary>
    public string Describe() =>
        $"executable={Executable}, arguments=[{string.Join(" ", Arguments)}], workingDirectory={WorkingDirectory}";
}
