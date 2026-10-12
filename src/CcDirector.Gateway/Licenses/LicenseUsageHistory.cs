using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE RECENT USAGE READINGS OF EVERY ACCOUNT, per tenant, kept in memory (License Switching). The forecast needs a
/// rate of use, and a rate needs more than the one freshest reading the store keeps; this is the short series
/// <see cref="LicenseForecaster"/> takes as <see cref="LicenseAccountHistory"/>.
///
/// Every ACCEPTED usage report adds one point per window for each holding it carries, at the time the Gateway judges
/// that reading by (its read time corrected for clock skew, contract section 5.2). A holding whose read failed
/// carries its previous numbers and their old read time (contract section 2.2), so it adds nothing new: a point at a
/// time already held is not added again.
///
/// Kept in memory on purpose: it is two hours of numbers that the Directors send again every two minutes, so after a
/// Gateway restart the forecast has a rate again within two reports. Until then it says there are not enough readings.
/// Nothing here can carry a sign-in: account emails, percentages and times.
/// </summary>
public sealed class LicenseUsageHistory
{
    /// <summary>How long a point is kept.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromHours(2);

    /// <summary>The most points kept for one window of one account - far more than two hours of two-minute reports
    /// from several machines, and a ceiling on what a misbehaving Director can make the Gateway hold.</summary>
    public const int MaxPointsPerWindow = 600;

    private sealed class Series
    {
        public readonly List<LicenseUsagePoint> FiveHour = new();
        public readonly List<LicenseUsagePoint> SevenDay = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<TenantId, Dictionary<string, Series>> _byTenant = new();

    /// <summary>
    /// Add the readings of one accepted report. <paramref name="readAtShift"/> is what the ingest adds to every
    /// <c>ReadAtUtc</c> to get the time staleness is judged by; <paramref name="nowUtc"/> is the Gateway's clock, and
    /// points older than <see cref="Kept"/> before it are dropped.
    /// </summary>
    public void Record(TenantId tenant, LicenseUsageReport report, TimeSpan readAtShift, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(report);
        var added = 0;
        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var accounts))
                _byTenant[tenant] = accounts = new Dictionary<string, Series>(StringComparer.Ordinal);

            foreach (var holding in report.Holdings)
            {
                if (holding.ReadAtUtc is not { } readAt)
                    continue; // never read: there is no number to keep
                var at = AsUtc(readAt) + readAtShift;
                var email = LicenseStore.NormalizeEmail(holding.AccountEmail);
                if (!accounts.TryGetValue(email, out var series))
                    accounts[email] = series = new Series();
                if (holding.FiveHour is { } five && Add(series.FiveHour, at, five.Utilization)) added++;
                if (holding.SevenDay is { } week && Add(series.SevenDay, at, week.Utilization)) added++;
            }
            Trim(accounts, nowUtc);
            if (accounts.Count == 0)
                _byTenant.Remove(tenant);
        }
        FileLog.Write($"[LicenseUsageHistory] Record: tenant={tenant.ToLogString()}, holdings={report.Holdings.Count}, pointsAdded={added}");
    }

    /// <summary>The kept readings of every account of <paramref name="tenant"/>, in account email order, each window
    /// oldest first. An account with no point left is not listed.</summary>
    public IReadOnlyList<LicenseAccountHistory> Read(TenantId tenant, DateTime nowUtc)
    {
        List<LicenseAccountHistory> histories;
        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var accounts))
                histories = new List<LicenseAccountHistory>();
            else
            {
                Trim(accounts, nowUtc);
                histories = accounts
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new LicenseAccountHistory(pair.Key, pair.Value.FiveHour.ToList(), pair.Value.SevenDay.ToList()))
                    .ToList();
                if (accounts.Count == 0)
                    _byTenant.Remove(tenant);
            }
        }
        FileLog.Write($"[LicenseUsageHistory] Read: tenant={tenant.ToLogString()}, accounts={histories.Count}");
        return histories;
    }

    // Kept oldest first. A point at a time already held is the same reading sent again (another report carrying
    // the numbers of the last successful read), so it is not added twice.
    private static bool Add(List<LicenseUsagePoint> points, DateTime atUtc, double utilization)
    {
        var index = points.Count;
        while (index > 0 && points[index - 1].AtUtc > atUtc)
            index--;
        if (index > 0 && points[index - 1].AtUtc == atUtc)
            return false;
        points.Insert(index, new LicenseUsagePoint(atUtc, utilization));
        if (points.Count > MaxPointsPerWindow)
            points.RemoveRange(0, points.Count - MaxPointsPerWindow);
        return true;
    }

    private static void Trim(Dictionary<string, Series> accounts, DateTime nowUtc)
    {
        var oldest = nowUtc - Kept;
        foreach (var (email, series) in accounts.ToList())
        {
            series.FiveHour.RemoveAll(p => p.AtUtc < oldest);
            series.SevenDay.RemoveAll(p => p.AtUtc < oldest);
            if (series.FiveHour.Count == 0 && series.SevenDay.Count == 0)
                accounts.Remove(email);
        }
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };
}
