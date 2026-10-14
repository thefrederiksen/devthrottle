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
    private readonly Func<CronJobDto, string?> _refuseFactoryWork;
    private readonly Func<CronJobDto, ScheduledRunSeat?> _seatWithABrief;

    /// <param name="refuseFactoryWork">Issue #3650: for a job about to fire, the reason it may not - its factory or
    /// seat is not in the account's registry - or null when it may. Every way a seed schedule starts a session comes
    /// through here (its own time, run-now, a factory's Restore switching it back on), so this is the one place that
    /// makes factory work on an unregistered seat impossible rather than merely refused at the write. A work-list
    /// schedule never comes here, and is refused a factory at the write instead (CronJobEndpoints.TrySettleSeat). REQUIRED: a
    /// harness that has no registry passes <c>_ =&gt; null</c>.</param>
    /// <param name="seatWithABrief">Factory Seats mission, phase 2c: for a job about to fire, the seat it runs as and
    /// that seat's PUBLISHED brief on the Gateway - or null for a schedule in no factory and for a seat with no
    /// published brief, which are started exactly as before. REQUIRED, so a wiring line that forgot it fails to
    /// compile rather than starting every seat without its brief; a harness with no briefs passes <c>_ =&gt; null</c>.</param>
    /// <summary>How a run that did not start says its seat's brief could not be read; the failure's own words follow.</summary>
    public const string BriefNotRead = "not started: the seat's brief could not be read, and a run is never started without its brief";

    public DirectorCronSessionStarter(MachineSessionSpawner spawner, IClock clock, Func<CronJobDto, string?> refuseFactoryWork,
        Func<CronJobDto, ScheduledRunSeat?> seatWithABrief)
    {
        _seatWithABrief = seatWithABrief ?? throw new ArgumentNullException(nameof(seatWithABrief));
        _spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _refuseFactoryWork = refuseFactoryWork ?? throw new ArgumentNullException(nameof(refuseFactoryWork));
    }

    public async Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
    {
        var start = await StartRunAsync(job, ct);
        return (start.SessionId, start.DirectorId, start.Error);
    }

    public async Task<CronRunStart> StartRunAsync(CronJobDto job, CancellationToken ct)
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));

        // NO FACTORY WORK WITHOUT A REGISTERED SEAT (issue #3650). A write is already refused, but a link can stop
        // validating afterwards - a seat dropped while its schedules were off, then switched back on. Such a job does
        // not start: the run is recorded as not started, with the reason, and the schedule stays on the Factories
        // screen's "outside any factory" list until its seat is registered or it is moved.
        if (_refuseFactoryWork(job) is { } refusal)
        {
            FileLog.Write($"[DirectorCronSessionStarter] start REFUSED: job={job.Id}, factory={job.Factory}, seat={job.Seat}: {refusal}");
            return new CronRunStart(null, null, $"not started: {refusal}", BriefVersion: null);
        }

        // THE GATEWAY OPENS THE RUN (Factory Seats mission, phase 2c). The seat and its published brief are read
        // once, here, and the version that is given is the version that is reported back to be recorded on the run.
        //
        // A READ THAT FAILS IS A RUN THAT DID NOT START, AND IS RECORDED AS ONE (phase 2c, review 1, finding 1). This
        // seam reports "could not start" rather than throwing, so the engine records the run with its reason. The
        // brief read is a database read in the fire path; left to throw, it would stop a factory's runs with no run
        // row and nothing on any screen. The run is never started without its brief instead.
        ScheduledRunSeat? seat;
        try
        {
            seat = _seatWithABrief(job);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorCronSessionStarter] start FAILED: job={job.Id}, factory={job.Factory}, seat={job.Seat}: the seat's brief could not be read: {ex}");
            return new CronRunStart(null, null, BriefNotRead + ": " + ex.Message, BriefVersion: null);
        }

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
            // The run is told to report how it went (Factory Control, step 1). Added HERE, to the text this run is
            // given - the schedule's stored prompt is not touched. Every way a seed schedule starts a session comes
            // through this method (its own time, a late catch-up, run-now), so this is the one place that adds it.
            // A seat with a published brief is also told who it is and given that brief, ahead of the schedule's
            // own prompt; every other run's text is the closing instruction's alone, as before.
            PrePrompt = ScheduledRunOpening.PromptFor(job.Action.Seed, job.Name, job.Id, seat),
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

        FileLog.Write($"[DirectorCronSessionStarter] start: job={job.Id}, machine={job.Target.Machine}, repo={job.Action.RepoPath}, seed={job.Action.Seed}, "
                      + (seat is null ? "brief=none given" : $"opened as {seat.Factory.Factory}/{seat.Seat.Id} with brief version {seat.Brief.Version}"));

        var (ok, dto, error, directorId) = await _spawner.SpawnOnMachineAsync(job.Target.Machine, req, ct);
        if (!ok || dto is null || string.IsNullOrEmpty(dto.SessionId))
        {
            FileLog.Write($"[DirectorCronSessionStarter] start FAILED: job={job.Id}, machine={job.Target.Machine}, {error}");
            return new CronRunStart(null, directorId, error ?? "could not start a session on the target machine", BriefVersion: null);
        }

        FileLog.Write($"[DirectorCronSessionStarter] started: job={job.Id}, sid={dto.SessionId}, director={directorId}");
        return new CronRunStart(dto.SessionId, directorId, null, seat?.Brief.Version);
    }
}
