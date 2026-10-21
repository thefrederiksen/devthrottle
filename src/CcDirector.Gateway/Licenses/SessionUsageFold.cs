using System.Globalization;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>How one account's weighted use becomes percent of its weekly limit (<see cref="SessionUsageFold.Calibrate"/>).</summary>
/// <param name="PercentPerWeighted">Percent of the weekly limit per weighted unit; null when not measured.</param>
public sealed record SessionUsageCalibration(
    double? PercentPerWeighted,
    DateTime? FromUtc,
    DateTime? ToUtc,
    double? RisePercent,
    double? Weighted,
    string Text)
{
    public bool IsMeasured => PercentPerWeighted is not null;
}

/// <summary>The span a calibration is measured over: two readings of the same week, and the rise between them.</summary>
public sealed record SessionUsageCalibrationSpan(DateTime FromUtc, DateTime ToUtc, double RisePercent);

/// <summary>Everything <see cref="SessionUsageFold.Fold"/> needs, read by <see cref="SessionUsageService"/>.</summary>
/// <param name="AccountEmail">The account asked about, lower-cased; null when none is known.</param>
/// <param name="Accounts">Every account known, with whether a machine is on it now.</param>
/// <param name="Rows">Every session row in the record that may be shown, and every one a shown row's controllers lead to.</param>
/// <param name="Live">The sessions in a connected Director's roster now.</param>
/// <param name="LastHour">Each session's weighted use per account over the last hour.</param>
/// <param name="Last4Hours">The same over the last four hours.</param>
/// <param name="MonthlyPriceCents">The account's subscription price a month, as set; null when not set.</param>
public sealed record SessionUsageFoldInput(
    DateTime NowUtc,
    string? AccountEmail,
    IReadOnlyList<(string AccountEmail, bool InUse)> Accounts,
    double? WeekUsedPercent,
    DateTime? WeekResetsAtUtc,
    SessionUsageCalibration Calibration,
    IReadOnlyList<SessionUsageEntity> Rows,
    IReadOnlySet<string> Live,
    IReadOnlyList<SessionAccountUse> LastHour,
    IReadOnlyList<SessionAccountUse> Last4Hours,
    long? MonthlyPriceCents,
    bool IncludeGone);

/// <summary>
/// WHAT IS USING THE LICENSE, FOLDED ONCE (the usage-by-session mandate; CLAUDE.md rule 7). Pure: the store's rows in,
/// the finished answer out. The Cockpit page and <c>cc-devthrottle license sessions</c> show it as given.
///
/// THE UNIT IS PERCENT OF ONE ACCOUNT'S WEEKLY LIMIT. A session's weighted use (<see cref="SessionUsageMath"/>) on the
/// account is multiplied by the account's CALIBRATION: the rise of the account's weekly use between two of its readings
/// of the same week, over the weighted use every session made on it between the same two times. So the price of a model
/// is measured from the account's own numbers, never assumed. Use the record cannot see - a chat in a browser, a machine
/// without a Director - rises the weekly use too, so it is spread over the sessions; the sessions together then add up to
/// what the account is really burning, which is what the "make it last" question needs.
///
/// A session is shown for an account when it has used that account, or is live on a machine that is on it now.
/// </summary>
public static class SessionUsageFold
{
    /// <summary>The weekly use must have risen at least this much (in points) before a calibration is trusted: the
    /// provider reports whole points, and one point over a large use is already a fair measure.</summary>
    public const double MinimumRisePercent = 1.0;

    /// <summary>Two readings whose reset times are within this of each other are of the same week: the forecaster's.</summary>
    public static readonly TimeSpan SameWeekTolerance = LicenseForecaster.SameWeekTolerance;

    private static readonly TimeSpan ShortestCalibration = TimeSpan.FromHours(1);

    /// <summary>
    /// The span to calibrate over, from one account's kept readings (any order): the earliest and the latest reading of
    /// the week the latest one is in. Null, with the reason, when there is no such span yet.
    /// </summary>
    public static (SessionUsageCalibrationSpan? Span, string? WhyNot) CalibrationSpan(IReadOnlyList<LicenseUsageReadingRecord> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var usable = readings
            .Where(r => r.SevenDayUtilization is not null && r.SevenDayResetsAtUtc is not null)
            .OrderBy(r => r.AtUtc)
            .ToList();
        if (usable.Count == 0)
            return (null, "Not measured yet: no reading of this account's weekly use has been kept.");
        var latest = usable[^1];
        var week = usable.Where(r => (r.SevenDayResetsAtUtc!.Value - latest.SevenDayResetsAtUtc!.Value).Duration() <= SameWeekTolerance).ToList();
        var first = week[0];
        var rise = latest.SevenDayUtilization!.Value - first.SevenDayUtilization!.Value;
        if (latest.AtUtc - first.AtUtc < ShortestCalibration || rise < MinimumRisePercent)
            return (null, $"Not measured yet: its weekly use has risen {Points(Math.Max(rise, 0))} since {Ago(first.AtUtc, latest.AtUtc)}, " +
                          $"and it needs at least {Points(MinimumRisePercent)} over at least an hour to measure what a session costs.");
        return (new SessionUsageCalibrationSpan(first.AtUtc, latest.AtUtc, rise), null);
    }

