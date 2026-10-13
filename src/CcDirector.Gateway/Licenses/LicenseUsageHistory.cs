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
/// carries its previous numbers and their old read time (contract section 2.2), so it adds nothing new: a reading is
/// known by the machine's OWN read time, as it was sent, and one already held is not added again. The corrected time
/// cannot be used for that - the correction of a machine whose clock is wrong differs by milliseconds from one
/// report to the next, and the same reading would be kept again by every report.
///
/// WHAT ONE DEVTHROTTLE ACCOUNT CAN MAKE THE GATEWAY HOLD IS BOUNDED, whatever its Directors send:
/// <list type="bullet">
/// <item>at most <see cref="MaxAccountsPerTenant"/> accounts - past that, the account heard from longest ago goes;</item>
/// <item>at most <see cref="MaxPointsPerWindow"/> points for one window of one account;</item>
/// <item>no point later than the Gateway's own clock. A reading stamped in the future - a machine with a wrong
/// clock, or a Director with a defect - is kept at now, so it ages out in <see cref="Kept"/> like any other
/// instead of staying for ever.</item>
/// </list>
/// A DevThrottle account whose machines stop reporting keeps its last two hours until it is read or the Gateway
/// restarts: that is at most the bound above, once.
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

    /// <summary>The most accounts kept for one DevThrottle account. One report may name
    /// <see cref="LicenseUsageIngest.MaxHoldings"/> accounts, so this is every account of a fleet many times the size
    /// of any real one, and still a fixed ceiling.</summary>
    public const int MaxAccountsPerTenant = 256;

    // One kept reading: when the Gateway judges it by, its number, and the machine's own read time as it was sent.
    private readonly record struct Reading(DateTime AtUtc, double Utilization, DateTime ReadAtUtc);

    private sealed class Series
    {
        public readonly List<Reading> FiveHour = new();
        public readonly List<Reading> SevenDay = new();

        public DateTime NewestAtUtc => new[] { FiveHour.Count > 0 ? FiveHour[^1].AtUtc : DateTime.MinValue, SevenDay.Count > 0 ? SevenDay[^1].AtUtc : DateTime.MinValue }.Max();
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
                var read = AsUtc(readAt);
                var at = read + readAtShift;
                if (at > nowUtc)
                    at = nowUtc; // never later than the Gateway's clock: see the class comment
                var email = LicenseStore.NormalizeEmail(holding.AccountEmail);
                if (!accounts.TryGetValue(email, out var series))
                    accounts[email] = series = new Series();
                if (holding.FiveHour is { } five && Add(series.FiveHour, at, five.Utilization, read)) added++;
                if (holding.SevenDay is { } week && Add(series.SevenDay, at, week.Utilization, read)) added++;
            }
            Trim(accounts, nowUtc);
            while (accounts.Count > MaxAccountsPerTenant)
                accounts.Remove(accounts.MinBy(pair => pair.Value.NewestAtUtc).Key);
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
                    .Select(pair => new LicenseAccountHistory(pair.Key, Points(pair.Value.FiveHour), Points(pair.Value.SevenDay)))
                    .ToList();
                if (accounts.Count == 0)
                    _byTenant.Remove(tenant);
            }
        }
        FileLog.Write($"[LicenseUsageHistory] Read: tenant={tenant.ToLogString()}, accounts={histories.Count}");
        return histories;
    }

    private static List<LicenseUsagePoint> Points(List<Reading> kept)
        => kept.Select(k => new LicenseUsagePoint(k.AtUtc, k.Utilization)).ToList();

    // Kept oldest first. A reading with a read time already held is the same reading sent again (another report
    // carrying the numbers of the last successful read), so it is not added twice.
    private static bool Add(List<Reading> points, DateTime atUtc, double utilization, DateTime readAtUtc)
    {
        if (points.Exists(p => p.ReadAtUtc == readAtUtc))
            return false;
        var index = points.Count;
        while (index > 0 && points[index - 1].AtUtc > atUtc)
            index--;
        points.Insert(index, new Reading(atUtc, utilization, readAtUtc));
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
