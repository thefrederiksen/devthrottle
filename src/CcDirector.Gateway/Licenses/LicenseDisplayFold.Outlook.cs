using System.Globalization;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE OUTLOOK: THE NEXT SEVEN DAYS, FOLDED ONCE (License Switching; CLAUDE.md rule 7). The Licenses page draws a
/// row per account across the next seven days, an alert when the fleet is forecast to have no account with room, three
/// tiles, and a Forecast box on every card. Every judgement in them is made here: which span of a row has room, which
/// is forecast, at the limit or not known, where no account has room and for how long, who is next, and every sentence
/// and tone. The page maps a time to a position between the axis start and end, and that is all.
///
/// A ROW is built from the same facts the engine judges, by the engine's own predicates:
/// <list type="bullet">
/// <item>a reading too old to trust, never read, or missing a window is <c>unknown</c> for the whole week;</item>
/// <item>a window at or over the threshold is <c>at-limit</c> until it resets, then the account has room; one whose reset
/// time has passed since the reading is <c>unknown</c> (the engine's <see cref="LicenseDecisionEngine.FirstBackWithRoom"/>
/// leaves it out for the same reason), and one with no reset time is at the limit to the end of the week;</item>
/// <item>an account IN USE that the forecaster expects to reach the threshold has room until then. For its weekly limit
/// the span from that time to the time its slower weekly AVERAGE pace (use so far this week over the hours since the
/// week began) reaches the threshold is <c>forecast</c>: it is expected to run out somewhere in it. Then it is at the
/// limit until the window resets. When the average pace would not reach it before the reset, the forecast span runs to
/// the reset and no at-limit span follows;</item>
/// <item>every other account has room all week.</item>
/// </list>
///
/// ON TODAY'S NUMBERS. An account that has room is not projected to be used up by the machines that would move to it,
/// exactly as the forecaster names a target on today's numbers. The legend says so.
///
/// A GAP is a span where no row has room; a forecast span is not room. The headline names the first gap; its second
/// sentence says the same at the slower pace, where a forecast span counts as room.
/// </summary>
public static partial class LicenseDisplayFold
{
    /// <summary>How far the outlook looks ahead.</summary>
    public static readonly TimeSpan OutlookLength = TimeSpan.FromDays(7);

    /// <summary>A gap shorter than this is not called one: two spans meeting a moment apart are not a stop.</summary>
    private static readonly TimeSpan ShortestGap = TimeSpan.FromMinutes(1);

    public const string OutlookLegendText =
        "Green: has room. Amber: forecast to reach 97% somewhere in this span, between its current pace and its slower weekly average pace. "
        + "Red: at a limit until it resets. Grey: not known. A dashed red band: no account has room. "
        + "On today's numbers: an account with room is not shown being used up by the machines that would move to it.";

    private sealed record OutlookSpan(DateTime Start, DateTime End, string Kind, string Text);

    /// <param name="RunsOutAt">For an account in use: when it is expected to reach the threshold at its current pace.</param>
    /// <param name="SlowRunsOutAt">When it reaches it at its slower weekly average pace; null when there is no forecast
    /// span, or when the average pace does not reach it before the reset.</param>
    /// <param name="LimitWindow">The window that is, or will be, at the limit; null when none is.</param>
    private sealed record AccountOutlook(string AccountEmail, bool InUse, List<OutlookSpan> Spans,
        DateTime? RunsOutAt, DateTime? SlowRunsOutAt, string? LimitWindow)
    {
        public bool HasRoomAt(DateTime at, bool forecastIsRoom) => Spans.Any(s => s.Start <= at && at < s.End
            && (s.Kind == LicenseOutlookSpanKinds.Room || (forecastIsRoom && s.Kind == LicenseOutlookSpanKinds.Forecast)));

        /// <summary>The start of its first span with room, or null when it has none in the outlook.</summary>
        public DateTime? FirstRoom => Spans.FirstOrDefault(s => s.Kind == LicenseOutlookSpanKinds.Room)?.Start;
    }

