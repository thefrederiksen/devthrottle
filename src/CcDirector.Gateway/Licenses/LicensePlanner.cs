using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>What the planner is asked: the account's numbers, the target, the sessions, and the what-if.</summary>
/// <param name="Usage">What is using the account (<see cref="SessionUsageFold"/>), live sessions only.</param>
/// <param name="Week">The account's weekly forecast: its average and recent paces.</param>
/// <param name="OtherAccountsBackAt">Each other account at its limit now, and when it has room again.</param>
/// <param name="Until">The target asked for: a target id, an ISO 8601 time, or null for the first target.</param>
public sealed record LicensePlanInput(
    DateTime NowUtc,
    LicenseUsageResponse Usage,
    LicenseWindowForecast? Week,
    IReadOnlyList<(string AccountEmail, DateTime BackAtUtc)> OtherAccountsBackAt,
    string? Until,
    IReadOnlyList<string> Stop,
    IReadOnlyList<string> StopGroups);

/// <summary>
/// THE "MAKE IT LAST" PLANNER, FOLDED ONCE (the usage-by-session mandate; CLAUDE.md rule 7). Pure: the account's numbers
/// in, the finished plan out. The Cockpit's "Make it last" panel and <c>cc-devthrottle license plan</c> show it as given.
///
/// THE QUESTION: from now to the target, can the account use what is left before 97% of its weekly limit without
/// running out? What is left over the hours to go is what can be used an hour. The BURN it is compared with is the
/// FORECAST'S OWN PACE - the faster of this week's average and the last 6 hours (<see cref="LicenseForecaster"/>) - so the
/// planner and the forecast never disagree about whether it runs out. A target after the account's own weekly reset only
/// has to be reached at the reset: the week starts again there.
///
/// THE SUGGESTION stops waiting sessions before working ones, biggest rate now first, until what it saves covers the
/// shortfall. Every top-level session is also offered as ONE choice that stops every live session under it. The what-if
/// takes sessions and groups to stop and answers the new burn, run-out time and verdict. A session's saving is its rate
/// now (the last hour); use the record cannot see is part of the burn and no stop removes it, which the words say.
///
/// SUGGESTIONS ONLY. Nothing here stops a session.
/// </summary>
public static class LicensePlanFold
{
    public const string ResetTarget = "reset";
    public const string RoomTargetPrefix = "room:";
    public const string TimeTarget = "time";

