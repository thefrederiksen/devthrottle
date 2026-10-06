namespace CcDirector.Engine.Jobs;

/// <summary>
/// A command started but recording it failed, so its process tree was killed. <see cref="ProcessStopped"/>
/// says whether the command process was then seen to exit; when it was not, the caller still holds the
/// process id and start time here, and must not let anything run the command again until that process
/// is proven gone.
/// </summary>
public sealed class CommandNotRecordedException : Exception
{
    public CommandNotRecordedException(string jobName, int processId, DateTime? processStartedAtUtc,
        bool processStopped, Exception inner)
        : base($"Job '{jobName}' started process {processId} but could not record it: {inner.Message}", inner)
    {
        ProcessId = processId;
        ProcessStartedAtUtc = processStartedAtUtc;
        ProcessStopped = processStopped;
    }

    public int ProcessId { get; }

    /// <summary>Null only when even the start time could not be read.</summary>
    public DateTime? ProcessStartedAtUtc { get; }

    public bool ProcessStopped { get; }
}
