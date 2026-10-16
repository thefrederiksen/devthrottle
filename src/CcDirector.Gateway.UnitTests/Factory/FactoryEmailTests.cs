using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Email;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// THE FACTORY EMAIL (Factory Control, step 4; the owner's rulings and his approval of the mockups, 10 October 2026).
/// The Gateway composes and sends it itself: at 08:00 every day, the problems still not fixed and one line per factory;
/// at 16:00 and midnight, only when the shift that just ended raised a NEW problem. Once per slot, whatever restarts.
/// Switched off, nothing at all. All times are Toronto time, the owner's account, both daylight-saving days included.
/// </summary>
public sealed class FactoryEmailTests : IDisposable
{
    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    private readonly GatewayDbTestHarness _h = new();
    private readonly SingleTenantContext _ctx = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open(_ctx);

    private readonly Dictionary<string, CronJobDto> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionEndingFact> _endings = new(StringComparer.Ordinal);
    private DateTime _now;
    private bool _enabled = true;

    public void Dispose() => _h.Dispose();

    private static DateTime TorontoTime(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), Toronto);

    // ---- the rig: the real run history, the real problems fold, the real store; only the send is a fake ---------------

    private sealed class FakeSender : IFactoryEmailSender
    {
        public List<(string Subject, string Html)> Sent { get; } = new();
        public Func<FactoryEmailSendResult> Answer { get; set; } = () => FactoryEmailSendResult.Ok("msg-1");

        public Task<FactoryEmailSendResult> SendAsync(TenantId tenant, string subject, string text, string html, CancellationToken ct)
        {
            var answer = Answer();
            if (answer.Sent) Sent.Add((subject, html));
            return Task.FromResult(answer);
        }
    }

    private CronRunHistoryStore? _runs;
    private CronRunHistoryStore Runs => _runs ??= new CronRunHistoryStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));

    private CronRunResultService Service() => new(Runs,
        jobById: id => _jobs.TryGetValue(id, out var j) ? j : null,
        allJobs: () => _jobs.Values.ToList(),
        endingsOf: ids => _endings.Where(e => ids.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
        zoneOf: _ => Toronto,
        writeActivity: (tenant, run, row, correction) => Runs.WriteActivityRow(tenant, run, row, correction),
        nowUtc: () => _now,
        triggers: Triggers);

    private CcDirector.Gateway.Factory.Triggers.TriggerStore? _triggers;
    private CcDirector.Gateway.Factory.Triggers.TriggerStore Triggers => _triggers ??= new(Db);

    private FactoryEmailStore? _store;
    private FactoryEmailStore Store => _store ??= new FactoryEmailStore(Db);

    // A new sweep over the SAME database and store - what a Gateway restart is.
    private FactoryEmailSweep Sweep(IFactoryEmailSender sender)
    {
        var service = Service();
        return new FactoryEmailSweep(
            new HostedTenantBoundary(_ctx, new DeviceRegistry(), hosted: false), new TenantRegistry(Db), _ctx,
            enabled: _ => _enabled,
            zoneOf: _ => Toronto,
            facts: (tenant, from, to, asOf) => service.ProblemsAndFactoryDays(tenant, from, to, asOf),
            factories: _ => Factories(),
            baseUrl: () => "https://gateway.example.test",
            store: Store,
            sender: sender,
            now: () => _now);
    }

    private static IReadOnlyDictionary<string, FactoryEmailFactory> Factories() =>
        new Dictionary<string, FactoryEmailFactory>(StringComparer.OrdinalIgnoreCase)
        {
            ["outreach"] = new("Outreach", new Dictionary<string, string> { ["sender"] = "Sender" }),
            ["research"] = new("Research", new Dictionary<string, string> { ["scout"] = "Scout" }),
        };

    private CronJobDto Job(string id, string name, string factory, string seat)
    {
        var job = new CronJobDto { Id = id, Name = name, Factory = factory, Seat = seat };
        _jobs[id] = job;
        return job;
    }

    // A run recorded the way the dispatcher records one: with its schedule's factory, seat and name fixed on it, which
    // is where the problems fold reads them from.
    private void AppendRun(string jobId, CronRunRecord run)
    {
        CronRunResultFold.StampFactoryLink(run, _jobs[jobId]);
        Runs.Append(jobId, run);
    }

    private static CronRunRecord Run(string? sessionId, DateTime firedUtc, string result, string? problem = null,
        string? reason = null, DateTime? resultUtc = null) => new()
    {
        ScheduledUtc = firedUtc,
        FiredUtc = firedUtc,
        Machine = "SOREN_NORTH",
        TargetDirectorId = "dir-1",
        SessionId = sessionId,
        InfraStatus = sessionId is null ? "did-not-run" : "started",
        TaskStatus = CronRunHistoryStore.TaskStatusUnknown,
        Result = result,
        Problem = problem,
        ResultReason = reason,
        ResultUtc = resultUtc ?? firedUtc.AddMinutes(10),
    };

    // ---- the slots -----------------------------------------------------------------------------------------------

    [Fact]
    public void DueAt_FiveMinutesAfterEight_IsTheMorningReportOfThatDay()
    {
        var slot = FactoryEmailSlots.DueAt(TorontoTime(2026, 10, 11, 8, 5), Toronto);

        Assert.NotNull(slot);
        Assert.Equal("2026-10-11T08", slot!.Key);
        Assert.Equal(FactoryEmailKinds.Morning, slot.Kind);
        Assert.Equal(TorontoTime(2026, 10, 11, 8), slot.BoundaryUtc);
        Assert.Equal(WorkShifts.Night, slot.EndedShift.Name);
        Assert.Equal(TorontoTime(2026, 10, 10, 8), slot.DayStartUtc);
    }

    [Theory]
    [InlineData(16, "2026-10-11T16", WorkShifts.Morning)]
    [InlineData(0, "2026-10-12T00", WorkShifts.Evening)]
    public void DueAt_AfterSixteenOrMidnight_IsACheckOfTheShiftThatEnded(int hour, string key, string endedShift)
    {
        var at = hour == 0 ? TorontoTime(2026, 10, 12, 0, 5) : TorontoTime(2026, 10, 11, hour, 5);

        var slot = FactoryEmailSlots.DueAt(at, Toronto);

        Assert.Equal(key, slot!.Key);
        Assert.Equal(FactoryEmailKinds.Check, slot.Kind);
        Assert.Equal(endedShift, slot.EndedShift.Name);
    }

    [Fact]
    public void DueAt_InsideTheSettleTimeOrPastTheLateWindow_IsNothing()
    {
        Assert.Null(FactoryEmailSlots.DueAt(TorontoTime(2026, 10, 11, 8, 4), Toronto));
        Assert.Null(FactoryEmailSlots.DueAt(TorontoTime(2026, 10, 11, 10, 1), Toronto));
        Assert.NotNull(FactoryEmailSlots.DueAt(TorontoTime(2026, 10, 11, 10, 0), Toronto));
    }

    [Fact]
    public void DueAt_TheFallBackDay_SendsAtEightLocal_AndItsNightIsNineHours()
    {
        // 1 November 2026: Toronto's clocks go back at 02:00. 08:00 is standard time (13:00 UTC), midnight was daylight
        // time (04:00 UTC), so the night shift the 08:00 report closes was nine real hours long.
        var slot = FactoryEmailSlots.DueAt(new DateTime(2026, 11, 1, 13, 5, 0, DateTimeKind.Utc), Toronto);

        Assert.Equal("2026-11-01T08", slot!.Key);
        Assert.Equal(new DateTime(2026, 11, 1, 13, 0, 0, DateTimeKind.Utc), slot.BoundaryUtc);
        Assert.Equal(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), slot.PreviousBoundaryUtc);
        Assert.Equal(TimeSpan.FromHours(25), slot.BoundaryUtc - slot.DayStartUtc);
    }

    [Fact]
    public void DueAt_TheSpringForwardDay_SendsAtEightLocal_AndItsNightIsSevenHours()
    {
        // 8 March 2026: the clocks go forward at 02:00. Midnight was standard time (05:00 UTC), 08:00 is daylight time
        // (12:00 UTC).
        var slot = FactoryEmailSlots.DueAt(new DateTime(2026, 3, 8, 12, 5, 0, DateTimeKind.Utc), Toronto);

        Assert.Equal("2026-03-08T08", slot!.Key);
        Assert.Equal(new DateTime(2026, 3, 8, 12, 0, 0, DateTimeKind.Utc), slot.BoundaryUtc);
        Assert.Equal(new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc), slot.PreviousBoundaryUtc);
        Assert.Equal(TimeSpan.FromHours(23), slot.BoundaryUtc - slot.DayStartUtc);
    }

    [Fact]
    public void AtBoundary_NotAShiftBoundary_Throws()
    {
        Assert.Throws<ArgumentException>(() => FactoryEmailSlots.AtBoundary(TorontoTime(2026, 10, 11, 9), Toronto));
    }

    // ---- phase 5b: a failed check is a problem, from either source; nothing to do is never "did not run" ----------

    private CcDirector.Gateway.Data.Entities.TriggerEntity Trigger(string name, string factory, string seat, bool definition)
    {
        using var ctx = Db.CreateContext(TenantId.Local);
        var t = new CcDirector.Gateway.Data.Entities.TriggerEntity
        {
            Name = name, Factory = factory, FactoryAgent = seat, Machine = "SOREN_NORTH", IntervalSeconds = 600, Prompt = "Go.",
            CreatedBy = "test", CreatedUtc = TorontoTime(2026, 10, 1, 0),
            // A definition trigger carries its check's path; a command trigger, a command line.
            Seat = definition ? seat : null,
            CheckPath = definition ? "checks/new_mail.py" : null,
            CheckCommand = definition ? "" : "python check.py",
            RepoPath = definition ? "" : @"D:\factories\research",
        };
        t.TenantId = ctx.ActiveTenant!;
        ctx.Triggers.Add(t);
        ctx.SaveChanges();
        return t;
    }

    private Guid TriggerCheck(CcDirector.Gateway.Data.Entities.TriggerEntity t, DateTime at, string outcome, string? reason = null)
    {
        using var ctx = Db.CreateContext(TenantId.Local);
        var run = new CcDirector.Gateway.Data.Entities.TriggerRunEntity
        {
            TriggerId = t.Id, CheckedUtc = at, RecordedUtc = at, Outcome = outcome, Reason = reason, DirectorId = "dir-1",
            Count = outcome == TriggerRunOutcome.Failed ? null : 0,
        };
        run.TenantId = ctx.ActiveTenant!;
        ctx.TriggerRuns.Add(run);
        ctx.SaveChanges();
        return run.Id;
    }

    private static RegisteredFactoryDto Outreach() => new()
    {
        Factory = "outreach",
        Title = "Outreach",
        Folder = @"D:\factories\outreach",
        Computer = "SOREN_NORTH",
        Seats = { new RegisteredFactorySeatDto { Id = "sender", Name = "Sender", Role = "Sender", BriefFile = "seats/sender.yaml", Computer = "SOREN_NORTH" } },
    };

    [Fact]
    public void Compose_MorningWithAFailedTimedCheckAndAFailedIntervalCheck_ListsBoth_OnTheEmailAndTheOpenProblemsCard()
    {
        // Arrange: the timed start's check broke its contract at 02:00; the interval start's check failed twice in a row.
        Job("cj_mail", "Mail desk", "outreach", "sender");
        AppendRun("cj_mail", Run(null, TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.CheckFailed,
            "the output is not JSON: oops"));
        var interval = Trigger("outreach-sender-start-2", "outreach", "sender", definition: true);
        TriggerCheck(interval, TorontoTime(2026, 10, 11, 6), TriggerRunOutcome.Failed, "the check exited with code 1");
        TriggerCheck(interval, TorontoTime(2026, 10, 11, 6, 10), TriggerRunOutcome.Failed, "the check exited with code 1");
        _now = TorontoTime(2026, 10, 11, 8, 5);
        var slot = FactoryEmailSlots.DueAt(_now, Toronto)!;

        // Act
        var (problems, days) = Service().ProblemsAndFactoryDays(TenantId.Local, slot.DayStartUtc, slot.BoundaryUtc, slot.CutoffUtc);
        var email = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, Toronto, problems.Problems, days, Factories(), "https://gateway.example.test"));
        var card = FactoryProblemsFold.Card(Outreach(), Service().Problems(TenantId.Local, "outreach", includeClosed: false));

        // Assert
        Assert.Equal("Factories: 2 problems still open - Sunday 11 October", email.Subject);
        Assert.Contains("Check failed: the output is not JSON: oops.", email.Html);
        Assert.Contains("Check failed: the check exited with code 1 (2 checks in a row failed).", email.Html);
        Assert.Equal(new[] { "Check failed: the check exited with code 1 (2 checks in a row failed).", "Check failed: the output is not JSON: oops." },
            card.Items.Select(i => i.What));
        Assert.All(problems.Problems, p => Assert.Equal(CronRunProblems.CheckFailed, p.Kind));
        Assert.Equal(3, problems.Occurrences);
        // No checked start found nothing to do, so there is no such column; the three failed checks - one timed, two
        // interval - are counted in the day table as well as listed (the phase 5b quality assurance report's finding 4).
        Assert.Contains("LAST 24 HOURS (done / did not run / checks failed or not run / left open)", email.Text);
        Assert.Contains("Outreach: 0 / 0 / 3 / 0", email.Text);
        Assert.Contains("1 factory, 0 runs done, 0 runs did not run, 3 checks failed or did not run.", email.Text);
        Assert.Contains(">Checks failed or not run</td>", email.Html);
        Assert.DoesNotContain("with nothing to do", email.Text);
        Assert.Contains(CronRunResultFold.CheckClearsSentence, email.Text);
        Assert.Contains(CronRunResultFold.CheckClearsSentence, card.Note);
    }

    [Fact]
    public void ProblemsAndFactoryDays_ADayWithOnlyIntervalCheckProblems_ListsTheFactoryWithItsFailedChecks_OnTheEmailAndTheCard()
    {
        // Arrange: a factory with no schedule at all - only an interval start, whose check failed twice in the window
        // and once the day before (outside it). Before this, the factory had no line in the day table.
        var interval = Trigger("outreach-sender-start-2", "outreach", "sender", definition: true);
        TriggerCheck(interval, TorontoTime(2026, 10, 10, 6), TriggerRunOutcome.Failed, "the check exited with code 1");
        TriggerCheck(interval, TorontoTime(2026, 10, 11, 6), TriggerRunOutcome.Failed, "the check exited with code 1");
        TriggerCheck(interval, TorontoTime(2026, 10, 11, 6, 10), TriggerRunOutcome.Failed, "the check exited with code 1");
        _now = TorontoTime(2026, 10, 11, 8, 5);
        var slot = FactoryEmailSlots.DueAt(_now, Toronto)!;

        // Act
        var (problems, days) = Service().ProblemsAndFactoryDays(TenantId.Local, slot.DayStartUtc, slot.BoundaryUtc, slot.CutoffUtc);
        var email = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, Toronto, problems.Problems, days, Factories(), "https://gateway.example.test"));
        var card = FactoryProblemsFold.Card(Outreach(), Service().Problems(TenantId.Local, "outreach", includeClosed: false));

        // Assert: one problem (failures in a row are one), its factory on the day table with the two failed checks of
        // the window, and both surfaces saying how a failed check clears.
        Assert.Equal(1, problems.Count);
        var day = Assert.Single(days);
        Assert.Equal(("outreach", 0, 0, 0, 2, 1), (day.Factory, day.Done, day.NothingToDo, day.DidNotRun, day.CheckFailed, day.OpenProblems));
        Assert.Equal("Factories: 1 problem still open - Sunday 11 October", email.Subject);
        Assert.Contains("LAST 24 HOURS (done / did not run / checks failed or not run / left open)", email.Text);
        Assert.Contains("Outreach: 0 / 0 / 2 / 0", email.Text);
        Assert.Contains("1 factory, 0 runs done, 0 runs did not run, 2 checks failed or did not run.", email.Text);
        Assert.Contains(CronRunResultFold.CheckClearsSentence, email.Html);
        Assert.Contains(CronRunResultFold.CheckClearsSentence, card.Note);
        Assert.Single(card.Items);
    }

    [Fact]
    public void ProblemsAndFactoryDays_ACommandTriggersFailedChecks_AddNoLineAndNoCount()
    {
        // Goal 14: a trigger a person wrote with a command line keeps today's behaviour - its failures are its own, so
        // its factory gets no day line and nothing is counted.
        var command = Trigger("research-scout-check", "research", "scout", definition: false);
        TriggerCheck(command, TorontoTime(2026, 10, 11, 6), TriggerRunOutcome.Failed, "the check exited with code 2");
        Job("cj_send", "Morning send", "outreach", "sender");
        AppendRun("cj_send", Run("s1", TorontoTime(2026, 10, 10, 9), CronRunResults.Ok));
        _now = TorontoTime(2026, 10, 11, 8, 5);
        var slot = FactoryEmailSlots.DueAt(_now, Toronto)!;

        var (problems, days) = Service().ProblemsAndFactoryDays(TenantId.Local, slot.DayStartUtc, slot.BoundaryUtc, slot.CutoffUtc);
        var email = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, Toronto, problems.Problems, days, Factories(), null));

        Assert.Equal(new[] { ("outreach", 0) }, days.Select(d => (d.Factory, d.CheckFailed)));
        Assert.Contains("LAST 24 HOURS (done / did not run / left open)", email.Text);
        Assert.DoesNotContain("check", email.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Problems_ACommandTriggersFailedCheck_IsNotAProblem()
    {
        // Goal 14: a trigger a person wrote with a command line keeps today's behaviour - its failures are its own.
        var command = Trigger("research-scout-check", "research", "scout", definition: false);
        TriggerCheck(command, TorontoTime(2026, 10, 11, 6), TriggerRunOutcome.Failed, "the check exited with code 2");
        _now = TorontoTime(2026, 10, 11, 8, 5);

        Assert.Empty(Service().Problems(TenantId.Local, null, includeClosed: true).Problems);
    }

    [Fact]
    public void Problems_AnIntervalChecksFailure_IsClearedByItsNextCheckThatDidNotFail()
    {
        var interval = Trigger("outreach-sender-start-2", "outreach", "sender", definition: true);
        var failed = TriggerCheck(interval, TorontoTime(2026, 10, 11, 6), TriggerRunOutcome.Failed, "the check exited with code 1");
        var quiet = TriggerCheck(interval, TorontoTime(2026, 10, 11, 6, 10), TriggerRunOutcome.NothingToDo);
        _now = TorontoTime(2026, 10, 11, 8, 5);

        var all = Service().Problems(TenantId.Local, null, includeClosed: true).Problems;

        var p = Assert.Single(all);
        Assert.Equal((failed.ToString("D"), CronRunProblemStates.Cleared, quiet.ToString("D")), (p.RunId, p.State, p.ClearedByRunId));
        Assert.Empty(Service().Problems(TenantId.Local, null, includeClosed: false).Problems);
    }

    [Fact]
    public void Resolve_AnIntervalChecksFailure_ResolvesItByTheFirstFailedCheck_AndRefusesALaterOneOfTheSameRun()
    {
        var interval = Trigger("outreach-sender-start-2", "outreach", "sender", definition: true);
        var first = TriggerCheck(interval, TorontoTime(2026, 10, 11, 6), TriggerRunOutcome.Failed, "the check exited with code 1");
        var second = TriggerCheck(interval, TorontoTime(2026, 10, 11, 6, 10), TriggerRunOutcome.Failed, "the check exited with code 1");
        _now = TorontoTime(2026, 10, 11, 8, 5);
        var service = Service();

        var later = service.Resolve(TenantId.Local, second.ToString("D"), CronRunResultService.ResolvedByYou, "fixed the parser");
        var resolved = service.Resolve(TenantId.Local, first.ToString("D"), CronRunResultService.ResolvedByYou, "fixed the parser");

        Assert.Equal(409, later.StatusCode);
        Assert.StartsWith($"Check {second:D} of trigger outreach-sender-start-2 failed as part of a problem raised by an earlier check", later.Refusal);
        Assert.Equal((CronRunProblemStates.Resolved, "resolved by you: fixed the parser"), (resolved.Answer!.State, resolved.Answer.StateText));
        Assert.Empty(service.Problems(TenantId.Local, null, includeClosed: false).Problems);
    }

    [Fact]
    public void Compose_MorningAfterATimedCheckFoundNothing_ReadsNothingToDo_NeverDidNotRun_AndTheEarlierFailureIsCleared()
    {
        // Arrange: the timed start's 02:00 check failed; its 03:00 check found nothing to do, so the check works again.
        Job("cj_mail", "Mail desk", "outreach", "sender");
        AppendRun("cj_mail", Run(null, TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.CheckFailed,
            "the output is not JSON: oops"));
        AppendRun("cj_mail", Run(null, TorontoTime(2026, 10, 11, 3), CronRunResults.NothingToDo));
        _now = TorontoTime(2026, 10, 11, 8, 5);
        var slot = FactoryEmailSlots.DueAt(_now, Toronto)!;

        // Act
        var (problems, days) = Service().ProblemsAndFactoryDays(TenantId.Local, slot.DayStartUtc, slot.BoundaryUtc, slot.CutoffUtc);
        var email = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, Toronto, problems.Problems, days, Factories(), "https://gateway.example.test"));

        // Assert
        Assert.Empty(problems.Problems);
        var day = Assert.Single(days);
        // The 02:00 check did fail in the window, so the day counts it, even though the 03:00 check cleared it.
        Assert.Equal((0, 1, 0, 1), (day.Done, day.NothingToDo, day.DidNotRun, day.CheckFailed));
        Assert.Equal("Factories: all clear - Sunday 11 October", email.Subject);
        Assert.Contains("1 factory, 0 runs done, 1 run with nothing to do, 0 runs did not run, 1 check failed or did not run.", email.Text);
        Assert.Contains("LAST 24 HOURS (done / nothing to do / did not run / checks failed or not run / left open)", email.Text);
        Assert.Contains("Outreach: 0 / 1 / 0 / 1 / 0", email.Text);
        Assert.Contains("Nothing to do", email.Html);
    }

    [Fact]
    public void Problems_ARunWhoseCheckFoundNothing_DoesNotClearAProblemTheSessionReported()
    {
        // A quiet check means the CHECK works again - not that the work a session reported trouble with was done.
        Job("cj_mail", "Mail desk", "outreach", "sender");
        AppendRun("cj_mail", Run("s1", TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.Reported, "the inbox would not load"));
        AppendRun("cj_mail", Run(null, TorontoTime(2026, 10, 11, 3), CronRunResults.NothingToDo));
        _now = TorontoTime(2026, 10, 11, 8, 5);

        var open = Assert.Single(Service().Problems(TenantId.Local, null, includeClosed: false).Problems);

        Assert.Equal(CronRunProblems.Reported, open.Kind);
    }

    [Fact]
    public void Problems_ARunStillWaitingOnItsCheck_IsNeitherAProblemNorDidNotRun()
    {
        Job("cj_mail", "Mail desk", "outreach", "sender");
        AppendRun("cj_mail", Run(null, TorontoTime(2026, 10, 11, 7), CronRunResults.Checking));
        _now = TorontoTime(2026, 10, 11, 8, 5);
        var slot = FactoryEmailSlots.DueAt(_now, Toronto)!;

        var (problems, days) = Service().ProblemsAndFactoryDays(TenantId.Local, slot.DayStartUtc, slot.BoundaryUtc, slot.CutoffUtc);

        Assert.Empty(problems.Problems);
        Assert.Equal((0, 0, 0), (days.Single().Done, days.Single().NothingToDo, days.Single().DidNotRun));
    }

    // ---- the 08:00 report, through the real problems fold -------------------------------------------------------------

    [Fact]
    public void Compose_MorningWithAnOpenProblemAndOneFixedYesterday_ListsOnlyTheOpenOne()
    {
        Job("cj_send", "Morning send", "outreach", "sender");
        Job("cj_scan", "Night scan", "research", "scout");
        // Fixed yesterday: the Sender's 09:00 run failed, and its 13:00 run reported ok, which clears it.
        AppendRun("cj_send", Run("s1", TorontoTime(2026, 10, 10, 9), CronRunResults.Problem, CronRunProblems.Reported, "the inbox would not load"));
        AppendRun("cj_send", Run("s2", TorontoTime(2026, 10, 10, 13), CronRunResults.Ok));
        // Still open: the Scout's 02:00 run never started.
        AppendRun("cj_scan", Run(null, TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.DidNotRun,
            "its machine was offline when it was due"));
        _now = TorontoTime(2026, 10, 11, 8, 5);
        var slot = FactoryEmailSlots.DueAt(_now, Toronto)!;

        var (problems, days) = Service().ProblemsAndFactoryDays(TenantId.Local, slot.DayStartUtc, slot.BoundaryUtc, slot.CutoffUtc);
        var email = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, Toronto, problems.Problems, days, Factories(), "https://gateway.example.test"));

        Assert.True(email.Send);
        Assert.Equal("Factories: 1 problem still open - Sunday 11 October", email.Subject);
        Assert.Contains("Did not run: its machine was offline when it was due.", email.Html);
        Assert.Contains("Scout - Night scan", email.Html);
        Assert.Contains("Due Sun 11 Oct 02:00 - night shift - open 5 hours", email.Html); // raised 02:10, counted at the 08:05 cut-off
        Assert.DoesNotContain("the inbox would not load", email.Html);
        Assert.Contains("https://gateway.example.test/factories/research#problems", email.Html);
        Assert.Equal(1, email.ProblemCount);
    }

    [Fact]
    public void Compose_ComposedLateInItsWindow_StillDescribesItsSlot()
    {
        Job("cj_send", "Morning send", "outreach", "sender");
        Job("cj_scan", "Night scan", "research", "scout");
        // Open at the 08:05 cut-off.
        AppendRun("cj_scan", Run(null, TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.DidNotRun, "its machine was offline when it was due"));
        // Raised at 08:40, after the cut-off: it belongs to the next email.
        AppendRun("cj_send", Run("s1", TorontoTime(2026, 10, 11, 8, 30), CronRunResults.Problem, CronRunProblems.Reported, "the inbox would not load"));
        // The Gateway was down and composes at 09:30.
        _now = TorontoTime(2026, 10, 11, 9, 30);
        var slot = FactoryEmailSlots.DueAt(_now, Toronto)!;

        var (problems, days) = Service().ProblemsAndFactoryDays(TenantId.Local, slot.DayStartUtc, slot.BoundaryUtc, slot.CutoffUtc);
        var email = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, Toronto, problems.Problems, days, Factories(), null));

        Assert.Equal(slot.CutoffUtc, problems.AsOfUtc);
        Assert.Equal(1, email.ProblemCount);
        Assert.Contains("Did not run: its machine was offline when it was due.", email.Html);
        Assert.Contains("open 5 hours", email.Html); // counted at 08:05, not at 09:30
        Assert.DoesNotContain("the inbox would not load", email.Html);
    }

    [Fact]
    public void ProblemsAndFactoryDays_LastDay_CountsDoneDidNotRunAndSeatsLeftOpen()
    {
        Job("cj_send", "Morning send", "outreach", "sender");
        Job("cj_scan", "Night scan", "research", "scout");
        AppendRun("cj_send", Run("s1", TorontoTime(2026, 10, 10, 9), CronRunResults.Ok));
        AppendRun("cj_send", Run("s2", TorontoTime(2026, 10, 10, 17), CronRunResults.Ok));
        AppendRun("cj_send", Run("s0", TorontoTime(2026, 10, 9, 9), CronRunResults.Ok)); // before the window
        AppendRun("cj_scan", Run(null, TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.DidNotRun, "offline"));
        AppendRun("cj_scan", Run("s3", TorontoTime(2026, 10, 10, 20), CronRunResults.Problem, CronRunProblems.Reported, "stuck"));
        _endings["s3"] = new SessionEndingFact(null, null); // still open
        _now = TorontoTime(2026, 10, 11, 8, 5);

        var (_, days) = Service().ProblemsAndFactoryDays(TenantId.Local, TorontoTime(2026, 10, 10, 8), TorontoTime(2026, 10, 11, 8), _now);

        var outreach = days.Single(d => d.Factory == "outreach");
        var research = days.Single(d => d.Factory == "research");
        Assert.Equal((2, 0, 0, 0), (outreach.Done, outreach.DidNotRun, outreach.LeftOpen, outreach.OpenProblems));
        Assert.Equal((0, 1, 1, 2), (research.Done, research.DidNotRun, research.LeftOpen, research.OpenProblems));
    }

    [Fact]
    public void Compose_MorningWithNothingOpen_IsAllClear()
    {
        var slot = FactoryEmailSlots.DueAt(TorontoTime(2026, 10, 11, 8, 5), Toronto)!;
        var days = new[] { new CronFactoryDayDto { Factory = "outreach", Done = 4 } };

        var email = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, Toronto, Array.Empty<CronRunProblemDto>(), days, Factories(), null));

        Assert.True(email.Send);
        Assert.Equal("Factories: all clear - Sunday 11 October", email.Subject);
        Assert.Contains("All clear", email.Html);
        Assert.Contains("open DevThrottle, Factories.", email.Html);
    }

    [Fact]
    public void Compose_MorningForAnAccountWithNoFactorySchedules_SendsNothing()
    {
        var slot = FactoryEmailSlots.DueAt(TorontoTime(2026, 10, 11, 8, 5), Toronto)!;

        var email = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, Toronto, Array.Empty<CronRunProblemDto>(),
            Array.Empty<CronFactoryDayDto>(), Factories(), null));

        Assert.False(email.Send);
        Assert.Contains("no factory schedules", email.NotSentReason);
    }

    [Fact]
    public void Compose_AReasonWithSymbolsAndMarkup_IsEncodedAsAscii()
    {
        var slot = FactoryEmailSlots.DueAt(TorontoTime(2026, 10, 11, 8, 5), Toronto)!;
        var problem = Problem("outreach", "sender", "Morning send", TorontoTime(2026, 10, 11, 1), CronRunProblems.Reported,
            "build <failed> — café", recordedUtc: TorontoTime(2026, 10, 11, 1, 30));

        var email = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, Toronto, new[] { problem }, Array.Empty<CronFactoryDayDto>(), Factories(), null));

        Assert.All(email.Html, c => Assert.True(c < 128, $"non-ASCII character {(int)c} in the email body"));
        Assert.Contains("build &lt;failed&gt; &#8212; caf&#233;", email.Html);
        Assert.DoesNotContain("<script", email.Html, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the 16:00 and midnight checks ------------------------------------------------------------------------------

    [Fact]
    public async Task SweepTenantAsync_SixteenWithNoNewProblem_SendsNothing()
    {
        Job("cj_scan", "Night scan", "research", "scout");
        // Open, but recorded at 02:00 - already in the 08:00 report.
        AppendRun("cj_scan", Run(null, TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.DidNotRun, "offline",
            resultUtc: TorontoTime(2026, 10, 11, 2)));
        _now = TorontoTime(2026, 10, 11, 16, 5);
        var sender = new FakeSender();

        var said = await Sweep(sender).SweepTenantAsync(TenantId.Local);

        Assert.Empty(sender.Sent);
        Assert.Contains("nothing to send", said);
        Assert.Equal(FactoryEmailStates.NothingToSend, Store.Find(TenantId.Local, "2026-10-11T16")!.State);
    }

    [Fact]
    public async Task SweepTenantAsync_SixteenWithANewProblem_SendsTheCheck()
    {
        Job("cj_scan", "Night scan", "research", "scout");
        Job("cj_send", "Morning send", "outreach", "sender");
        AppendRun("cj_scan", Run(null, TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.DidNotRun, "offline",
            resultUtc: TorontoTime(2026, 10, 11, 2)));
        // New: the Sender's 14:30 run was still going at 16:00, recorded a minute after the boundary.
        AppendRun("cj_send", Run("s9", TorontoTime(2026, 10, 11, 14, 30), CronRunResults.Problem, CronRunProblems.RanPastShift,
            "it had not reported and was still open when its morning shift ended", resultUtc: TorontoTime(2026, 10, 11, 16, 1)));
        _now = TorontoTime(2026, 10, 11, 16, 5);
        var sender = new FakeSender();

        await Sweep(sender).SweepTenantAsync(TenantId.Local);

        var (subject, html) = Assert.Single(sender.Sent);
        Assert.Equal("Factories: 1 new problem in the morning shift - Sunday 11 October", subject);
        Assert.Contains("Sender - Morning send", html);
        Assert.Contains("Ran past its shift", html);
        Assert.DoesNotContain("Night scan", html);
        Assert.Contains("Also still open from before: 1 problem, listed in this morning&#39;s 08:00 report.", html);
        Assert.Equal(FactoryEmailStates.Sent, Store.Find(TenantId.Local, "2026-10-11T16")!.State);
    }

    // ---- once per slot, and the switch -------------------------------------------------------------------------------

    [Fact]
    public async Task SweepTenantAsync_ARestartInsideTheSameSlot_DoesNotSendTwice()
    {
        Job("cj_scan", "Night scan", "research", "scout");
        AppendRun("cj_scan", Run(null, TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.DidNotRun, "offline"));
        var sender = new FakeSender();
        _now = TorontoTime(2026, 10, 11, 8, 5);
        await Sweep(sender).SweepTenantAsync(TenantId.Local);

        // The Gateway restarts: a new sweep, the same database, two ticks later.
        _now = TorontoTime(2026, 10, 11, 8, 9);
        var said = await Sweep(sender).SweepTenantAsync(TenantId.Local);

        Assert.Single(sender.Sent);
        Assert.Equal("slot 2026-10-11T08 already sent", said);
    }

    [Fact]
    public async Task SweepTenantAsync_SwitchedOff_ComposesKeepsAndSendsNothing()
    {
        Job("cj_scan", "Night scan", "research", "scout");
        AppendRun("cj_scan", Run(null, TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.DidNotRun, "offline"));
        _enabled = false;
        _now = TorontoTime(2026, 10, 11, 8, 5);
        var sender = new FakeSender();

        var said = await Sweep(sender).SweepTenantAsync(TenantId.Local);

        Assert.Equal("off", said);
        Assert.Empty(sender.Sent);
        Assert.Null(Store.Find(TenantId.Local, "2026-10-11T08"));
    }

    [Fact]
    public async Task SweepTenantAsync_AFailedSend_IsTriedAgainWithTheKeptBody()
    {
        Job("cj_scan", "Night scan", "research", "scout");
        AppendRun("cj_scan", Run(null, TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.DidNotRun, "offline"));
        var sender = new FakeSender { Answer = () => FactoryEmailSendResult.Failed("the service said no") };
        _now = TorontoTime(2026, 10, 11, 8, 5);
        await Sweep(sender).SweepTenantAsync(TenantId.Local);
        var failed = Store.Find(TenantId.Local, "2026-10-11T08")!;
        Assert.Equal((FactoryEmailStates.Failed, "the service said no", 1), (failed.State, failed.Detail, failed.Attempts));

        sender.Answer = () => FactoryEmailSendResult.Ok("msg-2");
        _now = TorontoTime(2026, 10, 11, 8, 7);
        await Sweep(sender).SweepTenantAsync(TenantId.Local);

        var sent = Store.Find(TenantId.Local, "2026-10-11T08")!;
        Assert.Equal((FactoryEmailStates.Sent, 2, "msg-2"), (sent.State, sent.Attempts, sent.ProviderId));
        Assert.Equal(failed.Html, Assert.Single(sender.Sent).Html);
    }

    [Fact]
    public async Task SweepTenantAsync_ASendThatTimesOut_IsCountedWithItsReason_AndTriedAgain()
    {
        Job("cj_scan", "Night scan", "research", "scout");
        AppendRun("cj_scan", Run(null, TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.DidNotRun, "offline"));
        // What the HTTP client raises when its own timeout runs out: a cancellation nobody asked for.
        var sender = new FakeSender { Answer = () => throw new TaskCanceledException("The request was canceled due to the configured timeout of 30 seconds elapsing.") };
        _now = TorontoTime(2026, 10, 11, 8, 5);

        await Sweep(sender).SweepTenantAsync(TenantId.Local);

        var failed = Store.Find(TenantId.Local, "2026-10-11T08")!;
        Assert.Equal((FactoryEmailStates.Failed, 1), (failed.State, failed.Attempts));
        Assert.Equal("The owner-email service did not answer in time: The request was canceled due to the configured timeout of 30 seconds elapsing.", failed.Detail);

        sender.Answer = () => FactoryEmailSendResult.Ok("msg-2");
        _now = TorontoTime(2026, 10, 11, 8, 7);
        await Sweep(sender).SweepTenantAsync(TenantId.Local);

        var sent = Store.Find(TenantId.Local, "2026-10-11T08")!;
        Assert.Equal((FactoryEmailStates.Sent, 2, "msg-2"), (sent.State, sent.Attempts, sent.ProviderId));
    }

    [Fact]
    public async Task SweepTenantAsync_ASweepAskedToStopMidSend_IsNotCountedAsAFailedSend()
    {
        Job("cj_scan", "Night scan", "research", "scout");
        AppendRun("cj_scan", Run(null, TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.DidNotRun, "offline"));
        using var stop = new CancellationTokenSource();
        var sender = new FakeSender
        {
            Answer = () =>
            {
                stop.Cancel();
                throw new TaskCanceledException("the Gateway is stopping");
            },
        };
        _now = TorontoTime(2026, 10, 11, 8, 5);

        await Assert.ThrowsAsync<TaskCanceledException>(() => Sweep(sender).SweepTenantAsync(TenantId.Local, stop.Token));

        Assert.Equal(0, Store.Find(TenantId.Local, "2026-10-11T08")!.Attempts);
    }

    [Fact]
    public async Task SweepTenantAsync_ATeamAccount_IsNotSupported_AndNotTriedAgain()
    {
        Job("cj_scan", "Night scan", "research", "scout");
        AppendRun("cj_scan", Run(null, TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.DidNotRun, "offline"));
        var calls = 0;
        var sender = new FakeSender { Answer = () => { calls++; return FactoryEmailSendResult.NotSupported(FactoryEmailSender.TeamNotSupported); } };
        _now = TorontoTime(2026, 10, 11, 8, 5);

        await Sweep(sender).SweepTenantAsync(TenantId.Local);
        _now = TorontoTime(2026, 10, 11, 8, 9);
        await Sweep(sender).SweepTenantAsync(TenantId.Local);

        Assert.Equal(1, calls);
        var row = Store.Find(TenantId.Local, "2026-10-11T08")!;
        Assert.Equal(FactoryEmailStates.NotSupported, row.State);
        Assert.Contains("team account", row.Detail);
    }

    [Fact]
    public async Task List_AFactory_IsItsSentEmailsNewestFirst_AndGetHasTheHtmlAsSent()
    {
        Job("cj_scan", "Night scan", "research", "scout");
        Job("cj_send", "Morning send", "outreach", "sender");
        AppendRun("cj_scan", Run(null, TorontoTime(2026, 10, 11, 2), CronRunResults.Problem, CronRunProblems.DidNotRun, "offline"));
        var sender = new FakeSender();
        _now = TorontoTime(2026, 10, 11, 8, 5);
        await Sweep(sender).SweepTenantAsync(TenantId.Local);        // sent, covers outreach and research
        _now = TorontoTime(2026, 10, 11, 16, 5);
        await Sweep(sender).SweepTenantAsync(TenantId.Local);        // nothing new: kept, not sent
        AppendRun("cj_send", Run("s9", TorontoTime(2026, 10, 11, 18), CronRunResults.Problem, CronRunProblems.Reported, "bounced",
            resultUtc: TorontoTime(2026, 10, 11, 18, 30)));
        _now = TorontoTime(2026, 10, 12, 0, 5);
        await Sweep(sender).SweepTenantAsync(TenantId.Local);        // sent, covers outreach only

        var research = Store.List(TenantId.Local, "research", kind: null, limit: 10, allStates: false);
        var outreach = Store.List(TenantId.Local, "Outreach", kind: null, limit: 10, allStates: false);
        var everything = Store.List(TenantId.Local, factory: null, kind: null, limit: 10, allStates: true);

        var only = Assert.Single(research.Emails);
        Assert.Equal(("2026-10-11T08", "08:00", "Factories: 1 problem still open - Sunday 11 October"), (only.SlotKey, only.Slot, only.Title));
        Assert.Equal(new[] { "outreach", "research" }, only.Factories);
        Assert.NotNull(only.SentUtc);
        Assert.Null(only.Html);
        Assert.Equal(new[] { "2026-10-12T00", "2026-10-11T08" }, outreach.Emails.Select(e => e.SlotKey));
        Assert.Equal(new[] { "2026-10-12T00", "2026-10-11T16", "2026-10-11T08" }, everything.Emails.Select(e => e.SlotKey));

        var full = Store.Get(TenantId.Local, Guid.Parse(only.Id))!;
        Assert.Equal(sender.Sent[0].Html, full.Html);
        Assert.Null(Store.Get(TenantId.Local, Guid.NewGuid()));
    }

    // ---- the approved mockup, rendered from a fixture ----------------------------------------------------------------

    /// <summary>
    /// The 08:00 report and the 16:00 check composed from the same sample data the approved mockups show. The HTML is
    /// written to <c>%TEMP%/factory-email-fixture/</c> so it can be screenshot beside the mockup.
    /// </summary>
    [Fact]
    public void Compose_TheApprovedMockupsSampleData_RendersEveryRowOfTheMockup()
    {
        var morning = FactoryEmailSlots.AtBoundary(TorontoTime(2026, 10, 11, 8), Toronto);
        var factories = new Dictionary<string, FactoryEmailFactory>(StringComparer.OrdinalIgnoreCase)
        {
            ["outreach"] = new("Outreach", new Dictionary<string, string> { ["sender"] = "Sender" }),
            ["research"] = new("Research", new Dictionary<string, string> { ["scout"] = "Scout" }),
            ["website"] = new("Website", new Dictionary<string, string> { ["boss"] = "Boss" }),
            ["docs"] = new("Docs", new Dictionary<string, string> { ["writer"] = "Writer" }),
        };
        var problems = new[]
        {
            Problem("research", "scout", "Night scan", TorontoTime(2026, 10, 11, 2), CronRunProblems.DidNotRun,
                "its machine was offline when it was due", started: false),
            Problem("outreach", "sender", "Morning send", TorontoTime(2026, 10, 10, 9), CronRunProblems.DidNotReport,
                "the session ended without saying how it went"),
            Problem("website", "boss", "Evening review", TorontoTime(2026, 10, 9, 17, 5), CronRunProblems.Reported,
                "Build failed - two pages are missing their images"),
        };
        var days = new[]
        {
            new CronFactoryDayDto { Factory = "outreach", Done = 5, OpenProblems = 1 },
            new CronFactoryDayDto { Factory = "research", Done = 3, DidNotRun = 1, OpenProblems = 1 },
            new CronFactoryDayDto { Factory = "website", Done = 6, LeftOpen = 1, OpenProblems = 1 },
            new CronFactoryDayDto { Factory = "docs", Done = 5 },
        };

        var report = FactoryEmailComposer.Compose(new FactoryEmailInput(morning, Toronto, problems, days, factories, "https://gateway.devthrottle.com"));

        Assert.Equal("Factories: 3 problems still open - Sunday 11 October", report.Subject);
        Assert.Contains("4 factories, 19 runs done, 1 run did not run.", report.Html);
        Assert.Contains("Did not report: the session ended without saying how it went.", report.Html);
        Assert.Contains("Started Sat 10 Oct 09:00 - morning shift - open 22 hours", report.Html);
        Assert.Contains("Reported a problem: Build failed - two pages are missing their images.", report.Html);
        Assert.Contains("Started Fri 9 Oct 17:05 - evening shift - open 38 hours", report.Html);
        Assert.Contains("Sent at 08:00 Toronto time to the account that set up these factories.", report.Html);
        Assert.Contains(">Docs</td>", report.Html); // no problem: not a link
        // No check anywhere: no check column and no word about how a check clears - the email reads as it always has.
        Assert.DoesNotContain("Checks failed", report.Html);
        Assert.DoesNotContain(CronRunResultFold.CheckClearsSentence, report.Html);

        var check = FactoryEmailSlots.AtBoundary(TorontoTime(2026, 10, 11, 16), Toronto);
        var fresh = Problem("docs", "writer", "Release notes", TorontoTime(2026, 10, 11, 14, 30), CronRunProblems.RanPastShift,
            "still working at 16:00 and has not said how it went", recordedUtc: TorontoTime(2026, 10, 11, 16, 1));
        var older = problems.Select(p => { p.RaisedAtUtc = TorontoTime(2026, 10, 11, 3); return p; });
        var checkEmail = FactoryEmailComposer.Compose(new FactoryEmailInput(check, Toronto, older.Append(fresh).ToList(),
            Array.Empty<CronFactoryDayDto>(), factories, "https://gateway.devthrottle.com"));

        Assert.Equal("Factories: 1 new problem in the morning shift - Sunday 11 October", checkEmail.Subject);
        Assert.Contains("16:00 check - Sunday 11 October - morning shift, 08:00 to 16:00", checkEmail.Html);
        Assert.Contains("Also still open from before: 3 problems", checkEmail.Html);

        var dir = Path.Combine(Path.GetTempPath(), "factory-email-fixture");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "morning-0800.html"), report.Html);
        File.WriteAllText(Path.Combine(dir, "check-1600.html"), checkEmail.Html);
    }

    [Fact]
    public void ZoneName_Toronto_IsTheCity()
    {
        Assert.Equal("Toronto", FactoryEmailComposer.ZoneName(Toronto));
    }

    private static CronRunProblemDto Problem(string factory, string seat, string job, DateTime atUtc, string kind, string reason,
        bool started = true, DateTime? recordedUtc = null)
    {
        // Filled the way the problems fold fills them, as of the morning cut-off the fixture's report is for.
        var raised = recordedUtc ?? atUtc.AddMinutes(10);
        var asOf = TorontoTime(2026, 10, 11, 8, 5);
        var local = TimeZoneInfo.ConvertTimeFromUtc(atUtc, Toronto).ToString("ddd d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return new()
        {
        RunId = Guid.NewGuid().ToString("D"),
        JobId = "cj_" + seat,
        JobName = job,
        Factory = factory,
        Seat = seat,
        ScheduledUtc = atUtc,
        StartedUtc = started ? atUtc : null,
        Shift = WorkShifts.ShiftOf(atUtc, Toronto).Name,
        Kind = kind,
        Reason = reason,
        Sentence = CronRunResultFold.SentenceOf(kind, reason),
        WhenText = (started ? "Started " : "Due ") + local,
        RaisedAtUtc = raised,
        OpenForText = raised <= asOf ? CronRunResultFold.OpenFor(asOf - raised) : null,
        State = CronRunProblemStates.Open,
        };
    }
}
