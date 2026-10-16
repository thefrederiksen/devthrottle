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

    /// <summary>
    /// Start the session a schedule's CHECK asked for (Factory Definition, phase 5b, design 3.3): exactly what
    /// <see cref="StartRunAsync"/> starts for the schedule - its brief, seat, version and kit - but on the Director that
    /// ran the check (<paramref name="directorId"/>, which is on the schedule's machine and was alive a moment ago), and
    /// given <paramref name="seed"/>, the schedule's prompt with what the check found put in, and <paramref name="found"/>,
    /// which the Director writes as check.json in the seat's kit. A starter that cannot pin a Director cannot start a
    /// checked run: it must say so rather than start the session somewhere else.
    /// </summary>
    Task<CronRunStart> StartCheckedRunAsync(CronJobDto job, string directorId, string seed, DueCheckFinding found, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} cannot start the session a schedule's check asked for on the Director that ran it.");
}

/// <summary>What a schedule's check found, as its report said (Factory Definition, phase 5b, design 3.4). The seat and
/// the start's number are added by the starter, from the published definition the session is built from.</summary>
public sealed record DueCheckFinding(string CheckPath, int Count, string? Context, DateTime CheckedUtc, int DefinitionVersion);

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
    string? BriefVersionKind = null)
{
    /// <summary>
    /// True when no session is known to have started but the create was sent and its outcome is not known: the
    /// Gateway stopped waiting, the connection dropped while the command was out, or the Gateway stopped in between
    /// (Factory Definition, phase 5, review of P10). The Director may have started the session, so the run is recorded
    /// as <see cref="Contracts.CronRunResults.StartUnknown"/>, never as one that did not run. <see cref="Error"/> is the
    /// sentence saying so. Always false when <see cref="SessionId"/> is set.
    /// </summary>
    public bool OutcomeUnknown { get; init; }
}
