using CcDirector.Core.Background;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Licenses;

/// <summary>
/// License Switching contract 8.2: the key holder renews this machine's sign-ins before they die. A sign-in lives a
/// little under thirty days however often its key is refreshed, and the ACTIVE account's dies too, so once a day and
/// once at start the key holder asks the license tool which held accounts are due (<c>license renew --check</c>: the
/// sign-in ends within five days) and signs each of those in again, in place.
///
/// Each one goes through <see cref="LicenseSignInCoordinator"/>, the same path a sign-in the Gateway asks for takes:
/// approved in the browser profile set for the account on this machine, or - with none here - on another machine
/// (contract 8.3). Every failure is recorded against its holding and travels in the next report (8.1), so a renewal
/// that failed is seen on the Licenses page and by email before the sign-in dies - never only in this log.
///
/// A renewal never interrupts a switch: both take the key store's lock, and a renewal that finds it taken is that
/// account's <c>store-refused</c>, tried again at the next run.
/// </summary>
public sealed class LicenseRenewer
{
    /// <summary>How often the job looks at whether a run is due. A run itself happens once per
    /// <see cref="LicenseContract.RenewalRunInterval"/>; the short check is what makes "once at start" happen as soon
    /// as this Director is the key holder, and what tries again soon after the tool could not answer.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);

    private readonly ILicenseCommandLine _tool;
    private readonly LicenseKeyHolderLock _holder;
    private readonly LicenseUsageReporter _reporter;
    private readonly LicenseSignInCoordinator _signIns;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private DateTime? _lastRunUtc;
    private string? _lastFailure;
    private BackgroundJob? _job;

    public LicenseRenewer(ILicenseCommandLine tool, LicenseKeyHolderLock holder, LicenseUsageReporter reporter,
        LicenseSignInCoordinator signIns, Func<DateTime> utcNow)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _holder = holder ?? throw new ArgumentNullException(nameof(holder));
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _signIns = signIns ?? throw new ArgumentNullException(nameof(signIns));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>When the last renewal run finished asking the tool; null before the first one.</summary>
    public DateTime? LastRunUtc => _lastRunUtc;

    /// <summary>Whether the renewal is on the scheduler.</summary>
    public bool IsScheduled => _job is not null;

    /// <summary>Puts the renewal on the Director's scheduler for the life of the process. Only called while license
    /// switching is on (<see cref="LicenseSwitching.Start"/>).</summary>
    public void Start(BackgroundJobs jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (_job is not null) return;
        _job = jobs.Register(
            new BackgroundJobSpec("License sign-in renewal", BackgroundJobTier.SlowAndSteady, CheckInterval,
                "license switching is turned off on this Director"),
            async ct => await TickAsync(ct));
        _job.StartTimer(TimeSpan.Zero);
        FileLog.Write($"[LicenseRenewer] Start: a run every {LicenseContract.RenewalRunInterval.TotalHours:0} hours and at start, checked every {CheckInterval.TotalMinutes:0} minutes");
    }

    /// <summary>Takes the renewal off the scheduler.</summary>
    public void Stop()
    {
        _job?.Dispose();
        _job = null;
    }

    /// <summary>
    /// One step: when this Director is the key holder and a run is due, renew what is due and record what happened.
    /// Returns the rows of the run, or null when no run happened. Public for tests; the scheduler is the only
    /// production caller.
    /// </summary>
    public async Task<IReadOnlyList<LicenseRenewalRow>?> TickAsync(CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            // The usage reporter takes the lock too, but only while a Gateway can take a report. A renewal is this
            // machine's own business and must not wait for a tunnel.
            if (_holder.TryAcquire())
                _reporter.RequestReport(LicenseReportTriggers.Connected);
            if (!_holder.IsHeld) return null;

            var now = _utcNow();
            if (_lastRunUtc is { } last && now - last < LicenseContract.RenewalRunInterval) return null;

            IReadOnlyList<LicenseRenewalRow> rows;
            try
            {
                rows = await _tool.CheckRenewalsAsync(cancellationToken);
            }
            catch (LicenseCommandLineException ex)
            {
                // Tried again at the next check. Logged once per distinct message, because it would repeat every ten minutes.
                if (_lastFailure != ex.Message)
                    FileLog.Write($"[LicenseRenewer] no renewal run FAILED: {ex.Message}");
                _lastFailure = ex.Message;
                return null;
            }
            _lastFailure = null;
            _lastRunUtc = now;

            var changed = false;
            foreach (var row in rows)
            {
                if (row.Result == LicenseRenewalResults.Due)
                    await RenewAsync(row, cancellationToken);

                changed |= row.Result == LicenseRenewalResults.Failed
                    ? _reporter.RecordRenewal(row.AccountEmail, _utcNow(), row.Error ?? LicenseRenewalErrors.LoginFailed)
                    : _reporter.RecordRenewal(row.AccountEmail, null, null) || row.Result == LicenseRenewalResults.Renewed;
            }

            FileLog.Write($"[LicenseRenewer] run: accounts={rows.Count}, renewed={rows.Count(r => r.Result == LicenseRenewalResults.Renewed)}, failed={rows.Count(r => r.Result == LicenseRenewalResults.Failed)}");
            // Section 8.2: each row that changed - renewed, or failed - makes a "changed" report.
            if (changed) _reporter.RequestReport(LicenseReportTriggers.Changed);
            return rows;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Signs one due account in again and writes what happened into its row: renewed, or failed with the reason.</summary>
    private async Task RenewAsync(LicenseRenewalRow row, CancellationToken cancellationToken)
    {
        var answer = await _signIns.RenewAsync(row.AccountEmail, row.HasBrowserProfileHere, cancellationToken);
        if (answer.Succeeded)
        {
            row.Result = LicenseRenewalResults.Renewed;
            row.Error = null;
            row.SignedInAtUtc = answer.SignedInAtUtc;
            row.SignInExpiresAtUtc = answer.SignInExpiresAtUtc ?? row.SignInExpiresAtUtc;
            return;
        }
        row.Result = LicenseRenewalResults.Failed;
        row.Error = answer.ReasonCode ?? LicenseRenewalErrors.LoginFailed;
    }
}
