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

    /// <summary>
    /// <see cref="StartAsync"/>, and with it which version of the seat's brief the Gateway gave the run (Factory Seats
    /// mission, phase 2c). This is what the engine calls, so the version is recorded on the run by the very start
    /// that gave it. A starter that gives no brief - every stand-in a test uses - need not implement it.
    /// </summary>
    async Task<CronRunStart> StartRunAsync(CronJobDto job, CancellationToken ct)
    {
        var (sessionId, directorId, error) = await StartAsync(job, ct);
        return new CronRunStart(sessionId, directorId, error, BriefVersion: null);
    }
}

/// <summary>What starting a scheduled run came to.</summary>
/// <param name="SessionId">The new session, or null when none started.</param>
/// <param name="DirectorId">The Director actually used, when one was resolved.</param>
/// <param name="Error">Why no session started, or null when one did.</param>
/// <param name="BriefVersion">The version of the seat's published brief the run was given by the Gateway, or null when
/// it was given none: a schedule in no factory, a seat with no published brief, or a run that did not start.</param>
/// <param name="BriefVersionKind">Which kind of version <paramref name="BriefVersion"/> is
/// (<see cref="Contracts.FactoryBriefVersionKinds"/>): the seat's own brief version, or the definition version of a
/// defined factory (Factory Definition, phase 4). Null exactly when <paramref name="BriefVersion"/> is.</param>
public sealed record CronRunStart(string? SessionId, string? DirectorId, string? Error, int? BriefVersion,
    string? BriefVersionKind = null);
