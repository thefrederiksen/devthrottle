using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>The outcome of triggering a work-list drain for a fired cron job (epic #479, #484).</summary>
public enum CronWorkListOutcome
{
    /// <summary>The list was claimable and a background drain was launched on the target Director.</summary>
    Started,

    /// <summary>The named list has no items - nothing to drain (skipped cleanly).</summary>
    EmptyList,

    /// <summary>No list with the job's <see cref="CronJobAction.WorkListName"/> exists.</summary>
    NoSuchList,

    /// <summary>The list already has an active draining consumer (#273) - not re-claimed.</summary>
    AlreadyClaimed,

    /// <summary>The job's target Director is not registered / has no control endpoint.</summary>
    NoSuchDirector,

    /// <summary>The target machine is already draining another list (#274 single-machine guard).</summary>
    MachineBusy,
}

/// <summary>
/// The seam the cron firing engine (epic #479, #484) uses to drain a named work list when a job's
/// action is a work-list action. The production implementation triggers the existing #274 runner;
/// tests inject a fake. It does the cheap pre-checks synchronously (so the engine gets an immediate
/// outcome to record) and launches the actual drain in the background.
/// </summary>
public interface ICronWorkListRunner
{
    /// <summary>
    /// Trigger a drain of <see cref="CronJobAction.WorkListName"/> on the job's target Director.
    /// Returns the synchronous outcome; on <see cref="CronWorkListOutcome.Started"/> the drain runs
    /// in the background.
    /// </summary>
    Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct);
}