    /// <summary>
    /// The outlook of <paramref name="response"/>, and the Forecast box of each of its accounts. Called by
    /// <see cref="Forecast"/> once the accounts are folded, on the same snapshot and forecast.
    /// </summary>
    private static void ApplyOutlook(LicenseForecastResponse response, LicensePicture picture, LicenseSnapshot snapshot, LicenseForecast forecast)
    {
        var now = Utc(snapshot.NowUtc);
        var end = now + OutlookLength;
        var inUse = LicenseDecisionEngine.AccountsInUse(snapshot).ToHashSet(StringComparer.Ordinal);
        var readings = snapshot.Accounts.ToDictionary(r => r.AccountEmail, StringComparer.Ordinal);
        var outlooks = forecast.Accounts
            .Select(a => AccountOutlookOf(readings[a.AccountEmail], a, inUse.Contains(a.AccountEmail), now, end))
            .ToList();

        var gaps = NoRoom(outlooks, now, end, forecastIsRoom: false);
        var slowGaps = NoRoom(outlooks, now, end, forecastIsRoom: true);
        var renewals = picture.Overview.Accounts
            .Where(a => a.RenewsOn is not null)
            .ToDictionary(a => a.AccountEmail.Trim().ToLowerInvariant(), a => a, StringComparer.Ordinal);

        var outlook = new LicenseOutlookDto
        {
            AxisStartUtc = now,
            AxisEndUtc = end,
            Headline = Headline(outlooks, gaps, slowGaps, forecast.NextSwitch, snapshot, now, end),
            RunsOut = RunsOutTile(forecast, inUse, now),
            NextRoom = NextRoomTile(outlooks, now),
            NextRenewal = NextRenewalTile(picture.Overview.Accounts, now),
            Rows = outlooks.Select(o => new LicenseOutlookRowDto
            {
                AccountEmail = o.AccountEmail,
                Spans = o.Spans.Select(s => new LicenseOutlookSpanDto { StartUtc = s.Start, EndUtc = s.End, Kind = s.Kind, Text = s.Text }).ToList(),
                Marks = renewals.TryGetValue(o.AccountEmail, out var renews) && RenewalMark(renews.RenewsOn!.Value, now, end) is { } mark
                    ? new List<LicenseOutlookMarkDto> { mark }
                    : new List<LicenseOutlookMarkDto>(),
            }).ToList(),
            Gaps = gaps.Select(g => new LicenseOutlookGapDto { StartUtc = g.Start, EndUtc = g.End, Text = GapText(g, end) }).ToList(),
            LegendText = OutlookLegendText,
        };
        response.Outlook = outlook;

        var byEmail = outlooks.ToDictionary(o => o.AccountEmail, StringComparer.Ordinal);
        var firstBack = FirstBackOutlook(outlooks, now);
        foreach (var account in response.Accounts)
        {
            var mine = byEmail[account.AccountEmail];
            account.Rows = ForecastRows(account, mine, outlooks, gaps, firstBack, snapshot, now);
            account.ProjectedWeekPercent = mine.InUse && mine.LimitWindow == LicenseWindowNames.SevenDay && mine.RunsOutAt is { } at && at > now
                ? LicenseDecisionEngine.ThresholdPercent
                : null;
        }
    }

    // ----- one row -----

