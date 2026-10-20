using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// What is using the license, as pure code: the weighting, the change between two reports, the calibration and the
/// folded answer. Sessions are invented ("Developer one", "Lead"); no real fleet's names.
/// </summary>
public sealed class SessionUsageFoldTests
{
    // ---- the weighting and the change between two reports ----

    [Fact]
    public void Weighted_UsesThePriceRatios_InputOneCacheWriteOneAndAQuarterCacheReadATenthOutputFive()
    {
        Assert.Equal(1000 + 125 + 10 + 500, SessionUsageMath.Weighted(new RawTokens(1000, 100, 100, 100)));
    }

    [Fact]
    public void Change_IsTheDifference_WhenTheSameConversationGrew()
    {
        var change = SessionUsageMath.Change(new RawTokens(10, 20, 30, 40), new RawTokens(15, 25, 130, 40), sameConversation: true);
        Assert.Equal(new RawTokens(5, 5, 100, 0), change);
    }

    [Fact]
    public void Change_IsTheWholeReport_WhenAnyCountFell_BecauseTheAgentStartedCountingAgain()
    {
        // A compaction into a fresh conversation: the new report is smaller, and all of it is new use.
        var change = SessionUsageMath.Change(new RawTokens(1000, 2000, 3000, 4000), new RawTokens(5, 6000, 7, 8), sameConversation: true);
        Assert.Equal(new RawTokens(5, 6000, 7, 8), change);
    }

    [Fact]
    public void Change_IsTheWholeReport_FromAnotherConversation_EvenWhenEveryCountIsLarger()
    {
        var change = SessionUsageMath.Change(new RawTokens(1, 1, 1, 1), new RawTokens(9, 9, 9, 9), sameConversation: false);
        Assert.Equal(new RawTokens(9, 9, 9, 9), change);
    }

    // ---- the calibration ----

    private static LicenseUsageReadingRecord Reading(double hoursAfterT0, double week, DateTime? resets = null)
        => new(A, T0.AddHours(hoursAfterT0), week, resets ?? T0.AddDays(4), 10, T0.AddHours(3));

    [Fact]
    public void CalibrationSpan_IsTheFirstAndLatestReadingOfTheLatestWeek()
    {
        var older = Reading(-30, 90, resets: T0.AddDays(-3)); // last week: a different reset time
        var (span, whyNot) = SessionUsageFold.CalibrationSpan(new[] { Reading(5, 46), older, Reading(1, 40), Reading(3, 43) });

        Assert.Null(whyNot);
        Assert.Equal((T0.AddHours(1), T0.AddHours(5), 6.0), (span!.FromUtc, span.ToUtc, span.RisePercent));
    }

    [Fact]
    public void CalibrationSpan_IsNotMeasured_UntilTheWeekHasRisenAPoint()
    {
        var (span, whyNot) = SessionUsageFold.CalibrationSpan(new[] { Reading(0, 40), Reading(3, 40.5) });

        Assert.Null(span);
        Assert.Contains("needs at least 1 point", whyNot);
    }

    [Fact]
    public void CalibrationSpan_IsNotMeasured_WithNoReading()
    {
        var (span, whyNot) = SessionUsageFold.CalibrationSpan(Array.Empty<LicenseUsageReadingRecord>());
        Assert.Null(span);
        Assert.Equal("Not measured yet: no reading of this account's weekly use has been kept.", whyNot);
    }

    [Fact]
    public void Calibrate_IsTheRiseOverTheWeightedUse()
    {
        var calibration = SessionUsageFold.Calibrate(new SessionUsageCalibrationSpan(T0, T0.AddHours(4), 2), null, 4_000_000);

        Assert.True(calibration.IsMeasured);
        Assert.Equal(0.5 / 1_000_000, calibration.PercentPerWeighted!.Value, 12);
        Assert.Contains("a million weighted units is about 0.5% of the week", calibration.Text);
    }

    [Fact]
    public void Calibrate_IsNotMeasured_WhenNoSessionUsedTheAccountInTheSpan()
    {
        var calibration = SessionUsageFold.Calibrate(new SessionUsageCalibrationSpan(T0, T0.AddHours(4), 2), null, 0);

        Assert.False(calibration.IsMeasured);
        Assert.Contains("came from somewhere DevThrottle does not see", calibration.Text);
    }

    // ---- the folded answer ----

