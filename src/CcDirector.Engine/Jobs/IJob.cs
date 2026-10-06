namespace CcDirector.Engine.Jobs;

public interface IJob
{
    string Name { get; }
    Task<JobResult> ExecuteAsync(CancellationToken cancellationToken);
}

/// <param name="ExitCode">The process exit code, or null when it never exited on its own (a timeout).</param>
/// <param name="ProcessStopped">False only when a timed-out command was killed and still did not exit.</param>
public record JobResult(
    bool Success,
    string Output,
    string? Error = null,
    bool TimedOut = false,
    int? ExitCode = null,
    string ErrorOutput = "",
    bool ProcessStopped = true
);
