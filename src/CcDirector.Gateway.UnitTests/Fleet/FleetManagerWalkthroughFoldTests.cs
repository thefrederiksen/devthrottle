using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Reports;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// "Take me through them" folded (the Fleet Manager mission, step 7). Pure: every input is handed in.
///
/// Sentences are asserted whole, as literals, because the Cockpit renders them verbatim.
/// </summary>
public sealed class FleetManagerWalkthroughFoldTests
{
    // 2026-09-16 14:30 UTC. The account's zone is UTC.
    internal static readonly DateTime Now = new(2026, 9, 16, 14, 30, 0, DateTimeKind.Utc);
    internal const string Marked = "80000000-0000-4000-8000-000000000001";
    private const string Layouts = "80000000-0000-4000-8000-000000000002";
    private const string Release = "80000000-0000-4000-8000-000000000003";
    private const string Quiet = "80000000-0000-4000-8000-000000000004";
    private const string Ended = "80000000-0000-4000-8000-000000000005";
    private const string Busy = "80000000-0000-4000-8000-000000000006";
    private const string Napping = "80000000-0000-4000-8000-000000000007";
    internal const string Director = "director-a";

    internal static SessionDto Session(string id, string name, string state = "WaitingForInput", string? controller = Marked,
        int? uncommitted = 0, string repo = "/work/widgets", DateTime? lastActivity = null, bool onHold = false,
        DateTime? snoozeUntil = null, bool handedOver = false) => new()
    {
        SessionId = id, Name = name, ActivityState = state, ControllerSessionId = controller,
        // An ordinary spawn: the owning session also started it. A hand over (step 8) changes only the owner.
        ParentSessionId = handedOver ? null : controller,
        IsControlled = controller is not null, HasLiveSupervisor = controller is not null,
        CreatedAt = Now.AddHours(-22), RepoPath = repo, DirectorId = Director, MachineName = "WORKSTATION-A",
        AgentToolDisplay = "Claude Code", UncommittedCount = uncommitted,
        LastActivityAt = lastActivity ?? Now.AddMinutes(-42), OnHold = onHold, SnoozeUntil = snoozeUntil,
    };

    private static SessionDto FleetManagerRow() => new()
    {
        SessionId = Marked, Name = "Fleet Manager", ActivityState = "WaitingForInput", CreatedAt = Now.AddDays(-1),
        DirectorId = Director,
    };

    internal static TurnVerdictDto Menu(string id = "verdict-layouts", string? agentRecommends = "It leans to A.") => new()
    {
        VerdictId = id,
        JudgedAtUtc = Now.AddMinutes(-41),
        TurnEndObservedAtUtc = Now.AddMinutes(-42),
        Verdict = "needed-you",
        Confidence = "high",
        Label = "Which fire-safety layout to keep",
        Summary = "Both layouts are built and running. It asks which one to keep; the other is deleted.",
        Evidence = "Which layout should I keep? The other one will be deleted.",
        AgentRecommends = agentRecommends,
        AnswerVia = "keys",
        Menu = new TurnVerdictMenuDto { Question = "Which layout should I keep?", SelectionMode = "single" },
        Options =
        {
            new TurnVerdictOptionDto { Key = "A - card grid", Send = "1", Recommended = true, Note = "B is deleted." },
            new TurnVerdictOptionDto { Key = "B - single column", Send = "2", Note = "A is deleted." },
        },
        Risk = "irreversible",
    };

    private static FleetOutcomeDto Record(string id, string kind, string title, DateTime created, string? session = null,
        string status = "open", string? answer = null, string? advice = null, string? pick = null) => new()
    {
        Id = id, Kind = kind, Title = title, Status = status, FiledBy = Marked, SessionId = session, CreatedAtUtc = created,
        Answer = answer, AnsweredAtUtc = answer is null ? null : Now.AddMinutes(-3),
        AnsweredBy = answer is null ? null : "owner", AnsweredByRole = answer is null ? null : "owner",
        Advice = advice, FleetManagerPick = pick,
        Ready = kind == "ready"
            ? new FleetReadyDetails
            {
                PullRequest = "https://example.test/acme/widgets/pull/12", Risk = "low", Checks = "passed",
                Tested = "Unit tests", ReviewedBy = "a second reviewer", Change = "The release is cut.",
            }
            : null,
        Finding = kind == "finding" ? new FleetFindingDetails { Answer = "Build our own." } : null,
        Decision = kind == "decision"
            ? new FleetDecisionDetails { Question = title, Options = { "Yes.", "No." }, Recommended = "Yes." }
            : null,
    };

