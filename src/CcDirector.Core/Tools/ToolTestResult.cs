namespace CcDirector.Core.Tools;

/// <summary>
/// The outcome of running a single <see cref="ToolTest"/>. Carries the raw process output so a
/// FAIL is debuggable in the UI's Logs tab - no swallowed errors, no derived guesses.
/// </summary>
public sealed class ToolTestResult
{
    public ToolTestResult(ToolTestKind kind, string label, bool passed, long durationMs, int? exitCode, string stdout, string stderr, string message)
    {
        Kind = kind;
        Label = label;
        Passed = passed;
        DurationMs = durationMs;
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
        Message = message;
    }

    public ToolTestKind Kind { get; }
    public string Label { get; }
    public bool Passed { get; }
    public long DurationMs { get; }

    /// <summary>The process exit code, or null when no process was launched (OnPath check).</summary>
    public int? ExitCode { get; }

    public string Stdout { get; }
    public string Stderr { get; }

    /// <summary>A short one-line explanation of the result (e.g. the version string, or why it failed).</summary>
    public string Message { get; }
}
