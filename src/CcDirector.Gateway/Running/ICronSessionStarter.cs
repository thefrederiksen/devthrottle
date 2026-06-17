using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// The seam the cron firing engine (epic #479, #483) uses to START a session for a due job on its
/// target Director. Mirrors <see cref="IImplSessionDriver"/> (issue #274): production resolves the
/// Director and uses the existing session-create + seed-prompt path; tests inject a fake so the
/// engine's scheduling logic is verified without a live Director.
/// </summary>
public interface ICronSessionStarter
{
    /// <summary>
    /// Start a session for <paramref name="job"/> on its <see cref="CronJobTarget.DirectorId"/>,
    /// seeded with <see cref="CronJobAction.Seed"/> in <see cref="CronJobAction.RepoPath"/>. Returns
    /// the new session id, or a non-null error string when no session started (e.g. the target
    /// Director is unknown/offline). Does not throw for an expected "could not start" - it reports
    /// the error so the engine records a not-started run.
    /// </summary>
    Task<(string? sessionId, string? error)> StartAsync(CronJobDto job, CancellationToken ct);
}
