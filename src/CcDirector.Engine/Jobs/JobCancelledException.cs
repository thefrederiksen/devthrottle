namespace CcDirector.Engine.Jobs;

/// <summary>
/// A command was cancelled by its caller and its process tree was killed. <see cref="ProcessStopped"/>
/// says whether the command process was then seen to exit; when it was not, the command may still be
/// running and nothing may start it again until that is proven otherwise.
/// </summary>
public sealed class JobCancelledException : OperationCanceledException
{
    public JobCancelledException(string jobName, bool processStopped, CancellationToken token)
        : base(processStopped
            ? $"Job '{jobName}' was cancelled and its process tree was killed."
            : $"Job '{jobName}' was cancelled; its process tree was killed but did not exit.", token)
    {
        ProcessStopped = processStopped;
    }

    public bool ProcessStopped { get; }
}
