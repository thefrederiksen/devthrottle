using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Running;

/// <summary>What one claim on a schedule's failure notice came to.</summary>
/// <param name="Send">True when the owner is to be told now.</param>
/// <param name="Occurrences">When <paramref name="Send"/>: how many failures this notice stands for - this one and the
/// ones held back since the last notice.</param>
/// <param name="AlreadyCounted">When not sent: true when this failure was counted before (read again after a restart),
/// false when it is held back because a notice of its cause went out inside the hour.</param>
public sealed record CronFailureNoticeClaim(bool Send, int Occurrences, bool AlreadyCounted = false);

/// <summary>
/// THE HOURLY LIMIT ON A SCHEDULE'S FAILURE NOTICES (issue #4436), in <c>cron_failure_notices</c>. One row per account,
/// schedule and cause holds when the owner was last told and how many failures were held back since. A claim inside the
/// hour counts the failure and says no; the first claim after it says yes and carries the count. Kept in the database
/// so a restart does not forget that the hour has not passed.
/// </summary>
public sealed class CronFailureNoticeStore
{
    /// <summary>At most one notice per cause of a schedule in this long.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly GatewayDatabase _db;
    private readonly object _gate = new();

    public CronFailureNoticeStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Claim the notice of one failure of <paramref name="cause"/> on schedule <paramref name="jobId"/>, recorded at
    /// <paramref name="failureUtc"/>. A failure recorded at or before the newest one already counted is not counted
    /// again (the issue #4436 review, finding 3: a restarted Gateway reads the last minutes of the run history again).
    /// </summary>
    public CronFailureNoticeClaim Claim(TenantId tenant, string jobId, string cause, DateTime failureUtc, DateTime nowUtc,
        string runId = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cause);
        var failedAt = DateTime.SpecifyKind(failureUtc, DateTimeKind.Utc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronFailureNotices.FirstOrDefault(e => e.JobId == jobId && e.Cause == cause);
            if (row is null)
            {
                ctx.CronFailureNotices.Add(new CronFailureNoticeEntity
                {
                    TenantId = ctx.ActiveTenant!,
                    JobId = jobId,
                    Cause = cause,
                    LastSentUtc = nowUtc,
                    HeldBack = 0,
                    LastCountedUtc = failedAt,
                    LastCountedRunId = runId,
                });
                ctx.SaveChanges();
                return new CronFailureNoticeClaim(true, 1);
            }
            // Older than the last one counted, or that very run: read again. At the same instant but another run: new -
            // a late sweep records every run it skipped with one result time (the #4436 review, second round, finding A).
            var lastCounted = DateTime.SpecifyKind(row.LastCountedUtc, DateTimeKind.Utc);
            if (failedAt < lastCounted || (failedAt == lastCounted && string.Equals(runId, row.LastCountedRunId, StringComparison.Ordinal)))
                return new CronFailureNoticeClaim(false, 0, AlreadyCounted: true);
            row.LastCountedUtc = failedAt;
            row.LastCountedRunId = runId;
            if (nowUtc - DateTime.SpecifyKind(row.LastSentUtc, DateTimeKind.Utc) < Window)
            {
                row.HeldBack++;
                ctx.SaveChanges();
                return new CronFailureNoticeClaim(false, 0);
            }
            var occurrences = row.HeldBack + 1;
            row.LastSentUtc = nowUtc;
            row.HeldBack = 0;
            ctx.SaveChanges();
            return new CronFailureNoticeClaim(true, occurrences);
        }
    }
}

/// <summary>Pushes one schedule failure notice to the account's phones. Never throws.</summary>
public delegate Task ScheduleFailurePusher(TenantId tenant, CronRunCompletedPayload notice, CancellationToken ct);

/// <summary>Emails one schedule failure notice to the account's owner, where the account has one. Never throws for a
/// send that did not happen; returns a sentence saying why it was not sent, or null when it was.</summary>
public delegate Task<string?> ScheduleFailureMailer(TenantId tenant, string subject, string body, CancellationToken ct);