    private static SessionUsageEntity Row(string id, int number, string name, string? controller = null, string state = "Working",
        double soFarOnA = 0, Guid? mission = null, string? missionName = null, string? account = A, double startedHoursAgo = 2)
        => new()
        {
            SessionId = id,
            Number = number,
            Name = name,
            ControllerSessionId = controller,
            State = state,
            AgentKind = "claude",
            MachineName = "machine-one",
            Model = "opus",
            MissionId = mission,
            MissionName = missionName,
            AccountEmail = account,
            StartedAtUtc = T0.AddHours(-startedHoursAgo),
            FirstSeenAtUtc = T0.AddHours(-startedHoursAgo),
            LastSeenAtUtc = T0,
            WeightedTotal = soFarOnA,
            WeightedByAccountJson = JsonSerializer.Serialize(new Dictionary<string, double> { [A] = soFarOnA }),
        };

    // One percent per million weighted units.
    private static readonly SessionUsageCalibration OnePercentPerMillion =
        SessionUsageFold.Calibrate(new SessionUsageCalibrationSpan(T0.AddHours(-5), T0, 5), null, 5_000_000);

    private static SessionUsageFoldInput Input(IReadOnlyList<SessionUsageEntity> rows, IReadOnlySet<string> live,
        IReadOnlyList<SessionAccountUse>? hour = null, IReadOnlyList<SessionAccountUse>? four = null,
        SessionUsageCalibration? calibration = null, long? priceCents = 20000, bool includeGone = false)
        => new(T0, A, new[] { (A, true), (B, false) }, 58, T0.AddDays(4), calibration ?? OnePercentPerMillion, rows, live,
            hour ?? Array.Empty<SessionAccountUse>(), four ?? Array.Empty<SessionAccountUse>(), priceCents, includeGone);

    [Fact]
    public void Fold_GivesEachSessionItsRateNowLastFourHoursAndSoFar_InPercentOfTheWeeklyLimit()
    {
        var rows = new[] { Row("s1", 101, "Developer one", soFarOnA: 3_000_000, startedHoursAgo: 6) };
        var answer = SessionUsageFold.Fold(Input(rows, new HashSet<string> { "s1" },
            hour: new[] { new SessionAccountUse("s1", A, 250_000), new SessionAccountUse("s1", B, 999_999) },
            four: new[] { new SessionAccountUse("s1", A, 1_000_000) }));

        var s = Assert.Single(answer.Sessions);
        Assert.Equal((0.25, 1.0, 3.0), (s.RateNowPercentPerHour!.Value, s.Last4HoursPercent!.Value, s.SoFarPercent!.Value));
        Assert.Equal(6.0, s.RunningHours, 6);
        Assert.Equal(0.5, s.AveragePercentPerHour!.Value, 6);
        // $200 a month is $46.15 a week, so 1% is $0.4615 and 3% is $1.38.
        Assert.Equal(1.3846, s.DollarsSoFar!.Value, 3);
        Assert.Equal(("working", "Working", true), (s.State, s.StateText, s.IsLive));
        Assert.Equal(0.25, answer.BurnPercentPerHour);
        Assert.Equal("account-a@example.com (in use)", answer.Accounts[0].Label);
        Assert.Contains("1% of a week is about $0.46", answer.UnitText[^1]);
    }

    [Fact]
    public void Fold_LeavesEveryPercentNull_WhenTheCalibrationIsNotMeasured_AndSaysWhy()
    {
        var notMeasured = SessionUsageFold.Calibrate(null, "Not measured yet: reason.", 0);
        var answer = SessionUsageFold.Fold(Input(new[] { Row("s1", 101, "Developer one", soFarOnA: 10) }, new HashSet<string> { "s1" }, calibration: notMeasured));

        var s = Assert.Single(answer.Sessions);
        Assert.Null(s.RateNowPercentPerHour);
        Assert.Null(s.SoFarPercent);
        Assert.Null(s.DollarsSoFar);
        Assert.Null(answer.BurnPercentPerHour);
        Assert.Equal("Not measured yet: reason.", answer.Calibration.Text);
        Assert.False(answer.Calibration.IsMeasured);
    }

