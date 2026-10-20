using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE OUTLOOK OF THE LICENSES PAGE (License Switching; CLAUDE.md rule 7): the next seven days of every account, the
/// gaps where no account has room, the headline, the three tiles and the Forecast box of each card. The facts go in as
/// in production - usage reports applied to the real store on a throwaway SQLite file and the usage history fed the
/// same reports - and the answer is the forecast route's own fold.
///
/// The main picture, at T0: account A in use at 45% of its weekly limit, rising 15% an hour (40% twenty minutes ago),
/// 72 hours into a week that resets at T0 + 4 days; account B a spare at 98% of its weekly limit until T0 + 2 days;
/// account C a spare whose reading is 31 minutes old, too old to trust.
/// </summary>
public sealed class LicenseOutlookFoldTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _source;
    private readonly LicenseUsageHistory _history = new();

    private static readonly TenantId Tenant = new("acct-a");

    // A reaches 97% at its current pace: 52 points to go at 15 an hour.
    private static readonly DateTime RunsOut = T0.AddHours(52.0 / 15);

    // At its weekly average pace - 45% over the 72 hours since the week began - it reaches 97% 83.2 hours from now.
    private static readonly DateTime RunsOutSlowly = T0.AddHours(52 / (45.0 / 72));

    public LicenseOutlookFoldTests()
    {
        _store = new LicenseStore(_harness.Open());
        _source = new LicenseSnapshotSource(_store, new LicenseReadModel(_store));
    }

    public void Dispose() => _harness.Dispose();

    private static LicenseHolding Week(LicenseHolding holding, DateTime resetsAt)
    {
        holding.SevenDay!.ResetsAtUtc = resetsAt;
        return holding;
    }

    /// <summary>A in use and rising, plus the spares given, on one machine; A's rate in the history.</summary>
    private void ApplyRising(params LicenseHolding[] spares)
    {
        _history.Record(Tenant, Report("m1", Holding(A, true, 10, 40, readAt: T0.AddMinutes(-20))), TimeSpan.Zero, T0);
        _history.Record(Tenant, Report("m1", Holding(A, true, 10, 45)), TimeSpan.Zero, T0);
        _store.ApplyReport(Tenant, "dir-1", 1, Report("m1", new[] { Holding(A, true, 10, 45) }.Concat(spares).ToArray()), T0, TimeSpan.Zero);
    }

    private LicenseForecastResponse Forecast(DateTime now)
        => LicenseDisplayFold.Forecast(_source.ReadPicture(Tenant, now, Array.Empty<LicenseExhaustion>()), _history.Read(Tenant, now));

    private static (string Kind, DateTime Start, DateTime End)[] Spans(LicenseOutlookDto outlook, string email)
        => outlook.Rows.Single(r => r.AccountEmail == email).Spans.Select(s => (s.Kind, s.StartUtc, s.EndUtc)).ToArray();

    private static (string Label, string Text, string Tone)[] Rows(LicenseForecastResponse forecast, string email)
        => forecast.Accounts.Single(a => a.AccountEmail == email).Rows.Select(r => (r.Label, r.Text, r.Tone)).ToArray();

    private static void Close(DateTime expected, DateTime actual) => Assert.Equal(expected, actual, TimeSpan.FromSeconds(1));

    [Fact]
    public void Outlook_TheAccountInUseRunsOut_WhileNoOtherHasRoom_IsAGap_AndTheHeadlineIsAnAlert()
    {
        ApplyRising(Week(Holding(B, false, 10, 98), T0.AddDays(2)), Holding(C, false, 10, 20, readAt: T0.AddMinutes(-31)));

        var forecast = Forecast(T0);
        var outlook = forecast.Outlook;

        Assert.Equal((T0, T0.AddDays(7)), (outlook.AxisStartUtc, outlook.AxisEndUtc));
        Assert.Equal(new[] { A, B, C }, outlook.Rows.Select(r => r.AccountEmail));

        // A: room until its current pace reaches 97%, forecast until its average pace does, at the limit until the
        // week resets, then room.
        var a = Spans(outlook, A);
        Assert.Equal(new[] { LicenseOutlookSpanKinds.Room, LicenseOutlookSpanKinds.Forecast, LicenseOutlookSpanKinds.AtLimit, LicenseOutlookSpanKinds.Room },
            a.Select(s => s.Kind));
        Assert.Equal(T0, a[0].Start);
        Close(RunsOut, a[0].End);
        Close(RunsOutSlowly, a[1].End);
        Assert.Equal((T0.AddDays(4), T0.AddDays(4), T0.AddDays(7)), (a[2].End, a[3].Start, a[3].End));

        // B: at its limit until its week resets inside the outlook, then room.
        Assert.Equal(new[] { (LicenseOutlookSpanKinds.AtLimit, T0, T0.AddDays(2)), (LicenseOutlookSpanKinds.Room, T0.AddDays(2), T0.AddDays(7)) }, Spans(outlook, B));
        Assert.Equal("At its weekly limit until it resets", outlook.Rows.Single(r => r.AccountEmail == B).Spans[0].Text);

        // C: too old to say, the whole week.
        Assert.Equal(new[] { (LicenseOutlookSpanKinds.Unknown, T0, T0.AddDays(7)) }, Spans(outlook, C));
        Assert.Equal("Not known: the latest reading is too old", outlook.Rows.Single(r => r.AccountEmail == C).Spans[0].Text);

        // The gap: from the moment A's current pace runs out until B's week resets. A forecast span is not room.
        var gap = Assert.Single(outlook.Gaps);
        Close(RunsOut, gap.StartUtc);
        Assert.Equal(T0.AddDays(2), gap.EndUtc);
        Assert.Equal("No account has room for about 1 day 20 hours.", gap.Text);

        Assert.True(outlook.Headline.IsAlert);
        Assert.Equal(LicenseTones.Bad, outlook.Headline.Tone);
        Assert.Equal($"{A} runs out in about 3 hours 28 minutes, and no other account has room for about 1 day 20 hours after that.", outlook.Headline.Text);
        Assert.Equal("At its slower weekly average pace it runs out in about 3 days 11 hours, and another account has room by then.", outlook.Headline.Detail);

        // The tiles.
        Assert.Equal(("Active account runs out", "In about 3 hours 28 minutes", LicenseTones.Warning), (outlook.RunsOut.Label, outlook.RunsOut.ValueText, outlook.RunsOut.Tone));
        Close(RunsOut, outlook.RunsOut.AtUtc!.Value);
        Assert.Equal($"{A}: 45% of its weekly limit now, rising 15% an hour.", outlook.RunsOut.DetailText);
        Assert.Equal(("Next account with room", "In about 2 days", T0.AddDays(2), $"{B}, when its weekly limit resets.", LicenseTones.Neutral),
            (outlook.NextRoom.Label, outlook.NextRoom.ValueText, outlook.NextRoom.AtUtc, outlook.NextRoom.DetailText, outlook.NextRoom.Tone));

        // The cards' Forecast boxes.
        Assert.Equal(new[]
        {
            ("Pace", "15% an hour on its weekly limit", LicenseTones.Neutral),
            ("Reaches 97%", "In about 3 hours 28 minutes", LicenseTones.Warning),
            ("Then switches to", "Nobody has room for about 1 day 20 hours", LicenseTones.Bad),
        }, Rows(forecast, A));
        Assert.Equal(97, forecast.Accounts.Single(x => x.AccountEmail == A).ProjectedWeekPercent);
        Assert.Equal(new[]
        {
            ("Has room again", "In about 2 days", LicenseTones.Good),
            ("Next in line", "Yes - the first account to have room again", LicenseTones.Good),
        }, Rows(forecast, B));
        Assert.Null(forecast.Accounts.Single(x => x.AccountEmail == B).ProjectedWeekPercent);
        Assert.Equal(new[] { ("Forecast", "Not known: the latest reading is too old.", LicenseTones.Neutral) }, Rows(forecast, C));
    }

    [Fact]
    public void Outlook_WhenAnotherAccountHasRoom_ThereIsNoGap_AndTheHeadlineIsCalm()
    {
        ApplyRising(Holding(B, false, 4, 22));

        var forecast = Forecast(T0);
        var outlook = forecast.Outlook;

        Assert.Empty(outlook.Gaps);
        Assert.False(outlook.Headline.IsAlert);
        Assert.Equal(LicenseTones.Good, outlook.Headline.Tone);
        Assert.Equal($"{A} runs out in about 3 hours 28 minutes, and {B} has room then.", outlook.Headline.Text);
        Assert.Null(outlook.Headline.Detail);
        Assert.Equal(new[] { (LicenseOutlookSpanKinds.Room, T0, T0.AddDays(7)) }, Spans(outlook, B));
        Assert.Equal(("Now", (DateTime?)null, $"{B} has room.", LicenseTones.Good),
            (outlook.NextRoom.ValueText, outlook.NextRoom.AtUtc, outlook.NextRoom.DetailText, outlook.NextRoom.Tone));
        Assert.Equal(("Then switches to", B, LicenseTones.Good), Rows(forecast, A)[2]);
        Assert.Equal(new[] { ("Has room", "Now", LicenseTones.Good) }, Rows(forecast, B));
    }

    [Fact]
    public void Outlook_NoAccountInUseRising_ExpectsNoRunOut_AndDrawsNoProjectedPart()
    {
        _store.ApplyReport(Tenant, "dir-1", 1, Report("m1", Holding(A, true, 10, 45), Holding(B, false, 4, 22)), T0, TimeSpan.Zero);

        var forecast = Forecast(T0);

        Assert.Empty(forecast.Outlook.Gaps);
        Assert.Equal("No account in use is expected to run out in the next 7 days.", forecast.Outlook.Headline.Text);
        Assert.Equal(("Not this week", LicenseTones.Good), (forecast.Outlook.RunsOut.ValueText, forecast.Outlook.RunsOut.Tone));
        Assert.Equal(new[] { (LicenseOutlookSpanKinds.Room, T0, T0.AddDays(7)) }, Spans(forecast.Outlook, A));
        Assert.Equal(("Reaches 97%", "Not expected before it resets", LicenseTones.Good), Rows(forecast, A)[1]);
        Assert.Null(forecast.Accounts.Single(x => x.AccountEmail == A).ProjectedWeekPercent);
    }

    [Fact]
    public void Outlook_AnAccountInUseAtItsFiveHourLimit_IsAtTheLimitUntilThatWindowResets_ThenHasRoom()
    {
        // The 5-hour window at 98% resets at T0 + 2 hours (the test data's reset); the spare B is at its weekly limit
        // until T0 + 1 day, so for those two hours no account has room.
        _store.ApplyReport(Tenant, "dir-1", 1, Report("m1", Holding(A, true, 98, 45), Week(Holding(B, false, 10, 99), T0.AddDays(1))), T0, TimeSpan.Zero);

        var forecast = Forecast(T0);
        var outlook = forecast.Outlook;

        Assert.Equal(new[] { (LicenseOutlookSpanKinds.AtLimit, T0, T0.AddHours(2)), (LicenseOutlookSpanKinds.Room, T0.AddHours(2), T0.AddDays(7)) }, Spans(outlook, A));
        Assert.Equal("At its 5-hour window until it resets", outlook.Rows.Single(r => r.AccountEmail == A).Spans[0].Text);
        var gap = Assert.Single(outlook.Gaps);
        Assert.Equal((T0, T0.AddHours(2)), (gap.StartUtc, gap.EndUtc));
        Assert.Equal("No account has room now, and none has room for about 2 hours.", outlook.Headline.Text);
        Assert.True(outlook.Headline.IsAlert);
        Assert.Equal(("Now", LicenseTones.Bad), (outlook.RunsOut.ValueText, outlook.RunsOut.Tone));
        Assert.Equal(new[] { ("Has room again", "In about 2 hours", LicenseTones.Good) }, Rows(forecast, A));
    }

    [Fact]
    public void Outlook_AnAccountNeverRead_OrMissingAWindow_IsNotKnown_AndIsNeverCountedAsRoom()
    {
        var noWeek = Holding(B, false, 10, 20);
        noWeek.SevenDay = null;
        _store.ApplyReport(Tenant, "dir-1", 1, Report("m1", Holding(A, true, 98, 45), noWeek), T0, TimeSpan.Zero);

        var outlook = Forecast(T0).Outlook;

        Assert.Equal(new[] { (LicenseOutlookSpanKinds.Unknown, T0, T0.AddDays(7)) }, Spans(outlook, B));
        Assert.Equal("Not known: the reading has no number for a window", outlook.Rows.Single(r => r.AccountEmail == B).Spans[0].Text);
        Assert.Equal((T0, T0.AddHours(2)), (outlook.Gaps.Single().StartUtc, outlook.Gaps.Single().EndUtc));
        Assert.Equal(("None in the next 7 days", LicenseTones.Bad), (outlook.NextRoom.ValueText, outlook.NextRoom.Tone));
    }

    [Fact]
    public void Outlook_TheNextRenewal_IsTheSoonestStoredDateFromToday_AndIsMarkedOnItsRow()
    {
        _store.ApplyReport(Tenant, "dir-1", 1, Report("m1", Holding(A, true, 10, 45), Holding(B, false, 4, 22), Holding(C, false, 4, 22)), T0, TimeSpan.Zero);

        Assert.Equal(("Next renewal", "Not set", "No account has a renewal date stored. Set one on its card.", LicenseTones.Neutral),
            Tile(Forecast(T0).Outlook.NextRenewal));

        Assert.True(_store.SetRenewalDate(Tenant, A, new DateOnly(2026, 10, 10)));
        Assert.Equal(("Next renewal", "None ahead", "Every renewal date stored has passed. Set the new one on its card.", LicenseTones.Neutral),
            Tile(Forecast(T0).Outlook.NextRenewal));

        Assert.True(_store.SetRenewalDate(Tenant, B, new DateOnly(2026, 10, 12)));
        Assert.True(_store.SetRenewalDate(Tenant, C, new DateOnly(2026, 10, 30)));
        var outlook = Forecast(T0).Outlook;

        Assert.Equal(("Next renewal", "Tomorrow, 12 Oct", $"{B}, Max.", LicenseTones.Warning), Tile(outlook.NextRenewal));
        var mark = Assert.Single(outlook.Rows.Single(r => r.AccountEmail == B).Marks);
        Assert.Equal(("2026-10-12", "Renews 12 Oct"), (mark.RenewsOn, mark.Text));
        // A's date has passed and C's is after the outlook ends: neither is marked.
        Assert.Empty(outlook.Rows.Single(r => r.AccountEmail == A).Marks);
        Assert.Empty(outlook.Rows.Single(r => r.AccountEmail == C).Marks);

        Assert.True(_store.SetRenewalDate(Tenant, B, new DateOnly(2026, 10, 11)));
        outlook = Forecast(T0).Outlook;
        Assert.Equal(("Next renewal", "Today, 11 Oct", $"{B}, Max.", LicenseTones.Warning), Tile(outlook.NextRenewal));
        // Today is marked too: the page places it at the start of the day, clamped to the axis start.
        Assert.Equal("2026-10-11", outlook.Rows.Single(r => r.AccountEmail == B).Marks.Single().RenewsOn);
    }

    // The review of pull request 4299, finding 1: B's week reset 30 seconds after A runs out made a stretch with no room
    // too short to be called a gap, and the calm headline then found nobody with room at the run-out and threw - so the
    // whole forecast route failed. Within a minute counts as "then", as the gaps count it.
    [Fact]
    public void Outlook_AnotherAccountHasRoomSecondsAfterTheRunOut_IsNoGap_AndNamesThatAccount_AndNeverThrows()
    {
        ApplyRising(Week(Holding(B, false, 10, 98), RunsOut.AddSeconds(30)));

        var forecast = Forecast(T0);

        Assert.Empty(forecast.Outlook.Gaps);
        Assert.False(forecast.Outlook.Headline.IsAlert);
        Assert.Equal($"{A} runs out in about 3 hours 28 minutes, and {B} has room then.", forecast.Outlook.Headline.Text);
        Assert.Equal(("Then switches to", B, LicenseTones.Good), Rows(forecast, A)[2]);
    }

    // The review's finding 2: the engine does not choose a barred account as a target, so the outlook must not draw it
    // with room, count it against a gap, or name it next.
    [Fact]
    public void Outlook_ABarredSpare_IsAtTheLimitUntilItsBarEnds_SoItIsNeitherRoomNorNext()
    {
        ApplyRising(Holding(B, false, 4, 22));
        _store.Bar(Tenant, B, LicenseWindowNames.SevenDay, T0.AddDays(1), T0);

        var forecast = Forecast(T0);
        var outlook = forecast.Outlook;

        Assert.Equal(new[] { (LicenseOutlookSpanKinds.AtLimit, T0, T0.AddDays(1)), (LicenseOutlookSpanKinds.Room, T0.AddDays(1), T0.AddDays(7)) }, Spans(outlook, B));
        Assert.Equal("Left out of the automatic choice until its weekly limit resets: it triggered a switch",
            outlook.Rows.Single(r => r.AccountEmail == B).Spans[0].Text);
        var gap = Assert.Single(outlook.Gaps);
        Close(RunsOut, gap.StartUtc);
        Assert.Equal(T0.AddDays(1), gap.EndUtc);
        Assert.True(outlook.Headline.IsAlert);
        Assert.Equal(("In about 1 day", $"{B}, when its weekly limit resets."), (outlook.NextRoom.ValueText, outlook.NextRoom.DetailText));
        Assert.Equal(("Then switches to", "Nobody has room for about 20 hours 32 minutes", LicenseTones.Bad), Rows(forecast, A)[2]);
    }

    [Fact]
    public void Outlook_AnAccountAtItsLimitWhoseResetHasPassedSinceTheReading_IsNotKnown()
    {
        _store.ApplyReport(Tenant, "dir-1", 1, Report("m1", Holding(A, true, 10, 45), Week(Holding(B, false, 10, 99), T0.AddMinutes(-1))), T0, TimeSpan.Zero);

        var outlook = Forecast(T0).Outlook;

        Assert.Equal(new[] { (LicenseOutlookSpanKinds.Unknown, T0, T0.AddDays(7)) }, Spans(outlook, B));
        Assert.Equal("Not known: it was at a limit, and that limit has reset since the reading", outlook.Rows.Single(r => r.AccountEmail == B).Spans[0].Text);
    }

    [Fact]
    public void Outlook_AnAccountAtItsLimitWithNoResetTime_IsAtTheLimitAllWeek_AndNoAccountHasRoomAgain()
    {
        var noReset = Holding(B, false, 10, 99);
        noReset.SevenDay!.ResetsAtUtc = null;
        _store.ApplyReport(Tenant, "dir-1", 1, Report("m1", Holding(A, true, 98, 45), noReset), T0, TimeSpan.Zero);

        var forecast = Forecast(T0);
        var outlook = forecast.Outlook;

        Assert.Equal(new[] { (LicenseOutlookSpanKinds.AtLimit, T0, T0.AddDays(7)) }, Spans(outlook, B));
        Assert.Equal("At its weekly limit, and the reset time is not known", outlook.Rows.Single(r => r.AccountEmail == B).Spans[0].Text);
        Assert.Equal(("None in the next 7 days", LicenseTones.Bad), (outlook.NextRoom.ValueText, outlook.NextRoom.Tone));
        Assert.Equal(new[] { ("Has room again", "Not in the next 7 days", LicenseTones.Bad) }, Rows(forecast, B));
    }

    [Fact]
    public void Outlook_WhenTheSlowerPaceWouldNotReach97BeforeTheReset_TheForecastSpanRunsToTheReset_AndNoAtLimitSpanFollows()
    {
        // A's week resets at T0 + 30 hours, so it began 138 hours ago: 45% over 138 hours would need 159 more hours.
        _history.Record(Tenant, Report("m1", Week(Holding(A, true, 10, 40, readAt: T0.AddMinutes(-20)), T0.AddHours(30))), TimeSpan.Zero, T0);
        _history.Record(Tenant, Report("m1", Week(Holding(A, true, 10, 45), T0.AddHours(30))), TimeSpan.Zero, T0);
        _store.ApplyReport(Tenant, "dir-1", 1, Report("m1", Week(Holding(A, true, 10, 45), T0.AddHours(30)), Week(Holding(B, false, 10, 98), T0.AddDays(2))), T0, TimeSpan.Zero);

        var outlook = Forecast(T0).Outlook;

        var a = Spans(outlook, A);
        Assert.Equal(new[] { LicenseOutlookSpanKinds.Room, LicenseOutlookSpanKinds.Forecast, LicenseOutlookSpanKinds.Room }, a.Select(s => s.Kind));
        Close(RunsOut, a[1].Start);
        Assert.Equal(T0.AddHours(30), a[1].End);
        Assert.Equal("At its slower weekly average pace it does not run out before its weekly limit resets.", outlook.Headline.Detail);
    }

    [Fact]
    public void Outlook_TwoAccountsInUse_TheOneThatHasRoomCountsAsRoom_AndIsNeverTheNextAccountWithRoom()
    {
        ApplyRising(Week(Holding(B, false, 10, 98), T0.AddDays(2)));
        _store.ApplyReport(Tenant, "dir-2", 1, Report("m2", Holding(C, true, 10, 20)), T0, TimeSpan.Zero);

        var outlook = Forecast(T0).Outlook;

        Assert.Equal(new[] { (LicenseOutlookSpanKinds.Room, T0, T0.AddDays(7)) }, Spans(outlook, C));
        Assert.Empty(outlook.Gaps);
        Assert.Equal($"{A} runs out in about 3 hours 28 minutes, and {C} has room then.", outlook.Headline.Text);
        // The next account with room is one not in use: B, when its week resets - not C.
        Assert.Equal(("In about 2 days", $"{B}, when its weekly limit resets."), (outlook.NextRoom.ValueText, outlook.NextRoom.DetailText));
    }

    // The re-review of pull request 4299, finding 3: an account in use on another machine can be barred too. Its row is
    // drawn as it is - its own machines still work on it - but it is never named as where A's machines go.
    [Fact]
    public void Outlook_AnAccountInUseThatIsBarred_IsNeverNamedAsTheTarget()
    {
        ApplyRising(Week(Holding(B, false, 10, 98), T0.AddDays(2)));
        _store.ApplyReport(Tenant, "dir-2", 1, Report("m2", Holding(C, true, 10, 20)), T0, TimeSpan.Zero);
        _store.Bar(Tenant, C, LicenseWindowNames.SevenDay, T0.AddDays(1), T0);

        var forecast = Forecast(T0);

        Assert.Equal(new[] { (LicenseOutlookSpanKinds.Room, T0, T0.AddDays(7)) }, Spans(forecast.Outlook, C));
        Assert.Equal($"{A} runs out in about 3 hours 28 minutes, and no other account can take its machines then: the one with room is left out of the automatic choice.",
            forecast.Outlook.Headline.Text);
        Assert.Equal(LicenseTones.Warning, forecast.Outlook.Headline.Tone);
        Assert.Equal(("Then switches to", "No account can be chosen", LicenseTones.Bad), Rows(forecast, A)[2]);
    }

    private static (string, string, string, string) Tile(LicenseOutlookTileDto tile) => (tile.Label, tile.ValueText, tile.DetailText, tile.Tone);
}