/// <summary>
/// A FAILED SCHEDULE TELLS ITS OWNER (issue #4436). Before this a schedule's notifications were off unless its owner
/// switched them on, and a schedule could fail every morning without anyone hearing; the owner of the trial account in
/// the issue found out by watching.
///
/// WHICH FAILURES. Every run <see cref="CronRunHistoryStore.FailedSince"/> returns - one that did not run (missed runs
/// included: the Gateway was down, the sweep came late, the deadline passed), did not start, whose start is not known,
/// or whose session ended with a problem - read from the run history on a timer, exactly as the administrator's failure
/// reports read it (<see cref="CronRunFailureReporter"/>), because every path that fails a run stamps its result time:
/// one read sees a failure whichever path recorded it. A schedule whose <see cref="CronJobDto.NotifyOn"/> is
/// <c>none</c> is not told; failure (the default) and always are.
///
/// IN WHAT WORDS. <see cref="ScheduleFailureWords"/>: the headline is one plain sentence - what went wrong, on which
/// machine, what to do - and the run's own reason rides along as the detail.
///
/// ON WHICH CHANNELS. The fleet notification channel (the per-Director doorbell event ring) and the schedule's webhook,
/// through <see cref="ICronNotifier"/>; the account's phones (<see cref="ScheduleFailurePusher"/>); and an email to the
/// account owner where the account has one - the hosted Gateway (<see cref="ScheduleFailureMailer"/>).
///
/// HOW OFTEN. At most once an hour for each cause of each schedule (<see cref="CronFailureNoticeStore"/>), so a broken
/// schedule firing every five minutes sends one notice an hour, which says how many failures it stands for.
///
/// ONCE PER FAILURE. A run is considered once for each result time it carries. The window reaches back
/// <see cref="Overlap"/> before the last scan, and the runs already considered inside it are remembered. A new
/// Gateway starts <see cref="RestartLookback"/> back; the hourly limit is in the database, so a failure it told before
/// the restart is not told again.
/// </summary>
public sealed class ScheduleFailureNotifier : IDisposable
{
    internal static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan RestartLookback = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    internal const int MaxPerScan = 200;

    private readonly CronRunHistoryStore _runs;
    private readonly CronFailureNoticeStore _notices;
    private readonly Func<TenantId, string, CronJobDto?> _jobOf;
    private readonly Func<TenantId, string, string?> _machineOsOf;
    private readonly ICronNotifier _fleet;
    private readonly ScheduleFailurePusher _push;
    private readonly ScheduleFailureMailer _mail;
    private readonly Func<TenantId, IDisposable> _enterScope;
    private readonly Func<DateTime> _clock;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly Dictionary<(string RunId, DateTime ResultUtc), DateTime> _seen = new();
    private DateTime _since;

    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _disposed;