    private static AccountOutlook AccountOutlookOf(LicenseAccountReading reading, LicenseAccountForecast forecast, bool inUse, DateTime now, DateTime end)
    {
        var spans = new List<OutlookSpan>();
        void Add(DateTime start, DateTime stop, string kind, string text)
        {
            var from = start < now ? now : start > end ? end : start;
            var to = stop > end ? end : stop < now ? now : stop;
            if (to <= from)
                return;
            if (spans.Count > 0 && spans[^1].Kind == kind && spans[^1].End == from)
                spans[^1] = spans[^1] with { End = to };
            else
                spans.Add(new OutlookSpan(from, to, kind, text));
        }
        AccountOutlook Done(DateTime? runsOut = null, DateTime? slow = null, string? limitWindow = null)
            => new(forecast.AccountEmail, inUse, spans, runsOut, slow, limitWindow);

        if (reading.ReadAtUtc is null)
        {
            Add(now, end, LicenseOutlookSpanKinds.Unknown, "Not known: it has never been read");
            return Done();
        }
        if (forecast.IsStale)
        {
            Add(now, end, LicenseOutlookSpanKinds.Unknown, "Not known: the latest reading is too old");
            return Done();
        }
        if (reading.FiveHour is not { } five || reading.SevenDay is not { } week)
        {
            Add(now, end, LicenseOutlookSpanKinds.Unknown, "Not known: the reading has no number for a window");
            return Done();
        }

        var windows = new[] { (Name: LicenseWindowNames.FiveHour, Reading: five), (Name: LicenseWindowNames.SevenDay, Reading: week) };
        if (windows.Any(w => w.Reading.Utilization >= LicenseDecisionEngine.ThresholdPercent && w.Reading.ResetsAtUtc is { } reset && Utc(reset) <= now))
        {
            Add(now, end, LicenseOutlookSpanKinds.Unknown, "Not known: it was at a limit, and that limit has reset since the reading");
            return Done();
        }

        var tired = windows.Where(w => LicenseDecisionEngine.IsTired(w.Reading, now)).ToList();
        if (tired.Count > 0)
        {
            // Room again once every window at the limit has reset; one with no reset time keeps it there all week.
            var last = tired.OrderBy(w => w.Reading.ResetsAtUtc is { } r ? Utc(r) : DateTime.MaxValue).Last();
            var back = last.Reading.ResetsAtUtc is { } lastReset ? Utc(lastReset) : end;
            Add(now, back, LicenseOutlookSpanKinds.AtLimit, last.Reading.ResetsAtUtc is null
                ? $"At its {WindowLabel(last.Name)}, and the reset time is not known"
                : $"At its {WindowLabel(last.Name)} until it resets");
            Add(back, end, LicenseOutlookSpanKinds.Room, "Has room");
            return Done(limitWindow: last.Name);
        }

        if (!inUse || forecast.ExpectedTiredAtUtc is not { } expected || forecast.ExpectedTiredWindow is not { } window)
        {
            Add(now, end, LicenseOutlookSpanKinds.Room, "Has room");
            return Done();
        }

        var fast = Utc(expected);
        var limit = window == LicenseWindowNames.FiveHour ? five : week;
        var resetAt = limit.ResetsAtUtc is { } resets ? Utc(resets) : end;
        var slow = window == LicenseWindowNames.SevenDay ? AveragePaceReachesThresholdAt(week, Utc(reading.ReadAtUtc.Value)) : null;
        Add(now, fast, LicenseOutlookSpanKinds.Room, "Has room");
        if (slow is { } slowAt && slowAt > fast)
        {
            var bandEnd = slowAt < resetAt ? slowAt : resetAt;
            Add(fast, bandEnd, LicenseOutlookSpanKinds.Forecast, $"Forecast to reach {Threshold} of its {WindowLabel(window)} in this span");
            Add(bandEnd, resetAt, LicenseOutlookSpanKinds.AtLimit, $"At its {WindowLabel(window)} until it resets");
            Add(resetAt, end, LicenseOutlookSpanKinds.Room, "Has room");
            return Done(fast, slowAt < resetAt ? slowAt : null, window);
        }
        Add(fast, resetAt, LicenseOutlookSpanKinds.AtLimit, $"At its {WindowLabel(window)} until it resets");
        Add(resetAt, end, LicenseOutlookSpanKinds.Room, "Has room");
        return Done(fast, null, window);
    }

    /// <summary>
    /// When the weekly window reaches the threshold at its average pace so far this week: its use over the hours since
    /// the week began (its reset time less seven days). Null when that cannot be told - no reset time, under an hour
    /// into the week, no use yet - or when it would take longer than a week.
    /// </summary>
    private static DateTime? AveragePaceReachesThresholdAt(LicenseWindowReading week, DateTime readAt)
    {
        if (week.ResetsAtUtc is not { } reset || week.Utilization <= 0)
            return null;
        var hoursIn = (readAt - (Utc(reset) - LicenseDecisionEngine.SevenDayWindowLength)).TotalHours;
        if (hoursIn < 1)
            return null;
        var hoursLeft = (LicenseDecisionEngine.ThresholdPercent - week.Utilization) / (week.Utilization / hoursIn);
        if (double.IsNaN(hoursLeft) || hoursLeft > LicenseDecisionEngine.SevenDayWindowLength.TotalHours)
            return null;
        return readAt.AddHours(hoursLeft);
    }

    // ----- the gaps -----

    /// <summary>Every span from <paramref name="now"/> to <paramref name="end"/> where no row has room, merged.</summary>
    private static List<(DateTime Start, DateTime End)> NoRoom(IReadOnlyList<AccountOutlook> rows, DateTime now, DateTime end, bool forecastIsRoom)
    {
        var edges = rows.SelectMany(r => r.Spans.SelectMany(s => new[] { s.Start, s.End }))
            .Append(now).Append(end).Distinct().Order().ToList();
        var gaps = new List<(DateTime Start, DateTime End)>();
        for (var i = 0; i + 1 < edges.Count; i++)
        {
            var (from, to) = (edges[i], edges[i + 1]);
            if (rows.Any(r => r.HasRoomAt(from, forecastIsRoom)))
                continue;
            if (gaps.Count > 0 && gaps[^1].End == from)
                gaps[^1] = (gaps[^1].Start, to);
            else
                gaps.Add((from, to));
        }
        return gaps.Where(g => g.End - g.Start >= ShortestGap).ToList();
    }

