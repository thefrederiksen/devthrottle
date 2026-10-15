using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.Factory.Triggers;

/// <summary>Why a due check's report was not recorded.</summary>
public enum DueCheckRefusal
{
    None,
    /// <summary>No run of that id in this account.</summary>
    NoSuchRun,
    /// <summary>The run is no longer waiting on its check: its report was recorded (or it was superseded).</summary>
    AlreadyRecorded,
    /// <summary>Another Director holds the run's check, or the reporting Director was never handed it.</summary>
    HeldByAnotherDirector,
    /// <summary>The reporting Director never said on Hello that it runs checks from a definition.</summary>
    CannotRunDefinitionChecks,
}

/// <summary>The answer to one due check's report: the run as it now stands, or why nothing was recorded.</summary>
/// <remarks>A check that counted work has its session start running: <see cref="Starting"/> is that start, which
/// writes the run's outcome when it returns.</remarks>
public sealed record DueCheckReportResult(DueCheckRefusal Refusal, CronRunRecord? Run, string? Error)
{
    /// <summary>The session start this check began, on the Gateway's lifetime; it ends with the run as it then stands,
    /// or null when nothing could be written. Null when the check began no start.</summary>
    public Task<CronRunRecord?>? Starting { get; init; }

    /// <summary>What the check counted, when it began a start.</summary>
    public int? StartingCount { get; init; }

    public static DueCheckReportResult Refused(DueCheckRefusal refusal, string error) => new(refusal, null, error);
}

/// <summary>
/// THE DUE CHECK OF A TIMED START (Factory Definition, phase 5b, design 3.3). A schedule with a check fires by writing a
/// run whose result is <c>checking</c> (<see cref="CronEngine"/>); this hands that run to a Director on the schedule's
/// machine through the trigger pull it already makes, takes the check's report on the trigger report route, and
/// DECIDES - the Director runs, the Gateway decides (rule 7):
///
///  - the check broke its contract  -> the run is a <c>check-failed</c> problem, with the contract's reason
///  - it counted nothing            -> <c>nothing-to-do</c>: not pending, not a problem, never "did not run"
///  - it counted work               -> the session is started through the SAME cron starter the schedule uses, on the
///                                     Director that ran the check, the prompt carrying the count and the context;
///                                     the run becomes <c>pending</c> with its session, or a <c>did-not-run</c> problem
///
/// ONE WRITER. A due check is CLAIMED by the Director it is handed to, and only that Director may report it; a claim
/// lapses after <see cref="ClaimLapse"/>, the fixed check timeout plus a minute, so a Director that went away hands
/// the check on. A run no longer waiting on its check is refused as "already recorded" - the word the Director's runner
/// treats as final (<c>DirectorTriggerRunner.IsAlreadyRecorded</c>) - and the moment the Gateway decides is written
/// before a session start begins, so a second report can never start a second session.
///
/// ONLY A CAPABLE DIRECTOR (design 2.6). A due check is handed to, and taken from, only a Director whose hello said
/// <c>definitionChecks</c>, exactly as a definition trigger is.
///
/// THE STATE (design 1.4 as amended on review of 4031) is kept under <c>cron:&lt;schedule id&gt;</c> only when the work
/// the check saw was dealt with: on nothing-to-do, and when the session it asked for STARTED. Never on a failed check
/// and never on a start that failed: the next check must count that work again.
/// </summary>
public sealed class DueCheckService
{
    /// <summary>How long a Director's claim on a due check lasts: the fixed check timeout plus one minute.</summary>
    public static readonly TimeSpan ClaimLapse = TimeSpan.FromSeconds(TriggerCheckLimits.DueCheckTimeoutSeconds + 60);

    /// <summary>The 409 a report for a run no longer waiting on its check gets. The Director treats it as final.</summary>
    public static string AlreadyRecordedSentence(string runId)
        => $"the check of run {runId} is already recorded; this report was not recorded";

