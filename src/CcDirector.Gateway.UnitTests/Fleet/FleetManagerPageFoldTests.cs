using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// The Fleet Manager page's folded answer (the Fleet Manager mission, step 6). Pure: every input is handed in.
///
/// Sentences are asserted whole, as literals, because the page renders them verbatim.
/// </summary>
public sealed class FleetManagerPageFoldTests
{
    // 2026-09-16 14:30 UTC. The account's zone is UTC unless a test says otherwise.
    private static readonly DateTime Now = new(2026, 9, 16, 14, 30, 0, DateTimeKind.Utc);
    private const string Marked = "60000000-0000-4000-8000-000000000001";
    private const string OwnedA = "60000000-0000-4000-8000-000000000002";
    private const string OwnedB = "60000000-0000-4000-8000-000000000003";
    private const string Loose = "60000000-0000-4000-8000-000000000004";
    private const string OtherCrew = "60000000-0000-4000-8000-000000000005";
    private const string OtherBoss = "60000000-0000-4000-8000-000000000006";

    private static SessionDto Session(string id, string name, string state = "Idle", string? controller = null,
        bool liveSupervisor = false, DateTime? created = null, string? repo = null) => new()
    {
        SessionId = id, Name = name, ActivityState = state, ControllerSessionId = controller,
        IsControlled = controller is not null, HasLiveSupervisor = liveSupervisor,
        CreatedAt = created ?? Now.AddMinutes(-38), RepoPath = repo ?? "",
    };

    private static FleetOutcomeDto Ready(string id, string title, DateTime created, string? session = null,
        string status = "open", DateTime? answeredAt = null, string? answer = null, string answeredByRole = "owner") => new()
    {
        Id = id, Kind = "ready", Title = title, Status = status, FiledBy = Marked, SessionId = session, CreatedAtUtc = created,
        AnsweredAtUtc = answeredAt, Answer = answer, AnsweredBy = answer is null ? null : answeredByRole == "owner" ? "owner" : Marked,
        AnsweredByRole = answer is null ? null : answeredByRole,
        Ready = new FleetReadyDetails
        {
            PullRequest = "https://example.test/acme/widgets/pull/2934", Risk = "low", Checks = "passed",
            Tested = "3 of 3 live", ReviewedBy = "a second reviewer, 1 finding fixed",
            Change = "The list now waits for the redraw itself.",
        },
    };

    private static FleetOutcomeDto Finding(string id, string title, DateTime created, string status = "open") => new()
    {
        Id = id, Kind = "finding", Title = title, Status = status, FiledBy = Marked, CreatedAtUtc = created,
        Finding = new FleetFindingDetails
        {
            Answer = "Build our own.", Reason = "Both reviews agree.",
            Links = { "https://example.test/reports/tool-a.md", "https://example.test/reports/tool-b.md" },
        },
    };

    private static FleetOutcomeDto Decision(string id, string title, DateTime created, string? session = null,
        string status = "open", string? answer = null) => new()
    {
        Id = id, Kind = "decision", Title = title, Status = status, FiledBy = Marked, SessionId = session, CreatedAtUtc = created,
        Answer = answer, AnsweredAtUtc = answer is null ? null : Now.AddMinutes(-5),
        Decision = new FleetDecisionDetails
        {
            Question = "Replace the inspector, or add to it?",
            Options = { "Replace it for ordinary changes.", "Always run both." },
            Recommended = "Replace it for ordinary changes.",
            Why = "Running both doubled the time.",
        },
    };

    private static FleetManagerPageInputs Inputs(
        IReadOnlyList<SessionDto>? roster = null,
        IReadOnlyList<FleetOutcomeDto>? open = null,
        IReadOnlyList<FleetOutcomeDto>? recent = null,
        string? marked = Marked,
        Func<string, TurnVerdictDto?>? verdict = null,
        TimeZoneInfo? tz = null,
        SessionDto? markedSession = null,
        IReadOnlyDictionary<string, FleetManagerEventDto>? answerEvents = null,
        string? successor = null)
        => new(marked,
            // The marked session's last reported row: the one passed, else its roster row, else an unowned row
            // that has ended (a Fleet Manager whose conversation is still there to read).
            markedSession ?? roster?.FirstOrDefault(s => s.SessionId == marked)
                ?? (marked is null ? null : new SessionDto { SessionId = marked, ActivityState = "Exited" }),
            roster ?? Array.Empty<SessionDto>(), open ?? Array.Empty<FleetOutcomeDto>(),
            recent ?? Array.Empty<FleetOutcomeDto>(), verdict ?? (_ => null), tz ?? TimeZoneInfo.Utc, Now,
            answerEvents, successor);

    // ---- an empty account -----------------------------------------------------------------------------------