    private static string GapText((DateTime Start, DateTime End) gap, DateTime end)
        => gap.End >= end ? "No account has room from here to the end of the 7 days." : $"No account has room for about {Span(gap.End - gap.Start)}.";

    // ----- the headline -----

    private static LicenseOutlookHeadlineDto Headline(IReadOnlyList<AccountOutlook> outlooks, List<(DateTime Start, DateTime End)> gaps,
        List<(DateTime Start, DateTime End)> slowGaps, LicenseNextSwitch? next, LicenseSnapshot snapshot, DateTime now, DateTime end)
    {
        if (gaps.Count == 0)
            return CalmHeadline(outlooks, next, snapshot, now);

        var gap = gaps[0];
        var runner = outlooks.Where(o => o.InUse && o.RunsOutAt == gap.Start).OrderBy(o => o.AccountEmail, StringComparer.Ordinal).FirstOrDefault();
        var toEnd = gap.End >= end;
        string text;
        if (runner is not null)
            text = $"{runner.AccountEmail} runs out {When(gap.Start, now)}, and no other account has room "
                   + (toEnd ? "in the rest of the 7 days." : $"for about {Span(gap.End - gap.Start)} after that.");
        else if (gap.Start <= now)
            text = "No account has room now, " + (toEnd ? "and none has room again in the next 7 days." : $"and none has room for about {Span(gap.End - gap.Start)}.");
        else
            text = $"No account has room {When(gap.Start, now)}, " + (toEnd ? "to the end of the next 7 days." : $"for about {Span(gap.End - gap.Start)}.");

        var detail = new List<string>();
        if (runner is not null && outlooks.First(o => o.AccountEmail == runner.AccountEmail).Spans.Any(s => s.Kind == LicenseOutlookSpanKinds.Forecast))
        {
            if (runner.SlowRunsOutAt is not { } slowAt)
                detail.Add("At its slower weekly average pace it does not run out before its weekly limit resets.");
            else
            {
                var slowGap = slowGaps.FirstOrDefault(g => g.Start == slowAt);
                detail.Add(slowGap == default
                    ? $"At its slower weekly average pace it runs out in about {Span(slowAt - now)}, and another account has room by then."
                    : $"At its slower weekly average pace it runs out in about {Span(slowAt - now)}, and no account has room for about {Span(slowGap.End - slowGap.Start)}.");
            }
        }
        if (gaps.Count > 1)
            detail.Add($"{Count(gaps.Count - 1, "more span")} with no account with room in the next 7 days.");
        return new LicenseOutlookHeadlineDto
        {
            Text = text,
            Detail = detail.Count == 0 ? null : string.Join(" ", detail),
            Tone = LicenseTones.Bad,
            IsAlert = true,
        };
    }

    private static LicenseOutlookHeadlineDto CalmHeadline(IReadOnlyList<AccountOutlook> outlooks, LicenseNextSwitch? next, LicenseSnapshot snapshot, DateTime now)
    {
        if (next is null)
            return new LicenseOutlookHeadlineDto
            {
                Text = outlooks.Any(o => o.InUse)
                    ? "No account in use is expected to run out in the next 7 days."
                    : "No machine is working on an account, so none is expected to run out.",
                Tone = LicenseTones.Good,
            };
        var at = Utc(next.ExpectedAtUtc);
        var then = RoomAt(outlooks, next.FromAccountEmail, at, snapshot) ?? throw new InvalidOperationException(
            "The outlook finds no gap, and no account with room when the account in use runs out.");
        return new LicenseOutlookHeadlineDto
        {
            Text = at <= now
                ? $"{next.FromAccountEmail} has reached its limit, and {then} has room."
                : $"{next.FromAccountEmail} runs out {When(at, now)}, and {then} has room then.",
            Tone = LicenseTones.Good,
        };
    }