    private readonly CronRunHistoryStore _runs;
    private readonly Func<TenantId, string, CronJobDto?> _jobOf;
    private readonly FactoryCheckStateStore _states;
    private readonly ICronSessionStarter _starter;
    private readonly Func<TenantId, string, bool> _directorRunsDefinitionChecks;
    private readonly Action<TenantId, CronRunRecord> _resultRecorded;
    private readonly Func<DateTime> _nowUtc;
    private readonly CancellationToken _startLifetime;

    /// <param name="jobOf">A schedule of the account by id, or null when it is gone.</param>
    /// <param name="starter">The cron starter the schedule itself starts sessions through.</param>
    /// <param name="directorRunsDefinitionChecks">Whether this account's Director (by id) said on Hello that it runs
    /// checks from a definition.</param>
    /// <param name="resultRecorded">Told of every run this records with its result on it, so its factory activity row
    /// is written (production: <see cref="CronRunResultService.WriteOwedActivityFor"/>).</param>
    /// <param name="startLifetime">The Gateway's own lifetime, which a session start runs on - never the request's.</param>
    public DueCheckService(CronRunHistoryStore runs, Func<TenantId, string, CronJobDto?> jobOf, FactoryCheckStateStore states,
        ICronSessionStarter starter, Func<TenantId, string, bool> directorRunsDefinitionChecks,
        Action<TenantId, CronRunRecord> resultRecorded, Func<DateTime> nowUtc, CancellationToken startLifetime)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _jobOf = jobOf ?? throw new ArgumentNullException(nameof(jobOf));
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _starter = starter ?? throw new ArgumentNullException(nameof(starter));
        _directorRunsDefinitionChecks = directorRunsDefinitionChecks ?? throw new ArgumentNullException(nameof(directorRunsDefinitionChecks));
        _resultRecorded = resultRecorded ?? throw new ArgumentNullException(nameof(resultRecorded));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
        _startLifetime = startLifetime;
    }

    /// <summary>True when <paramref name="id"/> names a run of this account - the report route's way of telling a due
    /// check's report from a trigger's.</summary>
    public bool IsRun(TenantId tenant, string id)
        => Guid.TryParse(id, out var runId) && _runs.FindDueCheck(tenant, runId) is not null;

    /// <summary>
    /// The due checks this Director must run now: every run on its machine still waiting on its check that is unclaimed,
    /// its own, or whose claim lapsed - each CLAIMED for it. None at all for a Director that never said it runs checks
    /// from a definition. A due check whose schedule is gone, or no longer has a check, is not handed out.
    /// </summary>
    public IReadOnlyList<TriggerAssignmentDto> AssignmentsFor(TenantId tenant, string directorId, string machine)
    {
        if (!_directorRunsDefinitionChecks(tenant, directorId)) return Array.Empty<TriggerAssignmentDto>();
        var claimed = _runs.ClaimDueChecks(tenant, directorId, machine, _nowUtc(), ClaimLapse);
        var assignments = new List<TriggerAssignmentDto>();
        foreach (var due in claimed)
        {
            var job = _jobOf(tenant, due.JobId);
            if (job is null || !job.HasCheck || string.IsNullOrWhiteSpace(job.Factory))
            {
                FileLog.Write($"[DueCheckService] AssignmentsFor: run={due.Run.RunId} of schedule {due.JobId} not handed out - the schedule is gone or has no check");
                continue;
            }
            assignments.Add(new TriggerAssignmentDto
            {
                Id = job.Id,
                Name = job.Name,
                CheckCommand = "",
                RepoPath = "",
                IntervalSeconds = 0,
                TimeoutSeconds = TriggerCheckLimits.DueCheckTimeoutSeconds,
                Kind = TriggerKinds.Definition,
                Factory = job.Factory,
                Seat = job.Seat,
                DefinitionVersion = job.DefinitionVersion,
                CheckPath = job.CheckPath,
                Secrets = job.CheckSecrets ?? new List<string>(),
                DueRunId = due.Run.RunId,
                State = _states.StateFor(tenant, job.Factory!, FactoryCheckStateStore.ScheduleOwner(job.Id)),
            });
        }
        if (claimed.Count > 0)
            FileLog.Write($"[DueCheckService] AssignmentsFor: tenant={tenant.ToLogString()}, director={directorId}, machine={machine}, due checks handed={assignments.Count}");
        return assignments;
    }

    /// <summary>
    /// Read one due check's report, decide, and record it - or, when the check counted work, record that it was
    /// decided and begin the session start WITHOUT waiting for it (a start outlasts the Director's wait for its answer,
    /// as a trigger's does). The result's <see cref="DueCheckReportResult.Starting"/> carries that start.
    /// </summary>
    public DueCheckReportResult Report(TenantId tenant, string directorId, Guid runId, TriggerCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        FileLog.Write($"[DueCheckService] Report: tenant={tenant.ToLogString()}, director={directorId}, run={runId}");
        var id = runId.ToString("D");
        var due = _runs.FindDueCheck(tenant, runId);
        if (due is null)
            return DueCheckReportResult.Refused(DueCheckRefusal.NoSuchRun, $"no run '{id}' in this account");
        if (!_directorRunsDefinitionChecks(tenant, directorId))
            return DueCheckReportResult.Refused(DueCheckRefusal.CannotRunDefinitionChecks,
                $"run {id} is a check from a factory's definition, and Director {directorId} has not said it runs those; this report was not recorded");
        if (due.Run.Result != CronRunResults.Checking || due.Run.CheckedUtc is not null)
            return DueCheckReportResult.Refused(DueCheckRefusal.AlreadyRecorded, AlreadyRecordedSentence(id));
        if (due.ClaimedByDirectorId != directorId)
            return HeldBy(due, directorId);

        var job = _jobOf(tenant, due.JobId);
        var reading = TriggerCheckContract.Read(report, definitionCheck: true);
        var now = _nowUtc();

        if (reading.Failed)
            return RecordWithoutStart(tenant, due, directorId, CronRunResults.Problem, CronRunProblems.CheckFailed,
                reading.FailureReason, count: null, CronRunProblems.CheckFailed, state: null, now);

        var count = reading.Count!.Value;
        if (count == 0)
            return RecordWithoutStart(tenant, due, directorId, CronRunResults.NothingToDo, null, null, count: 0,
                CronRunResults.NothingToDo, report.State, now);

        if (job is null || !job.HasCheck)
            return RecordWithoutStart(tenant, due, directorId, CronRunResults.Problem, CronRunProblems.DidNotRun,
                $"its check counted {count.ToString(CultureInfo.InvariantCulture)}, but the schedule is gone or no longer has a check, so no session was started",
                count, "not-started", state: null, now);

        var begun = _runs.BeginDueStart(tenant, runId, directorId, count, now);
        if (begun != CronRunHistoryStore.DueCheckWrite.Applied)
            return Refusal(begun, due, directorId);

        var seed = TriggerDefinition.PromptFor(job.Action.Seed ?? "", count, reading.Context);
        FileLog.Write($"[DueCheckService] Report: run={id}, schedule={job.Id}, count={count.ToString(CultureInfo.InvariantCulture)}, starting the session on {directorId} off the request");
        var state = report.State;
        var starting = Task.Run(() => FinishStartAsync(tenant, job, due, directorId, seed, state), CancellationToken.None);
        return new DueCheckReportResult(DueCheckRefusal.None, null, null) { Starting = starting, StartingCount = count };
    }

    private DueCheckReportResult RecordWithoutStart(TenantId tenant, CronRunHistoryStore.DueCheck due, string directorId,
        string result, string? problem, string? reason, int? count, string infraStatus, string? state, DateTime now)
    {
        var runId = Guid.Parse(due.Run.RunId);
        var written = _runs.RecordDueCheck(tenant, runId, directorId, result, problem, reason, count, infraStatus, now);
        if (written != CronRunHistoryStore.DueCheckWrite.Applied)
            return Refusal(written, due, directorId);

        var run = _runs.Find(tenant, runId)?.Run
                  ?? throw new InvalidOperationException($"run {runId} vanished while its check was being recorded");
        // Of the outcomes that start nothing, only nothing-to-do keeps the state: a failed check's state is discarded.
        if (result == CronRunResults.NothingToDo)
            KeepState(tenant, due.JobId, run.Factory, directorId, state, run.RunId);
        _resultRecorded(tenant, run);
        FileLog.Write($"[DueCheckService] Report: run={run.RunId}, schedule={due.JobId}, result={result}, problem={problem ?? "-"}, reason={reason ?? "none"}");
        return new DueCheckReportResult(DueCheckRefusal.None, run, null);
    }

    /// <summary>
    /// Run one start begun by <see cref="Report"/> and write its outcome on the run. Nothing awaits this in production,
    /// so it is an entry point: every failure ends logged, and as a did-not-run problem when it can be written.
    /// </summary>
    private async Task<CronRunRecord?> FinishStartAsync(TenantId tenant, CronJobDto job, CronRunHistoryStore.DueCheck due,
        string directorId, string seed, string? state)
    {
        var runId = Guid.Parse(due.Run.RunId);
        CronRunStart start;
        try
        {
            start = await _starter.StartCheckedRunAsync(job, directorId, seed, _startLifetime).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DueCheckService] FinishStartAsync: run={runId}, the start threw {ex.GetType().Name}: {ex.Message}");
            start = new CronRunStart(null, directorId, ex.Message, BriefVersion: null);
        }

        try
        {
            var failure = string.IsNullOrEmpty(start.SessionId)
                ? (string.IsNullOrWhiteSpace(start.Error) ? "it did not start a session" : $"it did not start a session: {start.Error}")
                : null;
            var run = _runs.FinishDueStart(tenant, runId, directorId, start, failure, _nowUtc());
            if (run is null) return null;
            if (run.Result == CronRunResults.Pending)
                KeepState(tenant, job.Id, run.Factory, directorId, state, run.RunId);
            else
                _resultRecorded(tenant, run);
            FileLog.Write($"[DueCheckService] FinishStartAsync: run={runId}, schedule={job.Id}, result={run.Result}, session={run.SessionId ?? "none"}, error={start.Error ?? "none"}");
            return run;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DueCheckService] FinishStartAsync FAILED to record: run={runId}, session={start.SessionId ?? "none"}, {ex.Message}");
            return null;
        }
    }

    private void KeepState(TenantId tenant, string jobId, string? factory, string directorId, string? state, string runRef)
    {
        if (state is null || string.IsNullOrWhiteSpace(factory)) return;
        _states.RecordIfChanged(tenant, factory, FactoryCheckStateStore.ScheduleOwner(jobId), state, directorId, runRef, _nowUtc());
    }

    private static DueCheckReportResult HeldBy(CronRunHistoryStore.DueCheck due, string directorId)
        => DueCheckReportResult.Refused(DueCheckRefusal.HeldByAnotherDirector,
            due.ClaimedByDirectorId is null
                ? $"run {due.Run.RunId}'s check was not handed to Director {directorId}; this report was not recorded"
                : $"Director {due.ClaimedByDirectorId} runs the check of run {due.Run.RunId}; this report was not recorded");

    private static DueCheckReportResult Refusal(CronRunHistoryStore.DueCheckWrite write, CronRunHistoryStore.DueCheck due, string directorId)
        => write switch
        {
            CronRunHistoryStore.DueCheckWrite.NoSuchRun => DueCheckReportResult.Refused(DueCheckRefusal.NoSuchRun, $"no run '{due.Run.RunId}' in this account"),
            CronRunHistoryStore.DueCheckWrite.AlreadyRecorded => DueCheckReportResult.Refused(DueCheckRefusal.AlreadyRecorded, AlreadyRecordedSentence(due.Run.RunId)),
            CronRunHistoryStore.DueCheckWrite.HeldByAnother => HeldBy(due, directorId),
            _ => throw new InvalidOperationException($"unhandled due check write: {write}"),
        };
}