    /// <param name="jobOf">The schedule, read in the given account; null when it no longer exists.</param>
    /// <param name="machineOsOf">The system a machine of the account last reported, or null: the folder sentences name it.</param>
    /// <param name="fleet">The fleet notification channel and the schedule's webhook.</param>
    /// <param name="enterScope">Enters one account's scope, so the fleet channel files the notice under that account.</param>
    public ScheduleFailureNotifier(CronRunHistoryStore runs, CronFailureNoticeStore notices,
        Func<TenantId, string, CronJobDto?> jobOf, Func<TenantId, string, string?> machineOsOf, ICronNotifier fleet,
        ScheduleFailurePusher push, ScheduleFailureMailer mail, Func<TenantId, IDisposable> enterScope,
        Func<DateTime>? clock = null)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _notices = notices ?? throw new ArgumentNullException(nameof(notices));
        _jobOf = jobOf ?? throw new ArgumentNullException(nameof(jobOf));
        _machineOsOf = machineOsOf ?? throw new ArgumentNullException(nameof(machineOsOf));
        _fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
        _push = push ?? throw new ArgumentNullException(nameof(push));
        _mail = mail ?? throw new ArgumentNullException(nameof(mail));
        _enterScope = enterScope ?? throw new ArgumentNullException(nameof(enterScope));
        _clock = clock ?? (() => DateTime.UtcNow);
        _since = _clock() - RestartLookback;
        FileLog.Write($"[ScheduleFailureNotifier] reading failed runs from {_since:o}");
    }

    /// <summary>Start the scan timer. Call once.</summary>
    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("the schedule failure notifier is already started");
        _loop = Task.Run(RunLoopAsync);
        FileLog.Write($"[ScheduleFailureNotifier] Start: telling owners of failed runs every {ScanInterval.TotalSeconds:0}s");
    }

    /// <summary>
    /// Tell the owner of every failed run recorded since the last scan that has not been considered yet, within the
    /// hourly limit. Returns how many notices were sent.
    /// </summary>
    public async Task<int> ScanAsync(CancellationToken ct)
    {
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock();
            var from = _since - Overlap;
            var seenInWindow = _seen.Count(s => s.Key.ResultUtc >= from);
            var limit = MaxPerScan + seenInWindow;
            var failed = _runs.FailedSince(from, limit);
            var more = failed.Count >= limit;
            var considered = 0;
            var sent = 0;
            DateTime? stoppedAt = null;
            foreach (var f in failed)
            {
                var key = (f.Run.RunId, f.Run.ResultUtc!.Value);
                if (_seen.ContainsKey(key)) continue;
                if (considered == MaxPerScan)
                {
                    more = true;
                    stoppedAt = key.Item2;
                    break;
                }
                _seen[key] = now;
                considered++;
                if (await ConsiderAsync(f, now, ct).ConfigureAwait(false))
                    sent++;
            }

            // While a backlog remains the window stays where it is (the seen set keeps it from being told twice); once it
            // is drained the window moves on to now.
            if (!more) _since = now;
            else if (stoppedAt is { } at && at > _since) _since = at;
            var forgetBefore = _since - Overlap;
            foreach (var stale in _seen.Where(s => s.Key.ResultUtc < forgetBefore).Select(s => s.Key).ToList())
                _seen.Remove(stale);

            if (considered > 0)
                FileLog.Write($"[ScheduleFailureNotifier] ScanAsync: considered {considered} failed run(s) since {from:o}, sent {sent} notice(s){(more ? "; more wait for the next scan" : "")}");
            return sent;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    // One failed run: told when its schedule asks for failure notices and the hour for its cause has passed.
    private async Task<bool> ConsiderAsync(CronRunHistoryStore.AccountRun f, DateTime now, CancellationToken ct)
    {
        var tenant = new TenantId(f.Account);
        var run = f.Run;
        var job = _jobOf(tenant, f.JobId);
        if (job is null)
        {
            FileLog.Write($"[ScheduleFailureNotifier] run={run.RunId}: schedule {f.JobId} no longer exists; not told");
            return false;
        }
        if (!CronNotify.ShouldNotify(job.NotifyOn, succeeded: false))
            return false;
        var failure = ScheduleFailureWords.Of(run, _machineOsOf(tenant, run.Machine));
        if (failure is null)
            return false;

        var claim = _notices.Claim(tenant, job.Id, failure.Cause, run.ResultUtc ?? run.FiredUtc, now, run.RunId);
        if (!claim.Send)
        {
            FileLog.Write(claim.AlreadyCounted
                ? $"[ScheduleFailureNotifier] run={run.RunId}, job={job.Id}, cause={failure.Cause}: already counted (read again after a restart)"
                : $"[ScheduleFailureNotifier] run={run.RunId}, job={job.Id}, cause={failure.Cause}: held back, a notice of this cause went out inside the hour");
            return false;
        }

        var notice = Notice(job, run, failure, claim.Occurrences);
        FileLog.Write($"[ScheduleFailureNotifier] telling the owner: tenant={tenant.ToLogString()}, job={job.Id}, run={run.RunId}, cause={failure.Cause}, occurrences={claim.Occurrences}");
        using (_enterScope(tenant))
        {
            // Each leg is best-effort and isolated: one that fails must not keep the owner from hearing on the others.
            try
            {
                await _fleet.NotifyRunCompletedAsync(job, run.TargetDirectorId ?? "", notice, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FileLog.Write($"[ScheduleFailureNotifier] fleet notice FAILED: job={job.Id}: {ex.Message}");
            }
            await _push(tenant, notice, ct).ConfigureAwait(false);
            var (subject, body) = Email(job, notice);
            var notSent = await _mail(tenant, subject, body, ct).ConfigureAwait(false);
            FileLog.Write(notSent is null
                ? $"[ScheduleFailureNotifier] emailed the account owner: job={job.Id}"
                : $"[ScheduleFailureNotifier] email not sent: job={job.Id}: {notSent}");
        }
        return true;
    }

    /// <summary>The notice of one failed run, as every channel carries it.</summary>
    public static CronRunCompletedPayload Notice(CronJobDto job, CronRunRecord run, ScheduleFailure failure, int occurrences)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(failure);
        return new CronRunCompletedPayload
        {
            JobId = job.Id,
            JobName = job.Name,
            Succeeded = false,
            InfraStatus = run.InfraStatus,
            TaskStatus = run.TaskStatus,
            Machine = run.Machine,
            SessionId = string.IsNullOrWhiteSpace(run.SessionId) ? null : run.SessionId,
            SessionLink = "",
            Reason = failure.Headline,
            Detail = failure.Detail,
            Cause = failure.Cause,
            Occurrences = occurrences,
            FiredUtc = run.FiredUtc,
        };
    }

    /// <summary>The push's title and body. The body is the headline, with how many failures it stands for.</summary>
    public static (string Title, string Body) PushWords(CronRunCompletedPayload notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return ($"Schedule failed: {notice.JobName}", notice.Reason + Times(notice.Occurrences));
    }

    /// <summary>The email's subject and plain-text body: the headline first, the run's own words last, as the detail.</summary>
    public static (string Subject, string Body) Email(CronJobDto job, CronRunCompletedPayload notice)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(notice);
        var lines = new List<string>
        {
            $"Your schedule \"{job.Name}\" failed on {(string.IsNullOrWhiteSpace(notice.Machine) ? "its machine" : notice.Machine)}.",
            "",
            notice.Reason + Times(notice.Occurrences),
            "",
            $"When: {DateTime.SpecifyKind(notice.FiredUtc, DateTimeKind.Utc):yyyy-MM-dd HH:mm} UTC",
            "See every run on the Schedule page of DevThrottle.",
            $"You are told at most once an hour for each kind of failure of this schedule. To stop these emails, set the schedule's Notify to Off.",
        };
        if (!string.IsNullOrWhiteSpace(notice.Detail))
        {
            lines.Add("");
            lines.Add($"Details: {notice.Detail}");
        }
        return ($"Schedule failed: {job.Name}", string.Join("\n", lines));
    }

    private static string Times(int occurrences) =>
        occurrences > 1 ? $" It has failed this way {occurrences} times since the last notice." : "";

    private async Task RunLoopAsync()
    {
        using var timer = new PeriodicTimer(ScanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                // One bad tick costs that tick, never the loop.
                try
                {
                    await ScanAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ScheduleFailureNotifier] ScanAsync FAILED this tick ({ex.GetType().Name}): {ex.Message}; the next tick tries again");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex)
        {
            FileLog.Write($"[ScheduleFailureNotifier] the scan loop ended with {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
        }
        _stop.Dispose();
        FileLog.Write("[ScheduleFailureNotifier] stopped");
    }
}
