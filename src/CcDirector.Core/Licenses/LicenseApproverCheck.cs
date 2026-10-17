using System.Text.RegularExpressions;
using CcDirector.Core.Background;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Licenses;

/// <summary>
/// License Switching phase 6 (Track C): the Director's way to the Gateway for approver records - this Director's own
/// hub connection. <see cref="CanReach"/> is false while the tunnel is down or the Gateway has neither
/// <c>GetLicenseApproverAssignments</c> nor <c>ReportLicenseApproverChecks</c>; then no check runs.
/// </summary>
public interface ILicenseApproverChannel
{
    bool CanReach { get; }

    /// <summary>The approver records that name this Director's machine and user. Throws
    /// <see cref="LicenseApprovalRelayException"/> when it could not be asked.</summary>
    Task<LicenseApproverAssignments> GetAssignmentsAsync(CancellationToken cancellationToken);

    /// <summary>Sends what the checks found. Throws <see cref="LicenseApprovalRelayException"/> when it could not be sent.</summary>
    Task<LicenseApproverCheckAnswer> ReportAsync(LicenseApproverCheckReport report, CancellationToken cancellationToken);
}

/// <summary>One of this Director's own browser profiles: its identifier (the slug) and its display name.</summary>
public sealed record LicenseBrowserProfile(string Id, string Name);

/// <summary>This Director's own browser profiles. Read once per check run.</summary>
public interface ILicenseBrowserProfiles
{
    IReadOnlyList<LicenseBrowserProfile> List();
}

/// <summary>The profiles in this machine's automation browser registry.</summary>
public sealed class AutomationBrowserProfiles : ILicenseBrowserProfiles
{
    public IReadOnlyList<LicenseBrowserProfile> List() =>
        Browsers.AutomationBrowserRegistry.Load().Select(b => new LicenseBrowserProfile(b.Id, b.Name)).ToList();
}

/// <summary>The sentence that goes with a check row: the Director's own fixed words, never a tool's or a page's
/// (the contract's detail rule). Each is short of <see cref="LicenseApproverContract.MaxDetailLength"/>.</summary>
public static class LicenseApproverCheckSentences
{
    public static string For(LicenseApproverToolAnswer answer) => answer.State switch
    {
        LicenseApproverStates.CanApprove => "The approval page showed the account signed in and ready to approve.",
        LicenseApproverStates.SignedOut => "The browser profile is not signed in to claude.ai.",
        LicenseApproverStates.NeedsFreshSignIn => "The approval page asks for a fresh claude.ai sign-in before it will approve.",
        LicenseApproverStates.WrongAccount => "The browser profile is signed in to claude.ai as another account.",
        _ when answer.ToolAnswered => "The check could not open or read the approval page in this browser profile.",
        _ => "The license tool gave no readable answer to the check on this machine.",
    };
}

/// <summary>
/// License Switching phase 6 (Track C): once at start and then every day, the key holder checks that each browser
/// profile it is an approver for could approve a sign-in NOW, by reading a real approval page
/// (<c>license check-approver</c>, which presses nothing), and reports what it found to the Gateway in one
/// <c>ReportLicenseApproverChecks</c>. An approver that cannot approve is then known within a day, rather than at the
/// moment a sign-in needs it (the 16 October report, problems 9 and 12).
///
/// What is checked: every record the Gateway says names this machine (<c>GetLicenseApproverAssignments</c>), with its
/// account; and, when this machine offers its profiles as approvers (<see cref="Configuration.LicenseApproverOfferSetting"/>),
/// every one of this Director's own profiles with no account, so the tool says who each is signed in as. A check that
/// failed is reported <c>check-failed</c>, never left out, and the report goes even when every row failed.
///
/// One check at a time, and never during a sign-in or an approval: each check enters the
/// <see cref="LicenseBrowserLane"/>, which says why it is that lane and not another lock. Starting a stopped profile
/// for a check and stopping it again after is the tool's, as it is for an approval.
/// </summary>
public sealed class LicenseApproverCheck
{
    /// <summary>How often the job looks at whether a run is due, as the renewal does: a run itself happens once per
    /// <see cref="RunInterval"/>, and a run the Gateway could not take is sent again at the next look.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);

