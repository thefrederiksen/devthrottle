using CcDirector.Core.Background;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Licenses;

/// <summary>Where a usage report goes: the Gateway tunnel. <see cref="CanSend"/> is false while the tunnel is down
/// or the Gateway has no <c>PushLicenseUsage</c> (contract 6.5 - inert alone).</summary>
public interface ILicenseReportSink
{
    bool CanSend { get; }

    Task SendAsync(LicenseUsageReport report, CancellationToken cancellationToken);
}

/// <summary>
/// License Switching contract 2.3: the usage report. Only the key holder (2.4) sends one, and only while the Gateway
/// can take it. It is always COMPLETE - every holding, every time - and is sent:
///
///   - every <see cref="LicenseContract.ReportInterval"/> ("interval"), reading the ACTIVE holding from the provider
///     for every report and each spare at most every <see cref="LicenseContract.SpareReadInterval"/>;
///   - at once when what the machine holds changes ("changed"): a holding added or removed, the active account
///     changed by hand, or a holding's read error appearing or clearing. A cheap listing with no provider read is
///     taken every <see cref="ChangeCheckInterval"/> to notice that;
///   - right after every switch ("after-switch");
///   - on every new connection, and when this Director becomes the key holder ("connected").
///
/// The key holder reports even when it holds nothing (contract 2.4, as amended by 8.3): the report with no holdings
/// is how the Gateway knows which Director to ask when this machine only APPROVES another machine's sign-in.
///
/// A holding whose last renewal failed carries that failure (contract 8.1), recorded with <see cref="RecordRenewal"/>.
///
/// A hand sign-in the key store healed (contract 2.6) makes a "changed" report at once, carrying
/// <see cref="LicenseUsageReport.SwitchedByHand"/>; it is carried until a report holding it has been sent, then cleared,
/// so the Gateway hears it once.
///
/// A failed read of a holding keeps its PREVIOUS numbers and their old read time, and sets the error, so the Gateway
/// sees how old the numbers are and never a zero standing in for "unknown".
///
/// This reader REPLACES the usage poll retired on 22 September 2026, for a reader that now exists: what it reads
/// reaches <c>PushLicenseUsage</c> (NoUnreadUsagePollTests holds it to that).
/// </summary>
public sealed class LicenseUsageReporter
{
    /// <summary>How often the key holder takes a listing (no provider read) to notice a change made by hand.</summary>
    public static readonly TimeSpan ChangeCheckInterval = TimeSpan.FromSeconds(30);

    private readonly ILicenseCommandLine _tool;
    private readonly LicenseKeyHolderLock _holder;
    private readonly Func<DateTime> _utcNow;
    private readonly string _machineName;
    private readonly string _osUser;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>The last numbers read for each account, kept across failed reads.</summary>
    private readonly Dictionary<string, LicenseHolding> _readings = new(StringComparer.Ordinal);

    /// <summary>The LAST renewal of each account's sign-in, when it failed (contract 8.1). No entry: it worked, or none was tried.</summary>
    private readonly Dictionary<string, (DateTime FailedAtUtc, string Error)> _renewalFailures = new(StringComparer.Ordinal);

    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private DateTime? _lastSpareReadUtc;
    private DateTime? _lastSentUtc;
    private string? _lastSentShape;
    private string? _lastFailure;
    private volatile ILicenseReportSink? _sink;
    private BackgroundJob? _job;
    private volatile string? _switchInProgressId;

    /// <summary>The last hand sign-in the key store healed that no sent report has carried yet (contract 2.6).</summary>
    private LicenseHandSwitch? _pendingHandSwitch;