    internal static StoredRepoState Repo(DateTime? collected = null, bool dirty = false, string? current = "main",
        bool? merged = true, int ahead = 0) => new()
    {
        DirectorId = Director, MachineName = "WORKSTATION-A", Name = "widgets", Path = "/work/widgets",
        DefaultBranch = "origin/main", CurrentBranch = current, IsDirty = dirty,
        CollectedAtUtc = collected ?? Now.AddMinutes(-5), ReceivedAtUtc = Now.AddMinutes(-5),
        Branches = current is null
            ? new List<RepoStateBranchDto>()
            : new List<RepoStateBranchDto> { new() { Name = current, MergedIntoDefault = merged, CommitsAheadOfDefault = ahead, CheckedOut = true } },
    };

    private sealed class World
    {
        public List<SessionDto> Live { get; } = new() { FleetManagerRow() };
        public Dictionary<string, SessionDto> Known { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<FleetOutcomeDto> Records { get; } = new();
        public Dictionary<string, TurnVerdictDto> Verdicts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<StoredRepoState> Repositories { get; } = new() { Repo() };
        public string? MarkedId { get; set; } = Marked;

        public FleetManagerWalkthroughInputs Inputs(IReadOnlyList<Guid>? round = null) => new(
            MarkedId,
            MarkedId is null ? null : Live.FirstOrDefault(s => s.SessionId == MarkedId) ?? Known.GetValueOrDefault(MarkedId),
            Live,
            sid => Live.FirstOrDefault(s => s.SessionId == sid) ?? Known.GetValueOrDefault(sid),
            Records.Where(r => r.Status == "open").ToList(),
            round,
            id => Records.FirstOrDefault(r => Guid.Parse(r.Id) == id),
            sid => Verdicts.GetValueOrDefault(sid),
            Repositories,
            60,
            TimeZoneInfo.Utc,
            Now);

        public FleetManagerWalkthroughDto Fold(IReadOnlyList<Guid>? round = null) => FleetManagerWalkthroughFold.Fold(Inputs(round));
    }

    private static string Id(int n) => $"90000000-0000-4000-8000-{n:D12}";

    // ---- order and counts ----------------------------------------------------------------------------------

    [Fact]
    public void Fold_Order_IsThePagesWaitingOrder_DecisionsThenReadyThenFindings_OldestFirst()
    {
        var w = new World();
        w.Records.Add(Record(Id(1), "finding", "Old finding", Now.AddHours(-5)));
        w.Records.Add(Record(Id(2), "ready", "New ready", Now.AddMinutes(-10)));
        w.Records.Add(Record(Id(3), "decision", "Newer decision", Now.AddMinutes(-20)));
        w.Records.Add(Record(Id(4), "ready", "Old ready", Now.AddHours(-2)));
        w.Records.Add(Record(Id(5), "decision", "Older decision", Now.AddHours(-1)));

        var dto = w.Fold();
        var page = FleetManagerPageFold.Fold(new FleetManagerPageInputs(Marked, FleetManagerRow(), w.Live,
            w.Records, w.Records, _ => null, TimeZoneInfo.Utc, Now));

        Assert.Equal(new[] { Id(5), Id(3), Id(4), Id(2), Id(1) }, dto.Items.Select(i => i.Id));
        Assert.Equal(page.Waiting.Items.Select(i => i.Id), dto.Items.Select(i => i.Id));
        Assert.Equal("Take me through them", page.WalkthroughLabel);
        Assert.Equal(dto.Items.Select(i => i.Id), dto.RoundIds);
        Assert.Equal("This round - 5", dto.RoundTitle);
        Assert.Equal(new[] { "1 of 5", "2 of 5", "3 of 5", "4 of 5", "5 of 5" }, dto.Items.Select(i => i.PositionLabel));
        Assert.Equal(5, dto.OpenCount);
        Assert.Equal("Take me through them", dto.Title);
        Assert.Equal("One at a time, most important first. The Wingman has already read each one; your answer goes straight to that session.", dto.Intro);
        Assert.Equal("Back to the conversation", dto.BackLabel);
        Assert.Null(dto.NotInRound);
        Assert.Equal(Marked, dto.FleetManagerSessionId);
    }