    /// <summary>The calibration from a span and the weighted use every session made on the account across it.</summary>
    public static SessionUsageCalibration Calibrate(SessionUsageCalibrationSpan? span, string? whyNot, double weightedInSpan)
    {
        if (span is null)
            return new SessionUsageCalibration(null, null, null, null, null, whyNot ?? "Not measured yet.");
        if (weightedInSpan <= 0)
            return new SessionUsageCalibration(null, span.FromUtc, span.ToUtc, span.RisePercent, 0,
                $"Not measured yet: its weekly use rose {Points(span.RisePercent)} over {Hours(span.ToUtc - span.FromUtc)}, " +
                "but no session in the record used it then - the use came from somewhere DevThrottle does not see.");
        var perWeighted = span.RisePercent / weightedInSpan;
        return new SessionUsageCalibration(perWeighted, span.FromUtc, span.ToUtc, span.RisePercent, weightedInSpan,
            $"Measured between this week's first kept reading and its latest, {Hours(span.ToUtc - span.FromUtc)} apart: the weekly use rose {Points(span.RisePercent)} while the sessions " +
            $"used {Millions(weightedInSpan)} weighted units, so a million weighted units is about {Pct(perWeighted * 1_000_000)} of the week.");
    }

    /// <summary>What one percent of a week is worth, from a monthly price in cents.</summary>
    public static double? DollarsPerPercent(long? monthlyPriceCents)
        => monthlyPriceCents is { } cents ? cents / 100.0 * 12 / 52 / 100 : null;

