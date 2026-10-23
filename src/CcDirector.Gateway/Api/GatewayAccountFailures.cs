using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway's own failures that belong to ONE ACCOUNT, into the error store under that account (issue #4403), so
/// the administrator's <c>/gateway/admin/director-errors?email=</c> shows the Gateway's side of a customer's trouble
/// next to what their Director sent in. Before this the Gateway's view of a refused session key or a failed scheduled
/// run was at most a log line on the App Service, which nobody can search by account.
///
/// What is reported, and from where:
///   - a session key or a device credential the Gateway refused, where it knows whose it was - revoked, expired, or a
///     session key presented on a route it may not call (<see cref="Util.AuthMiddleware"/>). A key nobody holds names
///     no account, so it is not reported: anybody on the internet can send one;
///   - a session key a Director asked to register and the Gateway refused (<see cref="Streaming.DirectorHub"/>);
///   - a scheduled run that failed, with the reason its run record carries (<see cref="Running.CronRunFailureReporter"/>).
///
/// Every report is component <c>gateway</c> - the store's own word for "the Gateway wrote this, not a Director" - and
/// goes through <see cref="GatewayErrorSink"/>, which scrubs every field, folds repeats into one row with a count, and
/// writes off the request thread. No key, hash, secret or prompt text is ever put in one: the messages here are built
/// from identifiers and from reasons the Gateway's own code wrote.
///
/// Registered as one singleton before the host is built, because the hub is constructed by dependency injection; the
/// sink is attached once it exists. Until then - and on a host that never attaches one - nothing is reported, and the
/// first report that finds no sink says so in the log.
/// </summary>
public sealed class GatewayAccountFailures
{
    /// <summary>The source of a refused credential's report.</summary>
    public const string CredentialSource = "credential";

    /// <summary>The source of a refused session-key registration's report.</summary>
    public const string RegistrationSource = "session-key-registration";

    /// <summary>The source of a failed scheduled run's report.</summary>
    public const string ScheduleSource = "schedule";

    /// <summary>The kind of every report here: a refusal or a failure the Gateway decided, not a line it logged.</summary>
    public const string Kind = "refused";

    /// <summary>The kind of a failed scheduled run's report.</summary>
    public const string ScheduleKind = "schedule-run-failed";