    [Fact]
    public void Fold_NothingWaiting_SaysSo()
    {
        var w = new World();
        var dto = w.Fold();
        var page = FleetManagerPageFold.Fold(new FleetManagerPageInputs(Marked, FleetManagerRow(), w.Live,
            w.Records, w.Records, _ => null, TimeZoneInfo.Utc, Now));

        Assert.Null(page.WalkthroughLabel);
        Assert.Empty(dto.Items);
        Assert.Equal("This round - 0", dto.RoundTitle);
        Assert.Equal("Nothing is waiting on you.", dto.EmptyText);
        Assert.Equal("That is the round.", dto.EndTitle);
        Assert.Equal("Everything in this round is settled. Nothing else is waiting on you.", dto.EndText);
        Assert.Null(dto.AgainLabel);
        Assert.Null(dto.NewRoundLabel);
    }

    [Fact]
    public void Fold_ASnoozedSessionsRecord_IsNotInANewRound_AndIsCountedOutside_WithWorkingSessions()
    {
        var w = new World();
        w.Live.Add(Session(Napping, "Napping session", onHold: true, snoozeUntil: Now.AddHours(1)));
        w.Live.Add(Session(Busy, "Busy session", state: "Working"));
        w.Live.Add(Session(Layouts, "Layouts session"));
        w.Records.Add(Record(Id(1), "decision", "Snoozed question", Now.AddHours(-1), Napping));
        w.Records.Add(Record(Id(2), "decision", "Live question", Now.AddMinutes(-5), Layouts));

        var dto = w.Fold();

        Assert.Equal(new[] { Id(2) }, dto.Items.Select(i => i.Id));
        Assert.Equal("Not in this round: 1 you snoozed and 1 of the Fleet Manager's sessions that is working.", dto.NotInRound);
    }

    [Fact]
    public void Fold_MoreThanARound_TheRestWaitForTheNextRound()
    {
        var w = new World();
        for (var i = 1; i <= FleetManagerWalkthroughFold.MaxRoundItems + 2; i++)
            w.Records.Add(Record(Id(i), "finding", $"Finding {i}", Now.AddMinutes(-100 + i)));

        var dto = w.Fold();

        Assert.Equal(FleetManagerWalkthroughFold.MaxRoundItems, dto.Items.Count);
        Assert.Equal("Not in this round: 2 more waiting, for the next round.", dto.NotInRound);
    }

    // ---- a round in progress -------------------------------------------------------------------------------

    [Fact]
    public void Fold_RoundInProgress_KeepsSettledItemsInPlace_Done_WithWhatWasDone_AndCountsANewRecordOutside()
    {
        var w = new World();
        w.Live.Add(Session(Layouts, "Layouts session"));
        w.Live.Add(Session(Napping, "Napping session", onHold: true, snoozeUntil: new DateTime(2026, 9, 16, 15, 30, 0, DateTimeKind.Utc)));
        w.Live.Add(Session(Release, "Release session"));
        w.Records.Add(Record(Id(1), "decision", "Build our own tool?", Now.AddHours(-3), status: "answered", answer: "Yes."));
        w.Records.Add(Record(Id(2), "decision", "Pick a layout", Now.AddHours(-2), Layouts));
        w.Records.Add(Record(Id(3), "decision", "Snoozed one", Now.AddHours(-1), Napping));
        w.Records.Add(Record(Id(4), "ready", "Release 2.0.7", Now.AddMinutes(-30), Release, status: "answered",
            answer: FleetManagerWalkthroughFold.CloseWords));
        var round = new[] { Id(4), Id(1), Id(2), Id(3) }.Select(Guid.Parse).ToList();
        // A record filed after the round began.
        w.Records.Add(Record(Id(5), "decision", "Arrived later", Now.AddMinutes(-1)));

        var dto = w.Fold(round);

        Assert.Equal(new[] { Id(1), Id(2), Id(3), Id(4) }, dto.Items.Select(i => i.Id));
        Assert.Equal(new[] { true, false, true, true }, dto.Items.Select(i => i.Done));
        Assert.Equal("You said \"Yes.\" - 14:27", dto.Items[0].StepLine);
        Assert.Equal("Layouts session - waiting 2h", dto.Items[1].StepLine);
        Assert.Equal("Snoozed until 15:30", dto.Items[2].StepLine);
        Assert.Equal("You closed the session - 14:27", dto.Items[3].StepLine);
        Assert.All(dto.Items.Where(i => i.Done), i => Assert.Equal("none", i.AnswerMode));
        Assert.All(dto.Items.Where(i => i.Done), i => Assert.False(i.Close.Offered));
        Assert.All(dto.Items.Where(i => i.Done), i => Assert.False(i.Snooze.Offered));
        Assert.Equal(1, dto.OpenCount);
        Assert.Equal("Not in this round: 1 more waiting, for the next round.", dto.NotInRound);
        Assert.Equal("End of the round", dto.EndTitle);
        Assert.Equal("1 item in this round is still waiting - the one you skipped.", dto.EndText);
        Assert.Equal("Go through the skipped ones again", dto.AgainLabel);
        Assert.Equal("Start the next round", dto.NewRoundLabel);
    }