    /// <summary>The plan, or null when <see cref="LicensePlanInput.Until"/> names no target and is no time.</summary>
    public static LicensePlanResponse? Plan(LicensePlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var now = input.NowUtc;
        var usage = input.Usage;
        var response = new LicensePlanResponse { AsOfUtc = now, AccountEmail = usage.AccountEmail, AccountInUse = usage.AccountInUse };
        if (usage.AccountEmail is not { } account)
        {
            response.EmptyText = usage.EmptyText ?? "No account to plan for.";
            response.Verdict = response.EmptyText;
            response.VerdictTone = LicenseTones.Neutral;
            return response;
        }

        response.Targets = Targets(input, account);
        var target = ChooseTarget(input.Until, response.Targets, now);
        if (target is null)
            return null;
        (response.TargetId, response.TargetAtUtc, response.TargetText) = target.Value;

        var sessions = usage.Sessions.Where(s => s.IsLive).ToList();
        response.Sessions = sessions
            .OrderByDescending(s => s.RateNowPercentPerHour ?? 0)
            .Select(s => new LicensePlanSessionDto
            {
                SessionId = s.SessionId,
                Number = s.Number,
                Name = s.Name,
                TopLevelSessionId = s.TopLevelSessionId,
                State = s.State,
                StateText = s.StateText,
                RateNowPercentPerHour = s.RateNowPercentPerHour,
            })
            .ToList();
        response.Choices = usage.TopLevel
            .Select(g => (Group: g, Live: g.SessionIds.Where(id => sessions.Any(s => s.SessionId == id)).ToList()))
            .Where(c => c.Live.Count > 0)
            .Select(c => new LicensePlanChoiceDto
            {
                TopLevelSessionId = c.Group.Id!,
                Number = c.Group.Number,
                Label = c.Group.Label,
                SessionIds = c.Live,
                RateNowPercentPerHour = usage.Calibration.IsMeasured ? c.Live.Sum(id => Rate(sessions, id)) : null,
                Working = c.Live.Count(id => sessions.First(s => s.SessionId == id).State == "working"),
                Waiting = c.Live.Count(id => sessions.First(s => s.SessionId == id).State != "working"),
            })
            .OrderByDescending(c => c.RateNowPercentPerHour ?? 0)
            .ToList();

        response.WeekUsedPercent = usage.WeekUsedPercent;
        var burn = Burn(input.Week);
        response.BurnPercentPerHour = burn.PerHour;
        response.BurnText = burn.Text;

        var reset = usage.WeekResetsAtUtc;
        var until = response.TargetAtUtc!.Value;
        var end = reset is { } r && r < until ? r : until;
        if (usage.WeekUsedPercent is not { } used)
        {
            response.Verdict = "Not known: there is no fresh reading of this account's weekly limit.";
            response.VerdictTone = LicenseTones.Neutral;
            response.Suggestion.Text = "Nothing can be suggested until the account is read.";
            return response;
        }
        if (end <= now)
        {
            response.Verdict = "Pick a time ahead of now.";
            response.VerdictTone = LicenseTones.Neutral;
            response.Suggestion.Text = "Nothing to suggest for a time that has passed.";
            return response;
        }

        var left = Math.Max(0, LicenseDecisionEngine.ThresholdPercent - used);
        var hours = (end - now).TotalHours;
        response.LeftPercent = left;
        response.HoursToGo = hours;
        response.AllowedPercentPerHour = left / hours;
        var resetFirst = end < until;

        var verdict = Verdict(left, hours, burn.PerHour, now, end, resetFirst);
        (response.Lasts, response.RunsOutAtUtc, response.Verdict, response.VerdictTone) = verdict;
        response.ShortfallPercentPerHour = burn.PerHour is { } b ? Math.Max(0, b - left / hours) : null;

        response.Suggestion = Suggest(sessions, response.ShortfallPercentPerHour, usage.Calibration.IsMeasured, left <= 0);
        foreach (var s in response.Sessions)
            s.Suggested = response.Suggestion.SessionIds.Contains(s.SessionId);

        if (input.Stop.Count > 0 || input.StopGroups.Count > 0)
            response.WhatIf = WhatIf(input, sessions, response.Choices, burn.PerHour, usage.Calibration.IsMeasured, left, hours, now, end, resetFirst);
        return response;
    }

    private static double Rate(IReadOnlyList<LicenseUsageSessionDto> sessions, string id)
        => sessions.First(s => s.SessionId == id).RateNowPercentPerHour ?? 0;

    private static (double? PerHour, string Text) Burn(LicenseWindowForecast? week)
    {
        var average = week?.AveragePercentPerHour;
        var recent = week?.RecentPercentPerHour;
        return (average, recent) switch
        {
            ({ } a, { } r) when r > a => (r, $"{Pct(r)} an hour over the last 6 hours, faster than this week's average of {Pct(a)}."),
            ({ } a, { } r) => (a, $"{Pct(a)} an hour, this week's average (the last 6 hours: {Pct(r)})."),
            ({ } a, null) => (a, $"{Pct(a)} an hour, this week's average."),
            (null, { } r) => (r, $"{Pct(r)} an hour over the last 6 hours."),
            _ => (null, "Not known yet: the account has no weekly pace to measure."),
        };
    }

    private static (bool? Lasts, DateTime? RunsOutAt, string Text, string Tone) Verdict(double left, double hours, double? burn,
        DateTime now, DateTime end, bool resetFirst)
    {
        var resetNote = resetFirst ? " Its weekly limit resets before then, so it only has to last until the reset." : "";
        if (left <= 0)
            return (false, now, "It is at its limit now." + resetNote, LicenseTones.Bad);
        if (burn is not { } perHour)
            return (null, null, "Not known yet: the account has no weekly pace to measure." + resetNote, LicenseTones.Neutral);
        if (perHour <= 0 || perHour * hours <= left)
            return (true, null,
                $"At {Pct(perHour)} an hour it lasts, with about {Points(left - perHour * hours)} to spare." + resetNote, LicenseTones.Good);
        var runsOut = now.AddHours(left / perHour);
        var allowed = left / hours;
        return (false, runsOut,
            $"At {Pct(perHour)} an hour it reaches 97% in about {Span(runsOut - now)}, {Span(end - runsOut)} too early. " +
            $"To make it last, use no more than {Pct(allowed)} an hour: cut about {Pct(perHour - allowed)} an hour." + resetNote,
            LicenseTones.Bad);
    }