    public static LicenseUsageResponse Fold(SessionUsageFoldInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var now = input.NowUtc;
        var account = input.AccountEmail;
        var f = input.Calibration.PercentPerWeighted;
        var dollarsPerPercent = DollarsPerPercent(input.MonthlyPriceCents);
        var response = new LicenseUsageResponse
        {
            AsOfUtc = now,
            AccountEmail = account,
            AccountInUse = account is not null && input.Accounts.Any(a => a.AccountEmail == account && a.InUse),
            Accounts = input.Accounts
                .OrderBy(a => a.AccountEmail, StringComparer.Ordinal)
                .Select(a => new LicenseUsageAccountChoiceDto { AccountEmail = a.AccountEmail, InUse = a.InUse, Label = a.InUse ? $"{a.AccountEmail} (in use)" : a.AccountEmail })
                .ToList(),
            Calibration = new LicenseUsageCalibrationDto
            {
                IsMeasured = input.Calibration.IsMeasured,
                PercentPerMillionWeighted = f * 1_000_000,
                FromUtc = input.Calibration.FromUtc,
                ToUtc = input.Calibration.ToUtc,
                RisePercent = input.Calibration.RisePercent,
                Weighted = input.Calibration.Weighted,
                Text = input.Calibration.Text,
            },
            WeekUsedPercent = input.WeekUsedPercent,
            WeekResetsAtUtc = input.WeekResetsAtUtc,
            MonthlyPriceUsd = input.MonthlyPriceCents is { } c ? c / 100.0 : null,
            DollarsPerPercent = dollarsPerPercent,
        };

        if (account is null)
        {
            response.EmptyText = "No machine has reported a Claude account yet, so there is nothing to measure.";
            response.BurnText = "Not known: no account.";
            response.UnitText = UnitText(null, null, null, input.MonthlyPriceCents, dollarsPerPercent);
            return response;
        }

        var byId = input.Rows.ToDictionary(r => r.SessionId, StringComparer.Ordinal);
        var hour = SumFor(input.LastHour, account);
        var four = SumFor(input.Last4Hours, account);

        // Every session that used the account, gone ones included: the burn and the groups are measured over all of
        // them, so the answer does not depend on whether gone sessions are listed.
        var all = new List<LicenseUsageSessionDto>();
        foreach (var row in input.Rows)
        {
            var live = input.Live.Contains(row.SessionId);
            var soFarWeighted = SessionUsageStore.WeightedByAccount(row).TryGetValue(account, out var w) ? w : 0;
            var onAccountNow = live && string.Equals(row.AccountEmail, account, StringComparison.Ordinal);
            if (soFarWeighted <= 0 && !onAccountNow)
                continue;
            var end = live ? now : row.LastSeenAtUtc;
            var runningHours = Math.Max(0, (end - row.StartedAtUtc).TotalHours);
            var soFar = f * soFarWeighted;
            all.Add(new LicenseUsageSessionDto
            {
                SessionId = row.SessionId,
                Number = row.Number,
                Name = string.IsNullOrWhiteSpace(row.Name) ? "(no name)" : row.Name!,
                MissionId = row.MissionId,
                MissionName = row.MissionName,
                TopLevelSessionId = TopLevel(row, byId),
                IsLive = live,
                State = !live ? "gone" : IsWorking(row.State) ? "working" : "waiting",
                StateText = !live ? "Gone" : IsWorking(row.State) ? "Working" : "Waiting",
                MachineName = row.MachineName,
                Model = row.Model,
                AgentKind = row.AgentKind,
                WeightedLastHour = hour.TryGetValue(row.SessionId, out var h) ? h : 0,
                WeightedLast4Hours = four.TryGetValue(row.SessionId, out var h4) ? h4 : 0,
                WeightedSoFar = soFarWeighted,
                RateNowPercentPerHour = f * (hour.TryGetValue(row.SessionId, out var hr) ? hr : 0),
                Last4HoursPercent = f * (four.TryGetValue(row.SessionId, out var fr) ? fr : 0),
                SoFarPercent = soFar,
                RunningHours = runningHours,
                AveragePercentPerHour = soFar is { } s && runningHours >= 0.05 ? s / runningHours : null,
                DollarsSoFar = soFar is { } sd && dollarsPerPercent is { } d ? sd * d : null,
                StartedAtUtc = row.StartedAtUtc,
                LastSeenAtUtc = row.LastSeenAtUtc,
            });
        }

        response.Sessions = all
            .Where(s => s.IsLive || input.IncludeGone)
            .OrderByDescending(s => s.WeightedLastHour)
            .ThenByDescending(s => s.WeightedSoFar)
            .ThenBy(s => s.Number ?? int.MaxValue)
            .ThenBy(s => s.SessionId, StringComparer.Ordinal)
            .ToList();
        response.BurnPercentPerHour = f is null ? null : all.Sum(s => s.RateNowPercentPerHour ?? 0);
        response.BurnText = response.BurnPercentPerHour is { } burn
            ? $"All sessions together are using about {Pct(burn)} of the weekly limit an hour, measured over the last hour."
            : "Not known until the account's calibration is measured.";
        response.UnitText = UnitText(account, response.BurnPercentPerHour, response.Sessions.FirstOrDefault(), input.MonthlyPriceCents, dollarsPerPercent);
        response.TopLevel = Listed(Groups(all, s => s.TopLevelSessionId, id => TopLabel(id, byId), id => byId.TryGetValue(id!, out var r) ? r.Number : null, dollarsPerPercent, topFirst: true), input.IncludeGone);
        response.Missions = Listed(Groups(all, s => s.MissionId?.ToString(), id => MissionLabel(id, all), _ => null, dollarsPerPercent, topFirst: false), input.IncludeGone);
        if (response.Sessions.Count == 0)
            response.EmptyText = input.IncludeGone
                ? $"No session in the record has used {account}."
                : $"No live session is using {account} now.";
        return response;
    }

    /// <summary>The session at the top of <paramref name="row"/>'s chain of controllers, as far as the record knows it.</summary>
    public static string TopLevel(SessionUsageEntity row, IReadOnlyDictionary<string, SessionUsageEntity> byId)
    {
        var current = row;
        var seen = new HashSet<string>(StringComparer.Ordinal) { row.SessionId };
        while (current.ControllerSessionId is { } controller
               && byId.TryGetValue(controller, out var above)
               && seen.Add(controller))
            current = above;
        return current.SessionId;
    }