    [Fact]
    public void Fold_EmptyAccount_EverySectionSaysSoAndNothingIsWaiting()
    {
        var dto = FleetManagerPageFold.Fold(Inputs(marked: null));

        Assert.Null(dto.FleetManagerSessionId);
        Assert.Equal("There is no Fleet Manager conversation yet. Start the Fleet Manager, then tell it what you want here.",
            dto.NoConversationText);
        Assert.Equal(0, dto.WaitingCount);
        Assert.Empty(dto.Cards);
        Assert.Equal("Waiting on you", dto.Waiting.Title);
        Assert.Equal("plain", dto.Waiting.Tone);
        Assert.Equal("Nothing is waiting on you.", dto.Waiting.EmptyText);
        Assert.Equal("Under way", dto.UnderWay.Title);
        Assert.Equal("No Fleet Manager is marked for this account yet, so no session is its.", dto.UnderWay.EmptyText);
        Assert.Equal("Answered today", dto.Landed.Title);
        Assert.Equal("No Ready card was answered today.", dto.Landed.EmptyText);
        Assert.Equal("What landed today needs the merge itself, which the Gateway does not record yet. "
                     + "This lists the Ready cards answered today.", dto.Landed.Note);
        Assert.Equal(0, dto.NotMine.Count);
        Assert.Equal("No session asks you directly.", dto.NotMine.Lead);
        Assert.Equal("Every live session is the Fleet Manager's or owned by another session.", dto.NotMine.Rest);
        var quick = Assert.Single(dto.QuickPrompts);
        Assert.Equal("What did I miss?", quick.Label);
        Assert.Equal("What did I miss?", quick.Words);
    }

    [Fact]
    public void Fold_MarkedSessionOwnedByAnotherSession_IsNotTheFleetManager()
    {
        // The one rule (FleetManagerSessions.IsFleetManager): a marked session that another session owns is not the
        // Fleet Manager, so it has no conversation here and its sessions are not the Fleet Manager's.
        var roster = new[]
        {
            Session(Marked, "Fleet Manager", controller: OtherBoss),
            Session(OtherCrew, "Started by the marked one", controller: Marked),
        };

        var dto = FleetManagerPageFold.Fold(Inputs(roster: roster));

        Assert.Null(dto.FleetManagerSessionId);
        Assert.Equal($"Session {Marked} is marked as the Fleet Manager, but it is owned by session {OtherBoss}, and a "
                     + "Fleet Manager answers to you only. Start the Fleet Manager from Settings.", dto.NoConversationText);
        Assert.Empty(dto.UnderWay.Items);
        Assert.Equal("The marked session is not the Fleet Manager, so no session is its.", dto.UnderWay.EmptyText);
    }

    [Fact]
    public void Fold_MarkedSessionNeverReported_IsNotTheFleetManager()
    {
        var dto = FleetManagerPageFold.Fold(Inputs(marked: Marked) with { MarkedSession = null });

        Assert.Null(dto.FleetManagerSessionId);
        Assert.Equal($"Session {Marked} is marked as the Fleet Manager, but no computer on this account has reported it. "
                     + "Start the Fleet Manager from Settings.", dto.NoConversationText);
    }

    [Fact]
    public void Fold_MarkedSessionEnded_KeepsItsConversation()
    {
        var dto = FleetManagerPageFold.Fold(Inputs());

        Assert.Equal(Marked, dto.FleetManagerSessionId);
        Assert.Null(dto.NoConversationText);
    }

    [Fact]
    public void Fold_MarkedButOwnsNothing_SaysNothingIsUnderWay()
    {
        var dto = FleetManagerPageFold.Fold(Inputs(roster: new[] { Session(Marked, "Fleet Manager") }));

        Assert.Equal(Marked, dto.FleetManagerSessionId);
        Assert.Null(dto.NoConversationText);
        Assert.Empty(dto.UnderWay.Items);
        Assert.Equal(0, dto.UnderWay.Count);
        Assert.Equal("Nothing is under way. The sessions the Fleet Manager starts show here.", dto.UnderWay.EmptyText);
    }

    // ---- waiting on you -----------------------------------------------------------------------------------

    [Fact]
    public void Fold_Waiting_DecisionsFirstThenReadyThenFindings_OldestFirstWithinEach()
    {
        var open = new[]
        {
            Finding("f1", "A finding", Now.AddHours(-10)),
            Ready("r1", "Merge the release", Now.AddHours(-2)),
            Decision("d2", "Pick a layout", Now.AddMinutes(-42)),
            Decision("d1", "Build our own tool?", Now.AddHours(-9)),
        };

        var dto = FleetManagerPageFold.Fold(Inputs(open: open));

        Assert.Equal(new[] { "d1", "d2", "r1", "f1" }, dto.Waiting.Items.Select(i => i.Id));
        Assert.Equal(4, dto.Waiting.Count);
        Assert.Equal(4, dto.WaitingCount);
        Assert.Equal("attention", dto.Waiting.Tone);
        Assert.Null(dto.Waiting.EmptyText);
        Assert.Equal(new[] { "9h", "42m", "2h", "10h" }, dto.Waiting.Items.Select(i => i.Age));
        Assert.All(dto.Waiting.Items, i =>
        {
            Assert.Equal("red", i.Dot);
            Assert.True(i.Attention);
        });
    }

