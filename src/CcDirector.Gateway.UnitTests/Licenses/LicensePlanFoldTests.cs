using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// MAKE IT LAST (the usage-by-session mandate): will the account in use last until a target, and if not, what to stop -
/// waiting sessions before working ones, biggest first, every top-level session offered as one choice, and a what-if.
///
/// The main picture: account A at 80% of its weekly limit, resetting in 24 hours, burning 3% an hour over the last 6
/// hours (1% on average this week). Four live sessions: #101 working at 1.5% an hour and #103 waiting at 0.5%, both under
/// top-level #101; #102 waiting at 1% and #104 working at 0.2%, each its own top level.
/// </summary>
public sealed class LicensePlanFoldTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private const string A = "account-a@example.com";
    private const string B = "account-b@example.com";

    private static LicenseUsageSessionDto Session(string id, int number, string top, string state, double rate) => new()
    {
        SessionId = id,
        Number = number,
        Name = "Session " + number,
        TopLevelSessionId = top,
        IsLive = true,
        State = state,
        StateText = state == "working" ? "Working" : "Waiting",
        RateNowPercentPerHour = rate,
    };

    private static LicenseUsageResponse Usage(double used = 80, bool measured = true)
    {
        var sessions = new List<LicenseUsageSessionDto>
        {
            Session("w1", 101, "w1", "working", 1.5),
            Session("s1", 102, "s1", "waiting", 1.0),
            Session("s2", 103, "w1", "waiting", 0.5),
            Session("w2", 104, "w2", "working", 0.2),
        };
        if (!measured)
            sessions.ForEach(s => s.RateNowPercentPerHour = null);
        return new LicenseUsageResponse
        {
            AsOfUtc = Now,
            AccountEmail = A,
            AccountInUse = true,
            Calibration = new LicenseUsageCalibrationDto { IsMeasured = measured },
            WeekUsedPercent = used,
            WeekResetsAtUtc = Now.AddHours(24),
            Sessions = sessions,
            TopLevel = new List<LicenseUsageGroupDto>
            {
                new() { Id = "w1", Number = 101, Label = "#101 Session 101", SessionIds = new() { "w1", "s2", "gone" }, LiveSessionIds = new() { "w1", "s2" } },
                new() { Id = "s1", Number = 102, Label = "#102 Session 102", SessionIds = new() { "s1" }, LiveSessionIds = new() { "s1" } },
                new() { Id = "w2", Number = 104, Label = "#104 Session 104", SessionIds = new() { "w2" }, LiveSessionIds = new() { "w2" } },
            },
        };
    }

    private static LicenseWindowForecast Week(double? average = 1, double? recent = 3) =>
        new(LicenseWindowNames.SevenDay, 80, Now.AddHours(24), recent ?? average, null, average, recent);

    private static LicensePlanResponse Plan(LicenseUsageResponse? usage = null, LicenseWindowForecast? week = null, string? until = null,
        IReadOnlyList<(string, DateTime)>? others = null, string[]? stop = null, string[]? stopGroups = null)
        => LicensePlanFold.Plan(new LicensePlanInput(Now, usage ?? Usage(), week ?? Week(), others ?? Array.Empty<(string, DateTime)>(),
            until, stop ?? [], stopGroups ?? []))!;

    [Fact]
    public void Plan_RunsOutBeforeTheReset_SaysWhen_AndWhatToCut()
    {
        var plan = Plan();

        // 17 points left over 24 hours is 0.708% an hour; at 3% an hour it is gone in 5.67 hours.
        Assert.Equal(("reset", Now.AddHours(24)), (plan.TargetId, plan.TargetAtUtc!.Value));
        Assert.Equal(17, plan.LeftPercent!.Value, 6);
        Assert.Equal(24, plan.HoursToGo!.Value, 6);
        Assert.Equal(17.0 / 24, plan.AllowedPercentPerHour!.Value, 6);
        Assert.Equal(3, plan.BurnPercentPerHour!.Value, 6);
        Assert.Equal(3 - 17.0 / 24, plan.ShortfallPercentPerHour!.Value, 6);
        Assert.False(plan.Lasts);
        Assert.Equal(Now.AddHours(17.0 / 3), plan.RunsOutAtUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(LicenseTones.Bad, plan.VerdictTone);
        Assert.StartsWith("At 3% an hour it reaches 97% in about 6 hours, 18 hours too early. To make it last, use no more than 0.71% an hour", plan.Verdict);
        Assert.Contains("faster than this week's average of 1%", plan.BurnText);
    }

    [Fact]
    public void Plan_SuggestsWaitingSessionsBeforeWorkingOnes_BiggestFirst_UntilTheShortfallIsCovered()
    {
        var plan = Plan();

        // Shortfall 2.29: waiting 1.0 and 0.5 give 1.5, then the biggest working one, 1.5, covers it.
        Assert.Equal(new[] { "s1", "s2", "w1" }, plan.Suggestion.SessionIds);
        Assert.Equal(3.0, plan.Suggestion.SavesPercentPerHour!.Value, 6);
        Assert.Equal("Stop 3 sessions (2 waiting, 1 working) to save about 3% an hour - enough to make it last.", plan.Suggestion.Text);
        Assert.Equal(new[] { "s1", "s2", "w1" }, plan.Sessions.Where(s => s.Suggested).Select(s => s.SessionId).Order());
    }

    [Fact]
    public void Plan_OffersEachTopLevelSession_AsOneChoiceOfItsLiveSessions()
    {
        var plan = Plan();

        Assert.Equal(new[] { "w1", "s1", "w2" }, plan.Choices.Select(c => c.TopLevelSessionId));
        var first = plan.Choices[0];
        Assert.Equal(new[] { "w1", "s2" }, first.SessionIds);
        Assert.Equal((1, 1), (first.Working, first.Waiting));
        Assert.Equal(2.0, first.RateNowPercentPerHour!.Value, 6);
    }

    [Fact]
    public void Plan_WhatIf_StopsAGroupAndASession_AndNamesWhatItDidNotFind()
    {
        var plan = Plan(stop: ["s1", "nobody"], stopGroups: ["w2"]);

        var whatIf = plan.WhatIf!;
        Assert.Equal(new[] { "s1", "w2" }, whatIf.StoppedSessionIds);
        Assert.Equal(new[] { "nobody" }, whatIf.NotFound);
        Assert.Equal(1.2, whatIf.SavesPercentPerHour!.Value, 6);
        Assert.Equal(1.8, whatIf.BurnPercentPerHour!.Value, 6);
        Assert.False(whatIf.Lasts);
        Assert.StartsWith("Stopping 2 sessions saves about 1.2% an hour. ", whatIf.Verdict);
    }

    [Fact]
    public void Plan_WhatIf_ThatCoversTheShortfall_Lasts()
    {
        var plan = Plan(stopGroups: ["w1", "s1"]);

        Assert.Equal(3.0, plan.WhatIf!.SavesPercentPerHour!.Value, 6);
        Assert.True(plan.WhatIf.Lasts);
        Assert.Equal(LicenseTones.Good, plan.WhatIf.VerdictTone);
    }

    [Fact]
    public void Plan_ThatLasts_SuggestsNothing()
    {
        var plan = Plan(usage: Usage(used: 50), week: Week(average: 0.5, recent: null));

        Assert.True(plan.Lasts);
        Assert.Equal(0, plan.ShortfallPercentPerHour);
        Assert.Empty(plan.Suggestion.SessionIds);
        Assert.Equal("Nothing needs to stop: it lasts at this pace.", plan.Suggestion.Text);
        Assert.StartsWith("At 0.5% an hour it lasts, with about 35 points to spare.", plan.Verdict);
    }

    [Fact]
    public void Plan_TheTargetsAreEachOtherAccountsReturn_ThenItsOwnReset_AndTheFirstIsTheDefault()
    {
        var plan = Plan(others: [(B, Now.AddHours(6)), ("account-c@example.com", Now.AddHours(-1))]);

        Assert.Equal(new[] { ("room:" + B, Now.AddHours(6)), ("reset", Now.AddHours(24)) }, plan.Targets.Select(t => (t.Id, t.AtUtc)));
        Assert.Equal(("room:" + B, $"until {B} has room again"), (plan.TargetId, plan.TargetText));
        // 17 points over 6 hours is 2.83% an hour: still short of 3.
        Assert.False(plan.Lasts);
    }

    [Fact]
    public void Plan_ATimeAfterTheReset_OnlyHasToLastUntilTheReset()
    {
        var plan = Plan(until: Now.AddHours(48).ToString("O"));

        Assert.Equal(("time", Now.AddHours(48)), (plan.TargetId, plan.TargetAtUtc!.Value));
        Assert.Equal(24, plan.HoursToGo!.Value, 6);
        Assert.EndsWith("Its weekly limit resets before then, so it only has to last until the reset.", plan.Verdict);
    }

    [Fact]
    public void Plan_AnUntilThatIsNeitherATargetNorATime_IsNotUnderstood()
    {
        Assert.Null(LicensePlanFold.Plan(new LicensePlanInput(Now, Usage(), Week(), [], "next tuesday-ish", [], [])));
    }

    [Fact]
    public void Plan_WithNoPace_SaysNotKnown_AndSuggestsNothing()
    {
        var plan = Plan(week: Week(average: null, recent: null));

        Assert.Null(plan.Lasts);
        Assert.Equal(LicenseTones.Neutral, plan.VerdictTone);
        Assert.StartsWith("Not known yet", plan.Verdict);
        Assert.Empty(plan.Suggestion.SessionIds);
    }

    [Fact]
    public void Plan_WithSessionRatesNotMeasured_GivesTheVerdict_ButNoSuggestion()
    {
        var plan = Plan(usage: Usage(measured: false));

        Assert.False(plan.Lasts);
        Assert.Empty(plan.Suggestion.SessionIds);
        Assert.Equal("The sessions' rates are not measured yet, so nothing can be suggested.", plan.Suggestion.Text);
    }

    [Fact]
    public void Plan_WhenStoppingEverySessionIsNotEnough_SaysTheRestIsUseDevThrottleDoesNotSee()
    {
        var plan = Plan(week: Week(average: 1, recent: 10));

        Assert.Equal(4, plan.Suggestion.SessionIds.Count);
        Assert.StartsWith("Even stopping every session using it saves only 3.2% an hour", plan.Suggestion.Text);
    }

    [Fact]
    public void Plan_AtTheLimitNow_SaysSo()
    {
        var plan = Plan(usage: Usage(used: 98));

        Assert.False(plan.Lasts);
        Assert.Equal(0, plan.LeftPercent);
        Assert.StartsWith("It is at its limit now.", plan.Verdict);
    }

    [Fact]
    public void Plan_WithNoAccount_SaysWhy()
    {
        var plan = Plan(usage: new LicenseUsageResponse { AsOfUtc = Now, EmptyText = "No machine has reported a Claude account yet, so there is nothing to measure." });

        Assert.Equal("No machine has reported a Claude account yet, so there is nothing to measure.", plan.EmptyText);
        Assert.Null(plan.TargetAtUtc);
        Assert.Empty(plan.Targets);
    }

    [Fact]
    public void BackAt_IsWhenTheLastOfAnAccountsFullWindowsResets_ForFreshReadingsOnly()
    {
        var snapshot = new LicenseSnapshot(Now, new List<LicenseAccountReading>
        {
            new(A, new LicenseWindowReading(10, Now.AddHours(2)), new LicenseWindowReading(50, Now.AddDays(3)), Now, true),
            new(B, new LicenseWindowReading(98, Now.AddHours(2)), new LicenseWindowReading(99, Now.AddDays(2)), Now, false),
            new("account-c@example.com", new LicenseWindowReading(98, Now.AddHours(2)), new LicenseWindowReading(20, Now.AddDays(2)), Now, false),
            new("account-d@example.com", new LicenseWindowReading(99, Now.AddHours(2)), new LicenseWindowReading(20, Now.AddDays(2)), Now.AddDays(-1), false),
        }, new List<LicenseMachineState>(), [], []);

        var back = LicensePlanService.BackAt(snapshot);

        Assert.Equal(new[] { (B, Now.AddDays(2)), ("account-c@example.com", Now.AddHours(2)) }, back);
    }
}