    /// <summary>
    /// The same refusal - same account, same key, same reason - is stored at most once in this window. A Director whose
    /// key is dead retries every few seconds; stored each time, one machine would spend the store's hourly budget
    /// (<see cref="GatewayErrorSink.MaxStoredPerHour"/>) and push every other account's errors out. The repeats held back
    /// are counted onto the next row, so its count stays true.
    /// </summary>
    internal static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);
    internal const int MaxThrottled = 2000;

    private volatile GatewayErrorSink? _sink;
    private int _noSinkLogged;
    private readonly Func<DateTime> _clock;
    private readonly object _throttleLock = new();
    private readonly Dictionary<string, (DateTime Stored, int HeldBack)> _throttle = new(StringComparer.Ordinal);

    public GatewayAccountFailures() : this(null) { }

    internal GatewayAccountFailures(Func<DateTime>? clock) => _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>Send this Gateway's account failures to <paramref name="sink"/>. Called once, when the sink exists.</summary>
    internal void Attach(GatewayErrorSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        FileLog.Write("[GatewayAccountFailures] Attach: the Gateway's account failures now reach the error store");
    }

    /// <summary>Stop reporting: the sink is being disposed.</summary>
    internal void Detach() => _sink = null;

    /// <summary>A credential the Gateway refused, whose account it knows.</summary>
    internal void CredentialRefused(CredentialRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        ReportThrottled(new GatewayError(
            Source: CredentialSource,
            Kind: Kind,
            Message: $"the Gateway refused a {refusal.Credential} ({refusal.HttpStatus}): {refusal.Reason}",
            Account: refusal.Account,
            Surface: $"{refusal.Method} {refusal.Route}",
            HttpStatus: refusal.HttpStatus,
            ErrorCode: refusal.ErrorCode,
            SessionId: refusal.SessionId));
    }

    /// <summary>A session key a Director asked to register, refused.</summary>
    internal void RegistrationRefused(string account, string directorId, string sessionId, string reason)
    {
        ReportThrottled(new GatewayError(
            Source: RegistrationSource,
            Kind: Kind,
            Message: $"the Gateway refused to register a session key for Director {directorId}: {reason}",
            Account: account,
            Surface: "director hub RegisterSessionKey",
            ErrorCode: "session_key_not_registered",
            SessionId: sessionId));
    }

    /// <summary>A scheduled run that failed, with the reason its record carries.</summary>
    internal void ScheduleRunFailed(CronRunHistoryStore.AccountRun failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        var run = failed.Run;
        var name = string.IsNullOrWhiteSpace(failed.JobName) ? failed.JobId : $"'{failed.JobName}' ({failed.JobId})";
        var how = run.Result == Contracts.CronRunResults.StartUnknown
            ? "whether it started is not known"
            : Contracts.CronRunProblems.Words(run.Problem);
        var machine = string.IsNullOrWhiteSpace(run.Machine) ? "" : $" on {run.Machine}";
        var reason = string.IsNullOrWhiteSpace(run.ResultReason) ? "the run record gives no reason" : run.ResultReason;
        Report(new GatewayError(
            Source: ScheduleSource,
            Kind: ScheduleKind,
            Message: $"schedule {name}{machine}: {how}: {reason}",
            Account: failed.Account,
            Surface: $"schedule {failed.JobId}",
            ErrorCode: run.Problem ?? run.Result,
            SessionId: run.SessionId));
    }

    private void ReportThrottled(GatewayError error)
    {
        var key = string.Join('|', error.Source, error.Account, error.SessionId ?? "", error.HttpStatus?.ToString() ?? "", error.Message);
        var now = _clock();
        int occurrences;
        lock (_throttleLock)
        {
            if (_throttle.TryGetValue(key, out var seen) && now - seen.Stored < RepeatWindow)
            {
                _throttle[key] = (seen.Stored, seen.HeldBack + 1);
                return;
            }
            occurrences = 1 + (_throttle.TryGetValue(key, out var before) ? before.HeldBack : 0);
            if (_throttle.Count >= MaxThrottled)
            {
                // Bounded: what has left its window is forgotten. Its held-back repeats were counted by nobody, so they
                // are written in the log rather than lost silently.
                var gone = _throttle.Where(t => now - t.Value.Stored >= RepeatWindow).ToList();
                foreach (var g in gone) _throttle.Remove(g.Key);
                var uncounted = gone.Sum(g => g.Value.HeldBack);
                if (uncounted > 0)
                    FileLog.Write($"[GatewayAccountFailures] forgot {gone.Count} refusal(s) past their window; {uncounted} repeat(s) of them were never stored");
            }
            _throttle[key] = (now, 0);
        }
        Report(error, occurrences);
    }

    private void Report(GatewayError error, int occurrences = 1)
    {
        var sink = _sink;
        if (sink is null)
        {
            if (Interlocked.Exchange(ref _noSinkLogged, 1) == 0)
                FileLog.Write($"[GatewayAccountFailures] no error sink is attached, so the Gateway's account failures are not stored (first: {error.Source})");
            return;
        }
        sink.Report(error, occurrences);
    }
}

/// <summary>One credential the Gateway refused, with the account it belongs to. Never carries the credential.</summary>
/// <param name="Credential">"session key" or "device credential".</param>
/// <param name="Route">The path, already redacted for a log.</param>
internal sealed record CredentialRefusal(
    string Account,
    string Credential,
    string Reason,
    int HttpStatus,
    string ErrorCode,
    string Method,
    string Route,
    string? SessionId);