    [Fact]
    public void Fold_RoundAllSettled_SaysTheRoundIsDone_AndHowManyMoreWait()
    {
        var w = new World();
        w.Records.Add(Record(Id(1), "finding", "Done one", Now.AddHours(-3), status: "answered", answer: "Got it."));
        w.Records.Add(Record(Id(2), "finding", "New one", Now.AddMinutes(-1)));
        w.Records.Add(Record(Id(3), "finding", "Another new one", Now.AddMinutes(-1)));

        var dto = w.Fold(new[] { Guid.Parse(Id(1)) });

        Assert.Equal(0, dto.OpenCount);
        Assert.Equal("That is the round.", dto.EndTitle);
        Assert.Equal("Everything in this round is settled. 2 more are waiting.", dto.EndText);
        Assert.Null(dto.AgainLabel);
        Assert.Equal("Start the next round", dto.NewRoundLabel);
    }

    [Fact]
    public void Fold_SessionHandedToTheFleetManager_IsNotSaidToBeStartedByIt_ButCountsAsItsWorkingSession()
    {
        // Step 8: the owner started these and handed them over, so the Fleet Manager owns them without having
        // started them. The line must not claim it did; the working one still counts as one of its sessions.
        var w = new World();
        w.Live.Add(Session(Layouts, "Layouts session", handedOver: true));
        w.Live.Add(Session(Busy, "Busy session", state: "Working", handedOver: true));
        w.Live.Add(Session(Napping, "Owner's own", controller: null));
        w.Records.Add(Record(Id(1), "decision", "Pick a layout", Now.AddMinutes(-42), Layouts));
        w.Records.Add(Record(Id(2), "decision", "Anything else", Now.AddMinutes(-40), Napping));

        var dto = w.Fold();

        Assert.Equal("widgets - WORKSTATION-A - Claude Code - handed to the Fleet Manager - started yesterday 16:30",
            dto.Items[0].Meta);
        Assert.Equal("widgets - WORKSTATION-A - Claude Code - started yesterday 16:30", dto.Items[1].Meta);
        Assert.Equal("Not in this round: 1 of the Fleet Manager's sessions that is working.", dto.NotInRound);
    }

    [Fact]
    public void Fold_RoundNamingAnUnknownRecord_LeavesItOut()
    {
        var w = new World();
        w.Records.Add(Record(Id(1), "finding", "Known", Now.AddHours(-3)));

        var dto = w.Fold(new[] { Guid.Parse(Id(1)), Guid.Parse(Id(99)) });

        Assert.Equal(new[] { Id(1) }, dto.RoundIds);
    }

    // ---- the reading ---------------------------------------------------------------------------------------

