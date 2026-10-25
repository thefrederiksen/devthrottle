using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// EVERY FAILURE CAUSE READS AS ONE PLAIN SENTENCE (issue #4436). Each reason a failed run is recorded with - by the
/// engine, the session starter, the machine resolver, the Director, the restart sweep, the result sweep - is given here
/// exactly as the code that writes it writes it, and the sentence it reads as is asserted: its cause, that it names the
/// machine where a machine is at fault, and that none of the technical words ("BadRequest", "director returned",
/// "repoPath", "the launcher", an exception's name) is the headline. The raw reason is the detail, unchanged.
/// </summary>
public sealed class ScheduleFailureWordsTests
{
    private const string Machine = "studio-mac";
    private static readonly DateTime Fired = new(2026, 10, 23, 18, 5, 0, DateTimeKind.Utc);

    // Words that are the code's, never the owner's: none of them may appear in a headline.
    private static readonly string[] Technical =
    {
        "BadRequest", "Conflict", "director returned", "repoPath", "launcher", "registered within", "Exception",
        "not started:", "whether it started a session is not known:", "NoSuchList", "MachineBusy", "EmptyList", "Timeout",
        "TunnelDropped", "it did not start a session:",
    };

    private static CronRunRecord DidNotRun(string reason) => new()
    {
        FiredUtc = Fired, ScheduledUtc = Fired, Machine = Machine, InfraStatus = "not-started", TaskStatus = "unknown",
        Result = CronRunResults.Problem, Problem = CronRunProblems.DidNotRun, ResultReason = reason, ResultUtc = Fired,
    };

    private static CronRunRecord Problem(string problem, string reason, string? sessionId = "sid-1") => new()
    {
        FiredUtc = Fired, ScheduledUtc = Fired, Machine = Machine, InfraStatus = "started", TaskStatus = "unknown",
        SessionId = sessionId, Result = CronRunResults.Problem, Problem = problem, ResultReason = reason, ResultUtc = Fired,
    };

    private static CronRunRecord StartUnknown(string reason) => new()
    {
        FiredUtc = Fired, ScheduledUtc = Fired, Machine = Machine, InfraStatus = CronEngine.StartUnknownStatus,
        TaskStatus = "unknown", Result = CronRunResults.StartUnknown, ResultReason = reason, ResultUtc = Fired,
    };

    private static void AssertPlain(ScheduleFailure failure, string reason)
    {
        foreach (var word in Technical)
            Assert.DoesNotContain(word, failure.Headline, StringComparison.Ordinal);
        Assert.Equal(reason, failure.Detail);
        Assert.EndsWith(".", failure.Headline);
    }