    private static LicensePlanSuggestionDto Suggest(IReadOnlyList<LicenseUsageSessionDto> sessions, double? shortfall, bool measured, bool atLimit)
    {
        if (atLimit)
            return new LicensePlanSuggestionDto { Text = "It is at its limit now: stopping sessions does not bring the week back." };
        if (shortfall is not { } need)
            return new LicensePlanSuggestionDto { Text = "Nothing can be suggested until the account's weekly pace is known." };
        if (need <= 0)
            return new LicensePlanSuggestionDto { SavesPercentPerHour = 0, Text = "Nothing needs to stop: it lasts at this pace." };
        if (!measured)
            return new LicensePlanSuggestionDto { Text = "The sessions' rates are not measured yet, so nothing can be suggested." };

        var order = sessions
            .Where(s => (s.RateNowPercentPerHour ?? 0) > 0)
            .OrderBy(s => s.State == "working")
            .ThenByDescending(s => s.RateNowPercentPerHour)
            .ToList();
        var chosen = new List<LicenseUsageSessionDto>();
        var saved = 0.0;
        foreach (var s in order)
        {
            if (saved >= need)
                break;
            chosen.Add(s);
            saved += s.RateNowPercentPerHour!.Value;
        }
        var waiting = chosen.Count(s => s.State != "working");
        var working = chosen.Count - waiting;
        var text = chosen.Count == 0
            ? $"No session is using it now, so stopping one saves nothing: the {Pct(need)} an hour too much is use DevThrottle does not see."
            : saved >= need
                ? $"Stop {Count(chosen.Count, "session")} ({waiting} waiting, {working} working) to save about {Pct(saved)} an hour - enough to make it last."
                : $"Even stopping every session using it saves only {Pct(saved)} an hour of the {Pct(need)} needed: the rest is use DevThrottle does not see.";
        return new LicensePlanSuggestionDto { SessionIds = chosen.Select(s => s.SessionId).ToList(), SavesPercentPerHour = saved, Text = text };
    }

    private static LicensePlanWhatIfDto WhatIf(LicensePlanInput input, IReadOnlyList<LicenseUsageSessionDto> sessions,
        IReadOnlyList<LicensePlanChoiceDto> choices, double? burn, bool measured, double left, double hours, DateTime now, DateTime end, bool resetFirst)
    {
        var stopped = new List<string>();
        var notFound = new List<string>();
        foreach (var id in input.Stop)
        {
            if (sessions.Any(s => s.SessionId == id))
                stopped.Add(id);
            else
                notFound.Add(id);
        }
        foreach (var id in input.StopGroups)
        {
            if (choices.FirstOrDefault(c => c.TopLevelSessionId == id) is { } choice)
                stopped.AddRange(choice.SessionIds);
            else
                notFound.Add(id);
        }
        stopped = stopped.Distinct(StringComparer.Ordinal).ToList();
        var dto = new LicensePlanWhatIfDto { StoppedSessionIds = stopped, NotFound = notFound };
        if (!measured || burn is not { } perHour)
        {
            dto.Verdict = "Not known: the sessions' rates or the account's pace are not measured yet.";
            dto.VerdictTone = LicenseTones.Neutral;
            return dto;
        }
        var saves = stopped.Sum(id => Rate(sessions, id));
        dto.SavesPercentPerHour = saves;
        dto.BurnPercentPerHour = Math.Max(0, perHour - saves);
        (dto.Lasts, dto.RunsOutAtUtc, dto.Verdict, dto.VerdictTone) = Verdict(left, hours, dto.BurnPercentPerHour, now, end, resetFirst);
        dto.Verdict = $"Stopping {Count(stopped.Count, "session")} saves about {Pct(saves)} an hour. " + dto.Verdict;
        return dto;
    }

    private static List<LicensePlanTargetDto> Targets(LicensePlanInput input, string account)
    {
        var targets = input.OtherAccountsBackAt
            .Where(o => o.AccountEmail != account && o.BackAtUtc > input.NowUtc)
            .OrderBy(o => o.BackAtUtc)
            .Select(o => new LicensePlanTargetDto { Id = RoomTargetPrefix + o.AccountEmail, Label = $"{o.AccountEmail} has room again", AtUtc = o.BackAtUtc })
            .ToList();
        if (input.Usage.WeekResetsAtUtc is { } reset && reset > input.NowUtc)
            targets.Add(new LicensePlanTargetDto { Id = ResetTarget, Label = "this account's own weekly reset", AtUtc = reset });
        return targets;
    }