    [Fact]
    public void Fold_CurrentReading_IsCopiedVerbatim_WithTheMenuAndBothPicksMarked()
    {
        var w = new World();
        w.Live.Add(Session(Layouts, "Layouts session"));
        w.Verdicts[Layouts] = Menu();
        w.Records.Add(Record(Id(1), "decision", "Pick a layout", Now.AddMinutes(-42), Layouts,
            advice: "You picked the long column for the last two client pages. I'd pick B.", pick: "B - single column"));

        var item = Assert.Single(w.Fold().Items);

        Assert.Equal("What it needs from you - read by the Wingman", item.Reading.Heading);
        Assert.True(item.Reading.Available);
        Assert.Null(item.Reading.Note);
        Assert.Equal("Which fire-safety layout to keep", item.Reading.Label);
        Assert.Equal("Both layouts are built and running. It asks which one to keep; the other is deleted.", item.Reading.Summary);
        Assert.Equal("In its own words:", item.Reading.EvidenceLead);
        Assert.Equal("Which layout should I keep? The other one will be deleted.", item.Reading.Evidence);
        Assert.Equal("It recommends:", item.Reading.AgentRecommendsLead);
        Assert.Equal("It leans to A.", item.Reading.AgentRecommends);
        Assert.Equal("Risk: irreversible", item.Reading.RiskLine);

        Assert.Equal("The Fleet Manager says", item.Advice.Heading);
        Assert.Equal("You picked the long column for the last two client pages. I'd pick B.", item.Advice.Text);
        Assert.Null(item.Advice.EmptyText);

        Assert.Equal("session", item.AnswerMode);
        Assert.Null(item.Card);
        var answer = item.Answer!;
        Assert.Equal("verdict-layouts", answer.VerdictId);
        Assert.Equal("Which layout should I keep?", answer.Question);
        Assert.False(answer.Multiple);
        Assert.False(answer.ParkedReply);
        Assert.Null(answer.PickNote);
        Assert.Equal(new[] { 0, 1 }, answer.Options.Select(o => o.Index));
        Assert.Equal(new[] { "A - card grid", "B - single column" }, answer.Options.Select(o => o.Label));
        Assert.Equal(new[] { true, false }, answer.Options.Select(o => o.SessionPick));
        Assert.Equal(new[] { false, true }, answer.Options.Select(o => o.FleetManagerPick));
        Assert.Equal(new[] { "its pick", "Fleet Manager's pick" }, answer.Options.Select(o => o.MarkText));
        Assert.Equal("B is deleted.", answer.Options[0].Note);

        Assert.Equal("Layouts session", item.SessionName);
        Assert.Equal("widgets - WORKSTATION-A - Claude Code - started by the Fleet Manager yesterday 16:30", item.Meta);
        Assert.Equal("waiting 42m", item.WaitLabel);
        Assert.True(item.Screen.Offered);
        Assert.Equal(FleetManagerWalkthroughFold.ScreenLines, item.Screen.Lines);
        Assert.Equal("The screen", item.Screen.Heading);
        Assert.True(item.Snooze.Offered);
        Assert.Equal("Snooze 1 hour", item.Snooze.Label);
        Assert.Equal(60, item.Snooze.Minutes);
        Assert.Equal("Skip for now", item.SkipLabel);
        Assert.True(item.Open.Offered);
        Assert.Equal("Open the session", item.Open.Label);
    }

    [Fact]
    public void Fold_BothPicksOnTheSameOption_SaySo()
    {
        var w = new World();
        w.Live.Add(Session(Layouts, "Layouts session"));
        w.Verdicts[Layouts] = Menu();
        w.Records.Add(Record(Id(1), "decision", "Pick a layout", Now.AddMinutes(-42), Layouts, advice: "Agree with it.",
            pick: "  A - card grid "));

        var options = Assert.Single(w.Fold().Items).Answer!.Options;

        Assert.Equal("its pick and the Fleet Manager's pick", options[0].MarkText);
        Assert.Null(options[1].MarkText);
    }

    [Fact]
    public void Fold_APickNoLongerOnTheScreen_MarksNothing_AndSaysSo()
    {
        var w = new World();
        w.Live.Add(Session(Layouts, "Layouts session"));
        w.Verdicts[Layouts] = Menu();
        w.Records.Add(Record(Id(1), "decision", "Pick a layout", Now.AddMinutes(-42), Layouts, advice: "Pick C.", pick: "C - carousel"));

        var answer = Assert.Single(w.Fold().Items).Answer!;

        Assert.All(answer.Options, o => Assert.False(o.FleetManagerPick));
        Assert.Equal("The Fleet Manager picked \"C - carousel\", which is not one of the options on the screen now.", answer.PickNote);
    }