    [Fact]
    public void Fold_GroupsEverySeatUnderTheSessionAtTheTopOfItsChainOfControllers()
    {
        var rows = new[]
        {
            Row("lead", 100, "Lead", state: "WaitingForInput", soFarOnA: 1_000_000),
            Row("dev", 101, "Developer one", controller: "lead", soFarOnA: 2_000_000),
            Row("rev", 102, "Reviewer", controller: "dev", soFarOnA: 500_000),
            Row("solo", 200, "On its own", soFarOnA: 100_000),
        };
        var answer = SessionUsageFold.Fold(Input(rows, new HashSet<string> { "lead", "dev", "rev", "solo" },
            hour: new[] { new SessionAccountUse("dev", A, 300_000), new SessionAccountUse("rev", A, 100_000), new SessionAccountUse("solo", A, 50_000) }));

        Assert.Equal("lead", answer.Sessions.Single(s => s.SessionId == "rev").TopLevelSessionId);
        var top = answer.TopLevel[0];
        Assert.Equal(("lead", 100, "#100 Lead"), (top.Id, top.Number, top.Label));
        Assert.Equal(new[] { "lead", "dev", "rev" }, top.SessionIds);
        Assert.Equal(0.4, top.RateNowPercentPerHour!.Value, 6);
        Assert.Equal(3.5, top.SoFarPercent!.Value, 6);
        Assert.Equal("solo", answer.TopLevel[1].Id);
        Assert.Equal(("waiting", "Waiting"), (answer.Sessions.Single(s => s.SessionId == "lead").State, answer.Sessions.Single(s => s.SessionId == "lead").StateText));
    }

    [Fact]
    public void TopLevel_StopsAtALoop_InsteadOfWalkingForEver()
    {
        var a = Row("a", 1, "A", controller: "b");
        var b = Row("b", 2, "B", controller: "a");
        var byId = new Dictionary<string, SessionUsageEntity> { ["a"] = a, ["b"] = b };

        Assert.Equal("b", SessionUsageFold.TopLevel(a, byId));
    }

    [Fact]
    public void Fold_GroupsByMission_WithOneGroupForSessionsOnNoMission()
    {
        var mission = Guid.NewGuid();
        var rows = new[]
        {
            Row("s1", 101, "One", mission: mission, missionName: "Build the thing", soFarOnA: 1_000_000),
            Row("s2", 102, "Two", soFarOnA: 1_000_000),
        };
        var answer = SessionUsageFold.Fold(Input(rows, new HashSet<string> { "s1", "s2" }));

        Assert.Equal(new[] { "Build the thing", "No Mission" }, answer.Missions.Select(m => m.Label).Order());
        Assert.Null(answer.Missions.Single(m => m.Label == "No Mission").Id);
    }

    [Fact]
    public void Fold_ShowsASessionThatHasGone_OnlyWhenAskedForClosedOnes_WithItsTotalKept()
    {
        var rows = new[] { Row("gone", 101, "Finished", soFarOnA: 2_000_000), Row("live", 102, "Running", soFarOnA: 1) };

        var liveOnly = SessionUsageFold.Fold(Input(rows, new HashSet<string> { "live" }));
        var withGone = SessionUsageFold.Fold(Input(rows, new HashSet<string> { "live" }, includeGone: true));

        Assert.Equal(new[] { "live" }, liveOnly.Sessions.Select(s => s.SessionId));
        var gone = withGone.Sessions.Single(s => s.SessionId == "gone");
        Assert.Equal(("gone", "Gone", false, 2.0), (gone.State, gone.StateText, gone.IsLive, gone.SoFarPercent));
    }

    [Fact]
    public void Fold_LeavesOutASessionThatNeverUsedTheAccount_AndIsNotOnItNow()
    {
        var rows = new[] { Row("other", 101, "On account B", account: B) };
        var answer = SessionUsageFold.Fold(Input(rows, new HashSet<string> { "other" }));

        Assert.Empty(answer.Sessions);
        Assert.Equal("No live session is using account-a@example.com now.", answer.EmptyText);
    }

    [Fact]
    public void Fold_SaysToSetThePrice_WhenNoneIsSet_AndGivesNoDollar()
    {
        var answer = SessionUsageFold.Fold(Input(new[] { Row("s1", 101, "One", soFarOnA: 1_000_000) }, new HashSet<string> { "s1" }, priceCents: null));

        Assert.Null(answer.Sessions[0].DollarsSoFar);
        Assert.Null(answer.DollarsPerPercent);
        Assert.Equal("Set what the plan costs a month on the Licenses page to see dollars.", answer.UnitText[^1]);
    }

    [Fact]
    public void Fold_WithNoAccount_SaysThereIsNothingToMeasure()
    {
        var input = Input(Array.Empty<SessionUsageEntity>(), new HashSet<string>()) with { AccountEmail = null, Accounts = Array.Empty<(string, bool)>() };
        var answer = SessionUsageFold.Fold(input);

        Assert.Equal("No machine has reported a Claude account yet, so there is nothing to measure.", answer.EmptyText);
        Assert.Empty(answer.Sessions);
    }
}
