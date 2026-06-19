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
    /// Start a session for <paramref name="job"/> on its target <see cref="CronJobTarget.Machine"/>,
    /// seeded with <see cref="CronJobAction.Seed"/> in <see cref="CronJobAction.RepoPath"/>. Resolves
    /// the machine to a Director (launching one if none is running) and returns the new session id +
    /// the Director actually used, or a non-null error when no session started. Does not throw for an
    /// expected "could not start" - it reports the error so the engine records a not-started run.
    /// </summary>
    Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct);
}