    [Fact]
    public void Fold_NoAdvice_SaysTheFleetManagerWroteNone()
    {
        var w = new World();
        w.Records.Add(Record(Id(1), "finding", "A finding", Now.AddMinutes(-2)));

        var advice = Assert.Single(w.Fold().Items).Advice;

        Assert.Null(advice.Text);
        Assert.Equal("The Fleet Manager wrote no advice for this one.", advice.EmptyText);
    }

    [Fact]
    public void Fold_AMultipleSelectMenu_AndAParkedReply_AreSaidByTheGateway()
    {
        var w = new World();
        w.Live.Add(Session(Layouts, "Layouts session"));
        w.Live.Add(Session(Release, "Release session"));
        var multiple = Menu();
        multiple.Menu!.SelectionMode = "multiple";
        w.Verdicts[Layouts] = multiple;
        var parked = Menu("verdict-parked");
        parked.Options.Clear();
        w.Verdicts[Release] = parked;
        w.Records.Add(Record(Id(1), "decision", "Pick layouts", Now.AddMinutes(-9), Layouts));
        w.Records.Add(Record(Id(2), "decision", "Confirm the reply", Now.AddMinutes(-8), Release));

        var items = w.Fold().Items;

        Assert.True(items[0].Answer!.Multiple);
        Assert.Equal("Send the chosen options", items[0].Answer!.SendChosenLabel);
        Assert.Equal("session", items[1].AnswerMode);
        Assert.True(items[1].Answer!.ParkedReply);
        Assert.Empty(items[1].Answer!.Options);
        Assert.Equal("Send the typed reply", items[1].Answer!.ParkedReplyLabel);
    }

    [Fact]
    public void Fold_ARecordAboutNoSession_IsAnsweredThroughItsCard()
    {
        var w = new World();
        w.Records.Add(Record(Id(1), "ready", "Release 2.0.7", Now.AddMinutes(-2)));

        var item = Assert.Single(w.Fold().Items);

        Assert.False(item.Reading.Available);
        Assert.Equal("This item is not about one session, so the Wingman has nothing to read. The Fleet Manager's record is below.",
            item.Reading.Note);
        Assert.Equal("fleet-manager", item.AnswerMode);
        Assert.Null(item.Answer);
        Assert.Equal(new[] { "Merge: Release 2.0.7" }, item.Card!.Actions.Where(a => a.Words is not null).Select(a => a.Words));
        Assert.Equal("Ready for you - not about one session", item.Meta);
        Assert.Equal("Ready - risk low", item.StepLine);
        Assert.False(item.Screen.Offered);
        Assert.Null(item.Screen.Note);
        Assert.False(item.Snooze.Offered);
        Assert.Null(item.Snooze.Note);
        Assert.False(item.Open.Offered);
        Assert.False(item.Close.Offered);
        Assert.Null(item.Close.RefusedText);
    }

    [Fact]
    public void Fold_NoCurrentReading_SaysSo_AndAnswersThroughTheCard()
    {
        var w = new World();
        w.Live.Add(Session(Layouts, "Layouts session"));
        w.Live.Add(Session(Busy, "Busy session", state: "Working"));
        w.Records.Add(Record(Id(1), "decision", "Unread question", Now.AddMinutes(-3), Layouts));
        w.Records.Add(Record(Id(2), "decision", "Busy question", Now.AddMinutes(-2), Busy));

        var items = w.Fold().Items;

        Assert.Equal("The Wingman has no reading of this session's current stop. It reads each stop when the session finishes a turn.",
            items[0].Reading.Note);
        Assert.Equal("This session is working, so there is no stop for the Wingman to read yet.", items[1].Reading.Note);
        Assert.All(items, i => Assert.False(i.Reading.Available));
        Assert.All(items, i => Assert.Equal("fleet-manager", i.AnswerMode));
        Assert.All(items, i => Assert.Equal(new[] { "Yes.", "No." }, i.Card!.Actions.Select(a => a.Words)));
        Assert.All(items, i => Assert.True(i.Screen.Offered));
    }