    [Fact]
    public void Fold_Waiting_MetaNamesTheKindTheRiskAndTheSession()
    {
        var roster = new[] { Session(OwnedA, "Fix the flaky list test", controller: Marked, liveSupervisor: true) };
        var open = new[]
        {
            Ready("r1", "Merge: Fix the flaky list test", Now.AddMinutes(-17), session: OwnedA),
            Decision("d1", "Replace or add the inspector?", Now),
            Finding("f1", "Reviews done", Now),
        };

        var dto = FleetManagerPageFold.Fold(Inputs(roster: roster, open: open));

        Assert.Equal("Decision", dto.Waiting.Items[0].Meta);
        Assert.Equal("now", dto.Waiting.Items[0].Age);
        Assert.Equal("Ready - risk low - checks passed - Fix the flaky list test", dto.Waiting.Items[1].Meta);
        Assert.Equal(OwnedA, dto.Waiting.Items[1].SessionId);
        Assert.Equal("17m", dto.Waiting.Items[1].Age);
        Assert.Equal("Finding", dto.Waiting.Items[2].Meta);
    }

    [Fact]
    public void Fold_Waiting_CarriesTheWingmansLabelOnlyForARecordWithACurrentReading()
    {
        var verdicts = new Dictionary<string, TurnVerdictDto>
        {
            [OwnedA] = new() { Label = "Asks which layout to keep" },
            [OwnedB] = new() { Label = "An old reading", SupersededAtUtc = Now.AddMinutes(-1) },
            [Loose] = new() { Failed = true, FailureReason = "no answer" },
        };
        var open = new[]
        {
            Decision("d1", "Layout", Now.AddMinutes(-3), session: OwnedA),
            Decision("d2", "Old", Now.AddMinutes(-2), session: OwnedB),
            Decision("d3", "Failed", Now.AddMinutes(-1), session: Loose),
            Decision("d4", "No session", Now),
        };

        var dto = FleetManagerPageFold.Fold(Inputs(open: open, verdict: sid => verdicts.GetValueOrDefault(sid)));

        Assert.Equal(new string?[] { "Asks which layout to keep", null, null, null }, dto.Waiting.Items.Select(i => i.Label));
    }

    [Fact]
    public void Fold_Waiting_IgnoresARecordThatIsNotOpen()
    {
        var open = new[] { Decision("d1", "Answered", Now, status: "answered", answer: "Always run both.") };

        var dto = FleetManagerPageFold.Fold(Inputs(open: open));

        Assert.Empty(dto.Waiting.Items);
        Assert.Equal(0, dto.WaitingCount);
    }

    // ---- under way ----------------------------------------------------------------------------------------

    [Fact]
    public void Fold_UnderWay_ListsOnlyLiveSessionsTheFleetManagerDirectlyOwns_OldestFirst()
    {
        var roster = new[]
        {
            Session(Marked, "Fleet Manager", "Working"),
            Session(OwnedB, "Ship workflow - review wording", "Working", Marked, true, Now.AddMinutes(-21), "/work/tools"),
            Session(OwnedA, "Worktree pool - design", "Working", Marked, true, Now.AddHours(-1).AddMinutes(-12), "/work/widgets"),
            Session("60000000-0000-4000-8000-000000000010", "Gone", "Exited", Marked, true),
            new SessionDto { SessionId = "60000000-0000-4000-8000-000000000011", Name = "Crashed", ActivityState = "Idle",
                ControllerSessionId = Marked, Crashed = true, CreatedAt = Now },
            Session(OtherCrew, "Someone else's worker", "Working", OtherBoss, true),
            Session(Loose, "A session of the owner's", "Working"),
        };

        var dto = FleetManagerPageFold.Fold(Inputs(roster: roster));

        Assert.Equal(new[] { OwnedA, OwnedB }, dto.UnderWay.Items.Select(i => i.Id));
        Assert.Equal(2, dto.UnderWay.Count);
        Assert.Null(dto.UnderWay.EmptyText);
        Assert.Equal("Worktree pool - design", dto.UnderWay.Items[0].Title);
        Assert.Equal("widgets - 1h 12m", dto.UnderWay.Items[0].Meta);
        Assert.Equal("tools - 21m", dto.UnderWay.Items[1].Meta);
        Assert.Equal("blue", dto.UnderWay.Items[0].Dot);
        Assert.False(dto.UnderWay.Items[0].Attention);
        Assert.Equal(OwnedA, dto.UnderWay.Items[0].SessionId);
    }