    private static (string Id, DateTime At, string Text)? ChooseTarget(string? until, IReadOnlyList<LicensePlanTargetDto> targets, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(until))
        {
            var first = targets.FirstOrDefault();
            return first is null ? (TimeTarget, now, "until a time - there is no target to offer") : (first.Id, first.AtUtc, "until " + first.Label);
        }
        if (targets.FirstOrDefault(t => string.Equals(t.Id, until.Trim(), StringComparison.OrdinalIgnoreCase)) is { } named)
            return (named.Id, named.AtUtc, "until " + named.Label);
        if (DateTime.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
            return (TimeTarget, DateTime.SpecifyKind(at, DateTimeKind.Utc), "until the time you picked");
        return null;
    }

    // ----- words -----

    private static string Pct(double value) => SessionUsageFold.Pct(value);

    private static string Points(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + " points";

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string Span(TimeSpan span) => SessionUsageFold.Hours(span < TimeSpan.Zero ? TimeSpan.Zero : span);
}

/// <summary>
/// READS THE "MAKE IT LAST" PLAN for one DevThrottle account: what is using the account, its weekly forecast and every
/// other account's return, folded by <see cref="LicensePlanFold"/>.
/// </summary>
public sealed class LicensePlanService
{
    private static readonly IReadOnlyList<LicenseExhaustion> NoExhaustions = Array.Empty<LicenseExhaustion>();

    private readonly SessionUsageService _usage;
    private readonly LicenseSnapshotSource _snapshots;
    private readonly LicenseUsageHistory _history;

    public LicensePlanService(SessionUsageService usage, LicenseSnapshotSource snapshots, LicenseUsageHistory history)
    {
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    /// <summary>The outcome of a plan read: the plan, or which part of the question was not understood.</summary>
    public sealed record Answer(LicensePlanResponse? Plan, bool UnknownAccount, bool UnknownTarget);

    public Answer Read(TenantId tenant, string? accountEmail, string? until, IReadOnlyList<string> stop, IReadOnlyList<string> stopGroups, DateTime nowUtc)
    {
        FileLog.Write($"[LicensePlanService] Read: tenant={tenant.ToLogString()}, account={accountEmail ?? "(default)"}, until={until ?? "(default)"}, stop={stop.Count}, stopGroups={stopGroups.Count}");
        try
        {
            var usage = _usage.Read(tenant, accountEmail, includeGone: false, nowUtc);
            if (usage is null)
                return new Answer(null, UnknownAccount: true, UnknownTarget: false);

            var snapshot = _snapshots.Read(tenant, nowUtc, NoExhaustions);
            var forecast = LicenseForecaster.Forecast(snapshot, _history.Read(tenant, nowUtc));
            var week = usage.AccountEmail is { } account
                ? forecast.Accounts.FirstOrDefault(a => a.AccountEmail == account)?.SevenDay
                : null;
            var plan = LicensePlanFold.Plan(new LicensePlanInput(nowUtc, usage, week, BackAt(snapshot), until, stop, stopGroups));
            FileLog.Write($"[LicensePlanService] Read: account={usage.AccountEmail ?? "(none)"}, target={plan?.TargetId ?? "(not understood)"}, lasts={plan?.Lasts?.ToString() ?? "unknown"}");
            return plan is null ? new Answer(null, false, UnknownTarget: true) : new Answer(plan, false, false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicensePlanService] Read FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>Every account at a limit now on a fresh reading, and when it has room again: when the last of its windows
    /// at the limit resets. One whose reset time is not known is left out - there is no time to offer.</summary>
    public static IReadOnlyList<(string AccountEmail, DateTime BackAtUtc)> BackAt(LicenseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var now = snapshot.NowUtc;
        var result = new List<(string, DateTime)>();
        foreach (var reading in snapshot.Accounts)
        {
            if (LicenseDecisionEngine.IsReadingStale(reading, now))
                continue;
            var tired = new[] { reading.FiveHour, reading.SevenDay }
                .Where(w => w is not null && LicenseDecisionEngine.IsTired(w, now))
                .ToList();
            if (tired.Count == 0 || tired.Any(w => w!.ResetsAtUtc is null))
                continue;
            result.Add((reading.AccountEmail, tired.Max(w => w!.ResetsAtUtc!.Value)));
        }
        return result;
    }
}