    [Fact]
    public void Fold_ARefusedReading_SaysWhy()
    {
        var w = new World();
        w.Live.Add(Session(Layouts, "Layouts session"));
        w.Verdicts[Layouts] = new TurnVerdictDto { VerdictId = "v-failed", Failed = true, FailureReason = "the judge timed out" };
        w.Records.Add(Record(Id(1), "decision", "Question", Now.AddMinutes(-3), Layouts));

        var item = Assert.Single(w.Fold().Items);

        Assert.False(item.Reading.Available);
        Assert.Equal("The Wingman could not read this stop: the judge timed out", item.Reading.Note);
        Assert.Equal("fleet-manager", item.AnswerMode);
    }

    [Fact]
    public void Fold_AStaleReading_IsShownWithItsAge_ButNotAnsweredThroughTheSession()
    {
        var w = new World();
        // Known, but its computer has not pushed recently: not in the live roster.
        w.Known[Quiet] = Session(Quiet, "Quiet session");
        w.Verdicts[Quiet] = Menu("verdict-quiet");
        w.Records.Add(Record(Id(1), "decision", "Quiet question", Now.AddMinutes(-50), Quiet, advice: "Wait for it.", pick: "A - card grid"));

        var item = Assert.Single(w.Fold().Items);

        Assert.True(item.Reading.Available);
        Assert.Equal("Which fire-safety layout to keep", item.Reading.Label);
        Assert.Equal("This reading is from 13:49. The session's computer has not reported since, so its screen may have changed. "
                     + "Answer it once the computer is back.", item.Reading.Note);
        Assert.Equal("fleet-manager", item.AnswerMode);
        Assert.Null(item.Answer);
        Assert.False(item.Screen.Offered);
        Assert.Equal("The session's computer is not reporting, so its screen cannot be read.", item.Screen.Note);
        Assert.False(item.Snooze.Offered);
        Assert.Equal("The session's computer is not reporting, so it cannot be snoozed now.", item.Snooze.Note);
        Assert.False(item.Close.Offered);
        Assert.Equal("Close is not offered: this session is not running, so there is nothing to close.", item.Close.RefusedText);
        Assert.True(item.Open.Offered);
    }

    [Fact]
    public void Fold_AnEndedSession_SaysSo_AndOffersNeitherSnoozeNorClose()
    {
        var w = new World();
        w.Live.Add(Session(Ended, "Ended session", state: "Exited"));
        w.Verdicts[Ended] = Menu("verdict-ended");
        w.Records.Add(Record(Id(1), "decision", "Ended question", Now.AddMinutes(-3), Ended));

        var item = Assert.Single(w.Fold().Items);

        Assert.False(item.Reading.Available);
        Assert.Equal("This session has ended, so there is nothing on its screen to answer.", item.Reading.Note);
        Assert.Equal("The session has ended, so there is no screen to show.", item.Screen.Note);
        Assert.Equal("The session has ended, so it cannot be snoozed.", item.Snooze.Note);
        Assert.False(item.Close.Offered);
        Assert.Equal("fleet-manager", item.AnswerMode);
    }

    [Fact]
    public void Fold_ASessionNoComputerReported_SaysSo()
    {
        var w = new World();
        w.Records.Add(Record(Id(1), "decision", "Unknown session", Now.AddMinutes(-3), Quiet));

        var item = Assert.Single(w.Fold().Items);

        Assert.Equal("No computer on this account has reported this session, so there is no reading of it.", item.Reading.Note);
        Assert.False(item.Open.Offered);
        Assert.Equal("Decision - not about one session", item.Meta);
    }

    // ---- close ---------------------------------------------------------------------------------------------

    [Fact]
    public void Fold_Close_AllowedWhenTheWorkHasLanded_WithTheConfirmationWords()
    {
        var w = new World();
        w.Live.Add(Session(Release, "Release session"));
        w.Records.Add(Record(Id(1), "ready", "Release 2.0.7", Now.AddMinutes(-3), Release));

        var close = Assert.Single(w.Fold().Items).Close;

        Assert.True(close.Offered);
        Assert.Null(close.RefusedText);
        Assert.Equal("Close the session...", close.Label);
        Assert.Equal("Close Release session?", close.ConfirmTitle);
        Assert.Equal("Its branch main is fully in origin/main and its working copy was clean when it was inspected at 14:25, "
                     + "after its last activity. Closing stops Release session; it cannot be undone. "
                     + "Your answer is recorded for the Fleet Manager as \"Close the session.\"", close.ConfirmMessage);
        Assert.Equal("Close it", close.ConfirmLabel);
        Assert.Equal("Closing...", close.BusyLabel);
    }

