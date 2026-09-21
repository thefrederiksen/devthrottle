using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Factory;

/// <summary>
/// The Factory Agents fold (Website Business Factory, product track): the rules the Cockpit's screens show, proven
/// by calling the pure fold directly. The Cockpit only renders what this returns (critical rule 7).
/// </summary>
public sealed class FactoryAgentsFoldTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private const string Factory = "website-business";

    private static FactoryActivityDto Row(string agent, string outcome, string what, DateTime at,
        string? session = null, Guid? corrects = null, string factory = Factory, string actor = "session-x",
        string? version = null) => new()
    {
        Id = Guid.NewGuid(),
        Factory = factory,
        FactoryAgent = agent,
        FactoryAgentVersion = version,
        SessionId = session,
        What = what,
        Outcome = outcome,
        Actor = actor,
        CorrectsId = corrects,
        OccurredUtc = at,
        RecordedUtc = at,
    };

    private static FactoryTriggerFacts Trigger(string agent = "front-desk", bool paused = false, bool red = false,
        string? status = null, string factory = Factory, string id = "t1") =>
        new(id, "New business mail", factory, agent, 300, paused, red, status, Now.AddMinutes(-5), "nothing to do");

    private static FactoryFoldInputs Inputs(IReadOnlyList<FactoryActivityDto> rows,
        IReadOnlyList<FactoryTriggerFacts>? triggers = null, IReadOnlySet<string>? live = null,
        IReadOnlyList<FactoryActivityDto>? waiting = null, IReadOnlyList<FactoryActivityDto>? corrections = null,
        string windowKey = FactoryAgentsFold.WindowLast24h) =>
        new(rows, false,
            waiting ?? rows.Where(r => r.Outcome is "asked" or "escalated").ToList(),
            corrections ?? rows.Where(r => r.CorrectsId is not null).ToList(),
            triggers ?? Array.Empty<FactoryTriggerFacts>(),
            live ?? new HashSet<string>(),
            FactoryAgentsFold.ResolveWindow(windowKey, null, null, Now, windowKey),
            Utc, Now);

    private static List<FactoryActivityDto> EmptyChecks(int count, DateTime start, string agent = "front-desk") =>
        Enumerable.Range(0, count)
            .Select(i => Row(agent, "nothing-to-do", "Checked for new business mail - nothing to do", start.AddMinutes(5 * i), actor: "trigger:t1"))
            .ToList();

    [Fact]
    public void Collapse_RunOfEmptyChecks_IsOneGreyLineWithTheCount()
    {
        var rows = new List<FactoryActivityDto> { Row("site-keeper", "started", "Started by schedule \"Daily upkeep\"", Now.AddHours(-6), "s131") };
        rows.AddRange(EmptyChecks(25, Now.AddHours(-5)));
        rows.Add(Row("front-desk", "started", "1 reply - started Front Desk", Now.AddHours(-3), "s132"));

        var view = FactoryAgentsFold.Activity(Inputs(rows), FactoryFilter.None, "/csv");

        Assert.Equal(3, view.Rows.Count);
        var line = view.Rows[1];
        Assert.True(line.Collapsed);
        Assert.Equal("Checked for new business mail 25 times - nothing to do", line.What);
        Assert.Equal("03:00-05:00", line.Time);
        Assert.Equal(FactoryTone.Grey, line.OutcomeTone);
        Assert.Null(line.SessionId);
    }

    [Fact]
    public void Collapse_EmptyChecksSplitByARealRow_AreTwoLines()
    {
        var rows = EmptyChecks(3, Now.AddHours(-5));
        rows.Add(Row("front-desk", "escalated", "Money question", Now.AddHours(-4), "s134"));
        rows.AddRange(EmptyChecks(17, Now.AddHours(-3)));

        var view = FactoryAgentsFold.Activity(Inputs(rows), FactoryFilter.None, "/csv");

        Assert.Equal(new[] { true, false, true }, view.Rows.Select(r => r.Collapsed));
        Assert.Equal("Checked for new business mail 3 times - nothing to do", view.Rows[0].What);
        Assert.Equal("Checked for new business mail 17 times - nothing to do", view.Rows[2].What);
    }

    [Fact]
    public void Collapse_OneEmptyCheck_IsShownAsItsOwnRow()
    {
        var view = FactoryAgentsFold.Activity(Inputs(EmptyChecks(1, Now.AddHours(-1))), FactoryFilter.None, "/csv");

        var line = Assert.Single(view.Rows);
        Assert.False(line.Collapsed);
        Assert.Equal("Checked for new business mail - nothing to do", line.What);
    }

    [Fact]
    public void Factories_TriggersExistAndNoRowsAtAll_IsTheRedFaultNoChecksRan()
    {
        var view = FactoryAgentsFold.Factories(Inputs(Array.Empty<FactoryActivityDto>(), new[] { Trigger() }));

        var card = Assert.Single(view.Factories);
        Assert.Equal("FAULT", card.StatusWord);
        Assert.Equal(FactoryTone.Red, card.StatusTone);
        Assert.StartsWith("No checks ran in the last 24 hours.", card.FaultText);
        Assert.Contains("not a quiet night", card.FaultText);
        var agent = Assert.Single(card.Agents);
        Assert.Equal("FAULT", agent.StatusWord);
    }

    [Fact]
    public void Activity_TriggersExistAndNoRows_CarriesTheNoChecksFault_ButAnEmptyOutcomeFilterDoesNot()
    {
        var none = FactoryAgentsFold.Activity(Inputs(Array.Empty<FactoryActivityDto>(), new[] { Trigger() }), FactoryFilter.None, "/csv");
        var fault = Assert.Single(none.Faults);
        Assert.StartsWith("Website Business: No checks ran", fault);

        // A quiet night that DID check, filtered to an outcome that did not happen, is not a fault.
        var quiet = FactoryAgentsFold.Activity(Inputs(EmptyChecks(10, Now.AddHours(-2)), new[] { Trigger() }),
            new FactoryFilter(null, null, "blocked"), "/csv");
        Assert.Empty(quiet.Faults);
        Assert.Empty(quiet.Rows);
        Assert.NotNull(quiet.EmptyText);
    }

    [Fact]
    public void Factories_AQuietNightThatChecked_IsRunningAndCountsTheEmptyChecks()
    {
        var view = FactoryAgentsFold.Factories(Inputs(EmptyChecks(188, Now.AddHours(-20)), new[] { Trigger() }));

        var card = Assert.Single(view.Factories);
        Assert.Equal("RUNNING", card.StatusWord);
        Assert.Null(card.FaultText);
        var n = Assert.Single(card.Numbers);
        Assert.Equal("188 empty checks", n.Text);
        Assert.Equal(FactoryTone.Grey, n.Tone);
    }

    [Fact]
    public void Factories_ATriggerTheStoreCallsRed_MakesTheFactoryAndItsAgentRed()
    {
        var view = FactoryAgentsFold.Factories(Inputs(EmptyChecks(2, Now.AddHours(-1)),
            new[] { Trigger(red: true, status: "check failed: exit code 1") }));

        var card = Assert.Single(view.Factories);
        Assert.Equal(FactoryTone.Red, card.StatusTone);
        Assert.Contains("Trigger \"New business mail\": check failed: exit code 1.", card.FaultText);
        Assert.Equal("FAULT", card.Agents[0].StatusWord);
    }

    [Fact]
    public void Activity_BlockedRow_IsShownRedWithItsSession()
    {
        var blocked = Row("scout", "blocked", "Tried to draft to an address on the remove-me list", Now.AddHours(-1), "abcdef1234567890", version: "2");

        var view = FactoryAgentsFold.Activity(Inputs(new[] { blocked }), FactoryFilter.None, "/csv");

        var line = Assert.Single(view.Rows);
        Assert.Equal("BLOCKED", line.OutcomeWord);
        Assert.Equal(FactoryTone.Red, line.OutcomeTone);
        Assert.Equal("Scout v2", line.Who);
        Assert.Equal("#abcdef12", line.SessionLabel);
        Assert.Equal("07:00", line.Time);
    }

    [Fact]
    public void Factories_BlockedRow_IsCountedOnTheCard()
    {
        var view = FactoryAgentsFold.Factories(Inputs(new[] { Row("scout", "blocked", "Blocked", Now.AddHours(-1)) }));

        Assert.Contains(view.Factories[0].Numbers, n => n.Text == "1 blocked" && n.Outcome == "blocked");
    }

    [Fact]
    public void Waiting_AnEscalationClearedByACorrectingRow_LeavesTheList()
    {
        var escalation = Row("front-desk", "escalated", "TN Tree & Crane asked what it costs", Now.AddHours(-3), "s134");
        var asked = Row("front-desk", "asked", "Drafted \"Your new hours are live\"", Now.AddHours(-4), "s132");

        var before = FactoryAgentsFold.Waiting(Inputs(new[] { escalation, asked }), null);
        Assert.Equal(2, before.Items.Count);
        Assert.Equal("ESCALATED", before.Items[0].Word);
        Assert.Equal("I have handled it", before.Items[0].HandledLabel);
        Assert.Null(before.Items[1].HandledLabel);

        var handled = FactoryAgentsFold.HandledRow(escalation, Array.Empty<FactoryActivityDto>(), "soren@example.com", Now);
        Assert.Equal(escalation.Id, handled.CorrectsId);
        Assert.Equal("done", handled.Outcome);
        Assert.StartsWith("Marked handled by soren@example.com:", handled.What);

        var correction = Row("front-desk", "done", handled.What!, Now, corrects: escalation.Id);
        var after = FactoryAgentsFold.Waiting(Inputs(new[] { escalation, asked, correction }), null);
        var left = Assert.Single(after.Items);
        Assert.Equal(asked.Id, left.Id);

        // The escalation row itself is unchanged, and Activity says what corrected it.
        var activity = FactoryAgentsFold.Activity(Inputs(new[] { escalation, asked, correction }), FactoryFilter.None, "/csv");
        var escLine = activity.Rows.Single(r => r.Key == escalation.Id.ToString());
        Assert.Equal("ESCALATED", escLine.OutcomeWord);
        Assert.StartsWith("Corrected ", escLine.Note);
        var fixLine = activity.Rows.Single(r => r.Key == correction.Id.ToString());
        Assert.StartsWith("Corrects the row of ", fixLine.Note);
    }

    [Fact]
    public void HandledRow_OnAnAskedRowOrAnAlreadyHandledEscalation_IsRefused()
    {
        var asked = Row("front-desk", "asked", "Draft", Now);
        Assert.Throws<FactoryViewValidationException>(() =>
            FactoryAgentsFold.HandledRow(asked, Array.Empty<FactoryActivityDto>(), "me", Now));

        var escalation = Row("front-desk", "escalated", "Money", Now);
        var fix = Row("front-desk", "done", "handled", Now, corrects: escalation.Id);
        Assert.Throws<FactoryViewValidationException>(() =>
            FactoryAgentsFold.HandledRow(escalation, new[] { fix }, "me", Now));
    }

    [Fact]
    public void Factories_AllTriggersPaused_ShowsPausedAndOffersResume()
    {
        var rows = new List<FactoryActivityDto> { Row("front-desk", "paused", "Checked - paused, not started", Now.AddMinutes(-5)) };
        var view = FactoryAgentsFold.Factories(Inputs(rows, new[] { Trigger(paused: true) }));

        var card = Assert.Single(view.Factories);
        Assert.Equal("PAUSED", card.StatusWord);
        Assert.Equal(FactoryTone.Paused, card.StatusTone);
        Assert.Equal("resume", card.Pause!.Action);
        Assert.Equal("Resume factory", card.Pause.Label);
        var agent = Assert.Single(card.Agents);
        Assert.Equal("PAUSED", agent.StatusWord);
        Assert.Equal("Trigger: New business mail, every 5 min (paused)", agent.WokenBy);
        Assert.Contains(card.Numbers, n => n.Text == "1 check while paused");
    }

    [Fact]
    public void AgentPage_PausedTrigger_IsShownPausedWithResume_AndTheDefinitionSentence()
    {
        var rows = new List<FactoryActivityDto> { Row("front-desk", "paused", "Checked - paused, not started", Now.AddMinutes(-5)) };
        var page = FactoryAgentsFold.AgentPage(Factory, "front-desk",
            Inputs(rows, new[] { Trigger(paused: true) }, windowKey: FactoryAgentsFold.WindowLast7d));

        Assert.Equal("PAUSED", page.StatusWord);
        Assert.Equal("resume", page.Pause!.Action);
        Assert.Equal("Resume factory agent", page.Pause.Label);
        Assert.Equal("Definition not stored yet - #2177 mission 1", page.DefinitionText);
        var woken = Assert.Single(page.WokenBy);
        Assert.Equal("PAUSED", woken.StatusWord);
        Assert.Contains("/fleet-manager?ask=", page.AskHref);
    }

    [Fact]
    public void AgentPage_NoTrigger_OffersNoPauseAndSaysWhy()
    {
        var page = FactoryAgentsFold.AgentPage(Factory, "scout",
            Inputs(new[] { Row("scout", "started", "Morning batch", Now.AddHours(-1), "s135") }, windowKey: FactoryAgentsFold.WindowLast7d));

        Assert.Null(page.Pause);
        Assert.NotNull(page.PauseUnavailableText);
        Assert.NotNull(page.WokenByEmptyText);
        Assert.Equal("IDLE", page.StatusWord);
    }

    [Fact]
    public void Factories_ALiveSessionTheAgentStarted_IsWorkingAndItsLastRunIsSummarised()
    {
        var rows = new[]
        {
            Row("scout", "started", "Morning batch", Now.AddHours(-1), "s135"),
            Row("scout", "asked", "Draft 1", Now.AddMinutes(-30), "s135"),
            Row("scout", "asked", "Draft 2", Now.AddMinutes(-20), "s135"),
            Row("scout", "blocked", "Remove-me", Now.AddMinutes(-10), "s135"),
        };
        var view = FactoryAgentsFold.Factories(Inputs(rows, live: new HashSet<string> { "s135" }));

        var agent = Assert.Single(view.Factories[0].Agents);
        Assert.Equal("WORKING", agent.StatusWord);
        Assert.Equal("07:00 - 2 asked, 1 blocked", agent.LastRun);
        Assert.Contains(view.Factories[0].Numbers, n => n.Text == "2 asked, waiting for you" && n.Target == "waiting");
        Assert.Equal("All factory agents (1)", view.Tabs[1].Label);
    }

    [Fact]
    public void Factories_NoFactoryAtAll_SaysSo()
    {
        var view = FactoryAgentsFold.Factories(Inputs(Array.Empty<FactoryActivityDto>()));

        Assert.Empty(view.Factories);
        Assert.NotNull(view.EmptyText);
    }

    [Fact]
    public void Report_CountsEachAgentAndATotal()
    {
        var rows = new List<FactoryActivityDto>
        {
            Row("scout", "started", "run", Now.AddHours(-2)),
            Row("scout", "asked", "draft", Now.AddHours(-2)),
            Row("front-desk", "escalated", "money", Now.AddHours(-1)),
        };
        rows.AddRange(EmptyChecks(4, Now.AddHours(-3)));

        var view = FactoryAgentsFold.Report(Inputs(rows, windowKey: FactoryAgentsFold.WindowLast7d), FactoryFilter.None, "/csv",
            Array.Empty<SavedFactoryReport>(), null);

        Assert.Equal(new[] { "Front Desk", "Scout" }, view.Rows.Select(r => r.Name));
        Assert.Equal(new[] { "0", "0", "0", "1", "0", "0", "0", "4" }, view.Rows[0].Cells);
        Assert.Equal(new[] { "1", "0", "1", "0", "0", "0", "0", "0" }, view.Rows[1].Cells);
        Assert.Equal(new[] { "1", "0", "1", "1", "0", "0", "0", "4" }, view.Total!.Cells);
        Assert.Equal(FactoryAgentsFold.ReportColumns.Length, view.Columns.Count);
        Assert.Equal(view.Columns.Count - 1, view.Rows[0].Cells.Count);
    }

    [Fact]
    public void Csv_IsEveryRowNotTheCollapsedLines_AndNeutralisesFormulas()
    {
        var rows = EmptyChecks(3, Now.AddHours(-1));
        rows.Add(Row("scout", "done", "=HYPERLINK(\"x\")", Now, "s1"));

        var csv = FactoryAgentsFold.Csv(rows, FactoryFilter.None);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(5, lines.Length);
        Assert.StartsWith("occurred_utc,recorded_utc,factory", lines[0]);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", lines[4]);
    }

    [Fact]
    public void ResolveWindow_RefusesAnUnknownKeyAndABackwardsCustomWindow()
    {
        Assert.Throws<FactoryViewValidationException>(() => FactoryAgentsFold.ResolveWindow("last-year", null, null, Now, "last-24h"));
        Assert.Throws<FactoryViewValidationException>(() => FactoryAgentsFold.ResolveWindow("custom", Now, Now.AddHours(-1), Now, "last-24h"));
        Assert.Throws<FactoryViewValidationException>(() => FactoryAgentsFold.ResolveWindow("custom", null, Now, Now, "last-24h"));
        var w = FactoryAgentsFold.ResolveWindow(null, null, null, Now, "last-7d");
        Assert.Equal(Now.AddDays(-7), w.FromUtc);
    }

    [Fact]
    public void NormaliseFilter_AnUnknownOutcomeIsRefusedNotMatchedToNothing()
    {
        Assert.Throws<FactoryViewValidationException>(() => FactoryAgentsFold.NormaliseFilter(null, null, "bogus"));
        Assert.Equal("sent-back", FactoryAgentsFold.NormaliseFilter(" ", "", "SENT-BACK").Outcome);
    }

    [Fact]
    public void StampChips_MarksAFactorySessionAndClearsEveryOther()
    {
        var factorySession = new SessionDto { SessionId = "s135", FactoryAgent = null };
        var plain = new SessionDto { SessionId = "s999", FactoryAgent = new SessionFactoryAgentDto { Text = "stale echo" } };
        var started = Row("scout", "started", "Morning batch", Now, "s135", version: "v2");

        FactoryAgentsFold.StampChips(new[] { factorySession, plain },
            new Dictionary<string, FactoryActivityDto> { ["s135"] = started });

        Assert.Equal("Scout v2 - Website Business", factorySession.FactoryAgent!.Text);
        Assert.Equal("Factory agent", factorySession.FactoryAgent.Label);
        Assert.Equal("/factory-agents/website-business/scout", factorySession.FactoryAgent.Href);
        Assert.Null(plain.FactoryAgent);
    }
}
