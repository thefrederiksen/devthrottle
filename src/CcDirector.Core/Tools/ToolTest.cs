namespace CcDirector.Core.Tools;

/// <summary>
/// The kind of health check a <see cref="ToolTest"/> performs. Every tool gets an
/// <see cref="OnPath"/> and a <see cref="Version"/> check (universal, no per-tool data);
/// a <see cref="Smoke"/> check exists only when the manifest declares a safe, read-only
/// command for that tool.
/// </summary>
public enum ToolTestKind
{
    /// <summary>The tool's binary exists in the bin directory. No process is launched.</summary>
    OnPath,

    /// <summary>Run <c>&lt;tool&gt; --version</c> and confirm it responds with exit code 0.</summary>
    Version,

    /// <summary>Run the manifest-declared read-only smoke command and confirm exit code 0.</summary>
    Smoke,
}

/// <summary>
/// One declared health check for a tool. Immutable description; the runner produces a
/// <see cref="ToolTestResult"/> when it executes the check.
/// </summary>
public sealed class ToolTest
{
    public ToolTest(ToolTestKind kind, IReadOnlyList<string> args, string? expectContains)
    {
        Kind = kind;
        Args = args ?? throw new ArgumentNullException(nameof(args));
        ExpectContains = expectContains;
    }

    /// <summary>Which kind of check this is.</summary>
    public ToolTestKind Kind { get; }

    /// <summary>
    /// The argument vector passed to the tool binary. Empty for <see cref="ToolTestKind.OnPath"/>
    /// (no process). The runner only ever executes the exact args declared here - it never
    /// builds a command from external input.
    /// </summary>
    public IReadOnlyList<string> Args { get; }

    /// <summary>
    /// Optional substring that must appear in stdout for the check to pass. Null means
    /// "exit code 0 is enough".
    /// </summary>
    public string? ExpectContains { get; }

    /// <summary>A human-readable label for the check, shown in the Tests tab.</summary>
    public string Label => Kind switch
    {
        ToolTestKind.OnPath => "binary on PATH",
        ToolTestKind.Version => "--version responds",
        ToolTestKind.Smoke => $"smoke: {string.Join(' ', Args)}",
        _ => Kind.ToString(),
    };
}