    [Fact]
    public void Fold_Close_RefusedWithUncommittedWork_AndWhenTheGatewayCannotTell()
    {
        var w = new World();
        w.Live.Add(Session(Layouts, "Dirty session", uncommitted: 3));
        w.Live.Add(Session(Release, "Unknown session", uncommitted: null));
        w.Records.Add(Record(Id(1), "decision", "Dirty", Now.AddMinutes(-3), Layouts));
        w.Records.Add(Record(Id(2), "decision", "Unknown", Now.AddMinutes(-2), Release));

        var items = w.Fold().Items;

        Assert.All(items, i => Assert.False(i.Close.Offered));
        Assert.Equal("Close is not offered: this session has 3 uncommitted files.", items[0].Close.RefusedText);
        Assert.Equal("Close is not offered: this session's computer has not said whether its working copy has uncommitted "
                     + "changes, so the Gateway cannot tell whether work would be lost.", items[1].Close.RefusedText);
    }

    [Fact]
    public void Fold_Close_NeverForTheFleetManagerItself()
    {
        var w = new World();
        w.Live[0] = new SessionDto
        {
            SessionId = Marked, Name = "Fleet Manager", ActivityState = "WaitingForInput", CreatedAt = Now.AddDays(-1),
            DirectorId = Director, RepoPath = "/work/widgets", UncommittedCount = 0, LastActivityAt = Now.AddMinutes(-30),
        };
        w.Records.Add(Record(Id(1), "finding", "About the Fleet Manager", Now.AddMinutes(-3), Marked));

        var item = Assert.Single(w.Fold().Items);

        Assert.False(item.Close.Offered);
        Assert.Equal("Close is not offered: this is the Fleet Manager itself. Restart or move it in Settings.", item.Close.RefusedText);
        Assert.False(item.Snooze.Offered);
        Assert.Equal("The Fleet Manager itself is not snoozed from here.", item.Snooze.Note);
    }

    [Theory]
    [InlineData(1, "1 minute")]
    [InlineData(59, "59 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(90, "1 hour 30 minutes")]
    [InlineData(240, "4 hours")]
    [InlineData(1440, "1 day")]
    [InlineData(1450, "1 day 10 minutes")]
    [InlineData(1470, "1 day 1 hour")]
    [InlineData(2 * 1440 + 180, "2 days 3 hours")]
    public void FormatLength_WritesTheLengthAsTheSnoozeMenuDoes(int minutes, string expected)
        => Assert.Equal(expected, FleetManagerWalkthroughFold.FormatLength(minutes));

    // ---- the pick check ------------------------------------------------------------------------------------

    [Fact]
    public void CheckPick_EveryRefusal_SaysWhy()
    {
        var noOptions = Menu();
        noOptions.Options.Clear();

        Assert.StartsWith("a pick names one of the Wingman's options for the record's session, and this record is about no session",
            Assert.Throws<ArgumentException>(() => FleetManagerWalkthroughFold.CheckPick("A", null, null)).Message);
        Assert.Equal($"session {Layouts} has no current Wingman reading, so there are no options to pick from; leave the pick out, "
                     + "or set it once the Wingman has read the stop",
            Assert.Throws<ArgumentException>(() => FleetManagerWalkthroughFold.CheckPick("A", Layouts, null)).Message);
        Assert.Equal($"the Wingman's current reading of session {Layouts} offers no options, so there is nothing to pick; leave the pick out",
            Assert.Throws<ArgumentException>(() => FleetManagerWalkthroughFold.CheckPick("A", Layouts, noOptions)).Message);
        FleetManagerWalkthroughFold.CheckPick("B - single column", Layouts, Menu());
        FleetManagerWalkthroughFold.CheckPick(null, null, null);
    }
}
