using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Production <see cref="ICronSessionStarter"/> (epic #479, #483, #503). Builds the cron job's
/// session request (the ONLY cron-specific work here) and hands it to the shared
/// <see cref="MachineSessionSpawner"/>, which resolves the target MACHINE to a Director (launching one
/// if none is running) and starts the session over the Gateway's existing session-create path. Cron
/// and the interactive POST /machines/{machine}/sessions relay therefore share ONE resolve-then-create
/// method; no new Director surface is introduced, and an unresolvable target is reported as an error,
/// not thrown.
/// </summary>
public sealed class DirectorCronSessionStarter : ICronSessionStarter
{
    private readonly MachineSessionSpawner _spawner;
    private readonly IClock _clock;

    public DirectorCronSessionStarter(MachineSessionSpawner spawner, IClock clock)
    {
        _spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));

        var req = new NewSessionRequest
        {
            RepoPath = job.Action.RepoPath,
            Agent = "ClaudeCode",
            // Name the spawned session after the SCHEDULE plus when it ran (e.g.
            // "Daily Error Triage - 2026-07-24 05:00"), so a fired job reads as itself in the rail
            // instead of the generic auto-name. The Gateway owns this naming (CLAUDE.md rule 7) - the
            // scheduler knows the name and the fire time, so it stamps them rather than asking the
            // seeded agent to rename itself. A job always has a name (required by CronSchedule.Validate),
            // so this is never the bare-folder name the Director would reject.
            Name = $"{job.Name} - {CronSchedule.LocalRunLabel(job, _clock.UtcNow)}",
            PrePrompt = job.Action.Seed,
            // Scheduled-run auto-dismiss (issue #1200): a seed run defaults to closing itself when it
            // finishes with nothing needing a human, so an hourly job stops piling leftover sessions into
            // the rail. The job can opt out (AutoDismiss=false) to keep the run open like a normal session.
            AutoDismiss = job.Action.AutoDismiss,
            // Session origin (devthrottle_internal issue #982). A fired schedule is the third origin:
            // nobody was at a keyboard and no session made the call, so it is neither "human" nor
            // "agent". Recording it as either would corrupt the one number the field exists to answer -
            // what share of sessions agents start - and on a fleet with hourly jobs the error would not
            // be small. This is a measurement, not a default: this code path only ever runs for a cron
            // firing, so it knows both facts for certain.
            Origin = SessionOriginKinds.Schedule,
            OriginSurface = SessionOriginSurfaces.Cron,
            // THE SESSION IS BORN INTO THE SCHEDULE'S FACTORY (Factory Memory mission, phase 1). The Website
            // Factory's Scout runs on a schedule, so without this the one agent with the most to remember would
            // start outside its factory every morning and could neither read nor write its memory. The Gateway
            // stamps it from the schedule row; a create body never carries it here.
            Factory = job.Factory,
        };

        FileLog.Write($"[DirectorCronSessionStarter] start: job={job.Id}, machine={job.Target.Machine}, repo={job.Action.RepoPath}, seed={job.Action.Seed}");

        var (ok, dto, error, directorId) = await _spawner.SpawnOnMachineAsync(job.Target.Machine, req, ct);
        if (!ok || dto is null || string.IsNullOrEmpty(dto.SessionId))
        {
            FileLog.Write($"[DirectorCronSessionStarter] start FAILED: job={job.Id}, machine={job.Target.Machine}, {error}");
            return (null, directorId, error ?? "could not start a session on the target machine");
        }

        FileLog.Write($"[DirectorCronSessionStarter] started: job={job.Id}, sid={dto.SessionId}, director={directorId}");
        return (dto.SessionId, directorId, null);
    }
}