    // Each reason exactly as its writer writes it, its cause, and a fragment the sentence must hold.
    public static IEnumerable<object[]> DidNotRunCases() => new[]
    {
        // The issue's own run: the Director refused a folder it does not have (DirectorCommandRouter.DescribeFailure).
        new object[] { @"it did not start a session: director returned BadRequest: repoPath does not exist: /Users/you/projects/site",
            ScheduleFailureCauses.FolderMissing, "The folder '/Users/you/projects/site' does not exist on studio-mac (a Mac)." },
        // The machine's Director is not running and the launcher could not start it (RegistryDirectorTargetResolver).
        new object[] { "it did not start a session: no Director on 'studio-mac' and the launcher could not start one",
            ScheduleFailureCauses.MachineOffline, "studio-mac was not reachable" },
        new object[] { "it did not start a session: the target machine has no registered director",
            ScheduleFailureCauses.MachineOffline, "Switch studio-mac on and open DevThrottle on it" },
        new object[] { "it did not start a session: launched a Director on 'studio-mac' but none registered within 90s",
            ScheduleFailureCauses.DirectorDidNotConnect, "did not connect within 90 seconds" },
        new object[] { "it did not start a session: no target machine",
            ScheduleFailureCauses.NoMachine, "names no machine" },
        new object[] { "it did not start a session: no Director 'night-shift' on 'studio-mac' is registered. It is either not running or is named something else - list what is registered with: cc-devthrottle director list",
            ScheduleFailureCauses.DirectorNotFound, "is not running on studio-mac" },
        // The Director is connected but answered nothing in time (DirectorCommandRouter.DescribeTimeout), definite.
        new object[] { "it did not start a session: The Director on studio-mac did not answer within 30 seconds. It is not known whether the command was carried out.",
            ScheduleFailureCauses.DirectorDidNotAnswer, "did not answer within 30 seconds" },
        new object[] { "it did not start a session: director returned Error: Object reference not set to an instance of an object.",
            ScheduleFailureCauses.DirectorError, "hit an error while starting the session" },
        new object[] { "it did not start a session: director returned Conflict: a session with that name is already starting",
            ScheduleFailureCauses.DirectorRefused, "refused to start the session" },
        new object[] { "it did not start a session: " + DirectorCronSessionStarter.GatewayStoppedBeforeCreate(Machine),
            ScheduleFailureCauses.GatewayRestarted, "restarted while this run was starting" },
        // Missed runs: the Gateway was down (CronJobStore.Load), the sweep came late, the deadline passed (CronEngine).
        new object[] { "the Gateway was not running when it was due (it started again at 2026-10-23 18:00 UTC)",
            ScheduleFailureCauses.GatewayWasDown, "was not running when it was due" },
        new object[] { "the schedule was reached late, so it ran once for an earlier time and moved on past this one",
            ScheduleFailureCauses.ReachedLate, "reached late" },
        new object[] { "it was reached only after its deadline (2026-10-23 07:00 UTC), so it was not started late",
            ScheduleFailureCauses.PastDeadline, "only after its deadline" },
        // A factory seat's run on an account without factories (CronEngine.FactoryOffReason).
        new object[] { "factory agents are switched off for this account, so the scheduled run was not started",
            ScheduleFailureCauses.FactoryAgentsOff, "Switch them on" },
        new object[] { "factories come with the Pro and Teams plans and this account's plan does not include them, so the scheduled run was not started",
            ScheduleFailureCauses.FactoriesNeedAPlan, "Change the plan" },
        // A seat refused by the registry: an archived factory (FactoryScheduleLink.Check), and a seat not registered.
        new object[] { "it did not start a session: not started: Website Factory is archived, so its schedules stay off. Restore it from the Factories screen, which switches its schedules back on.",
            ScheduleFailureCauses.FactoryArchived, "This run did not start: Website Factory is archived" },
        new object[] { "it did not start a session: not started: the seat 'scout' of 'site' is not registered as its definition says, so no session was started. Publish the definition again.",
            ScheduleFailureCauses.FactorySetup, "Publish the definition again." },
        new object[] { "it did not start a session: " + DirectorCronSessionStarter.BriefNotRead + ": database is locked",
            ScheduleFailureCauses.BriefNotRead, "brief could not be read" },
        // The work-list runner said no (CronEngine.DidNotStartReason over CronWorkListOutcome).
        new object[] { "its work list did not start: NoSuchList", ScheduleFailureCauses.WorkListMissing, "does not exist" },
        new object[] { "its work list did not start: MachineBusy", ScheduleFailureCauses.WorkListBusy, "already being worked on studio-mac" },
        new object[] { "its work list did not start: NoSuchDirector", ScheduleFailureCauses.MachineOffline, "studio-mac was not reachable" },
        // A reason nobody wrote a sentence for still names the machine and points at the details.
        new object[] { "it did not start a session: something new went wrong", ScheduleFailureCauses.Other, "did not start on studio-mac" },
    };

    [Theory]
    [MemberData(nameof(DidNotRunCases))]
    public void Of_ARunThatDidNotRun_ReadsAsItsCausesPlainSentence(string reason, string cause, string fragment)
    {
        var failure = ScheduleFailureWords.Of(DidNotRun(reason), DirectorOs.MacOs);

        Assert.NotNull(failure);
        Assert.Equal(cause, failure!.Cause);
        Assert.Contains(fragment, failure.Headline, StringComparison.Ordinal);
        AssertPlain(failure, reason);
    }

    public static IEnumerable<object[]> StartUnknownCases() => new[]
    {
        // The engine's words for a create whose outcome is not known (CronEngine.StartUnknownReason).
        new object[] { "whether it started a session is not known: The Director on studio-mac did not answer within 30 seconds. It is not known whether the command was carried out.",
            ScheduleFailureCauses.DirectorDidNotAnswer, "it is not known whether this run started" },
        new object[] { "whether it started a session is not known: The connection to the Director on studio-mac dropped while the command was being sent. It is not known whether the command was carried out.",
            ScheduleFailureCauses.ConnectionDropped, "dropped while the run was starting" },
        new object[] { "whether it started a session is not known: " + DirectorCronSessionStarter.GatewayStoppedDuringCreate,
            ScheduleFailureCauses.GatewayRestarted, "restarted while this run was starting" },
        // The sweep gave up looking for it (CronRunResultService.SettleUnknownStarts).
        new object[] { "whether it started a session was not known, and no session named 'Morning report - 2026-10-23 06:00' appeared on its Director by 2026-10-23 16:00 UTC, when its window closed",
            ScheduleFailureCauses.StartNotKnown, "no session named 'Morning report - 2026-10-23 06:00' appeared on studio-mac" },
        new object[] { "whether it started a session is not known", ScheduleFailureCauses.StartNotKnown, "Look for the session on studio-mac" },
    };