    [Fact]
    public void Fold_UnderWay_AStoppedOwnedSessionIsGrey()
    {
        var roster = new[] { Session(Marked, "Fleet Manager"), Session(OwnedA, "Checking the fix", "Idle", Marked, true) };

        var dto = FleetManagerPageFold.Fold(Inputs(roster: roster));

        Assert.Equal("grey", Assert.Single(dto.UnderWay.Items).Dot);
    }

    [Fact]
    public void Fold_UnderWay_ASessionWithNoRepositoryShowsOnlyItsAge()
    {
        var roster = new[] { Session(OwnedA, "A report", "Working", Marked, true, Now.AddMinutes(-6)) };

        var dto = FleetManagerPageFold.Fold(Inputs(roster: roster));

        Assert.Equal("6m", Assert.Single(dto.UnderWay.Items).Meta);
    }

    // ---- answered today -----------------------------------------------------------------------------------

    [Fact]
    public void Fold_AnsweredToday_ListsReadyCardsAnsweredTodayInTheAccountsZone_MostRecentFirst()
    {
        // 14:30 UTC is 10:30 in New York; "today" there began at 04:00 UTC.
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var recent = new[]
        {
            Ready("r1", "Session tree on the web", Now.AddHours(-12), status: "answered",
                answeredAt: new DateTime(2026, 9, 16, 5, 52, 0, DateTimeKind.Utc), answer: "Merge: Session tree on the web"),
            Ready("r2", "Flaky list test", Now.AddHours(-12), status: "answered",
                answeredAt: new DateTime(2026, 9, 16, 7, 5, 0, DateTimeKind.Utc), answer: "Send it back: Flaky list test. Use the real clock."),
            // Answered yesterday in New York (03:00 UTC is 23:00 the day before).
            Ready("r3", "Yesterday's", Now.AddDays(-1), status: "answered",
                answeredAt: new DateTime(2026, 9, 16, 3, 0, 0, DateTimeKind.Utc), answer: "Merge: Yesterday's"),
            Ready("r4", "Still open", Now.AddHours(-1)),
            Decision("d1", "A decision answered today", Now.AddHours(-1), status: "answered", answer: "Always run both."),
        };

        var dto = FleetManagerPageFold.Fold(Inputs(recent: recent, tz: tz));

        Assert.Equal(new[] { "r2", "r1" }, dto.Landed.Items.Select(i => i.Id));
        Assert.Equal(2, dto.Landed.Count);
        Assert.Equal("You said \"Send it back: Flaky list test. Use the real clock.\" at 03:05", dto.Landed.Items[0].Meta);
        Assert.Equal("You said \"Merge: Session tree on the web\" at 01:52", dto.Landed.Items[1].Meta);
        Assert.All(dto.Landed.Items, i => Assert.Equal("green", i.Dot));
        Assert.Null(dto.Landed.EmptyText);
        Assert.NotNull(dto.Landed.Note);
    }

    [Fact]
    public void Fold_AnsweredTodayByTheFleetManager_SaysTheFleetManagerAnswered()
    {
        var recent = new[]
        {
            Ready("r1", "Session tree on the web", Now.AddHours(-2), status: "answered",
                answeredAt: Now.AddHours(-1), answer: "Merge: Session tree on the web", answeredByRole: "fleet-manager"),
        };

        var dto = FleetManagerPageFold.Fold(Inputs(recent: recent));

        Assert.Equal("The Fleet Manager said \"Merge: Session tree on the web\" at 13:30", Assert.Single(dto.Landed.Items).Meta);
    }

    // ---- not the Fleet Manager's --------------------------------------------------------------------------

    [Fact]
    public void Fold_NotMine_CountsLiveSessionsWithNoLiveOwnerThatAreNotTheFleetManagerOrItsOwn()
    {
        var roster = new[]
        {
            Session(Marked, "Fleet Manager"),
            Session(OwnedA, "Owned", "Working", Marked, true),
            // The Fleet Manager's, whatever its liveness answer says: it is handed back, not counted.
            Session(OwnedB, "Owned, its owner stopped", "Idle", Marked, false),
            // The owner's own sessions: they ask the owner directly.
            Session(Loose, "Loose one"),
            Session("60000000-0000-4000-8000-000000000020", "Loose two", "Working"),
            // A session whose owner died goes red for the owner again.
            Session("60000000-0000-4000-8000-000000000021", "Orphan", "Idle", "60000000-0000-4000-8000-00000000dead", false),
            // Another session's worker reports to that session, not to the owner.
            Session(OtherCrew, "Crew member", "Working", OtherBoss, true),
            Session(OtherBoss, "Crew boss", "Working"),
            // Gone.
            Session("60000000-0000-4000-8000-000000000022", "Closed", "Exited"),
        };

        var dto = FleetManagerPageFold.Fold(Inputs(roster: roster));

        Assert.Equal(4, dto.NotMine.Count);
        Assert.Equal("4 sessions are not the Fleet Manager's.", dto.NotMine.Lead);
        Assert.Equal("They still ask you directly.", dto.NotMine.Rest);
    }

