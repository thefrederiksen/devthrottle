using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;

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
    private readonly Func<CronJobDto, PublishedFactoryDefinition?> _definitionOf;

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
    /// <param name="definitionOf">Factory Definition mission, phase 5: for a job about to fire, its factory's PUBLISHED
    /// definition (<see cref="FactorySeatStart.ReadPublished"/>), or null when the factory is not defined - then the run
    /// starts exactly as before. A defined factory's run is built from the definition by <see cref="FactorySeatStart"/>,
    /// in no folder. REQUIRED, so a wiring line that forgot it fails to compile rather than starting every defined seat
    /// in a folder; a harness with no definitions passes <c>_ =&gt; null</c>.</param>
    /// <summary>How a run that did not start says its seat's brief could not be read; the failure's own words follow.</summary>
    public const string BriefNotRead = "not started: the seat's brief could not be read, and a run is never started without its brief";

    public DirectorCronSessionStarter(MachineSessionSpawner spawner, IClock clock, Func<CronJobDto, string?> refuseFactoryWork,
        Func<CronJobDto, ScheduledRunSeat?> seatWithABrief, Func<CronJobDto, PublishedFactoryDefinition?> definitionOf)
    {
        _definitionOf = definitionOf ?? throw new ArgumentNullException(nameof(definitionOf));
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

    public Task<CronRunStart> StartRunAsync(CronJobDto job, CancellationToken ct)
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));
        return StartRunCoreAsync(job, job.Action.Seed, director: null, found: null, ct);
    }

    /// <summary>
    /// The session a schedule's check asked for (Factory Definition, phase 5b): the same request a scheduled run makes,
    /// pinned to the Director that ran the check and seeded with the prompt the check's findings were put into.
    /// </summary>
    public Task<CronRunStart> StartCheckedRunAsync(CronJobDto job, string directorId, string seed, DueCheckFinding found,
        CancellationToken ct)
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));
        if (string.IsNullOrWhiteSpace(directorId))
            throw new ArgumentException("the Director that ran the check is required", nameof(directorId));
        ArgumentNullException.ThrowIfNull(found);
        return StartRunCoreAsync(job, seed ?? "", directorId, found, ct);
    }

    private async Task<CronRunStart> StartRunCoreAsync(CronJobDto job, string seed, string? director, DueCheckFinding? found,
        CancellationToken ct)
    {

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

        // A DEFINED FACTORY'S RUN IS BUILT FROM ITS DEFINITION (Factory Definition mission, phase 5). Read once, here,
        // like the brief, and a read that fails is a run that did not start, recorded with its reason.
        PublishedFactoryDefinition? published;
        try
        {
            published = _definitionOf(job);
        }
        catch (InvalidOperationException ex)
        {
            FileLog.Write($"[DirectorCronSessionStarter] start REFUSED: job={job.Id}, factory={job.Factory}, seat={job.Seat}: {ex.Message}");
            return new CronRunStart(null, null, $"not started: {ex.Message}", BriefVersion: null);
        }
        if (published is not null)
            return await StartDefinedAsync(job, published, seat, seed, director, found, ct);
        if (found is not null)
        {
            // A schedule gets its check only from a publish, so a checked run of a factory with nothing published is a
            // definition that went away: refused, never started without what its check found.
            var undefined = $"'{job.Factory}' has no published definition, so the session the check of '{job.Name}' asked for cannot be built. Publish the definition again.";
            FileLog.Write($"[DirectorCronSessionStarter] start REFUSED: job={job.Id}: {undefined}");
            return new CronRunStart(null, null, $"not started: {undefined}", BriefVersion: null);
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
            PrePrompt = ScheduledRunOpening.PromptFor(seed, job.Name, job.Id, seat),
            // A checked run starts on the Director that ran its check; every other run is left to the machine.
            Director = director,
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

        FileLog.Write($"[DirectorCronSessionStarter] start: job={job.Id}, machine={job.Target.Machine}, director={director ?? "the machine's"}, repo={job.Action.RepoPath}, seed={seed}, "
                      + (seat is null ? "brief=none given" : $"opened as {seat.Factory.Factory}/{seat.Seat.Id} with brief {seat.Brief.VersionWords}"));

        var (ok, dto, error, directorId, unknown) = await SpawnAsync(job, job.Target.Machine, req, foldUnknown: found is null, ct);
        if (!ok || dto is null || string.IsNullOrEmpty(dto.SessionId))
        {
            FileLog.Write($"[DirectorCronSessionStarter] start FAILED: job={job.Id}, machine={job.Target.Machine}, outcomeUnknown={unknown}, {error}");
            return new CronRunStart(null, directorId, error ?? "could not start a session on the target machine", BriefVersion: null)
                { OutcomeUnknown = unknown };
        }

        FileLog.Write($"[DirectorCronSessionStarter] started: job={job.Id}, sid={dto.SessionId}, director={directorId}");
        return new CronRunStart(dto.SessionId, directorId, null, seat?.Brief.Version, seat?.Brief.Kind);
    }

    /// <summary>
    /// A DEFINED FACTORY'S SCHEDULED RUN (Factory Definition mission, phase 5; design section 3): the create is built by
    /// <see cref="FactorySeatStart"/> from the published definition - the seat's agent, model, machine and repository
    /// name, a seat kit, and no folder - and the run is given the brief of that SAME version, so the version the run is
    /// told is the version its kit is built from and the version recorded on the run. Who started it and the run's own
    /// settings are the schedule's, as for every run. A seat the published version does not hold does not start.
    /// </summary>
    private async Task<CronRunStart> StartDefinedAsync(CronJobDto job, PublishedFactoryDefinition published,
        ScheduledRunSeat? registered, string seed, string? director, DueCheckFinding? found, CancellationToken ct)
    {
        if (published.Seat(job.Seat) is not { } seat)
        {
            var notHeld = FactorySeatStart.SeatNotInVersion(published, job.Seat ?? "");
            FileLog.Write($"[DirectorCronSessionStarter] start REFUSED: job={job.Id}: {notHeld}");
            return new CronRunStart(null, null, $"not started: {notHeld}", BriefVersion: null);
        }
        if (registered is null)
        {
            // The publish writes the registration a run is opened as, so a defined seat without one is a registry that
            // went its own way since: refused, never started without being told who it is.
            var unregistered = $"the seat '{seat.Seat}' of '{published.Definition.Factory}' is not registered as its definition says, so no session was started. Publish the definition again.";
            FileLog.Write($"[DirectorCronSessionStarter] start REFUSED: job={job.Id}: {unregistered}");
            return new CronRunStart(null, null, $"not started: {unregistered}", BriefVersion: null);
        }

        var opened = registered with { Brief = published.BriefOf(seat) };
        var built = FactorySeatStart.Build(published, seat,
            name: $"{job.Name} - {CronSchedule.LocalRunLabel(job, _clock.UtcNow)}",
            prePrompt: ScheduledRunOpening.PromptFor(seed, job.Name, job.Id, opened));
        var req = built.Request;
        req.AutoDismiss = job.Action.AutoDismiss;
        req.Origin = SessionOriginKinds.Schedule;
        req.OriginSurface = SessionOriginSurfaces.Cron;
        // A checked run (phase 5b) starts on the Director that ran its check, with the prompt its count was put into,
        // and what the check found reaches the agent as check.json in its kit (design 3.4).
        req.Director = director;
        if (found is not null)
        {
            if (Factory.Triggers.FactoryCheckStateFold.StartNumberOf(published.Definition, null, job) is not { } number)
            {
                var noStart = $"version {published.Version} of the definition of '{published.Definition.Factory}' has no checked start of seat '{seat.Seat}' that schedule '{job.Name}' stands for, so no session was started. Publish the definition again.";
                FileLog.Write($"[DirectorCronSessionStarter] start REFUSED: job={job.Id}: {noStart}");
                return new CronRunStart(null, null, $"not started: {noStart}", BriefVersion: null);
            }
            req.FactoryCheck = new FactoryCheckDto
            {
                CheckPath = found.CheckPath,
                Count = found.Count,
                Context = found.Context,
                CheckedUtc = DateTime.SpecifyKind(found.CheckedUtc, DateTimeKind.Utc),
                DefinitionVersion = found.DefinitionVersion,
                Seat = seat.Seat,
                Start = number,
            };
        }

        FileLog.Write($"[DirectorCronSessionStarter] start (defined): job={job.Id}, machine={built.Machine}, director={director ?? "the machine's"}, seat={seat.Seat}, definition v{published.Version}, seed={seed}");
        var (ok, dto, error, directorId, unknown) = await SpawnAsync(job, built.Machine, req, foldUnknown: found is null, ct);
        if (!ok || dto is null || string.IsNullOrEmpty(dto.SessionId))
        {
            FileLog.Write($"[DirectorCronSessionStarter] start FAILED: job={job.Id}, machine={built.Machine}, outcomeUnknown={unknown}, {error}");
            return new CronRunStart(null, directorId, error ?? "could not start a session on the target machine", BriefVersion: null)
                { OutcomeUnknown = unknown };
        }

        FileLog.Write($"[DirectorCronSessionStarter] started (defined): job={job.Id}, sid={dto.SessionId}, director={directorId}");
        return new CronRunStart(dto.SessionId, directorId, null, opened.Brief.Version, opened.Brief.Kind);
    }

    /// <summary>The sentence of a run whose create was out when the Gateway stopped.</summary>
    public const string GatewayStoppedDuringCreate =
        "The Gateway stopped while the session was being started, so it is not known whether it started.";

    /// <summary>The sentence of a run cut by the Gateway stopping before any create was sent: it was still waiting for a
    /// Director to start on the machine.</summary>
    public static string GatewayStoppedBeforeCreate(string machine) =>
        $"The Gateway stopped while it waited for a Director on {machine} to start, so no session was asked for.";

    /// <summary>
    /// The one spawn of a run, and whether its outcome is known (Factory Definition, phase 5, review of P10, findings 1
    /// and 2). A create the Gateway stopped waiting for, or whose connection dropped, may still have started its session,
    /// so it comes back UNKNOWN rather than as a failure. So does a create cut by the Gateway stopping: the Director is
    /// not told, and carries it on. A stop while the machine's Director is still being launched comes back as a run that
    /// did not start, in words, never as an exception that leaves no record. Which of the two a stop is, is known from
    /// the spawner's refusal hook, which it asks once the machine has resolved to a Director, before the create.
    ///
    /// <paramref name="foldUnknown"/> is false for the session a schedule's CHECK asked for (phase 5b): that start keeps
    /// its own bookkeeping (<see cref="CronRunHistoryStore.FinishDueStart"/>), and is passed through exactly as before.
    /// </summary>
    private async Task<(bool Ok, SessionDto? Dto, string? Error, string? DirectorId, bool OutcomeUnknown)> SpawnAsync(
        CronJobDto job, string machine, NewSessionRequest req, bool foldUnknown, CancellationToken ct)
    {
        if (!foldUnknown)
        {
            var (ok, dto, error, directorId) = await _spawner.SpawnOnMachineAsync(machine, req, ct);
            return (ok, dto, error, directorId, false);
        }

        string? resolved = null;
        try
        {
            var r = await _spawner.SpawnOnMachineWithOutcomeAsync(machine, req, ct, refuseDirector: id =>
            {
                resolved = id;   // the create goes to this Director next; nothing is refused here
                return null;
            });
            var unknown = !r.Ok && r.OutcomeUnknown;
            // A host stop during the create reaches the router as SignalR's cancelled invocation, which it can only name
            // a dropped connection. The connection did not drop: the Gateway stopped.
            var error = unknown && ct.IsCancellationRequested ? GatewayStoppedDuringCreate : r.Error;
            return (r.Ok, r.Dto, error, r.DirectorId, unknown);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (resolved is not null)
            {
                FileLog.Write($"[DirectorCronSessionStarter] start CUT: job={job.Id}, machine={machine}, director={resolved}: the Gateway stopped while the create was out");
                return (false, null, GatewayStoppedDuringCreate, resolved, true);
            }
            FileLog.Write($"[DirectorCronSessionStarter] start CUT: job={job.Id}, machine={machine}: the Gateway stopped before any create was sent");
            return (false, null, GatewayStoppedBeforeCreate(machine), null, false);
        }
    }
}