    [Theory]
    [MemberData(nameof(StartUnknownCases))]
    public void Of_AStartWhoseOutcomeIsNotKnown_ReadsAsItsCausesPlainSentence(string reason, string cause, string fragment)
    {
        var failure = ScheduleFailureWords.Of(StartUnknown(reason), DirectorOs.MacOs);

        Assert.NotNull(failure);
        Assert.Equal(cause, failure!.Cause);
        Assert.Contains(fragment, failure.Headline, StringComparison.OrdinalIgnoreCase);
        AssertPlain(failure, reason);
    }

    public static IEnumerable<object[]> EndedWithAProblemCases() => new[]
    {
        // The result sweep's own words (CronRunResultFold.GatewayProblemFor).
        new object[] { CronRunProblems.DidNotReport, "its session ended without reporting how the run went (closed itself after 6 min)",
            ScheduleFailureCauses.DidNotReport, "ended without reporting" },
        new object[] { CronRunProblems.RanPastShift, "it had not reported and was still open when its morning shift, Fri 23 Oct, 08:00-16:00 ended",
            ScheduleFailureCauses.RanPastShift, "still open when its shift ended" },
        // The session's own report: its one line, in its own words, is what the owner reads.
        // A session's own report is its words, so it is the detail, never the headline - even one that quotes a
        // Director's error text (the #4436 review, finding 4).
        new object[] { CronRunProblems.Reported, "director returned BadRequest: repoPath does not exist: /x - so I stopped", ScheduleFailureCauses.Reported,
            "The run's session on studio-mac reported a problem. Its own words are in the details." },
        new object[] { CronRunProblems.CheckNotRun, "no Director on studio-mac ran the check of Inbox before its next time",
            ScheduleFailureCauses.CheckNotRun, "No DevThrottle on studio-mac ran the schedule's check" },
        new object[] { CronRunProblems.CheckFailed, "the check exited 2 and printed no count: Exception at line 4", ScheduleFailureCauses.CheckFailed,
            "The schedule's check failed on studio-mac. What the check said is in the details." },
        // A seat at its limit (issue #3907): the agent's own words about the limit are only the detail.
        new object[] { CronRunProblems.SeatLimit,
            "its session started on studio-mac, but the agent answered that its seat is at a limit (\"You've hit your monthly spend limit · contact your admin to raise it\"), so the work could not be done",
            ScheduleFailureCauses.SeatLimit, "its agent's seat is at its usage or spend limit, so no work was done" },
    };

    [Theory]
    [MemberData(nameof(EndedWithAProblemCases))]
    public void Of_ARunThatEndedWithAProblem_ReadsAsItsCausesPlainSentence(string problem, string reason, string cause, string fragment)
    {
        var failure = ScheduleFailureWords.Of(Problem(problem, reason), DirectorOs.MacOs);

        Assert.NotNull(failure);
        Assert.Equal(cause, failure!.Cause);
        Assert.Contains(fragment, failure.Headline, StringComparison.Ordinal);
        AssertPlain(failure, reason);
    }

    [Fact]
    public void Of_ARunThatDidNotFail_IsNull()
    {
        var ok = new CronRunRecord { FiredUtc = Fired, Machine = Machine, SessionId = "sid-1", InfraStatus = "started", Result = CronRunResults.Ok };
        var pending = new CronRunRecord { FiredUtc = Fired, Machine = Machine, SessionId = "sid-2", InfraStatus = "started", Result = CronRunResults.Pending };

        Assert.Null(ScheduleFailureWords.Of(ok, null));
        Assert.Null(ScheduleFailureWords.Of(pending, null));
    }