    public LicenseUsageReporter(ILicenseCommandLine tool, LicenseKeyHolderLock holder, Func<DateTime> utcNow, string machineName, string osUser)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _holder = holder ?? throw new ArgumentNullException(nameof(holder));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _machineName = machineName ?? throw new ArgumentNullException(nameof(machineName));
        _osUser = osUser ?? throw new ArgumentNullException(nameof(osUser));
    }

    /// <summary>The tunnel to send on. Replaced when the Director's Gateway settings change; null while there is none.</summary>
    public void AttachSink(ILicenseReportSink? sink) => _sink = sink;

    /// <summary>The SwitchId being executed right now, carried in every report while it runs (contract 2.2).</summary>
    public string? SwitchInProgressId
    {
        get => _switchInProgressId;
        set => _switchInProgressId = value;
    }

    /// <summary>The read error of <paramref name="accountEmail"/>'s last provider read, or null when it read cleanly
    /// or was never read. The switch refuses a spare whose sign-in the provider rejected (contract 3.3 step 9).</summary>
    public string? LastReadErrorFor(string accountEmail)
    {
        lock (_gate)
            return _readings.TryGetValue(accountEmail.Trim().ToLowerInvariant(), out var h) ? h.ReadError : null;
    }

    /// <summary>When the last read said this account's sign-in stops working; null when not known.</summary>
    public DateTime? SignInExpiryFor(string accountEmail)
    {
        lock (_gate)
            return _readings.TryGetValue(accountEmail.Trim().ToLowerInvariant(), out var h) ? h.SignInExpiresAtUtc : null;
    }

    /// <summary>
    /// Records how the last renewal of <paramref name="accountEmail"/>'s sign-in went (contract 8.1): a failure with
    /// its time and its <see cref="LicenseRenewalErrors"/> word, or - with a null <paramref name="failedAtUtc"/> - that
    /// it worked, which clears an earlier failure. Returns whether anything changed. The next report carries it.
    /// </summary>
    public bool RecordRenewal(string accountEmail, DateTime? failedAtUtc, string? error)
    {
        var email = accountEmail.Trim().ToLowerInvariant();
        lock (_gate)
        {
            if (failedAtUtc is null || error is null)
            {
                var cleared = _renewalFailures.Remove(email);
                if (cleared) FileLog.Write($"[LicenseUsageReporter] RecordRenewal: account={email}, the earlier renewal failure is cleared");
                return cleared;
            }
            // Only the contract's words travel: anything else could be anything a tool printed (rule 6.1).
            var word = LicenseRenewalErrors.All.Contains(error) ? error : LicenseRenewalErrors.LoginFailed;
            var changed = !_renewalFailures.TryGetValue(email, out var previous) || previous.Error != word;
            _renewalFailures[email] = (failedAtUtc.Value, word);
            // Every failure is named here, whichever route found it: until the Gateway reads these fields this log is
            // the one place a person can see which account failed and why.
            FileLog.Write($"[LicenseUsageReporter] RecordRenewal: account={email}, renewal FAILED ({word}) at {failedAtUtc.Value:O}, new={changed}");
            return changed;
        }
    }

    /// <summary>Asks for a report with this trigger as soon as one can be sent. Never blocks.</summary>
    public void RequestReport(string trigger)
    {
        FileLog.Write($"[LicenseUsageReporter] RequestReport: trigger={trigger}");
        lock (_gate) _pending.Add(trigger);
        _job?.Trigger();
    }

    /// <summary>
    /// Puts the reporter on the Director's scheduler for the life of the process: a tick every
    /// <see cref="ChangeCheckInterval"/>, skipped while no Gateway can take a report (the off switch), and a
    /// <see cref="RequestReport"/> asks for one at once. The scheduler never runs a requested tick sooner than the
    /// cadence allows, so a requested report goes at the latest on the next tick.
    /// </summary>
    public void Start(BackgroundJobs jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (_job is not null) return;
        _job = jobs.Register(
            new BackgroundJobSpec("License usage report", BackgroundJobTier.SlowAndSteady, ChangeCheckInterval,
                "no Gateway tunnel that has PushLicenseUsage", () => _sink?.CanSend == true),
            async ct => await TickAsync(ct));
        _job.StartTimer(TimeSpan.Zero);
        FileLog.Write($"[LicenseUsageReporter] Start: machine={_machineName}, user={_osUser}, every {ChangeCheckInterval.TotalSeconds:0}s");
    }

    /// <summary>Whether the reporter is on the scheduler.</summary>
    public bool IsScheduled => _job is not null;

    /// <summary>Takes the reporter off the scheduler.</summary>
    public void Stop()
    {
        _job?.Dispose();
        _job = null;
    }

    /// <summary>
    /// One step: take the key holder lock when it is free, then send a report when one is due. Returns the report
    /// sent, or null when none was. Public for tests; the scheduler is the only production caller.
    /// </summary>
    public async Task<LicenseUsageReport?> TickAsync(CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            if (_holder.TryAcquire())
                lock (_gate) _pending.Add(LicenseReportTriggers.Connected);
            if (!_holder.IsHeld) return null;

            var sink = _sink;
            if (sink is null || !sink.CanSend) return null;

            var trigger = TakePendingTrigger();
            IReadOnlyList<LicenseHolding>? listing = null;
            if (trigger is null)
            {
                if (_lastSentUtc is null || _utcNow() - _lastSentUtc.Value >= LicenseContract.ReportInterval)
                {
                    trigger = LicenseReportTriggers.Interval;
                }
                else
                {
                    listing = await RunToolAsync(() => _tool.ListAsync(cancellationToken));
                    TakeHandSwitches();
                    if (listing is null) return null;
                    if (ShapeOf(listing.Select(h => (h.AccountEmail, h.IsActive, ErrorOf(h.AccountEmail)))) == _lastSentShape
                        && PendingHandSwitch() is null)
                        return null;
                    trigger = LicenseReportTriggers.Changed;
                }
            }

            // A report that could not be built or sent keeps its reason waiting, so the next tick still says
            // "connected" or "after-switch" rather than a plain "interval".
            var report = await BuildReportAsync(trigger, listing, cancellationToken);
            if (report is null)
            {
                Requeue(trigger);
                return null;
            }

            try
            {
                await sink.SendAsync(report, cancellationToken);
            }
            catch
            {
                Requeue(trigger);
                throw;
            }
            _lastSentUtc = report.SentAtUtc;
            _lastSentShape = ShapeOf(report.Holdings.Select(h => (h.AccountEmail, h.IsActive, h.ReadError)));
            if (report.SwitchedByHand is not null)
            {
                lock (_gate)
                    if (ReferenceEquals(_pendingHandSwitch, report.SwitchedByHand)) _pendingHandSwitch = null;
                FileLog.Write($"[LicenseUsageReporter] sent: the report says the machine was switched by hand to {report.SwitchedByHand.AccountEmail}");
            }
            FileLog.Write($"[LicenseUsageReporter] sent: trigger={trigger}, holdings={report.Holdings.Count}, active={report.Holdings.FirstOrDefault(h => h.IsActive)?.AccountEmail ?? "-"}, switch={report.SwitchInProgressId ?? "-"}");
            return report;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Takes the hand sign-ins the tool's last runs healed and keeps the newest for the next report.</summary>
    private void TakeHandSwitches()
    {
        var healed = _tool.TakeHandSwitches();
        if (healed.Count == 0) return;
        lock (_gate) _pendingHandSwitch = healed[^1];
        foreach (var h in healed)
            FileLog.Write($"[LicenseUsageReporter] the key store healed a hand sign-in: account={h.AccountEmail}, at={h.HealedAtUtc:O}; the next report says the machine was switched by hand");
    }

    private LicenseHandSwitch? PendingHandSwitch()
    {
        lock (_gate) return _pendingHandSwitch;
    }

    private void Requeue(string trigger)
    {
        if (trigger == LicenseReportTriggers.Interval) return;
        lock (_gate) _pending.Add(trigger);
    }

    private string? TakePendingTrigger()
    {
        lock (_gate)
        {
            // One report answers every waiting reason; the most specific word is the one it carries.
            string? chosen = null;
            foreach (var t in new[] { LicenseReportTriggers.AfterSwitch, LicenseReportTriggers.Connected, LicenseReportTriggers.Changed })
                if (_pending.Contains(t)) { chosen = t; break; }
            _pending.Clear();
            return chosen;
        }
    }

    /// <summary>Reads, merges and builds one complete report. Null when the tool gave no usable answer - a report
    /// that is not complete is not sent, because the Gateway reads a missing holding as a removed one.</summary>
    private async Task<LicenseUsageReport?> BuildReportAsync(string trigger, IReadOnlyList<LicenseHolding>? listing, CancellationToken cancellationToken)
    {
        var now = _utcNow();
        var readSpares = _lastSpareReadUtc is null || now - _lastSpareReadUtc.Value >= LicenseContract.SpareReadInterval;

        var read = await RunToolAsync(() => _tool.ReadUsageAsync(activeOnly: !readSpares, cancellationToken));
        TakeHandSwitches();
        if (read is null) return null;
        if (readSpares)
        {
            listing = read;
        }
        else
        {
            listing ??= await RunToolAsync(() => _tool.ListAsync(cancellationToken));
            if (listing is null) return null;
        }

        var holdings = new List<LicenseHolding>();
        lock (_gate)
        {
            foreach (var row in read) Merge(row);
            if (readSpares) _lastSpareReadUtc = now;

            var held = new HashSet<string>(listing.Select(h => h.AccountEmail), StringComparer.Ordinal);
            foreach (var gone in _readings.Keys.Where(k => !held.Contains(k)).ToList())
                _readings.Remove(gone);
            foreach (var gone in _renewalFailures.Keys.Where(k => !held.Contains(k)).ToList())
                _renewalFailures.Remove(gone);

            foreach (var h in listing.OrderByDescending(h => h.IsActive))
            {
                _readings.TryGetValue(h.AccountEmail, out var numbers);
                (DateTime FailedAtUtc, string Error)? renewal = _renewalFailures.TryGetValue(h.AccountEmail, out var failed) ? failed : null;
                holdings.Add(new LicenseHolding
                {
                    AccountEmail = h.AccountEmail,
                    IsActive = h.IsActive,
                    Plan = numbers?.Plan ?? h.Plan,
                    RateLimitTier = numbers?.RateLimitTier ?? h.RateLimitTier,
                    FiveHour = numbers?.FiveHour,
                    SevenDay = numbers?.SevenDay,
                    ExtraUsage = numbers?.ExtraUsage,
                    ReadAtUtc = numbers?.ReadAtUtc,
                    ReadError = numbers?.ReadError,
                    ReadErrorDetail = numbers?.ReadErrorDetail,
                    SignedInAtUtc = numbers?.SignedInAtUtc,
                    SignInExpiresAtUtc = numbers?.SignInExpiresAtUtc,
                    RenewalFailedAtUtc = renewal?.FailedAtUtc,
                    RenewalError = renewal?.Error,
                    RenewalErrorDetail = LicenseRenewalSentences.For(renewal?.Error),
                });
            }
        }

        return new LicenseUsageReport
        {
            MachineName = _machineName,
            OsUser = _osUser,
            SentAtUtc = now,
            Trigger = trigger,
            SwitchInProgressId = _switchInProgressId,
            Holdings = holdings,
            SwitchedByHand = HandSwitchFor(holdings),
        };
    }

    /// <summary>The pending hand switch, when its account is this report's active holding. One whose account is no
    /// longer active (a switch ran since) is dropped: the machine has moved on, and a report naming an account that is
    /// not its active holding is malformed at the Gateway (contract 2.6).</summary>
    private LicenseHandSwitch? HandSwitchFor(IReadOnlyList<LicenseHolding> holdings)
    {
        lock (_gate)
        {
            if (_pendingHandSwitch is null) return null;
            var active = holdings.FirstOrDefault(h => h.IsActive)?.AccountEmail;
            if (string.Equals(active, _pendingHandSwitch.AccountEmail, StringComparison.Ordinal)) return _pendingHandSwitch;
            FileLog.Write($"[LicenseUsageReporter] the hand switch to {_pendingHandSwitch.AccountEmail} is not reported: the active account is now {active ?? "none"}");
            _pendingHandSwitch = null;
            return null;
        }
    }

    /// <summary>Keeps the newest good numbers. A failed read keeps the previous numbers and their read time, and
    /// carries the error (contract 2.2).</summary>
    private void Merge(LicenseHolding row)
    {
        if (row.ReadError is null || !_readings.TryGetValue(row.AccountEmail, out var previous))
        {
            row.ReadErrorDetail = ReadErrorSentence(row.ReadError);
            _readings[row.AccountEmail] = row;
            return;
        }
        _readings[row.AccountEmail] = new LicenseHolding
        {
            AccountEmail = row.AccountEmail,
            IsActive = row.IsActive,
            Plan = row.Plan ?? previous.Plan,
            RateLimitTier = row.RateLimitTier ?? previous.RateLimitTier,
            FiveHour = previous.FiveHour,
            SevenDay = previous.SevenDay,
            ExtraUsage = previous.ExtraUsage,
            ReadAtUtc = previous.ReadAtUtc,
            ReadError = row.ReadError,
            ReadErrorDetail = ReadErrorSentence(row.ReadError),
            // The sign-in's dates come from the stored sign-in, not from the provider read that failed.
            SignedInAtUtc = row.SignedInAtUtc ?? previous.SignedInAtUtc,
            SignInExpiresAtUtc = row.SignInExpiresAtUtc ?? previous.SignInExpiresAtUtc,
        };
    }

    /// <summary>
    /// The sentence that goes with a read error. Written here, never taken from the tool's own words: the contract
    /// (rule 6.1) lets no detail quote a provider answer or a process's output.
    /// </summary>
    internal static string? ReadErrorSentence(string? readError) => readError switch
    {
        null => null,
        LicenseReadErrors.Unauthorized => "The provider refused this sign-in; the account needs a new sign-in.",
        LicenseReadErrors.RefreshFailed => "The stored sign-in could not be renewed; the account needs a new sign-in.",
        LicenseReadErrors.RateLimited => "The provider asked for fewer usage reads; the numbers shown are the last ones read.",
        LicenseReadErrors.Network => "The provider could not be reached; the numbers shown are the last ones read.",
        LicenseReadErrors.UnexpectedAnswer => "The provider's answer could not be read; the numbers shown are the last ones read.",
        LicenseReadErrors.StoreUnreadable => "This machine could not read the sign-in for this account.",
        _ => "The usage read failed for a reason this Director does not recognise.",
    };

    private string? ErrorOf(string accountEmail)
    {
        lock (_gate) return _readings.TryGetValue(accountEmail, out var h) ? h.ReadError : null;
    }

    private static string ShapeOf(IEnumerable<(string Email, bool Active, string? Error)> holdings) =>
        string.Join("|", holdings.OrderBy(h => h.Email, StringComparer.Ordinal).Select(h => $"{h.Email}:{h.Active}:{h.Error}"));

    /// <summary>Runs one tool call; a tool failure is logged once per distinct message (it repeats every 30 seconds
    /// otherwise) and answers null.</summary>
    private async Task<IReadOnlyList<LicenseHolding>?> RunToolAsync(Func<Task<IReadOnlyList<LicenseHolding>>> call)
    {
        try
        {
            var rows = await call();
            if (_lastFailure is not null)
            {
                FileLog.Write("[LicenseUsageReporter] the license tool answers again");
                _lastFailure = null;
            }
            return rows;
        }
        catch (LicenseCommandLineException ex)
        {
            if (_lastFailure != ex.Message)
                FileLog.Write($"[LicenseUsageReporter] no report sent FAILED: {ex.Message}");
            _lastFailure = ex.Message;
            return null;
        }
    }
}
