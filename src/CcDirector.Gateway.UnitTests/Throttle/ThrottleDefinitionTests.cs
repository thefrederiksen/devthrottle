using CcDirector.Gateway.Throttle;
using Xunit;
using static CcDirector.Gateway.Throttle.ThrottleDefinition;

namespace CcDirector.Gateway.UnitTests.Throttle;

/// <summary>
/// The guard for ruling R17 of the "Clean up Your Throttle" mission (2026-09-05): the ledger predicate is
/// stated EXACTLY, once, and its three consequences are true in the code. These run the pure fold, so
/// nothing about a database can excuse a wrong count.
///
/// The three consequences, each with a row shaped exactly like the ledger records it:
///   1. a turn typed at the desktop terminal (null SendSource, present InputOrigin) is IN;
///   2. agent traffic (Agent SendSource, no InputOrigin) is OUT by record;
///   3. a submission with no InputOrigin is OUT and disclosed as a count beside the share.
/// </summary>
public sealed class ThrottleDefinitionTests
{
    private static readonly DateTime From = new(2026, 8, 24, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 8, 31, 4, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyDictionary<string, SessionFacts> NoSessions = new Dictionary<string, SessionFacts>();

    /// <summary>
    /// The tests written before who-started-the-session mattered are about other things (the predicate's three
    /// consequences, the hours, the splits), so this wrapper states every session in them as started by a
    /// person: a session missing from their history map, or listed without an origin, is filled in as human.
    /// It shadows the library's <c>Fold</c> inside this class only. The starter rule itself is proven by the
    /// tests that call <see cref="ThrottleDefinition.Fold"/> directly.
    /// </summary>
    private static CcDirector.Gateway.Throttle.ThrottleFigureDto Fold(
        IEnumerable<LedgerSubmission> rows, DateTime from, DateTime to, IReadOnlyDictionary<string, SessionFacts> sessions)
    {
        var list = rows.ToList();
        var human = new Dictionary<string, SessionFacts>(StringComparer.Ordinal);
        foreach (var (id, f) in sessions) human[id] = f with { OriginKind = f.OriginKind ?? "human" };
        foreach (var r in list) if (!human.ContainsKey(r.SessionId)) human[r.SessionId] = new SessionFacts(null, null, "human");
        return ThrottleDefinition.Fold(list, from, to, human);
    }

    private static LedgerSubmission Row(string? origin, string? source, string session = "s1", string? agent = "ClaudeCode",
        int hoursIn = 1)
        => new(From.AddHours(hoursIn), session, agent, origin, source);

    [Fact]
    public void ThePredicateIsStatedExactlyAsRulingR17StatesIt()
    {
        // Verbatim. A paraphrase here is a paraphrase on the page and in the mentor report, and the ruling
        // says phase three does not get to paraphrase it.
        Assert.Equal(
            "The shared figure is computed over activity_events rows where EventType is turn-submitted and " +
            "InputOrigin is present, in sessions a person started, grouped by the origin's modality and surface.",
            Predicate);
        Assert.Equal("turn-submitted", TurnSubmitted);
        Assert.Equal("submitted turns", Unit);
        Assert.Equal(30, RetentionDays);
    }

    [Fact]
    public void OnlySessionsAPersonStarted_AreInTheHeadline_AndEveryStarterGroupIsReported()
    {
        // The owner's ruling (2026-09-27): a turn says how he drives only in a session he started. The same
        // voice-from-the-phone turn goes into four sessions; only the human one is counted.
        var sessions = new Dictionary<string, SessionFacts>
        {
            ["mine"] = new(null, null, "human"),
            ["seat"] = new(null, null, "agent"),
            ["cron"] = new(null, null, "schedule"),
            ["asked-nothing"] = new(null, null, "unknown"),
            ["old-row"] = new(null, null, null),
        };
        var figure = ThrottleDefinition.Fold(new[]
        {
            Row("voice/phone", "Delivery", session: "mine"),
            Row("typed/desktop", null, session: "mine"),
            Row("typed/desktop", null, session: "seat"),
            Row("typed/desktop", null, session: "seat"),
            Row(null, AgentSendSource, session: "seat"),
            Row("typed/desktop", null, session: "cron"),
            Row("voice/phone", "Delivery", session: "asked-nothing"),
            Row("typed/desktop", null, session: "old-row"),
            Row("typed/desktop", null, session: "not-in-history"),
        }, From, To, sessions);

        Assert.Equal(2, figure.Turns);
        Assert.Equal(1, figure.VoiceTurns);
        Assert.Equal(1, figure.TypedTurns);
        Assert.Equal(1, figure.Sessions);
        Assert.Equal(50, figure.Headline.Voice.Percent);
        Assert.Equal(50, figure.Headline.Phone.Percent);
        // Six origin-carrying turns went into sessions nobody is known to have started by hand.
        Assert.Equal(6, figure.Excluded.NotStartedByYou);
        // Agent traffic is still counted beside the figure, whoever started the session it went into.
        Assert.Equal(1, figure.AgentDrivenTurns);
        Assert.Equal(2, Assert.Single(figure.Agents).Turns);
        Assert.Equal(2, figure.HourlyTurns.Sum(h => h.Turns));

        var s = figure.Starters;
        Assert.True(s.HasData);
        Assert.Equal(6, s.Sessions);
        Assert.Equal(9, s.Turns);
        Assert.Equal(new[] { "human", "agent", "schedule", "notRecorded" }, s.Groups.Select(g => g.Kind));
        Assert.Equal(new[] { "Started by you", "Run by other sessions", "Run by a schedule", "Starter not recorded" },
            s.Groups.Select(g => g.Label));
        Assert.Equal(new[] { 1, 1, 1, 3 }, s.Groups.Select(g => g.Sessions));
        Assert.Equal(new long[] { 2, 3, 1, 3 }, s.Groups.Select(g => g.Turns));
        Assert.Equal(17, s.HumanPercent);
        Assert.Equal(1.0 / 6.0, s.HumanShare!.Value, 12);
        Assert.Equal(new int?[] { 17, 17, 17, 50 }, s.Groups.Select(g => g.SessionPercent));
    }

    [Fact]
    public void AMalformedOriginInASessionYouDidNotStart_IsNeverRead_SoItCannotRefuseTheFigure()
    {
        var sessions = new Dictionary<string, SessionFacts>
        {
            ["mine"] = new(null, null, "human"),
            ["seat"] = new(null, null, "agent"),
        };
        var figure = ThrottleDefinition.Fold(new[]
        {
            Row("voice/phone", "Delivery", session: "mine"),
            Row("not-an-origin", null, session: "seat"),
        }, From, To, sessions);

        Assert.Equal(1, figure.Turns);
        Assert.Equal(1, figure.Excluded.NotStartedByYou);
        // The same row in a session you started is still a producer defect and still fails loud.
        Assert.Throws<InvalidOperationException>(() => ThrottleDefinition.Fold(
            new[] { Row("not-an-origin", null, session: "mine") }, From, To, sessions));
    }

    [Fact]
    public void WithNoSession_TheStartersBlockIsEmpty_AndEveryGroupIsStillServed()
    {
        var figure = ThrottleDefinition.Fold(Array.Empty<LedgerSubmission>(), From, To, NoSessions);
        Assert.False(figure.Starters.HasData);
        Assert.Null(figure.Starters.HumanPercent);
        Assert.Null(figure.Starters.HumanShare);
        Assert.Equal(4, figure.Starters.Groups.Count);
        Assert.All(figure.Starters.Groups, g => Assert.Null(g.SessionPercent));
    }

    [Fact]
    public void ATurnTypedAtTheTerminal_NullSendSourceWithAPresentOrigin_IsIn()
    {
        // Consequence 1 - the 594. The raw-byte terminal path carries no SendSource at all.
        var figure = Fold(new[] { Row("typed/desktop", source: null) }, From, To, NoSessions);

        Assert.Equal(1, figure.Turns);
        Assert.Equal(1, figure.TypedTurns);
        var bucket = Assert.Single(figure.Buckets);
        Assert.Equal("typed", bucket.Modality);
        Assert.Equal("desktop", bucket.Surface);
        Assert.Equal(0, figure.Excluded.NoInputOrigin);
    }

    [Fact]
    public void AgentTraffic_IsOutByRecord_AndReportedBesideTheFigure()
    {
        // Consequence 2. Stamped Agent by R12, no origin. Out of the human figure, counted as the fleet
        // driving itself, attributed to the agent running the session it went into.
        var figure = Fold(new[]
        {
            Row("voice/desktop", "UserInput", agent: "Codex"),
            Row(null, AgentSendSource, agent: "Codex"),
            Row(null, AgentSendSource, agent: "Codex"),
        }, From, To, NoSessions);

        Assert.Equal(1, figure.Turns);
        Assert.Equal(2, figure.AgentDrivenTurns);
        Assert.Equal(2, figure.Excluded.AgentDriven);
        Assert.Equal(0, figure.Excluded.Unresolved);
        var codex = Assert.Single(figure.Agents);
        Assert.Equal("Codex", codex.Agent);
        Assert.Equal(1, codex.Turns);
        Assert.Equal(2, codex.AgentDrivenTurns);
    }

    [Fact]
    public void ASubmissionWithNoOrigin_IsOut_AndDisclosedAsACountBesideTheShare()
    {
        // Consequence 3 - the 502. A person's submission the product could not place, plus the framework's
        // own seed prompt: neither is a bucket, both are counted in the disclosure, and only the person's is
        // "unresolved".
        var figure = Fold(new[]
        {
            Row("voice/phone", "Delivery"),
            Row(null, "UserInput"),
            Row(null, "UserInput"),
            Row(null, FrameworkSendSource),
            Row(null, null),
        }, From, To, NoSessions);

        Assert.Equal(1, figure.Turns);
        Assert.Equal(4, figure.Excluded.NoInputOrigin);
        Assert.Equal(1, figure.Excluded.Framework);
        Assert.Equal(0, figure.Excluded.AgentDriven);
        // UserInput x2 and the honest nothing-at-all row: a person's submissions nobody could place.
        Assert.Equal(3, figure.Excluded.Unresolved);
        Assert.Single(figure.Buckets);
    }

    [Fact]
    public void MembershipIsDecidedByTheOriginAlone_NeverByTheSendSource()
    {
        // Every send source with an origin is in; every send source without one is out. If a future edit
        // starts reading SendSource for membership, one of these eight rows moves and this fails.
        var rows = new List<LedgerSubmission>();
        foreach (var source in new[] { null, "UserInput", "Delivery", "Agent", "Framework" })
        {
            rows.Add(Row("typed/desktop", source));
            rows.Add(Row(null, source));
        }
        var figure = Fold(rows, From, To, NoSessions);

        Assert.Equal(5, figure.Turns);
        Assert.Equal(5, figure.Excluded.NoInputOrigin);
    }

    [Fact]
    public void TheUnknownSurface_StaysItsOwnBucket_AndIsStillACountedTurn()
    {
        var figure = Fold(new[] { Row("typed/unknown", "UserInput") }, From, To, NoSessions);
        Assert.Equal(1, figure.Turns);
        Assert.Equal("unknown", Assert.Single(figure.Buckets).Surface);
    }

    [Fact]
    public void RowsOutsideTheWindow_AreIgnored_AndTheWindowIsHalfOpen()
    {
        var rows = new[]
        {
            new LedgerSubmission(From.AddSeconds(-1), "s1", "ClaudeCode", "typed/desktop", null),   // before
            new LedgerSubmission(From, "s1", "ClaudeCode", "typed/desktop", null),                 // at the start: in
            new LedgerSubmission(To.AddSeconds(-1), "s1", "ClaudeCode", "typed/desktop", null),    // just before the end: in
            new LedgerSubmission(To, "s1", "ClaudeCode", "typed/desktop", null),                   // at the end: out
        };
        var figure = Fold(rows, From, To, NoSessions);
        Assert.Equal(2, figure.Turns);
    }

    [Fact]
    public void TheHourlySeries_KeysByUtcClockHour_AndSplitsVoiceOverTyped()
    {
        var figure = Fold(new[]
        {
            new LedgerSubmission(new DateTime(2026, 8, 24, 13, 5, 0, DateTimeKind.Utc), "s1", "ClaudeCode", "voice/desktop", "UserInput"),
            new LedgerSubmission(new DateTime(2026, 8, 24, 13, 59, 0, DateTimeKind.Utc), "s1", "ClaudeCode", "typed/desktop", null),
            new LedgerSubmission(new DateTime(2026, 8, 24, 14, 0, 0, DateTimeKind.Utc), "s1", "ClaudeCode", "typed/desktop", null),
        }, From, To, NoSessions);

        Assert.Equal(2, figure.HourlyTurns.Count);
        Assert.Equal("2026-08-24T13", figure.HourlyTurns[0].Hour);
        Assert.Equal(2, figure.HourlyTurns[0].Turns);
        Assert.Equal(1, figure.HourlyTurns[0].VoiceTurns);
        Assert.Equal(1, figure.HourlyTurns[0].TypedTurns);
        Assert.Equal("2026-08-24T14", figure.HourlyTurns[1].Hour);
    }

    [Fact]
    public void TheRepositorySplit_JoinsThroughSessionHistory_AndDisclosesWhatItCannotPlace()
    {
        var sessions = new Dictionary<string, SessionFacts>
        {
            ["named"] = new("thefrederiksen/devthrottle", @"D:\ReposFred\devthrottle-throttle"),
            ["named-2"] = new("thefrederiksen/devthrottle", @"D:\ReposFred\devthrottle"),
            ["path-only"] = new(null, @"D:\ReposFred\mindzieWeb"),
            ["empty"] = new(null, null),
        };
        var figure = Fold(new[]
        {
            Row("typed/desktop", null, session: "named"),
            Row("voice/desktop", "UserInput", session: "named"),
            Row("typed/desktop", null, session: "named-2"),
            Row("typed/desktop", null, session: "path-only"),
            Row("typed/desktop", null, session: "empty"),
            Row("typed/desktop", null, session: "not-in-history"),
        }, From, To, sessions);

        Assert.Equal(6, figure.Turns);
        Assert.Equal(2, figure.Repos.Count);
        var devthrottle = figure.Repos[0];
        Assert.Equal("thefrederiksen/devthrottle", devthrottle.Repo);
        Assert.Equal("devthrottle", devthrottle.RepoName);
        Assert.Equal(3, devthrottle.Turns);
        Assert.Equal(1, devthrottle.VoiceTurns);
        Assert.Equal(2, devthrottle.Sessions);
        Assert.Equal(new[] { @"D:\ReposFred\devthrottle", @"D:\ReposFred\devthrottle-throttle" }, devthrottle.Checkouts);
        var mindzie = figure.Repos[1];
        Assert.Equal("mindzieWeb", mindzie.Repo);
        Assert.Equal("mindzieWeb", mindzie.RepoName);
        // "empty" and "not-in-history" are disclosed, never guessed into a row (R7).
        Assert.Equal(2, figure.ReposUnattributedTurns);
    }

    [Fact]
    public void TheAgentSplit_CountsDistinctSessions_AndRanksMostDrivenFirst()
    {
        var figure = Fold(new[]
        {
            Row("typed/desktop", null, session: "a", agent: "Codex"),
            Row("typed/desktop", null, session: "b", agent: "ClaudeCode"),
            Row("voice/desktop", "UserInput", session: "b", agent: "ClaudeCode"),
            Row("voice/desktop", "UserInput", session: "c", agent: null),
        }, From, To, NoSessions);

        Assert.Equal(3, figure.Sessions);
        Assert.Equal(3, figure.Agents.Count);
        Assert.Equal("ClaudeCode", figure.Agents[0].Agent);
        Assert.Equal("Claude Code", figure.Agents[0].AgentName);
        Assert.Equal(2, figure.Agents[0].Turns);
        Assert.Equal(1, figure.Agents[0].Sessions);
        Assert.Contains(figure.Agents, a => a.Agent == "" && a.AgentName == "(unknown)" && a.Turns == 1);
    }

    [Fact]
    public void AMalformedOrigin_RefusesTheFigure_RatherThanGuessingABucket()
    {
        // The mentor harness's reader exits on the same row, so both consumers fail the same way.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Fold(new[] { Row("desktop", "UserInput") }, From, To, NoSessions));
        Assert.Contains("not '<modality>/<surface>'", ex.Message);

        var ex2 = Assert.Throws<InvalidOperationException>(() =>
            Fold(new[] { Row("spoken/desktop", "UserInput") }, From, To, NoSessions));
        Assert.Contains("neither typed nor voice", ex2.Message);
    }

    [Fact]
    public void TheFigureCarriesItsDefinitionAndUnit_SoAReaderCanCheckTheNumberAgainstTheSentence()
    {
        var figure = Fold(Array.Empty<LedgerSubmission>(), From, To, NoSessions);
        Assert.Equal(Predicate, figure.Definition);
        Assert.Equal(Unit, figure.Unit);
        Assert.Equal(From, figure.Window.FromUtc);
        Assert.Equal(To, figure.Window.ToUtc);
        Assert.Equal(0, figure.Turns);
        Assert.Empty(figure.Buckets);
    }

    [Fact]
    public void TheMeasuredWeek_ReproducesPhaseOnesLedgerFigure()
    {
        // The shape of 2026-W35 from reconciliation.md, population B: 1,015 voice and 771 typed carrying an
        // origin (56.83 per cent spoken), 502 UserInput rows with no origin, 160 Framework rows. The fold
        // over rows of that shape must land on exactly those counts - the number the mentor report was
        // reconciled against.
        var rows = new List<LedgerSubmission>();
        void Add(int n, string? origin, string? source) { for (var i = 0; i < n; i++) rows.Add(Row(origin, source, hoursIn: 1 + (i % 100))); }
        Add(835, "voice/desktop", "UserInput");
        Add(591, "typed/desktop", null);
        Add(3, "typed/phone", null);
        Add(502, null, "UserInput");
        Add(180, "voice/phone", "Delivery");
        Add(105, "typed/desktop", "UserInput");
        Add(65, "typed/phone", "UserInput");
        Add(7, "typed/unknown", "UserInput");
        Add(160, null, FrameworkSendSource);

        var figure = Fold(rows, From, To, NoSessions);

        Assert.Equal(1786, figure.Turns);
        Assert.Equal(1015, figure.VoiceTurns);
        Assert.Equal(771, figure.TypedTurns);
        Assert.Equal(0.5683, Math.Round((double)figure.VoiceTurns / figure.Turns, 4));
        Assert.Equal(502, figure.Excluded.Unresolved);
        Assert.Equal(160, figure.Excluded.Framework);
        Assert.Equal(662, figure.Excluded.NoInputOrigin);

        // The headline is the library's, finished: 1015 of 1786 is 56.83 per cent, printed as 57; 180 of 1786
        // from the phone is 10.08 per cent, printed as 10. Neither consumer divides these for itself (F-01).
        Assert.True(figure.Headline.HasData);
        Assert.Equal(1786, figure.Headline.Denominator);
        Assert.Equal(1015, figure.Headline.Voice.Turns);
        Assert.Equal(57, figure.Headline.Voice.Percent);
        Assert.Equal(43, figure.Headline.Typed.Percent);
        Assert.Equal(248, figure.Headline.Phone.Turns);
        Assert.Equal(14, figure.Headline.Phone.Percent);
    }

    // ---- the headline: the final ratios are computed here, once (final inspection finding F-01) ----------

    [Fact]
    public void TheHeadline_CarriesTheDenominator_EveryShare_AndTheRoundedPercentTheReaderSees()
    {
        // 3 voice of 8 counted is 37.5 per cent, which half-up rounding prints as 38; 5 typed is 62.5, printed
        // as 63. Two consumers rounding a fraction each on their own is how 38 and 37 end up on two pages.
        var rows = new List<LedgerSubmission>
        {
            Row("voice/phone", "Delivery"), Row("voice/phone", "Delivery"), Row("voice/desktop", "UserInput"),
            Row("typed/desktop", null), Row("typed/desktop", null), Row("typed/desktop", null),
            Row("typed/cockpit", "UserInput"), Row("typed/unknown", "UserInput"),
        };

        var h = Fold(rows, From, To, NoSessions).Headline;

        Assert.True(h.HasData);
        Assert.Equal(8, h.Denominator);
        Assert.Equal(3, h.Voice.Turns);
        Assert.Equal(0.375, h.Voice.Share);
        Assert.Equal(38, h.Voice.Percent);
        Assert.Equal(5, h.Typed.Turns);
        Assert.Equal(63, h.Typed.Percent);
        Assert.Equal(2, h.Phone.Turns);
        Assert.Equal(25, h.Phone.Percent);
        // The phone ring's other side, served, so no consumer subtracts (fix-round finding F-01).
        Assert.Equal(6, h.Phone.Remainder);
        Assert.Equal(new long[] { 4, 7, 6, 7 }, h.Surfaces.Select(s => s.Remainder).ToArray());
        // Every surface, in the drawing order, zero or not, with the Gateway's own label.
        Assert.Equal(new[] { "desktop", "cockpit", "phone", "unknown" }, h.Surfaces.Select(s => s.Surface).ToArray());
        Assert.Equal(new[] { "Desktop", "Cockpit", "Phone", "Unknown" }, h.Surfaces.Select(s => s.Label).ToArray());
        Assert.Equal(new long[] { 4, 1, 2, 1 }, h.Surfaces.Select(s => s.Turns).ToArray());
        Assert.Equal(new int?[] { 50, 13, 25, 13 }, h.Surfaces.Select(s => s.Percent).ToArray());
        // The phone entry at the top IS the phone entry of the list - one computation, surfaced twice.
        var phone = Assert.Single(h.Surfaces, s => s.Surface == "phone");
        Assert.Equal(h.Phone.Percent, phone.Percent);
        Assert.Equal(h.Phone.Share, phone.Share);
    }

    [Fact]
    public void TheHeadline_WithNothingCounted_IsTheEmptyState_NeverAZeroPercent()
    {
        // Ten submissions, none with an origin: nothing counted. The library says so, and every share and
        // percent is null - a consumer that prints 0% here has invented a number.
        var rows = Enumerable.Range(0, 10).Select(i => Row(null, "UserInput", hoursIn: 1 + i)).ToList();

        var h = Fold(rows, From, To, NoSessions).Headline;

        Assert.False(h.HasData);
        Assert.Equal(0, h.Denominator);
        Assert.Null(h.Voice.Share);
        Assert.Null(h.Voice.Percent);
        Assert.Null(h.Typed.Percent);
        Assert.Null(h.Phone.Percent);
        Assert.Equal(4, h.Surfaces.Count);
        Assert.All(h.Surfaces, s => { Assert.Null(s.Share); Assert.Null(s.Percent); Assert.Equal(0, s.Turns); });
    }

    [Fact]
    public void TheHeadline_RoundsHalfUp_TheOneRuleForEveryConsumer()
    {
        // 1 of 200 is exactly 0.5 per cent: half up prints 1, banker's rounding would print 0. The rule is
        // pinned here because it used to live in two places, each consumer's own.
        var h = Headline(200, 1, 199, new[]
        {
            new ThrottleBucketDto { Modality = "voice", Surface = "phone", Turns = 1 },
            new ThrottleBucketDto { Modality = "typed", Surface = "desktop", Turns = 199 },
        });
        Assert.Equal(1, h.Voice.Percent);
        Assert.Equal(100, h.Typed.Percent);
        Assert.Equal(1, h.Phone.Percent);
    }

    [Fact]
    public void TheHeadline_RefusesASurfaceItDoesNotKnow_RatherThanFoldingItIntoAGuess()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Headline(1, 0, 1, new[]
        {
            new ThrottleBucketDto { Modality = "typed", Surface = "watch", Turns = 1 },
        }));
        Assert.Contains("watch", ex.Message);
        Assert.Contains("refused", ex.Message);
    }

    // ---- fix-round finding F-01: every ratio the pages print is finished here ------------------------------

    [Fact]
    public void EveryHour_CarriesItsOwnSpokenAndTypedShares_SoTheChartDividesNothing()
    {
        var rows = new List<LedgerSubmission>
        {
            Row("voice/phone", "Delivery", hoursIn: 1), Row("voice/desktop", "UserInput", hoursIn: 1), Row("typed/desktop", null, hoursIn: 1),
            Row("typed/desktop", null, hoursIn: 2),
        };
        var hours = Fold(rows, From, To, NoSessions).HourlyTurns;
        Assert.Equal(2, hours.Count);
        Assert.Equal(3, hours[0].Turns);
        Assert.Equal(2.0 / 3.0, hours[0].VoiceShare!.Value, 12);
        Assert.Equal(1.0 / 3.0, hours[0].TypedShare!.Value, 12);
        Assert.Equal(0.0, hours[1].VoiceShare);
        Assert.Equal(1.0, hours[1].TypedShare);
    }

    [Fact]
    public void EveryAgentRow_CarriesItsShareOfTurnsAndSessions_AndItsOwnVoiceShare_Rounded()
    {
        var rows = new List<LedgerSubmission>
        {
            Row("voice/phone", "Delivery", session: "a1", agent: "ClaudeCode"), Row("voice/desktop", "UserInput", session: "a1", agent: "ClaudeCode"),
            Row("typed/desktop", null, session: "a2", agent: "ClaudeCode"),
            Row("typed/desktop", null, session: "b1", agent: "Codex"),
            Row(null, "Agent", session: "b1", agent: "Codex"), Row(null, "Agent", session: "b1", agent: "Codex"), Row(null, "Agent", session: "b1", agent: "Codex"),
        };
        var figure = Fold(rows, From, To, NoSessions);
        var claude = figure.Agents[0];
        var codex = figure.Agents[1];
        Assert.Equal("ClaudeCode", claude.Agent);
        Assert.Equal(0.75, claude.TurnShare);
        Assert.Equal(75, claude.TurnPercent);
        Assert.Equal(2.0 / 3.0, claude.SessionShare!.Value, 12);
        Assert.Equal(67, claude.SessionPercent);
        Assert.Equal(2.0 / 3.0, claude.VoiceShare!.Value, 12);
        Assert.Equal(67, claude.VoicePercent);
        Assert.Equal(0.25, codex.TurnShare);
        Assert.Equal(25, codex.TurnPercent);
        Assert.Equal(0.0, codex.VoiceShare);
        Assert.Equal(0, codex.VoicePercent);

        var summary = figure.AgentsSummary;
        Assert.Equal(2, summary.AgentCount);
        Assert.Equal(4, summary.TotalTurns);
        Assert.Equal(3, summary.TotalSessions);
        Assert.Equal(2, summary.VoiceTurns);
        Assert.Equal(0.5, summary.VoiceShare);
        Assert.Equal(50, summary.VoicePercent);
        Assert.Equal("Claude Code", summary.TopAgentName);
        Assert.Equal(0.75, summary.TopShare);
        Assert.Equal(75, summary.TopPercent);
        Assert.Equal(3, summary.AgentDrivenTurns);
        Assert.Equal(0.75, summary.Leverage);
        Assert.Equal("0.8x", summary.LeverageText);
        Assert.True(summary.HasData);
    }

    [Fact]
    public void TheAgentsSummary_WithNothingDriven_IsTheEmptyState_AndAFleetDrivingItselfIsNot()
    {
        var empty = Fold(Array.Empty<LedgerSubmission>(), From, To, NoSessions).AgentsSummary;
        Assert.False(empty.HasData);
        Assert.Equal(0, empty.AgentCount);
        Assert.Null(empty.TopAgentName);
        Assert.Null(empty.TopShare);
        Assert.Null(empty.VoicePercent);
        Assert.Null(empty.Leverage);
        Assert.Null(empty.LeverageText);

        var fleetOnly = Fold(new[] { Row(null, "Agent", agent: "Codex") }, From, To, NoSessions);
        Assert.True(fleetOnly.AgentsSummary.HasData);
        Assert.Equal(0, fleetOnly.AgentsSummary.AgentCount);
        Assert.Equal(1, fleetOnly.AgentsSummary.AgentDrivenTurns);
        Assert.Null(fleetOnly.AgentsSummary.Leverage);
        Assert.Null(fleetOnly.AgentsSummary.TopAgentName);
        // The row for Codex exists (it was driven into) and carries no share of a zero total.
        var codex = Assert.Single(fleetOnly.Agents);
        Assert.Null(codex.TurnShare);
        Assert.Null(codex.TurnPercent);
        Assert.Null(codex.VoicePercent);
    }

    [Fact]
    public void EveryRepoRow_CarriesItsShares_AndTheReposSummaryIsFinished()
    {
        var sessions = new Dictionary<string, SessionFacts>
        {
            ["s1"] = new SessionFacts("owner/devthrottle", @"D:\devthrottle"),
            ["s2"] = new SessionFacts("owner/devthrottle", @"D:\devthrottle-two"),
            ["s3"] = new SessionFacts("owner/mindzie", @"D:\mindzie"),
        };
        var rows = new List<LedgerSubmission>
        {
            Row("voice/phone", "Delivery", session: "s1"), Row("voice/desktop", "UserInput", session: "s1"), Row("typed/desktop", null, session: "s2"),
            Row("typed/desktop", null, session: "s3"),
            Row("typed/desktop", null, session: "nowhere"),
        };
        var figure = Fold(rows, From, To, sessions);
        var devthrottle = figure.Repos[0];
        Assert.Equal(0.75, devthrottle.TurnShare);
        Assert.Equal(75, devthrottle.TurnPercent);
        Assert.Equal(2.0 / 3.0, devthrottle.SessionShare!.Value, 12);
        Assert.Equal(67, devthrottle.SessionPercent);
        Assert.Equal(67, devthrottle.VoicePercent);
        Assert.Equal(25, figure.Repos[1].TurnPercent);
        Assert.Equal(0, figure.Repos[1].VoicePercent);

        var summary = figure.ReposSummary;
        Assert.Equal(2, summary.RepoCount);
        Assert.Equal(4, summary.TotalTurns);
        Assert.Equal(3, summary.TotalSessions);
        Assert.Equal(2, summary.VoiceTurns);
        Assert.Equal(50, summary.VoicePercent);
        Assert.Equal("devthrottle", summary.TopRepoName);
        Assert.Equal(75, summary.TopPercent);
        Assert.True(summary.HasData);
        Assert.Equal(1, figure.ReposUnattributedTurns);

        var empty = Fold(Array.Empty<LedgerSubmission>(), From, To, NoSessions).ReposSummary;
        Assert.False(empty.HasData);
        Assert.Null(empty.TopRepoName);
        Assert.Null(empty.TopShare);
        Assert.Null(empty.VoicePercent);
    }
}