    [Fact]
    public void Of_AWorkListThatFoundNothingToDo_IsNotAFailure()
    {
        // The engine records an empty drain as a run that did not run; nothing was due, so nobody is told.
        Assert.Null(ScheduleFailureWords.Of(DidNotRun("its work list did not start: EmptyList"), null));
    }

    [Fact]
    public void Of_ARunWithNoMachine_SaysItsMachine()
    {
        var run = DidNotRun("it did not start a session: something new went wrong");
        run.Machine = "";

        Assert.Equal("This run did not start on its machine. The details below say why.", ScheduleFailureWords.Of(run, null)!.Headline);
    }

    [Fact]
    public void LastRunWords_TheNewestRunFailed_ReadsAsLastRunFailed()
    {
        var words = ScheduleFailureWords.LastRunWords(
            DidNotRun("it did not start a session: director returned BadRequest: repoPath does not exist: /Users/you/site"), DirectorOs.MacOs);

        Assert.Equal(("Last run failed: The folder '/Users/you/site' does not exist on studio-mac (a Mac). " +
                      "Choose a folder that exists on studio-mac. Then edit the schedule.", "err"), words);
    }

    [Fact]
    public void LastRunWords_TheNewestRunMayNotHaveStarted_IsAWarning()
    {
        var words = ScheduleFailureWords.LastRunWords(StartUnknown("whether it started a session is not known"), null);

        Assert.NotNull(words);
        Assert.StartsWith("Last run may not have started: It is not known whether this run started on studio-mac.", words!.Value.Text);
        Assert.Equal("warn", words.Value.Tone);
    }

    [Fact]
    public void LastRunWords_TheNewestRunWentFine_OrThereIsNone_IsNull()
    {
        Assert.Null(ScheduleFailureWords.LastRunWords(null, null));
        Assert.Null(ScheduleFailureWords.LastRunWords(
            new CronRunRecord { FiredUtc = Fired, Machine = Machine, SessionId = "sid", InfraStatus = "started", Result = CronRunResults.Ok }, null));
    }

    [Fact]
    public void NotifyNote_ASchedulTheDefaultSwitchedOn_SaysSoUntilTheOwnerTurnsItOff()
    {
        var at = new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc);

        Assert.Equal("Failure notifications were switched on for this schedule on 24 Oct 2026, when they became the default. " +
                     "To stop them, set Notify to Off.", ScheduleFailureWords.NotifyNote(CronNotify.Failure, at));
        Assert.Null(ScheduleFailureWords.NotifyNote(CronNotify.None, at));
        Assert.Null(ScheduleFailureWords.NotifyNote(CronNotify.Always, at));
        Assert.Null(ScheduleFailureWords.NotifyNote(CronNotify.Failure, null));
    }

    [Fact]
    public void Stamp_AFailedRun_CarriesItsSentenceInTheRunTable_AndKeepsTheRawReason()
    {
        var reason = "it did not start a session: no Director on 'studio-mac' and the launcher could not start one";
        var notStarted = DidNotRun(reason);
        var didNotReport = Problem(CronRunProblems.DidNotReport, "its session ended without reporting how the run went (closed itself after 6 min)");

        CronRunEndingFold.Stamp(new[] { notStarted, didNotReport }, new Dictionary<string, SessionEndingFact>(), Fired.AddMinutes(10));

        Assert.Equal(ScheduleFailureCauses.MachineOffline, notStarted.FailureCause);
        Assert.Equal("did not start a session: " + notStarted.FailureText, notStarted.EndingText);
        Assert.Equal(reason, notStarted.ResultReason);
        Assert.Equal(ScheduleFailureCauses.DidNotReport, didNotReport.FailureCause);
        Assert.StartsWith("The run's session on studio-mac ended without reporting", didNotReport.FailureText);
    }

    [Theory]
    [InlineData("failure", "When a run fails")]
    [InlineData("", "When a run fails")]
    [InlineData("ALWAYS", "Every run, started or failed")]
    [InlineData("none", "Off")]
    public void NotifyText_ThePolicyInTheWordsEverySurfaceShows(string policy, string words)
    {
        Assert.Equal(words, CronNotify.Text(policy));
    }

    [Theory]
    [InlineData("failure", true)]
    [InlineData(" Always ", true)]
    [InlineData("none", true)]
    [InlineData("off", false)]
    [InlineData("", false)]
    public void IsKnown_OnlyTheThreePolicies(string policy, bool known)
    {
        Assert.Equal(known, CronNotify.IsKnown(policy));
    }
}