    /// <summary>The account other than <paramref name="source"/> with room at <paramref name="at"/>: the engine's own
    /// choice when it has room then, else the first by email. Null when none has.</summary>
    private static string? RoomAt(IReadOnlyList<AccountOutlook> outlooks, string source, DateTime at, LicenseSnapshot snapshot)
    {
        var candidates = outlooks.Where(o => o.AccountEmail != source && o.HasRoomAt(at, forecastIsRoom: false)).Select(o => o.AccountEmail).ToList();
        var engine = LicenseDecisionEngine.NextTarget(snapshot, source);
        return engine is not null && candidates.Contains(engine) ? engine : candidates.FirstOrDefault();
    }

    // ----- the tiles -----

    private static LicenseOutlookTileDto RunsOutTile(LicenseForecast forecast, IReadOnlySet<string> inUse, DateTime now)
    {
        var tile = new LicenseOutlookTileDto { Label = "Active account runs out" };
        if (forecast.NextSwitch is { } next)
        {
            var at = Utc(next.ExpectedAtUtc);
            var account = forecast.Accounts.First(a => a.AccountEmail == next.FromAccountEmail);
            var window = next.Window == LicenseWindowNames.FiveHour ? account.FiveHour : account.SevenDay;
            tile.ValueText = at <= now ? "Now" : $"In about {Span(at - now)}";
            tile.AtUtc = at;
            tile.Tone = at <= now ? LicenseTones.Bad : LicenseTones.Warning;
            tile.DetailText = $"{next.FromAccountEmail}: {Percent(window.Utilization ?? 0)} of its {WindowLabel(next.Window)} now"
                              + (window.PercentPerHour is { } rate && rate > 0 ? $", rising {Rate(rate)}." : ".");
            return tile;
        }
        if (inUse.Count == 0)
        {
            tile.ValueText = "No account in use";
            tile.DetailText = "No machine is working on an account.";
            tile.Tone = LicenseTones.Neutral;
            return tile;
        }
        tile.ValueText = "Not this week";
        tile.DetailText = $"{string.Join(", ", inUse.Order(StringComparer.Ordinal))}: not expected to reach {Threshold} at the current pace.";
        tile.Tone = LicenseTones.Good;
        return tile;
    }

    private static AccountOutlook? FirstBackOutlook(IReadOnlyList<AccountOutlook> outlooks, DateTime now)
        => outlooks.Where(o => !o.InUse && o.FirstRoom is not null)
            .OrderBy(o => o.FirstRoom)
            .ThenBy(o => o.AccountEmail, StringComparer.Ordinal)
            .FirstOrDefault();

    private static LicenseOutlookTileDto NextRoomTile(IReadOnlyList<AccountOutlook> outlooks, DateTime now)
    {
        var tile = new LicenseOutlookTileDto { Label = "Next account with room" };
        var others = outlooks.Where(o => !o.InUse).ToList();
        if (others.Count == 0)
        {
            tile.ValueText = "No other account";
            tile.DetailText = "Every account held is in use.";
            tile.Tone = LicenseTones.Neutral;
            return tile;
        }
        var first = FirstBackOutlook(outlooks, now);
        if (first is null)
        {
            tile.ValueText = "None in the next 7 days";
            tile.DetailText = "Every other account is at a limit or not known for the whole 7 days.";
            tile.Tone = LicenseTones.Bad;
            return tile;
        }
        var at = first.FirstRoom!.Value;
        if (at <= now)
        {
            tile.ValueText = "Now";
            tile.DetailText = $"{first.AccountEmail} has room.";
            tile.Tone = LicenseTones.Good;
            return tile;
        }
        tile.ValueText = $"In about {Span(at - now)}";
        tile.AtUtc = at;
        tile.DetailText = first.LimitWindow is { } window ? $"{first.AccountEmail}, when its {WindowLabel(window)} resets." : $"{first.AccountEmail}.";
        tile.Tone = LicenseTones.Neutral;
        return tile;
    }

