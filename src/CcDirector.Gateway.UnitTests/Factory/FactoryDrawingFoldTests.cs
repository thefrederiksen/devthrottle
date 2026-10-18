using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using Xunit;
using static CcDirector.Gateway.Tests.Factory.Definition.DefinitionFixture;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// What a factory does, drawn (owner, 18 October 2026): a seat's steps come from its definition and parse with the
/// check's rules; the fold lays each seat out as a lane from its start to its end, with a decision's branch below it,
/// and paints the newest run on the start, the end and the Overview's status line. The fixture is the definition
/// fixture's Writer seat, given the Machine Care Cleaner's shape: four sweeps, a decision, and an email off the line.
/// </summary>
public sealed class FactoryDrawingFoldTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    // 1  seat: writer ... 5  steps:
    private const string WriterWithSteps = """
        seat: writer
        name: Writer
        role: Writer
        brief: Write the posts.
        steps:
          - name: Note free space
            detail: C and D
          - name: Worktree sweep
            detail: ReposFred and D:/wt
            runs: powershell -NoProfile -Command "& 'worktree_sweep.ps1' -Apply"
            repeats: until it says DONE, at most 12 times
          - name: Anything wrong?
            decision: a failure, an ALERT, or under 60 GB free
            continueWhen: no
            branch:
              when: yes
              name: Email the owner
              detail: needs you
              ends: done
        """;

    private static FactoryDefinition Definition(string writer = WriterWithSteps)
    {
        var read = FactoryDefinitionCheck.Read(With("seats/writer.yaml", writer));
        Assert.True(read.Definition is not null, string.Join("\n", read.Findings.Where(f => f.IsError).Select(f => $"{f.File}:{f.Line} {f.Message}")));
        return read.Definition!;
    }

    private static RegisteredFactoryDto Factory() => new()
    {
        Factory = FactoryId, Title = "DevThrottle", Folder = @"D:\f", Computer = "SOREN_NORTH",
        Seats = new()
        {
            new() { Id = "writer", Name = "Writer", Role = "Writer", BriefFile = "briefs/writer.md", Computer = "SOREN_NORTH", Schedules = new() { "cj_1" } },
            new() { Id = "outreach", Name = "Outreach", Role = "Sales", BriefFile = "briefs/outreach.md", Computer = "SOREN_SOUTH" },
        },
    };

    private static FactorySeatsViewDto Rows() => new()
    {
        Rows = new()
        {
            new() { SeatId = "writer", Name = "Writer", WhenText = "Daily 00:00", ComputerText = "SOREN_NORTH" },
            new() { SeatId = "outreach", Name = "Outreach", WhenText = "Talk only", ComputerText = "SOREN_SOUTH" },
        },
    };

    private static CronRunRecord NotStarted() => new()
    {
        ScheduledUtc = new DateTime(2026, 10, 18, 4, 0, 0, DateTimeKind.Utc), InfraStatus = "not-started",
        Ending = CronRunEndings.NoSession, Result = CronRunResults.Problem,
        ResultReason = "it did not start a session: The Director on SOREN_NORTH is version 2.20.1. Update this Director and try again.",
    };

    private static CronRunRecord ClosedItself() => new()
    {
        ScheduledUtc = new DateTime(2026, 10, 18, 4, 0, 0, DateTimeKind.Utc), InfraStatus = "started", SessionId = "s1",
        Ending = CronRunEndings.ClosedItself, EndingText = "closed itself after 6 min", Result = CronRunResults.Ok,
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<CronRunRecord>> Runs(params CronRunRecord[] runs) =>
        new Dictionary<string, IReadOnlyList<CronRunRecord>> { ["cj_1"] = runs };

    private static FactoryDrawingViewDto Draw(FactoryDefinition? definition, IReadOnlyDictionary<string, IReadOnlyList<CronRunRecord>> runs) =>
        FactoryDrawingFold.View(Factory(), Rows(), definition, runs, TimeZoneInfo.Utc, Now);

    [Fact]
    public void Read_SeatWithSteps_KeepsEveryStepItsDecisionAndItsBranch()
    {
        var writer = Definition().Seats.Single(s => s.Seat == "writer");

        Assert.Equal(new[] { "Note free space", "Worktree sweep", "Anything wrong?" }, writer.Steps.Select(s => s.Name));
        Assert.Equal("until it says DONE, at most 12 times", writer.Steps[1].Repeats);
        Assert.StartsWith("powershell", writer.Steps[1].Runs);
        Assert.Equal("no", writer.Steps[2].ContinueWhen);
        Assert.Equal(new SeatStepBranchDefinition("yes", "Email the owner", "needs you", "done"), writer.Steps[2].Branch);
        Assert.Empty(Definition().Seats.Single(s => s.Seat == "outreach").Steps);
    }

    [Fact]
    public void Check_BranchWithoutDecision_IsOneErrorOnTheBranchLine()
    {
        var result = Check(With("seats/writer.yaml", """
            seat: writer
            name: Writer
            role: Writer
            brief: Write the posts.
            steps:
              - name: Send
                branch:
                  when: yes
                  name: Email
                  ends: done
            """));

        AssertOneError(result, "seats/writer.yaml", 7, "'Send'", "branch", "no decision");
    }

    [Fact]
    public void Check_DecisionThatRunsACommand_IsOneError()
    {
        var result = Check(With("seats/writer.yaml", """
            seat: writer
            name: Writer
            role: Writer
            brief: Write the posts.
            steps:
              - name: Safe?
                decision: only benign requests
                runs: py check.py
            """));

        AssertOneError(result, "seats/writer.yaml", 8, "'Safe?'", "decision", "run a command");
    }

    [Fact]
    public void View_SeatWithSteps_IsALaneFromStartThroughEachStepToDone_InOrder()
    {
        var view = Draw(Definition(), Runs(ClosedItself()));

        var writer = view.Nodes.Where(n => n.SeatId == "writer").ToList();
        Assert.Equal(
            new[] { "writer/start", "writer/1", "writer/2", "writer/3", "writer/end", "writer/3/branch", "writer/3/branch/end" },
            writer.Select(n => n.Id));
        Assert.Equal(FactoryDrawingKinds.StartClock, writer[0].Kind);
        Assert.Equal("Daily 00:00", writer[0].Title);
        Assert.Equal(FactoryDrawingKinds.Decision, writer[3].Kind);
        Assert.True(writer[2].Repeats);
        Assert.False(writer[1].Repeats);
        // Each shape sits to the right of the one before it on the main line.
        var main = writer.Take(5).ToList();
        for (var i = 1; i < main.Count; i++) Assert.True(main[i].X >= main[i - 1].X + main[i - 1].Width);
        // The branch sits below its decision.
        Assert.True(writer[5].Y > writer[3].Y + writer[3].Height);
        Assert.Contains(view.Arrows, a => a.From == "writer/3" && a.To == "writer/end" && a.Label == "no");
        Assert.Contains(view.Arrows, a => a.From == "writer/3" && a.To == "writer/3/branch" && a.Label == "yes");
    }

    [Fact]
    public void View_Step_PanelHoldsItsCommandAndHowItRepeats()
    {
        var view = Draw(Definition(), Runs());

        var sweep = view.Nodes.Single(n => n.Id == "writer/2");
        Assert.NotNull(sweep.Panel);
        Assert.Equal("Worktree sweep (step 2 of 3, Writer)", sweep.Panel!.Heading);
        Assert.Contains("Repeats until it says DONE, at most 12 times.", sweep.Panel.Lines);
        Assert.StartsWith("powershell", sweep.Panel.Runs);
    }

    [Fact]
    public void View_RunThatDidNotStart_IsARedStart_ADashedFirstArrow_AndTheStatusSaysWhy()
    {
        var view = Draw(Definition(), Runs(NotStarted()));

        var start = view.Nodes.Single(n => n.Id == "writer/start");
        Assert.Equal(FactoryTone.Red, start.Tone);
        Assert.Equal("did not start, today 04:00", start.Sub);
        var first = view.Arrows.Single(a => a.From == "writer/start");
        Assert.True(first.Dashed);
        Assert.Equal(FactoryTone.Grey, view.Nodes.Single(n => n.Id == "writer/end").Tone);
        Assert.NotNull(view.Status);
        Assert.Equal(FactoryTone.Red, view.Status!.Tone);
        Assert.Equal("The Writer's run of today 04:00 did not start.", view.Status.Text);
        Assert.Equal("The Director on SOREN_NORTH is version 2.20.1. Update this Director and try again.", view.Status.Sub);
    }

    [Fact]
    public void View_RunThatClosedItself_IsAGreenStartAndEnd_AndTheStatusSaysItWentWell()
    {
        var view = Draw(Definition(), Runs(ClosedItself()));

        Assert.Equal(FactoryTone.Ok, view.Nodes.Single(n => n.Id == "writer/start").Tone);
        var end = view.Nodes.Single(n => n.Id == "writer/end");
        Assert.Equal(FactoryTone.Ok, end.Tone);
        Assert.Equal("closed itself after 6 min", end.Sub);
        Assert.Equal(FactoryTone.Ok, view.Status!.Tone);
    }

    [Fact]
    public void View_SeatWithNoSteps_IsItsStart_ADashedBriefBox_AndANote()
    {
        var view = Draw(Definition(), Runs());

        var outreach = view.Nodes.Where(n => n.SeatId == "outreach").ToList();
        Assert.Equal(new[] { FactoryDrawingKinds.StartEvery, FactoryDrawingKinds.Task, FactoryDrawingKinds.EndDone }, outreach.Select(n => n.Kind));
        Assert.StartsWith("Every ", outreach[0].Title);
        Assert.True(outreach[1].Dashed);
        // The note is the lane's own, so a seat's page shows only its own.
        Assert.StartsWith("Outreach lists no steps yet", view.Lanes.Single(l => l.SeatId == "outreach").Note);
        Assert.Null(view.Lanes.Single(l => l.SeatId == "writer").Note);
        Assert.DoesNotContain(view.Notes, n => n.Contains("Outreach", StringComparison.Ordinal));
    }

    [Fact]
    public void View_NoPublishedDefinition_StillDrawsEachSeat_AndSaysWhy()
    {
        var view = Draw(null, Runs());

        Assert.Equal(new[] { "writer", "outreach" }, view.Lanes.Select(l => l.SeatId));
        Assert.Contains(view.Notes, n => n.Contains("no published definition", StringComparison.Ordinal));
        Assert.Null(view.Status);
        // With no definition, a seat with no schedule is started by a person.
        Assert.Equal(FactoryDrawingKinds.StartTalk, view.Nodes.Single(n => n.Id == "outreach/start").Kind);
    }

    [Fact]
    public void View_ManySteps_WrapOntoASecondRow_AndEveryShapeFitsTheDrawing()
    {
        var steps = string.Join("\n", Enumerable.Range(1, 9).Select(i => $"  - name: Step {i}"));
        var view = Draw(Definition($"seat: writer\nname: Writer\nrole: Writer\nbrief: Write the posts.\nsteps:\n{steps}\n"), Runs());

        var writer = view.Nodes.Where(n => n.SeatId == "writer").ToList();
        var first = writer.Single(n => n.Id == "writer/1");
        Assert.Contains(writer, n => n.Y > first.Y + first.Height);
        Assert.All(view.Nodes, n =>
        {
            Assert.True(n.X + n.Width <= view.Width, $"{n.Id} runs past the drawing's width");
            Assert.True(n.Y + n.Height <= view.Height, $"{n.Id} runs past the drawing's height");
        });
        var lane = view.Lanes.Single(l => l.SeatId == "writer");
        Assert.All(writer, n => Assert.True(n.Y + n.Height <= lane.Y + lane.Height, $"{n.Id} runs out of its lane"));
    }

    private static CronRunRecord NoSession(string infra, string? ending, string result, string? problem = null) => new()
    {
        ScheduledUtc = new DateTime(2026, 10, 18, 4, 0, 0, DateTimeKind.Utc), InfraStatus = infra, Ending = ending,
        Result = result, Problem = problem, ResultReason = "the check broke its contract",
    };

    [Theory]
    [InlineData("check-failed", CronRunEndings.NoSession, CronRunResults.Problem, CronRunProblems.CheckFailed)]
    [InlineData("check-due", CronRunEndings.NoSession, CronRunResults.Problem, CronRunProblems.CheckNotRun)]
    [InlineData("not-started", CronRunEndings.NoSession, CronRunResults.Problem, CronRunProblems.DidNotRun)]
    public void View_RunWithNoSessionThatIsAProblem_IsARedStartThatDidNotStart_NeverAGreenOne(string infra, string ending, string result, string problem)
    {
        var view = Draw(Definition(), Runs(NoSession(infra, ending, result, problem)));

        var start = view.Nodes.Single(n => n.Id == "writer/start");
        Assert.Equal(FactoryTone.Red, start.Tone);
        Assert.StartsWith("did not start", start.Sub);
        Assert.True(view.Arrows.Single(a => a.From == "writer/start").Dashed);
        Assert.Equal("The Writer's run of today 04:00 did not start.", view.Status!.Text);
    }

    [Fact]
    public void View_StartTheGatewayNeverHeardBackFrom_IsAmberNotConfirmed_NotStarted()
    {
        var view = Draw(Definition(), Runs(NoSession("start-unknown", CronRunEndings.StartUnknown, CronRunResults.StartUnknown)));

        var start = view.Nodes.Single(n => n.Id == "writer/start");
        Assert.Equal(FactoryTone.Amber, start.Tone);
        Assert.Equal("start not confirmed, today 04:00", start.Sub);
        Assert.Equal(FactoryTone.Amber, view.Status!.Tone);
        Assert.Equal("The Writer's run of today 04:00 may not have started.", view.Status.Text);
    }

    [Fact]
    public void View_CheckStillDue_IsCheckingAndNeverRunningNow()
    {
        var view = Draw(Definition(), Runs(NoSession("check-due", CronRunEndings.Checking, CronRunResults.Checking)));

        Assert.Equal("checking, today 04:00", view.Nodes.Single(n => n.Id == "writer/start").Sub);
        Assert.Equal(FactoryTone.Grey, view.Nodes.Single(n => n.Id == "writer/end").Tone);
        Assert.DoesNotContain("running", view.Status!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void View_WorkListDrainThatWentWell_IsGreen_AndNotHeadlinedAmber()
    {
        var run = NoSession("started", CronRunEndings.WorkList, CronRunResults.Ok);
        run.EndingText = "a work-list drain";
        var view = Draw(Definition(), Runs(run));

        Assert.Equal(FactoryTone.Ok, view.Nodes.Single(n => n.Id == "writer/start").Tone);
        Assert.Equal(FactoryTone.Ok, view.Status!.Tone);
    }

    [Fact]
    public void View_CircleCaptions_ClearTheLabelBand_AndStayInsideTheLaneAndTheDrawing()
    {
        // The longest captions the fold makes: an interval start with a second start, a red sub, and a long end sub.
        var run = ClosedItself();
        run.EndingText = "stopped by another session after a very long while";
        var view = Draw(Definition(), Runs(run));

        foreach (var n in view.Nodes.Where(n => n.Kind is not (FactoryDrawingKinds.Task or FactoryDrawingKinds.Decision)))
        {
            var half = Math.Max(n.Title.Length * 6.6, (n.Sub?.Length ?? 0) * 5.8) / 2;
            var lane = view.Lanes.Single(l => l.SeatId == n.SeatId);
            var cx = n.X + n.Width / 2;
            Assert.True(cx - half >= FactoryDrawingFold.Margin + FactoryDrawingFold.LabelWidth, $"{n.Id}'s caption runs onto the label band");
            Assert.True(cx + half <= lane.X + lane.Width, $"{n.Id}'s caption runs out of its lane");
            Assert.True(cx + half <= view.Width, $"{n.Id}'s caption runs out of the drawing");
        }
    }

    [Fact]
    public void View_AnswerThatStaysOnTheLine_HasRoomInTheGapAfterItsDecision()
    {
        var view = Draw(Definition(WriterWithSteps.Replace("continueWhen: no", "continueWhen: RED, or any doubt at all")), Runs());

        var decision = view.Nodes.Single(n => n.Id == "writer/3");
        var next = view.Nodes.Single(n => n.Id == "writer/end");
        var label = view.Arrows.Single(a => a.From == "writer/3" && a.To == "writer/end").Label!;
        Assert.True(next.X - (decision.X + decision.Width) >= label.Length * 5.6 + 16, "the label does not fit its gap");
    }

    [Fact]
    public void View_BranchHeading_UsesTheWholeDecisionName_NotTheClippedOne()
    {
        var view = Draw(Definition(WriterWithSteps.Replace("name: Anything wrong?", "name: Is the machine safe tonight?")), Runs());

        Assert.Equal("Email the owner (when Is the machine safe tonight?: yes, Writer)",
            view.Nodes.Single(n => n.Id == "writer/3/branch").Panel!.Heading);
        Assert.Contains("when Is the machine safe tonight?: yes", view.Nodes.Single(n => n.Id == "writer/3/branch").Panel!.Heading);
    }

    [Fact]
    public void View_Legend_NamesEveryStartShapeTheDrawingHas_AndNoOther()
    {
        var view = Draw(Definition(), Runs());

        var kinds = view.Legend.Select(l => l.Kind).ToList();
        Assert.Contains(FactoryDrawingKinds.StartClock, kinds);
        Assert.Contains(FactoryDrawingKinds.StartEvery, kinds);
        Assert.DoesNotContain(FactoryDrawingKinds.StartTalk, kinds);
        Assert.DoesNotContain(FactoryDrawingKinds.EndStop, kinds);
    }

    [Fact]
    public void View_SeatTheDefinitionDoesNotHold_SaysSo_AndNotToAddStepsToAFileThatIsNotThere()
    {
        var f = Factory();
        f.Seats.Add(new() { Id = "ghost", Name = "Ghost", Role = "Ghost", BriefFile = "briefs/ghost.md", Computer = "SOREN_NORTH" });
        var view = FactoryDrawingFold.View(f, Rows(), Definition(), Runs(), TimeZoneInfo.Utc, Now);

        Assert.Equal("Ghost is not in the published definition, so only how it starts is drawn.", view.Lanes.Single(l => l.SeatId == "ghost").Note);
    }
}