    [Fact]
    public void Fold_NotMine_OneSessionIsSingular()
    {
        var dto = FleetManagerPageFold.Fold(Inputs(roster: new[] { Session(Marked, "Fleet Manager"), Session(Loose, "Loose one") }));

        Assert.Equal(1, dto.NotMine.Count);
        Assert.Equal("1 session is not the Fleet Manager's.", dto.NotMine.Lead);
        Assert.Equal("It still asks you directly.", dto.NotMine.Rest);
    }

    [Fact]
    public void Fold_NotMine_WithNoFleetManagerMarked_CountsEveryUnownedLiveSession()
    {
        var roster = new[] { Session(Marked, "Was the Fleet Manager"), Session(Loose, "Loose one") };

        var dto = FleetManagerPageFold.Fold(Inputs(roster: roster, marked: null));

        Assert.Equal(2, dto.NotMine.Count);
    }

    [Fact]
    public void Fold_NotMine_ListIsTheCountedSessions_ThoseThatNeedYouFirst_EachOfferingTheHandOver()
    {
        var roster = new[]
        {
            Session(Marked, "Fleet Manager"),
            Session(OwnedA, "Owned", "Working", Marked, true),
            Session(Loose, "Oldest, working", "Working", created: Now.AddHours(-3), repo: "/src/widgets"),
            Session("60000000-0000-4000-8000-000000000030", "Newer, needs you", "WaitingForInput", created: Now.AddMinutes(-10)),
            Session(OtherCrew, "Crew member", "Working", OtherBoss, true),
            Session(OtherBoss, "Crew boss", "Idle", created: Now.AddHours(-1)),
        };
        roster[3].StateLabel = "Needs you";
        roster[5].OnHold = true;   // snoozed: a grey dot, and still the owner's

        var dto = FleetManagerPageFold.Fold(Inputs(roster: roster));

        Assert.Equal(3, dto.NotMine.Count);
        Assert.Equal(dto.NotMine.Count, dto.NotMine.Sessions.Count);
        Assert.Equal(new[] { "Newer, needs you", "Oldest, working", "Crew boss" }, dto.NotMine.Sessions.Select(i => i.Title));
        Assert.Equal(new[] { "red", "blue", "grey" }, dto.NotMine.Sessions.Select(i => i.Dot));
        Assert.True(dto.NotMine.Sessions[0].Attention);
        Assert.Equal("Needs you - started 10m", dto.NotMine.Sessions[0].Meta);
        Assert.Equal("widgets - started 3h", dto.NotMine.Sessions[1].Meta);
        Assert.All(dto.NotMine.Sessions, i =>
        {
            Assert.Equal(i.Id, i.SessionId);
            Assert.Equal("fleet-manager", i.Action!.To);
            Assert.Equal("Hand to the Fleet Manager", i.Action.Label);
        });
        Assert.Equal("Hand sessions to the Fleet Manager...", dto.NotMine.ShowLabel);
        Assert.Equal("Hide the list", dto.NotMine.HideLabel);
        Assert.Equal("Sessions that ask you directly", dto.NotMine.ListTitle);
        Assert.Equal("Hand a session over and the Fleet Manager owns it: when it stops, the Fleet Manager is told instead of you. "
                     + "You can hand it back from its menu in the session list.", dto.NotMine.ListNote);
    }

    [Fact]
    public void Fold_NotMine_WithNoRunningFleetManager_ListsTheSessionsWithNoActionAndSaysWhy()
    {
        var roster = new[] { Session(Marked, "Was the Fleet Manager", "Exited"), Session(Loose, "Loose one") };

        var dto = FleetManagerPageFold.Fold(Inputs(roster: roster));

        var item = Assert.Single(dto.NotMine.Sessions);
        Assert.Equal(Loose, item.SessionId);
        Assert.Null(item.Action);
        Assert.Equal("There is no running Fleet Manager, so no session can be handed over now. Start the Fleet Manager from Settings.",
            dto.NotMine.ListNote);
    }

    [Fact]
    public void Fold_NotMine_NothingToHandOver_OffersNoList()
    {
        var dto = FleetManagerPageFold.Fold(Inputs(roster: new[] { Session(Marked, "Fleet Manager"), Session(OwnedA, "Owned", "Working", Marked, true) }));

        Assert.Empty(dto.NotMine.Sessions);
        Assert.Null(dto.NotMine.ShowLabel);
    }