    private static LicenseOutlookTileDto NextRenewalTile(IReadOnlyList<LicenseAccountView> accounts, DateTime now)
    {
        var tile = new LicenseOutlookTileDto { Label = "Next renewal" };
        var today = DateOnly.FromDateTime(now);
        var set = accounts.Where(a => a.RenewsOn is not null).ToList();
        var next = set.Where(a => a.RenewsOn >= today)
            .OrderBy(a => a.RenewsOn)
            .ThenBy(a => a.AccountEmail, StringComparer.Ordinal)
            .FirstOrDefault();
        if (next is null)
        {
            tile.ValueText = set.Count == 0 ? "Not set" : "None ahead";
            tile.DetailText = set.Count == 0
                ? "No account has a renewal date stored. Set one on its card."
                : "Every renewal date stored has passed. Set the new one on its card.";
            tile.Tone = LicenseTones.Neutral;
            return tile;
        }
        var date = next.RenewsOn!.Value;
        var day = date.ToString(date.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
        var days = date.DayNumber - today.DayNumber;
        tile.ValueText = days switch { 0 => $"Today, {day}", 1 => $"Tomorrow, {day}", _ => day };
        tile.DetailText = $"{next.AccountEmail.Trim().ToLowerInvariant()}, {PlanText(next.Plan)}.";
        tile.Tone = days <= 1 ? LicenseTones.Warning : LicenseTones.Neutral;
        return tile;
    }

    /// <summary>The mark of a renewal date on its row: at the start of that day in Coordinated Universal Time, or at the
    /// axis start when the day has begun. Null when the day is outside the axis.</summary>
    private static LicenseOutlookMarkDto? RenewalMark(DateOnly renewsOn, DateTime now, DateTime end)
    {
        var dayStart = renewsOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        if (dayStart >= end || dayStart.AddDays(1) <= now)
            return null;
        return new LicenseOutlookMarkDto { AtUtc = dayStart < now ? now : dayStart, Text = RenewsText(renewsOn, now) };
    }

    // ----- the Forecast box of a card -----

    private static List<LicenseForecastRowDto> ForecastRows(LicenseAccountForecastDto account, AccountOutlook mine,
        IReadOnlyList<AccountOutlook> outlooks, List<(DateTime Start, DateTime End)> gaps, AccountOutlook? firstBack,
        LicenseSnapshot snapshot, DateTime now)
    {
        var rows = new List<LicenseForecastRowDto>();
        void Row(string label, string text, string tone, DateTime? at = null)
            => rows.Add(new LicenseForecastRowDto { Label = label, Text = text, Tone = tone, AtUtc = at });

        var first = mine.Spans[0];
        if (first.Kind == LicenseOutlookSpanKinds.Unknown)
        {
            Row("Forecast", first.Text + ".", LicenseTones.Neutral);
            return rows;
        }
        if (first.Kind == LicenseOutlookSpanKinds.AtLimit)
        {
            var back = mine.FirstRoom;
            if (back is { } at)
                Row("Has room again", $"In about {Span(at - now)}", LicenseTones.Good, at);
            else
                Row("Has room again", "Not in the next 7 days", LicenseTones.Bad);
            if (!mine.InUse && firstBack?.AccountEmail == mine.AccountEmail)
                Row("Next in line", "Yes - the first account to have room again", LicenseTones.Good);
            return rows;
        }
        if (!mine.InUse)
        {
            Row("Has room", "Now", LicenseTones.Good);
            return rows;
        }

        var paceWindow = mine.LimitWindow == LicenseWindowNames.FiveHour ? account.FiveHour : account.SevenDay;
        Row("Pace", paceWindow.PercentPerHour is { } rate && rate > 0
            ? $"{Rate(rate)} on its {WindowLabel(mine.LimitWindow ?? LicenseWindowNames.SevenDay)}"
            : paceWindow.RateText, LicenseTones.Neutral);
        if (mine.RunsOutAt is not { } runsOut)
        {
            Row("Reaches 97%", "Not expected before it resets", LicenseTones.Good);
            var target = RoomAt(outlooks, mine.AccountEmail, now, snapshot);
            Row("Then switches to", target ?? "Nobody has room now", target is null ? LicenseTones.Bad : LicenseTones.Neutral);
            return rows;
        }
        Row("Reaches 97%", runsOut <= now ? "Now" : $"In about {Span(runsOut - now)}", LicenseTones.Warning, runsOut);
        var then = RoomAt(outlooks, mine.AccountEmail, runsOut, snapshot);
        if (then is not null)
            Row("Then switches to", then, LicenseTones.Good);
        else
        {
            var gap = gaps.FirstOrDefault(g => g.Start <= runsOut && runsOut < g.End);
            Row("Then switches to", gap == default ? "Nobody has room" : $"Nobody has room for about {Span(gap.End - runsOut)}", LicenseTones.Bad);
        }
        return rows;
    }

    // ----- words -----

    private static string When(DateTime at, DateTime now) => at <= now ? "now" : $"in about {Span(at - now)}";

    private static string Rate(double perHour) => $"{perHour.ToString("0.#", CultureInfo.InvariantCulture)}% an hour";
}
