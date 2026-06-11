namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Request body for <c>POST /tools/run</c> (issue #328): invoke ONE catalog cc-* tool on the
/// Director that owns its resources. <see cref="Name"/> must be a bare catalog name from the
/// Director's embedded tool manifest - the endpoint rejects anything with a path shape and never
/// executes a binary the catalog did not resolve (no arbitrary paths, no shell passthrough).
/// </summary>
public sealed class ToolRunRequest
{
    /// <summary>Bare catalog tool name, e.g. "cc-vault". Required.</summary>
    public string Name { get; set; } = "";

    /// <summary>Arguments passed verbatim to the tool (argument list, never a shell string).</summary>
    public List<string> Args { get; init; } = new();

    /// <summary>
    /// Optional working directory for the invocation. Must exist on the Director's machine;
    /// defaults to the tool binary's directory (same as the tool health checks).
    /// </summary>
    public string? Cwd { get; set; }

    /// <summary>
    /// Optional wall-clock bound in seconds (1..3600). Default 120. On expiry the Director kills
    /// the whole process tree and reports a distinct timeout error in the exit chunk.
    /// </summary>
    public int? TimeoutS { get; set; }

    public const int DefaultTimeoutS = 120;
    public const int MinTimeoutS = 1;
    public const int MaxTimeoutS = 3600;
}

/// <summary>
/// One streamed NDJSON line of a <c>POST /tools/run</c> response. The response is
/// <c>application/x-ndjson</c>: a single <c>start</c> chunk (pid), then <c>stdout</c>/<c>stderr</c>
/// chunks as the tool prints (delivered incrementally, before process exit), then exactly one
/// terminal <c>exit</c> chunk carrying exitCode/timedOut/durationMs (and error when it failed).
/// </summary>
public sealed class ToolRunChunk
{
    /// <summary>One of: "start" | "stdout" | "stderr" | "exit".</summary>
    public string Stream { get; set; } = "";

    /// <summary>One output line (stdout/stderr chunks only).</summary>
    public string? Data { get; set; }

    /// <summary>Process id of the launched tool (start chunk only) - lets a caller verify the timeout kill.</summary>
    public int? Pid { get; set; }

    /// <summary>Process exit code (exit chunk; null when the run timed out or failed to launch).</summary>
    public int? ExitCode { get; set; }

    /// <summary>True when the run hit the timeout bound and the process tree was killed (exit chunk).</summary>
    public bool? TimedOut { get; set; }

    /// <summary>Wall-clock duration of the run (exit chunk).</summary>
    public long? DurationMs { get; set; }

    /// <summary>Failure detail when the run did not complete normally (exit chunk).</summary>
    public string? Error { get; set; }

    public const string StreamStart = "start";
    public const string StreamStdout = "stdout";
    public const string StreamStderr = "stderr";
    public const string StreamExit = "exit";
}