    /// <summary>A run a day. The Gateway reads a record with no check in 26 hours as not checked.</summary>
    public static readonly TimeSpan RunInterval = TimeSpan.FromHours(24);

    /// <summary>The shape the Gateway takes for a profile's display name (<c>LicenseApproverStore.BrowserProfileShape</c>).
    /// A name of any other shape is not sent - the row would be refused whole for an optional field.</summary>
    private static readonly Regex NameShape = new("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$", RegexOptions.CultureInvariant);

    private readonly ILicenseCommandLine _tool;
    private readonly LicenseKeyHolderLock _holder;
    private readonly LicenseBrowserLane _lane;
    private readonly ILicenseBrowserProfiles _profiles;
    private readonly Func<DateTime> _utcNow;
    private readonly bool _enabled;
    private readonly bool _offersApprovers;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private volatile ILicenseApproverChannel? _channel;
    private LicenseApproverCheckReport? _unsent;
    private DateTime? _lastRunUtc;
    private string? _lastFailure;
    private BackgroundJob? _job;

    /// <param name="enabled">The license switching setting. Off: no check ever runs.</param>
    /// <param name="offersApprovers">The offer setting: whether this Director's own profiles are checked too.</param>
    public LicenseApproverCheck(ILicenseCommandLine tool, LicenseKeyHolderLock holder, LicenseBrowserLane lane,
        ILicenseBrowserProfiles profiles, Func<DateTime> utcNow, bool enabled, bool offersApprovers)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _holder = holder ?? throw new ArgumentNullException(nameof(holder));
        _lane = lane ?? throw new ArgumentNullException(nameof(lane));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _enabled = enabled;
        _offersApprovers = offersApprovers;
    }

    /// <summary>Whether this machine offers its browser profiles as approvers (read once, at start).</summary>
    public bool OffersApprovers => _offersApprovers;

    /// <summary>When the last run's report was taken by the Gateway; null before the first.</summary>
    public DateTime? LastRunUtc => _lastRunUtc;

    /// <summary>Whether the check is on the scheduler.</summary>
    public bool IsScheduled => _job is not null;

    /// <summary>The hub connection to ask and report on. Replaced when the Director's Gateway settings change; null
    /// while there is none.</summary>
    public void AttachChannel(ILicenseApproverChannel? channel)
    {
        FileLog.Write($"[LicenseApproverCheck] AttachChannel: attached={channel is not null}");
        _channel = channel;
    }

    /// <summary>Puts the check on the Director's scheduler for the life of the process. Only called while license
    /// switching is on (<see cref="LicenseSwitching.Start"/>).</summary>
    public void Start(BackgroundJobs jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (_job is not null) return;
        _job = jobs.Register(
            new BackgroundJobSpec("License approver check", BackgroundJobTier.SlowAndSteady, CheckInterval,
                "license switching is turned off on this Director"),
            async ct => await TickAsync(ct));
        _job.StartTimer(TimeSpan.Zero);
        FileLog.Write($"[LicenseApproverCheck] Start: a run every {RunInterval.TotalHours:0} hours and at start, offersApprovers={_offersApprovers}");
    }

    /// <summary>Takes the check off the scheduler.</summary>
    public void Stop()
    {
        _job?.Dispose();
        _job = null;
    }

    /// <summary>
    /// One step: when license switching is on, this Director is the key holder, the Gateway can be reached and a run is
    /// due, check every profile there is to check and report it. Returns the report sent, or null when none was. Public
    /// for tests; the scheduler is the only production caller.
    /// </summary>
    public async Task<LicenseApproverCheckReport?> TickAsync(CancellationToken cancellationToken)
    {
        if (!_enabled || !_holder.IsHeld) return null;
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            var channel = _channel;
            if (channel is null || !channel.CanReach)
            {
                NoteFailure("no Gateway tunnel that keeps approver records");
                return null;
            }

            // A report the Gateway did not take is sent again as it is, without checking every profile again - but
            // only for a day: the Gateway refuses a row older than its not-checked window, so after that the checks
            // are run again.
            if (_unsent is not null && _unsent.Rows.Count > 0 && _utcNow() - _unsent.Rows.Min(r => r.CheckedAtUtc) >= RunInterval)
            {
                FileLog.Write("[LicenseApproverCheck] the unsent report is a day old; the checks are run again");
                _unsent = null;
            }
            if (_unsent is null)
            {
                if (_lastRunUtc is { } last && _utcNow() - last < RunInterval) return null;
                LicenseApproverAssignments assignments;
                try
                {
                    assignments = await channel.GetAssignmentsAsync(cancellationToken);
                }
                catch (LicenseApprovalRelayException ex)
                {
                    NoteFailure($"the approver records could not be asked for: {ex.Message}");
                    return null;
                }
                _unsent = await CheckAllAsync(assignments, cancellationToken);
            }

            var report = _unsent;
            try
            {
                var answer = await channel.ReportAsync(report, cancellationToken);
                FileLog.Write($"[LicenseApproverCheck] report taken: rows={report.Rows.Count}, outcomes={string.Join(",", answer.Rows.GroupBy(r => r.Outcome + (r.Reason is null ? "" : "/" + r.Reason)).Select(g => $"{g.Key}={g.Count()}"))}");
            }
            catch (LicenseApprovalRelayException ex)
            {
                NoteFailure($"the approver report could not be sent, it is sent again at the next look: {ex.Message}");
                return null;
            }
            _unsent = null;
            _lastFailure = null;
            _lastRunUtc = _utcNow();
            return report;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Checks each assigned record with its account and, when offered, each own profile with none.</summary>
    private async Task<LicenseApproverCheckReport> CheckAllAsync(LicenseApproverAssignments assignments, CancellationToken cancellationToken)
    {
        var own = _profiles.List();
        var names = own.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var work = assignments.Rows.Select(a => (Profile: a.BrowserProfile, Email: (string?)a.AccountEmail.Trim().ToLowerInvariant())).ToList();
        if (_offersApprovers)
            work.AddRange(own.Select(p => (Profile: p.Id, Email: (string?)null)));

        var report = new LicenseApproverCheckReport { OffersApprovers = _offersApprovers };
        foreach (var (profile, email) in work)
        {
            if (report.Rows.Count == LicenseApproverContract.MaxCheckRows)
            {
                FileLog.Write($"[LicenseApproverCheck] {work.Count - report.Rows.Count} more checks not run: one report takes at most {LicenseApproverContract.MaxCheckRows} rows");
                break;
            }
            LicenseApproverToolAnswer answer;
            using (await _lane.EnterCheckAsync(cancellationToken))
                answer = await _tool.CheckApproverAsync(profile, email, cancellationToken);
            names.TryGetValue(profile, out var name);
            report.Rows.Add(new LicenseApproverCheckRow
            {
                BrowserProfile = profile,
                BrowserProfileName = name is not null && NameShape.IsMatch(name) ? name : null,
                AccountEmail = answer.AccountEmail,
                ShownAccount = answer.ShownAccount,
                State = answer.State,
                CheckedAtUtc = _utcNow(),
                Detail = LicenseApproverCheckSentences.For(answer),
            });
            FileLog.Write($"[LicenseApproverCheck] checked profile={profile}, account={answer.AccountEmail ?? "-"}, state={answer.State}");
        }
        FileLog.Write($"[LicenseApproverCheck] run: assigned={assignments.Rows.Count}, ownProfilesChecked={(_offersApprovers ? own.Count : 0)}, rows={report.Rows.Count}, failed={report.Rows.Count(r => r.State == LicenseApproverStates.CheckFailed)}");
        return report;
    }

    /// <summary>Logged once per distinct reason, because it would repeat every ten minutes.</summary>
    private void NoteFailure(string reason)
    {
        if (_lastFailure != reason)
            FileLog.Write($"[LicenseApproverCheck] no run: {reason}");
        _lastFailure = reason;
    }
}
