using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE RECENT READINGS the forecast takes its rates from (License Switching): for each account of each tenant, the
/// readings of the last <see cref="Kept"/> of each window, kept in memory and fed by every accepted usage report.
///
/// IN MEMORY ON PURPOSE. A rate needs minutes of readings, not months, and a report arrives every two minutes; after a
/// Gateway restart the series is empty and fills again from the next reports, and until it holds two readings the
/// forecast simply shows no rate. Nothing is written to the database for it.
///
/// A reading is one point per (account, window, read time). A failed read repeats the previous numbers with their old
/// read time (contract section 2.2), and several machines can hold one account, so the same point arrives many times
/// and is kept once. A read time the Gateway judges to be ahead of its own clock by more than the skew limit is not
/// kept: it could never age out.
///
/// Logging: none. <see cref="Record"/> runs on every accepted report, whose acceptance the ingest already logs, and
/// <see cref="Read"/> is a pure read of memory.
/// </summary>
public sealed class LicenseUsageHistory
{
    /// <summary>How long a reading is kept.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromHours(2);

    private sealed class Series
    {
        public readonly SortedDictionary<DateTime, double> FiveHour = new();
        public readonly SortedDictionary<DateTime, double> SevenDay = new();
    }

    private readonly ConcurrentDictionary<TenantId, ConcurrentDictionary<string, Series>> _tenants = new();

    /// <summary>
    /// Keep the readings of one ACCEPTED report. <paramref name="readAtShift"/> is what the ingest adds to each
    /// <c>ReadAtUtc</c> to get the time the Gateway judges the read by (contract section 5.2), so a machine with a wrong
    /// clock lands its points where the Gateway's clock puts them.
    /// </summary>
    public void Record(TenantId tenant, LicenseUsageReport report, DateTime receivedAtUtc, TimeSpan readAtShift)
    {
        ArgumentNullException.ThrowIfNull(report);
        var accounts = _tenants.GetOrAdd(tenant, _ => new ConcurrentDictionary<string, Series>(StringComparer.Ordinal));
        var oldest = receivedAtUtc - Kept;
        var newest = receivedAtUtc + LicenseContract.ClockSkewLoggedAbove;
        foreach (var holding in report.Holdings)
        {
            if (holding.ReadAtUtc is not { } readAt)
                continue;
            var at = readAt + readAtShift;
            if (at < oldest || at > newest)
                continue;
            var series = accounts.GetOrAdd(LicenseStore.NormalizeEmail(holding.AccountEmail), _ => new Series());
            lock (series)
            {
                if (holding.FiveHour is { } five)
                    series.FiveHour[at] = five.Utilization;
                if (holding.SevenDay is { } week)
                    series.SevenDay[at] = week.Utilization;
                Prune(series.FiveHour, oldest);
                Prune(series.SevenDay, oldest);
            }
        }
    }

    /// <summary>The readings of the last <see cref="Kept"/> for every account of <paramref name="tenant"/> that has any,
    /// in account order, each window oldest first.</summary>
    public IReadOnlyList<LicenseAccountHistory> Read(TenantId tenant, DateTime nowUtc)
    {
        if (!_tenants.TryGetValue(tenant, out var accounts))
            return Array.Empty<LicenseAccountHistory>();
        var oldest = nowUtc - Kept;
        var histories = new List<LicenseAccountHistory>();
        foreach (var (email, series) in accounts.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            lock (series)
            {
                var five = Points(series.FiveHour, oldest);
                var week = Points(series.SevenDay, oldest);
                if (five.Count > 0 || week.Count > 0)
                    histories.Add(new LicenseAccountHistory(email, five, week));
            }
        }
        return histories;
    }

    private static List<LicenseUsagePoint> Points(SortedDictionary<DateTime, double> window, DateTime oldest)
        => window.Where(p => p.Key >= oldest).Select(p => new LicenseUsagePoint(p.Key, p.Value)).ToList();

    private static void Prune(SortedDictionary<DateTime, double> window, DateTime oldest)
    {
        while (window.Count > 0 && window.First().Key < oldest)
            window.Remove(window.First().Key);
    }
}