    private static bool IsWorking(string state) => string.Equals(state, "Working", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, double> SumFor(IReadOnlyList<SessionAccountUse> uses, string account)
        => uses.Where(u => u.AccountEmail == account)
            .GroupBy(u => u.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(u => u.Weighted), StringComparer.Ordinal);

    // Without gone sessions, a group none of whose sessions is live is not listed; the ones listed keep the use of all
    // their sessions.
    private static List<LicenseUsageGroupDto> Listed(List<LicenseUsageGroupDto> groups, bool includeGone)
        => includeGone ? groups : groups.Where(g => g.LiveSessionIds.Count > 0).ToList();

    private static List<LicenseUsageGroupDto> Groups(IReadOnlyList<LicenseUsageSessionDto> sessions,
        Func<LicenseUsageSessionDto, string?> keyOf, Func<string?, string> labelOf, Func<string?, int?> numberOf,
        double? dollarsPerPercent, bool topFirst)
    {
        return sessions
            .GroupBy(keyOf)
            .Select(g =>
            {
                var members = g.OrderByDescending(s => topFirst && s.SessionId == g.Key)
                    .ThenByDescending(s => s.WeightedLastHour)
                    .ThenBy(s => s.Number ?? int.MaxValue)
                    .ToList();
                var calibrated = members.All(s => s.SoFarPercent is not null);
                var soFar = calibrated ? members.Sum(s => s.SoFarPercent!.Value) : (double?)null;
                return new LicenseUsageGroupDto
                {
                    Id = g.Key,
                    Number = numberOf(g.Key),
                    Label = labelOf(g.Key),
                    SessionIds = members.Select(s => s.SessionId).ToList(),
                    LiveSessionIds = members.Where(s => s.IsLive).Select(s => s.SessionId).ToList(),
                    RateNowPercentPerHour = calibrated ? members.Sum(s => s.RateNowPercentPerHour!.Value) : null,
                    Last4HoursPercent = calibrated ? members.Sum(s => s.Last4HoursPercent!.Value) : null,
                    SoFarPercent = soFar,
                    DollarsSoFar = soFar is { } s && dollarsPerPercent is { } d ? s * d : null,
                };
            })
            .OrderByDescending(g => g.RateNowPercentPerHour ?? 0)
            .ThenByDescending(g => g.SoFarPercent ?? 0)
            .ThenBy(g => g.Label, StringComparer.Ordinal)
            .ToList();
    }

    private static string TopLabel(string? id, IReadOnlyDictionary<string, SessionUsageEntity> byId)
    {
        if (id is null || !byId.TryGetValue(id, out var row))
            return "(unknown session)";
        var name = string.IsNullOrWhiteSpace(row.Name) ? "(no name)" : row.Name!;
        return row.Number is { } n ? $"#{n} {name}" : name;
    }

    private static string MissionLabel(string? id, IReadOnlyList<LicenseUsageSessionDto> sessions)
        => id is null ? "No Mission" : sessions.First(s => s.MissionId?.ToString() == id).MissionName ?? "(unnamed Mission)";

    private static List<string> UnitText(string? account, double? burn, LicenseUsageSessionDto? top, long? priceCents, double? dollarsPerPercent)
    {
        var lines = new List<string>
        {
            account is null
                ? "One unit for everything: percent of an account's weekly limit."
                : $"One unit for everything: percent of {account}'s weekly limit.",
            "Rate now is how much of the weekly limit a session is using an hour, measured over its last hour."
            + (burn is { } b && b > 0 && top?.RateNowPercentPerHour is { } r && r > 0
                ? $" All sessions together are using about {Pct(b)} an hour, so a session at {Pct(r)} an hour is {Share(r / b)} of it."
                : ""),
            "So far is how much of the weekly limit a session has used since it started, through every compaction.",
        };
        lines.Add(priceCents is { } c && dollarsPerPercent is { } d
            ? $"The dollar figure is that share of a {Dollars(c / 100.0)}-a-month plan: 1% of a week is about {Dollars(d)}."
            : "Set what the plan costs a month on the Licenses page to see dollars.");
        return lines;
    }

    // ----- words -----

    internal static string Pct(double value) => value.ToString(value < 0.1 ? "0.###" : "0.##", CultureInfo.InvariantCulture) + "%";

    private static string Points(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + (value == 1 ? " point" : " points");

    private static string Millions(double value) => (value / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + " million";

    private static string Dollars(double value) => "$" + value.ToString(value < 10 ? "0.00" : "0", CultureInfo.InvariantCulture);

    private static string Share(double fraction) => fraction switch
    {
        >= 0.95 => "nearly all",
        >= 0.45 and <= 0.55 => "about half",
        _ => $"about {Math.Round(fraction * 100).ToString(CultureInfo.InvariantCulture)} percent",
    };

    internal static string Hours(TimeSpan span)
    {
        var hours = span.TotalHours;
        if (hours < 1.5)
            return $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} minutes";
        if (hours < 48)
            return $"{(int)Math.Round(hours)} hours";
        return $"{(int)Math.Round(span.TotalDays)} days";
    }

    private static string Ago(DateTime from, DateTime to) => $"the reading {Hours(to - from)} before the latest";
}
