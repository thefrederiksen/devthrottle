using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// READS WHAT IS USING THE LICENSE for one DevThrottle account: everything <see cref="SessionUsageFold"/> needs, from the
/// usage record, the license picture and the live roster, and the folded answer. What the usage route and the
/// "make it last" planner answer with.
/// </summary>
public sealed class SessionUsageService
{
    /// <summary>The most a session row may lie unseen and still be shown with <c>closed=true</c>.</summary>
    public static readonly TimeSpan GoneShownFor = TimeSpan.FromDays(7);

    private readonly SessionUsageStore _usage;
    private readonly LicenseStore _licenses;
    private readonly LicenseReadModel _reads;
    private readonly Func<TenantId, IReadOnlySet<string>> _liveSessionIds;

    /// <param name="liveSessionIds">The ids of the sessions in a connected Director's roster of a tenant now.</param>
    public SessionUsageService(SessionUsageStore usage, LicenseStore licenses, LicenseReadModel reads,
        Func<TenantId, IReadOnlySet<string>> liveSessionIds)
    {
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _licenses = licenses ?? throw new ArgumentNullException(nameof(licenses));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _liveSessionIds = liveSessionIds ?? throw new ArgumentNullException(nameof(liveSessionIds));
    }

    /// <summary>
    /// What is using <paramref name="accountEmail"/> (null: the account in use, else the first account known) for
    /// <paramref name="tenant"/>. Null when the account asked for is not one this DevThrottle account knows.
    /// </summary>
    public LicenseUsageResponse? Read(TenantId tenant, string? accountEmail, bool includeGone, DateTime nowUtc)
    {
        FileLog.Write($"[SessionUsageService] Read: tenant={tenant.ToLogString()}, account={accountEmail ?? "(default)"}, includeGone={includeGone}");
        try
        {
            var input = Input(tenant, accountEmail, includeGone, nowUtc);
            if (input is null)
            {
                FileLog.Write("[SessionUsageService] Read: no such account");
                return null;
            }
            var response = SessionUsageFold.Fold(input);
            FileLog.Write($"[SessionUsageService] Read: account={response.AccountEmail ?? "(none)"}, sessions={response.Sessions.Count}, measured={response.Calibration.IsMeasured}");
            return response;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionUsageService] Read FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>The fold's input; null when <paramref name="accountEmail"/> names no account this tenant knows.</summary>
    public SessionUsageFoldInput? Input(TenantId tenant, string? accountEmail, bool includeGone, DateTime nowUtc)
    {
        var overview = _reads.Read(tenant, nowUtc);
        var inUse = overview.Machines
            .Where(m => !m.IsStale && m.ActiveAccountEmail is not null)
            .Select(m => m.ActiveAccountEmail!)
            .ToHashSet(StringComparer.Ordinal);
        var accounts = overview.Accounts
            .Select(a => (a.AccountEmail, InUse: inUse.Contains(a.AccountEmail)))
            .ToList();

        string? account;
        if (accountEmail is not null)
        {
            account = LicenseStore.NormalizeEmail(accountEmail);
            if (accounts.All(a => a.AccountEmail != account))
                return null;
        }
        else
            account = accounts.Where(a => a.InUse).Select(a => a.AccountEmail).Order(StringComparer.Ordinal).FirstOrDefault()
                      ?? accounts.Select(a => a.AccountEmail).Order(StringComparer.Ordinal).FirstOrDefault();

        var live = _liveSessionIds(tenant);
        var rows = _usage.ReadSessions(tenant, nowUtc - GoneShownFor);
        var freshest = account is null ? null : overview.Accounts.First(a => a.AccountEmail == account).Freshest;
        var calibration = account is null
            ? SessionUsageFold.Calibrate(null, "Not measured yet: no account.", 0)
            : Calibration(tenant, account, nowUtc);
        long? price = account is not null && _licenses.ReadMonthlyPrices(tenant).TryGetValue(account, out var cents) ? cents : null;

        return new SessionUsageFoldInput(
            nowUtc, account, accounts,
            freshest?.SevenDayUtilization, freshest?.SevenDayResetsAtUtc,
            calibration, rows, live,
            _usage.UseSince(tenant, nowUtc - TimeSpan.FromHours(1)),
            _usage.UseSince(tenant, nowUtc - TimeSpan.FromHours(4)),
            price, includeGone);
    }

    /// <summary>The account's calibration, measured over the week its kept readings show.</summary>
    public SessionUsageCalibration Calibration(TenantId tenant, string accountEmail, DateTime nowUtc)
    {
        var email = LicenseStore.NormalizeEmail(accountEmail);
        var readings = _usage.ReadReadings(tenant, nowUtc - SessionUsageStore.ReadingRetention)
            .Where(r => r.AccountEmail == email)
            .ToList();
        var (span, whyNot) = SessionUsageFold.CalibrationSpan(readings);
        var weighted = span is null ? 0 : _usage.AccountUseBetween(tenant, email, span.FromUtc, span.ToUtc);
        return SessionUsageFold.Calibrate(span, whyNot, weighted);
    }
}