    // ---- cards --------------------------------------------------------------------------------------------

    [Fact]
    public void Fold_Cards_AreOldestFirstWithTheWhoLineInTheAccountsZone()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var recent = new[]
        {
            Decision("d1", "Later", new DateTime(2026, 9, 16, 14, 31, 0, DateTimeKind.Utc)),
            Ready("r1", "Earlier", new DateTime(2026, 9, 16, 14, 14, 0, DateTimeKind.Utc)),
            Finding("f1", "Yesterday", new DateTime(2026, 9, 15, 20, 0, 0, DateTimeKind.Utc)),
        };

        var dto = FleetManagerPageFold.Fold(Inputs(open: recent, tz: tz));

        Assert.Equal(new[] { "f1", "r1", "d1" }, dto.Cards.Select(c => c.Id));
        Assert.Equal("Fleet Manager - yesterday 16:00", dto.Cards[0].WhoLine);
        Assert.Equal("Fleet Manager - 10:14", dto.Cards[1].WhoLine);
        Assert.Equal(new DateTime(2026, 9, 16, 14, 14, 0, DateTimeKind.Utc), dto.Cards[1].FiledAtUtc);
    }

    [Fact]
    public void Fold_Cards_EveryOpenRecordIsACardWithItsButtons_AndOnlyTheAnsweredHistoryComesFromTheLimitedRead()
    {
        // Open records older than every answered one - the case the old 100-record window dropped.
        var open = Enumerable.Range(0, 150)
            .Select(i => Decision($"open-{i:D3}", $"Question {i}", Now.AddDays(-30).AddMinutes(i)))
            .ToList();
        var answered = Enumerable.Range(0, FleetManagerPageFold.CardCount)
            .Select(i => Decision($"done-{i:D3}", $"Settled {i}", Now.AddHours(-10).AddMinutes(i), status: "answered", answer: "Always run both."))
            .ToList();

        var dto = FleetManagerPageFold.Fold(Inputs(open: open, recent: answered));

        Assert.Equal(150 + FleetManagerPageFold.CardCount, dto.Cards.Count);
        var openCards = dto.Cards.Where(c => !c.Answered).ToList();
        Assert.Equal(open.Select(o => o.Id), openCards.Select(c => c.Id));
        Assert.All(openCards, c => Assert.Equal(new[] { "Replace it for ordinary changes.", "Always run both." }, c.Actions.Select(a => a.Words)));
        Assert.Equal(150, dto.WaitingCount);
    }

    [Fact]
    public void Fold_Cards_ARecordInBothReads_IsDrawnOnce_AsItNowStands()
    {
        var open = new[] { Decision("d1", "Keep it?", Now.AddHours(-1)) };
        var answered = new[] { Decision("d1", "Keep it?", Now.AddHours(-1), status: "answered", answer: "Always run both.") };

        var dto = FleetManagerPageFold.Fold(Inputs(open: open, recent: answered));

        var card = Assert.Single(dto.Cards);
        Assert.True(card.Answered);
    }

    public static IEnumerable<object?[]> Deliveries() => new[]
    {
        new object?[] { null, "Recorded without being passed to the Fleet Manager as an event. It sees the answer among the records answered in the last day." },
        new object?[] { new FleetManagerEventDto { Kind = "answered", OutcomeId = "r1" },
            "Waiting to reach the Fleet Manager: it is passed on the next time the Fleet Manager is free." },
        new object?[] { new FleetManagerEventDto { Kind = "answered", OutcomeId = "r1", DeliveredAtUtc = Now },
            "Passed to the Fleet Manager. It has not said it acted on it yet." },
        new object?[] { new FleetManagerEventDto { Kind = "answered", OutcomeId = "r1", DeliveredAtUtc = Now, AcknowledgedAtUtc = Now },
            "The Fleet Manager has acted on it." },
    };

    [Theory]
    [MemberData(nameof(Deliveries))]
    public void Fold_AnOwnersAnsweredCard_SaysHowFarTheAnswerHasGot(FleetManagerEventDto? told, string expected)
    {
        var answered = new[] { Ready("r1", "Flaky list test", Now.AddHours(-2), status: "answered", answeredAt: Now.AddHours(-1), answer: "Merge: Flaky list test") };
        var events = told is null ? null : new Dictionary<string, FleetManagerEventDto> { ["r1"] = told };

        var dto = FleetManagerPageFold.Fold(Inputs(recent: answered, answerEvents: events));

        Assert.Equal(expected, Assert.Single(dto.Cards).AnswerDelivery);
    }

    [Fact]
    public void Fold_ACardTheFleetManagerAnsweredItself_SaysNothingAboutPassingItOn()
    {
        var answered = new[] { Ready("r1", "Flaky list test", Now.AddHours(-2), status: "answered", answeredAt: Now.AddHours(-1),
            answer: "Merge: Flaky list test", answeredByRole: "fleet-manager") };

        var dto = FleetManagerPageFold.Fold(Inputs(recent: answered));

        Assert.Null(Assert.Single(dto.Cards).AnswerDelivery);
    }

    [Fact]
    public void Card_EveryButton_CarriesItsLabelsFromTheGateway()
    {
        var cards = new[] { Ready("r1", "A", Now), Finding("f1", "B", Now), Decision("d1", "C", Now) }
            .Select(r => FleetManagerPageFold.Card(r, TimeZoneInfo.Utc, Now)).ToList();

        Assert.All(cards, c => Assert.Equal("Your answer was not recorded, and nothing was passed to the Fleet Manager:", c.AnswerRefusedLead));
        Assert.All(cards.SelectMany(c => c.Actions), a => Assert.Equal("Recording...", a.BusyLabel));
        var sendBack = cards[0].Actions.Single(a => a.AsksForWords);
        Assert.Equal(("Send it back", "Cancel"), (sendBack.SendLabel, sendBack.CancelLabel));
    }

    [Fact]
    public void Fold_TheNewFleetManagerWaitingToTakeOver_IsNotASessionThatAsksTheOwner()
    {
        const string Successor = "50000000-0000-4000-8000-0000000000aa";
        var roster = new[]
        {
            Session(Marked, "Fleet Manager", "Working", null, false, Now.AddHours(-3)),
            Session(Successor, "Fleet Manager", "WaitingForInput", null, false, Now.AddMinutes(-1)),
            Session(Loose, "The owner's own", "WaitingForInput", null, false, Now.AddMinutes(-2)),
        };

        var dto = FleetManagerPageFold.Fold(Inputs(roster: roster, successor: Successor));

        Assert.Equal(new[] { Loose }, dto.NotMine.Sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void Card_Ready_CarriesTheRecordsFieldsVerbatimAndItsButtons()
    {
        var card = FleetManagerPageFold.Card(Ready("r1", "Fix the flaky list test", Now), TimeZoneInfo.Utc, Now);

        Assert.Equal("ready", card.Tone);
        Assert.Equal("Ready for you", card.KindLabel);
        Assert.Equal("Fix the flaky list test", card.Title);
        Assert.False(card.Answered);
        var ready = card.Ready!;
        Assert.Equal("Risk low", ready.RiskLabel);
        Assert.Equal("low", ready.RiskTone);
        Assert.Equal(new[] { ("Checks", "passed"), ("Tested", "3 of 3 live"), ("Reviewed by", "a second reviewer, 1 finding fixed") },
            ready.Facts.Select(f => (f.Label, f.Value)));
        Assert.Equal("The list now waits for the redraw itself.", ready.Change);
        Assert.Equal("Open pull request #2934", ready.PullRequest.Label);
        Assert.Equal("https://example.test/acme/widgets/pull/2934", ready.PullRequest.Url);
        Assert.Null(card.Finding);
        Assert.Null(card.Decision);

        Assert.Equal(2, card.Actions.Count);
        Assert.Equal("Merge", card.Actions[0].Label);
        Assert.Equal("primary", card.Actions[0].Style);
        Assert.Equal("Merge: Fix the flaky list test", card.Actions[0].Words);
        Assert.False(card.Actions[0].AsksForWords);
        Assert.Equal("Send it back...", card.Actions[1].Label);
        Assert.True(card.Actions[1].AsksForWords);
        Assert.Null(card.Actions[1].Words);
        Assert.Equal("Send it back: Fix the flaky list test. ", card.Actions[1].WordsPrefix);
        Assert.Equal("What should change?", card.Actions[1].Placeholder);
        Assert.Equal("Send it back", card.Actions[1].SendLabel);
    }

    [Fact]
    public void Card_Ready_APullRequestLinkWithNoNumberIsLabelledPlainly()
    {
        var record = Ready("r1", "A change", Now);
        record.Ready!.PullRequest = "https://example.test/review/abc";
        record.Ready.Tested = "";
        record.Ready.ReviewedBy = "";

        var card = FleetManagerPageFold.Card(record, TimeZoneInfo.Utc, Now);

        Assert.Equal("Open pull request", card.Ready!.PullRequest.Label);
        Assert.Equal(new[] { "Checks" }, card.Ready.Facts.Select(f => f.Label));
    }

    [Fact]
    public void Card_Finding_AnswerFirstThenReasonThenLinks()
    {
        var card = FleetManagerPageFold.Card(Finding("f1", "Reviews done", Now), TimeZoneInfo.Utc, Now);

        Assert.Equal("finding", card.Tone);
        Assert.Equal("Finding", card.KindLabel);
        Assert.Equal("Build our own.", card.Finding!.Answer);
        Assert.Equal("Both reviews agree.", card.Finding.Reason);
        Assert.Equal(new[] { ("Open tool-a.md", "https://example.test/reports/tool-a.md"), ("Open tool-b.md", "https://example.test/reports/tool-b.md") },
            card.Finding.Links.Select(l => (l.Label, l.Url)));
        var got = Assert.Single(card.Actions);
        Assert.Equal("Got it", got.Label);
        Assert.Equal("Got it: Reviews done", got.Words);
    }

    [Fact]
    public void Card_Decision_OneButtonPerOptionWithTheOptionAsItsWords_RecommendedMarked()
    {
        var card = FleetManagerPageFold.Card(Decision("d1", "When our check runs", Now), TimeZoneInfo.Utc, Now);

        Assert.Equal("decision", card.Tone);
        Assert.Equal("Decision - only you can make this", card.KindLabel);
        var d = card.Decision!;
        Assert.Equal("Replace the inspector, or add to it?", d.Question);
        Assert.Equal(new[] { ("Replace it for ordinary changes.", true), ("Always run both.", false) },
            d.Options.Select(o => (o.Text, o.Recommended)));
        Assert.Equal("Recommended", d.RecommendedLabel);
        Assert.Equal("Why: Running both doubled the time.", d.Why);
        Assert.Equal(new[] { ("Replace it for ordinary changes.", "primary", "Replace it for ordinary changes."), ("Always run both.", "secondary", "Always run both.") },
            card.Actions.Select(a => (a.Label, a.Style, a.Words!)));
    }

    [Fact]
    public void Card_Decision_AQuestionThatIsTheTitleIsNotRepeated_AndNoReasonMeansNoWhy()
    {
        var record = Decision("d1", "Replace the inspector, or add to it?", Now);
        record.Decision!.Why = null;
        record.Decision.Recommended = null;

        var card = FleetManagerPageFold.Card(record, TimeZoneInfo.Utc, Now);

        Assert.Null(card.Decision!.Question);
        Assert.Null(card.Decision.Why);
        Assert.All(card.Decision.Options, o => Assert.False(o.Recommended));
        Assert.All(card.Actions, a => Assert.Equal("secondary", a.Style));
    }

    [Fact]
    public void Card_Answered_ShowsTheAnswerVerbatimAndNoButtons()
    {
        var ready = Ready("r1", "A change", Now.AddHours(-1), status: "answered", answeredAt: Now.AddMinutes(-3), answer: "Merge: A change");
        var decision = Decision("d1", "A question", Now.AddHours(-1), status: "answered", answer: "something else entirely");
        var finding = Finding("f1", "A finding", Now.AddHours(-1), status: "answered");
        finding.Answer = "Got it: A finding";
        finding.AnsweredAtUtc = Now.AddMinutes(-1);

        foreach (var card in new[] { ready, decision, finding }.Select(r => FleetManagerPageFold.Card(r, TimeZoneInfo.Utc, Now)))
        {
            Assert.True(card.Answered);
            Assert.Empty(card.Actions);
        }
        var readyCard = FleetManagerPageFold.Card(ready, TimeZoneInfo.Utc, Now);
        Assert.Equal("Answered 14:27", readyCard.AnswerLabel);
        Assert.Equal("Merge: A change", readyCard.Answer);
        Assert.Equal("something else entirely", FleetManagerPageFold.Card(decision, TimeZoneInfo.Utc, Now).Answer);
    }

    [Fact]
    public void Card_OpenCard_HasNoAnswer()
    {
        var card = FleetManagerPageFold.Card(Ready("r1", "A change", Now), TimeZoneInfo.Utc, Now);

        Assert.Null(card.AnswerLabel);
        Assert.Null(card.Answer);
    }

    [Fact]
    public void Card_UnknownKind_Throws()
    {
        var record = new FleetOutcomeDto { Id = "x", Kind = "gossip", Title = "?", Status = "open", CreatedAtUtc = Now };

        Assert.Throws<InvalidOperationException>(() => FleetManagerPageFold.Card(record, TimeZoneInfo.Utc, Now));
    }

    // ---- ages ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "now")]
    [InlineData(59, "now")]
    [InlineData(60, "1m")]
    [InlineData(42 * 60, "42m")]
    [InlineData(2 * 3600, "2h")]
    [InlineData(4320, "1h 12m")]
    [InlineData(3 * 86400 + 5, "3d")]
    public void Age_WritesHowLongAgo(int secondsAgo, string expected)
        => Assert.Equal(expected, FleetManagerPageFold.Age(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void Age_NotARealStamp_IsNull()
        => Assert.Null(FleetManagerPageFold.Age(default, Now));
}
