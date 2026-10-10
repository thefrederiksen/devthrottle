using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// EVERY RUN SAYS HOW IT WENT (Factory Control, step 1 and step 6; the owner's rulings of 10 October 2026). A scheduled
/// run reports ok or a problem with one line why; a run that ends without reporting, runs past its shift, or never runs
/// is recorded as a problem by the Gateway; a problem stays open until the next run of the same schedule reports ok or
/// someone resolves it with a reason; and every result of a factory's schedule leaves a row in the factory activity
/// record, so no factory is silent.
///
/// The shifts are counted in Toronto time, the owner's account, including both daylight-saving days of 2026.
/// </summary>
public sealed class CronRunResultTests : IDisposable
{
    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();

    // The facts a test hands the service: the account's schedules, its sessions' endings, and the clock.
    private readonly Dictionary<string, CronJobDto> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionEndingFact> _endings = new(StringComparer.Ordinal);
    private DateTime _now;

    public void Dispose() => _h.Dispose();

    private CronRunHistoryStore NewRuns() => new(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));

    private (CronRunResultService Service, CronRunHistoryStore Runs, FactoryActivityRecord Activity) Build()
    {
        var runs = NewRuns();
        var activity = new FactoryActivityRecord(Db);
        var service = new CronRunResultService(runs,
            jobById: id => _jobs.TryGetValue(id, out var j) ? j : null,
            allJobs: () => _jobs.Values.ToList(),
            endingsOf: ids => _endings.Where(e => ids.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
            zoneOf: _ => Toronto,
            appendActivity: (tenant, row) => activity.Append(tenant, row, callingActor: null).Id,
            nowUtc: () => _now);
        return (service, runs, activity);
    }

    private CronJobDto Job(string id, string? factory = null, string? seat = null)
    {
        var job = new CronJobDto { Id = id, Name = "Mail Desk " + id, Factory = factory, Seat = seat };
        _jobs[id] = job;
        return job;
    }

    // A run that started a session, as a fire records it: owing its report.
    private static CronRunRecord Started(string sessionId, DateTime firedUtc) => new()
    {
        ScheduledUtc = firedUtc,
        FiredUtc = firedUtc,
        Machine = "SOREN_NORTH",
        TargetDirectorId = "dir-1",
        SessionId = sessionId,
        InfraStatus = "started",
        TaskStatus = CronRunHistoryStore.TaskStatusUnknown,
        Result = CronRunResults.Pending,
    };

    // A local Toronto wall-clock time as UTC.
    private static DateTime TorontoTime(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), Toronto);

    // ---- the shifts ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(3, WorkShifts.Night, 0, 8)]
    [InlineData(8, WorkShifts.Morning, 8, 16)]
    [InlineData(15, WorkShifts.Morning, 8, 16)]
    [InlineData(16, WorkShifts.Evening, 16, 24)]
    [InlineData(23, WorkShifts.Evening, 16, 24)]
    public void ShiftOf_AnOrdinaryTorontoDay_IsTheShiftItsHourFallsIn(int hour, string shift, int startHour, int endHour)
    {
        var start = WorkShifts.ShiftOf(TorontoTime(2026, 10, 10, hour, 30), Toronto);

        Assert.Equal(shift, start.Name);
        Assert.Equal(TorontoTime(2026, 10, 10, startHour), start.StartUtc);
        Assert.Equal(new DateTime(2026, 10, 10, 0, 0, 0).AddHours(endHour),
            TimeZoneInfo.ConvertTimeFromUtc(start.EndUtc, Toronto));
        Assert.Equal(start.EndUtc, WorkShifts.EndOfShift(TorontoTime(2026, 10, 10, hour, 30), Toronto));
    }

    [Fact]
    public void ShiftOf_TheEveningShift_EndsAtMidnightOnTheNextDay()
    {
        var evening = WorkShifts.ShiftOf(TorontoTime(2026, 10, 10, 20), Toronto);

        Assert.Equal(TorontoTime(2026, 10, 11, 0), evening.EndUtc);
        Assert.Equal("evening shift, Sat 10 Oct, 16:00-24:00", evening.Text);
    }

    [Fact]
    public void ShiftOf_TheNightOfTheSpringForwardDay_IsSevenHoursLong()
    {
        // 8 March 2026: Toronto's clocks jump from 02:00 to 03:00, so 00:00-08:00 holds seven real hours.
        var night = WorkShifts.ShiftOf(TorontoTime(2026, 3, 8, 4), Toronto);

        Assert.Equal(WorkShifts.Night, night.Name);
        Assert.Equal(TimeSpan.FromHours(7), night.EndUtc - night.StartUtc);
        Assert.Equal(new DateTime(2026, 3, 8, 12, 0, 0, DateTimeKind.Utc), night.EndUtc); // 08:00 EDT
    }

    [Fact]
    public void ShiftOf_TheNightOfTheFallBackDay_IsNineHoursLong()
    {
        // 1 November 2026: 02:00 comes back to 01:00, so 00:00-08:00 holds nine real hours.
        var night = WorkShifts.ShiftOf(new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc), Toronto); // 01:30 EDT

        Assert.Equal(WorkShifts.Night, night.Name);
        Assert.Equal(TimeSpan.FromHours(9), night.EndUtc - night.StartUtc);
        Assert.Equal(new DateTime(2026, 11, 1, 13, 0, 0, DateTimeKind.Utc), night.EndUtc); // 08:00 EST
    }

    // ---- the run's own report --------------------------------------------------------------------------------

    [Fact]
    public void Report_Ok_IsRecordedOnTheRun_AndTellsTheSessionToCloseItself()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        _now = TorontoTime(2026, 10, 10, 9);
        runs.Append("cj_a", Started("s1", _now.AddMinutes(-8)));

        var outcome = service.Report(TenantId.Local, "s1", "ok", null);

        Assert.NotNull(outcome.Answer);
        Assert.True(outcome.Answer!.CloseSession);
        var run = Assert.Single(runs.List("cj_a"));
        Assert.Equal(CronRunResults.Ok, run.Result);
        Assert.Null(run.Problem);
        Assert.Equal(_now, run.ResultUtc);
    }

    [Fact]
    public void Report_Problem_IsRecordedWithItsReason_AndTheSessionStaysOpen()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        _now = TorontoTime(2026, 10, 10, 9);
        runs.Append("cj_a", Started("s1", _now.AddMinutes(-8)));

        var outcome = service.Report(TenantId.Local, "s1", "problem", "the inbox would not load");

        Assert.False(outcome.Answer!.CloseSession);
        var run = Assert.Single(runs.List("cj_a"));
        Assert.Equal(CronRunResults.Problem, run.Result);
        Assert.Equal(CronRunProblems.Reported, run.Problem);
        Assert.Equal("the inbox would not load", run.ResultReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("two\nlines")]
    public void Report_AProblemWithoutOneLineWhy_IsRefusedAndNothingIsRecorded(string? reason)
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        _now = TorontoTime(2026, 10, 10, 9);
        runs.Append("cj_a", Started("s1", _now));

        var outcome = service.Report(TenantId.Local, "s1", "problem", reason);

        Assert.Null(outcome.Answer);
        Assert.Equal(400, outcome.StatusCode);
        Assert.Equal(CronRunResults.Pending, runs.List("cj_a").Single().Result);
    }

    [Fact]
    public void Report_FromASessionNoScheduleStarted_IsRefused_NotASilentSuccess()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        _now = TorontoTime(2026, 10, 10, 9);
        runs.Append("cj_a", Started("s1", _now));

        var outcome = service.Report(TenantId.Local, "a-hand-started-session", "ok", null);

        Assert.Null(outcome.Answer);
        Assert.Equal(404, outcome.StatusCode);
        Assert.Contains("was not started by a schedule", outcome.Refusal);
        Assert.Equal(CronRunResults.Pending, runs.List("cj_a").Single().Result);
    }

    [Fact]
    public void Report_WithNoSessionKey_IsRefused()
    {
        var (service, _, _) = Build();

        var outcome = service.Report(TenantId.Local, null, "ok", null);

        Assert.Equal(403, outcome.StatusCode);
    }

    [Fact]
    public void Report_AnUnknownResultWord_IsRefused()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Started("s1", TorontoTime(2026, 10, 10, 9)));

        Assert.Equal(400, service.Report(TenantId.Local, "s1", "fine", null).StatusCode);
    }

    // ---- what the Gateway records by itself ------------------------------------------------------------------

    [Fact]
    public void Sweep_ASessionThatEndedWithoutReporting_IsRecordedAsDidNotReport()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        var fired = TorontoTime(2026, 10, 10, 9);
        runs.Append("cj_a", Started("s1", fired));
        _endings["s1"] = new SessionEndingFact(SessionHistoryEndings.Finished, fired.AddMinutes(6));
        _now = fired.AddMinutes(10);

        Assert.Equal(1, service.Sweep(TenantId.Local));

        var run = runs.List("cj_a").Single();
        Assert.Equal(CronRunResults.Problem, run.Result);
        Assert.Equal(CronRunProblems.DidNotReport, run.Problem);
        Assert.Contains("ended without reporting", run.ResultReason);
    }

    [Fact]
    public void Sweep_AnOpenRunInsideItsShift_IsLeftAlone()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        var fired = TorontoTime(2026, 10, 10, 9);
        runs.Append("cj_a", Started("s1", fired));
        _endings["s1"] = new SessionEndingFact(null, null);
        _now = TorontoTime(2026, 10, 10, 15, 59);

        Assert.Equal(0, service.Sweep(TenantId.Local));
        Assert.Equal(CronRunResults.Pending, runs.List("cj_a").Single().Result);
    }

    [Fact]
    public void Sweep_AnOpenRunPastTheEndOfItsShift_IsRecordedAsRanPastItsShift()
    {
        // Started at 15:30 in the morning shift; still open at 16:00, when the evening shift begins.
        var (service, runs, _) = Build();
        Job("cj_a");
        var fired = TorontoTime(2026, 10, 10, 15, 30);
        runs.Append("cj_a", Started("s1", fired));
        _endings["s1"] = new SessionEndingFact(null, null);
        _now = TorontoTime(2026, 10, 10, 16, 0);

        Assert.Equal(1, service.Sweep(TenantId.Local));

        var run = runs.List("cj_a").Single();
        Assert.Equal(CronRunProblems.RanPastShift, run.Problem);
        Assert.Contains("morning shift, Sat 10 Oct, 08:00-16:00", run.ResultReason);
    }

    [Fact]
    public void Sweep_OnTheSpringForwardDay_TheNightShiftEndsAtEightLocalTime_NotEightHoursAfterMidnight()
    {
        // 8 March 2026: a night run started at 01:00 EST. The night shift ends at 08:00 EDT, seven real hours after
        // midnight. At 07:59 EDT the run is still inside its shift; at 08:00 EDT it is past it.
        var (service, runs, _) = Build();
        Job("cj_a");
        var fired = TorontoTime(2026, 3, 8, 1);
        runs.Append("cj_a", Started("s1", fired));
        _endings["s1"] = new SessionEndingFact(null, null);

        _now = TorontoTime(2026, 3, 8, 7, 59);
        Assert.Equal(0, service.Sweep(TenantId.Local));

        _now = TorontoTime(2026, 3, 8, 8, 0);
        Assert.Equal(TimeSpan.FromHours(7), _now - TorontoTime(2026, 3, 8, 0));
        Assert.Equal(1, service.Sweep(TenantId.Local));
        Assert.Equal(CronRunProblems.RanPastShift, runs.List("cj_a").Single().Problem);
    }

    [Fact]
    public void Sweep_ASessionWithNoHistoryRowYet_IsNotCondemnedInsideItsShift()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        var fired = TorontoTime(2026, 10, 10, 9);
        runs.Append("cj_a", Started("s1", fired));
        _now = fired.AddMinutes(1);

        Assert.Equal(0, service.Sweep(TenantId.Local));
    }

    [Fact]
    public void Report_AfterTheRunWentPastItsShift_IsKeptButTheRunStaysAProblem()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Started("s1", TorontoTime(2026, 10, 10, 15, 30)));
        _endings["s1"] = new SessionEndingFact(null, null);
        _now = TorontoTime(2026, 10, 10, 16, 5);
        service.Sweep(TenantId.Local);

        var outcome = service.Report(TenantId.Local, "s1", "ok", "sent 4 replies");

        Assert.True(outcome.Answer!.CloseSession);
        var run = runs.List("cj_a").Single();
        Assert.Equal(CronRunProblems.RanPastShift, run.Problem);
        Assert.Contains("it then reported ok: sent 4 replies", run.ResultReason);
    }

    // ---- did not run ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Engine_AFireThatStartsNoSession_IsRecordedAsDidNotRun_AndToldToTheFactory()
    {
        var store = new CronJobStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        var runs = NewRuns();
        var job = store.Create(Recurring());
        var told = new List<CronRunRecord>();
        var clock = new FixedClock(job.NextRunUtc!.Value.AddSeconds(30));
        var engine = new CronEngine(store, runs, new NoSessionStarter(), new NoWorkList(), new NullCronNotifier(), clock,
            onResultRecorded: (_, _, run) => told.Add(run));

        await engine.EvaluateDueAsync(CancellationToken.None);

        var run = Assert.Single(runs.List(job.Id));
        Assert.Equal(CronRunResults.Problem, run.Result);
        Assert.Equal(CronRunProblems.DidNotRun, run.Problem);
        Assert.Contains("the machine is asleep", run.ResultReason);
        Assert.Equal(run.RunId, Assert.Single(told).RunId);
    }

    [Fact]
    public async Task Engine_AFireThatStartsASession_OwesItsReport()
    {
        var store = new CronJobStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        var runs = NewRuns();
        var job = store.Create(Recurring());
        var engine = new CronEngine(store, runs, new OneSessionStarter(), new NoWorkList(), new NullCronNotifier(),
            new FixedClock(job.NextRunUtc!.Value.AddSeconds(30)));

        await engine.EvaluateDueAsync(CancellationToken.None);

        Assert.Equal(CronRunResults.Pending, Assert.Single(runs.List(job.Id)).Result);
    }

    [Fact]
    public async Task Engine_ARunDueWhileTheGatewayWasDown_IsRecordedOnceAsDidNotRun()
    {
        var jobsPath = _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json");
        var created = new CronJobStore(Db, jobsPath).Create(Recurring());
        var missedAt = DateTime.UtcNow.AddHours(-3);
        using (var ctx = Db.CreateContext())
        {
            ctx.CronJobs.Single(e => e.Id == created.Id).NextRunUtc = missedAt;
            ctx.SaveChanges();
        }

        // The Gateway starts again: the store moves the schedule on, and remembers the run it missed.
        var reloaded = new CronJobStore(Db, jobsPath);
        Assert.True(reloaded.Get(created.Id)!.NextRunUtc > DateTime.UtcNow);
        var runs = NewRuns();
        var engine = new CronEngine(reloaded, runs, new OneSessionStarter(), new NoWorkList(), new NullCronNotifier(),
            new FixedClock(DateTime.UtcNow));

        await engine.EvaluateDueAsync(CancellationToken.None);
        await engine.EvaluateDueAsync(CancellationToken.None);

        var run = Assert.Single(runs.List(created.Id));
        Assert.Equal(CronRunProblems.DidNotRun, run.Problem);
        Assert.Equal(missedAt, run.ScheduledUtc, TimeSpan.FromSeconds(1));
        Assert.Contains("the Gateway was not running", run.ResultReason);
        Assert.Null(run.SessionId);
    }

    // ---- open, cleared, resolved -----------------------------------------------------------------------------

    [Fact]
    public void Problems_AProblemFromLastNight_IsStillOpenTheNextMorning()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        var fired = TorontoTime(2026, 10, 10, 2);
        runs.Append("cj_a", Started("s1", fired));
        _now = fired.AddMinutes(20);
        service.Report(TenantId.Local, "s1", "problem", "the site answered 500");

        _now = TorontoTime(2026, 10, 11, 8);
        var open = service.Problems(TenantId.Local, factory: null, includeClosed: false);

        var problem = Assert.Single(open.Problems);
        Assert.Equal(1, open.Count);
        Assert.Equal(CronRunProblemStates.Open, problem.State);
        Assert.Equal(WorkShifts.Night, problem.Shift);
        Assert.Equal("night shift, Sat 10 Oct, 00:00-08:00", problem.ShiftText);
        Assert.Equal("s1", problem.SessionId);
        Assert.Equal("the site answered 500", problem.Reason);
        Assert.Equal("Mail Desk cj_a", problem.JobName);
    }

    [Fact]
    public void Problems_TheNextRunOfTheSameScheduleReportsOk_ClearsIt()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        Job("cj_b");
        runs.Append("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2)));
        runs.Append("cj_b", Started("other", TorontoTime(2026, 10, 10, 3)));
        _now = TorontoTime(2026, 10, 10, 2, 30);
        service.Report(TenantId.Local, "s1", "problem", "the site answered 500");

        // Another schedule's ok clears nothing.
        service.Report(TenantId.Local, "other", "ok", null);
        Assert.Single(service.Problems(TenantId.Local, null, includeClosed: false).Problems);

        // The next run of the SAME schedule reports ok.
        runs.Append("cj_a", Started("s2", TorontoTime(2026, 10, 11, 2)));
        _now = TorontoTime(2026, 10, 11, 2, 30);
        service.Report(TenantId.Local, "s2", "ok", null);

        Assert.Equal(0, service.Problems(TenantId.Local, null, includeClosed: false).Count);
        var all = service.Problems(TenantId.Local, null, includeClosed: true);
        var cleared = Assert.Single(all.Problems);
        Assert.Equal(CronRunProblemStates.Cleared, cleared.State);
        Assert.Equal(runs.List("cj_a")[0].RunId, cleared.ClearedByRunId);
    }

    [Fact]
    public void Resolve_WithAReason_ClosesTheProblem_AndKeepsTheReasonAndWhoDidIt()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2)));
        _now = TorontoTime(2026, 10, 10, 2, 30);
        service.Report(TenantId.Local, "s1", "problem", "the site answered 500");
        var runId = runs.List("cj_a").Single().RunId;

        _now = TorontoTime(2026, 10, 10, 9);
        var outcome = service.Resolve(TenantId.Local, runId, "you", "the host was down; it is back");

        Assert.Equal(CronRunProblemStates.Resolved, outcome.Answer!.State);
        Assert.Equal(0, service.Problems(TenantId.Local, null, includeClosed: false).Count);
        var kept = runs.List("cj_a").Single();
        Assert.Equal("the host was down; it is back", kept.ResolvedReason);
        Assert.Equal("you", kept.ResolvedBy);
        Assert.Equal(_now, kept.ResolvedUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Resolve_WithNoReason_IsRefusedAndTheProblemStaysOpen(string? reason)
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2)));
        _now = TorontoTime(2026, 10, 10, 2, 30);
        service.Report(TenantId.Local, "s1", "problem", "the site answered 500");

        var outcome = service.Resolve(TenantId.Local, runs.List("cj_a").Single().RunId, "you", reason);

        Assert.Equal(400, outcome.StatusCode);
        Assert.Equal(1, service.Problems(TenantId.Local, null, includeClosed: false).Count);
        Assert.Null(runs.List("cj_a").Single().ResolvedUtc);
    }

    [Fact]
    public void Resolve_ARunWithNoProblem_OrAnUnknownRun_IsRefused()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2)));
        service.Report(TenantId.Local, "s1", "ok", null);

        Assert.Equal(409, service.Resolve(TenantId.Local, runs.List("cj_a").Single().RunId, "you", "why").StatusCode);
        Assert.Equal(404, service.Resolve(TenantId.Local, Guid.NewGuid().ToString(), "you", "why").StatusCode);
        Assert.Equal(400, service.Resolve(TenantId.Local, "not-a-run", "you", "why").StatusCode);
    }

    [Fact]
    public void Problems_NarrowedToAFactory_ListOnlyItsSchedules_AndAnEmptyListCountsZero()
    {
        var (service, runs, _) = Build();
        Job("cj_a", "mail", "mail-desk");
        Job("cj_b");
        runs.Append("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2)));
        runs.Append("cj_b", Started("s2", TorontoTime(2026, 10, 10, 3)));
        service.Report(TenantId.Local, "s1", "problem", "a");
        service.Report(TenantId.Local, "s2", "problem", "b");

        var mail = service.Problems(TenantId.Local, "mail", includeClosed: false);
        Assert.Equal("cj_a", Assert.Single(mail.Problems).JobId);
        Assert.Equal("mail-desk", mail.Problems[0].Seat);

        var none = service.Problems(TenantId.Local, "website", includeClosed: false);
        Assert.Equal(0, none.Count);
        Assert.Empty(none.Problems);
    }

    // ---- step 6: no silent factories --------------------------------------------------------------------------

    [Fact]
    public void AFactoryLinkedRun_LeavesAnActivityRowForEveryResult_AndResolvingMarksTheFailureHandled()
    {
        var (service, runs, activity) = Build();
        Job("cj_a", "mail", "mail-desk");
        runs.Append("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2)));
        runs.Append("cj_a", Started("s2", TorontoTime(2026, 10, 10, 9)));
        _now = TorontoTime(2026, 10, 10, 9, 10);
        _endings["s1"] = new SessionEndingFact(SessionHistoryEndings.Finished, TorontoTime(2026, 10, 10, 2, 5));

        // The first run ended without reporting: the Gateway records it, and the factory hears.
        service.Sweep(TenantId.Local);
        var failed = Assert.Single(activity.Query(TenantId.Local, factory: "mail").Rows);
        Assert.Equal(FactoryActivityOutcome.Failed, failed.Outcome);
        Assert.Equal("mail-desk", failed.FactoryAgent);
        Assert.Equal("Mail Desk cj_a", failed.Subject);
        Assert.Equal("s1", failed.SessionId);
        Assert.Equal(CronRunResultService.ActorPrefix + "cj_a", failed.Actor);

        // The second run reports ok: a done row about the same subject from the same seat.
        service.Report(TenantId.Local, "s2", "ok", "sent 4 replies");
        var rows = activity.Query(TenantId.Local, factory: "mail").Rows;
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Outcome == FactoryActivityOutcome.Done && r.Subject == "Mail Desk cj_a" && r.SessionId == "s2");
    }

    [Fact]
    public void Resolve_AFactoryProblem_WritesTheRowThatMarksItsFailureHandled()
    {
        var (service, runs, activity) = Build();
        Job("cj_a", "mail", "mail-desk");
        runs.Append("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2)));
        service.Report(TenantId.Local, "s1", "problem", "the site answered 500");
        var failed = activity.Query(TenantId.Local, factory: "mail").Rows.Single();

        service.Resolve(TenantId.Local, runs.List("cj_a").Single().RunId, "session boss-1", "fixed the login");

        var handled = activity.Query(TenantId.Local, factory: "mail").Rows.Single(r => r.CorrectsId is not null);
        Assert.Equal(failed.Id, handled.CorrectsId);
        Assert.Equal("Resolved by session boss-1: fixed the login", handled.What);
    }

    [Fact]
    public async Task Engine_AFactoryRunThatDidNotRun_LeavesAFailedActivityRow()
    {
        var store = new CronJobStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        var (service, runs, activity) = Build();
        var job = store.Create(Recurring());
        // The engine passes the job it fired; give it a factory link as the store would hold one.
        var linked = new CronJobDto { Id = job.Id, Name = job.Name, Factory = "mail", Seat = "mail-desk" };
        var engine = new CronEngine(store, runs, new NoSessionStarter(), new NoWorkList(), new NullCronNotifier(),
            new FixedClock(job.NextRunUtc!.Value.AddSeconds(30)),
            onResultRecorded: (tenant, _, run) => service.Announce(tenant, linked, run));

        await engine.EvaluateDueAsync(CancellationToken.None);

        var row = Assert.Single(activity.Query(TenantId.Local, factory: "mail").Rows);
        Assert.Equal(FactoryActivityOutcome.Failed, row.Outcome);
        Assert.Contains("did not run", row.What);
    }

    [Fact]
    public void ARunInNoFactory_WritesNoActivityRow()
    {
        var (service, runs, activity) = Build();
        Job("cj_a");
        runs.Append("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2)));

        service.Report(TenantId.Local, "s1", "problem", "x");

        Assert.Empty(activity.Query(TenantId.Local).Rows);
    }

    // ---- the run history stays honest about runs from before results -----------------------------------------

    [Fact]
    public void ARunRecordedBeforeResultsExisted_IsUntracked_AndNeverSweptIntoAProblem()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        var old = Started("s1", TorontoTime(2026, 10, 1, 2));
        old.Result = CronRunResults.Untracked;
        runs.Append("cj_a", old);
        _endings["s1"] = new SessionEndingFact(SessionHistoryEndings.Finished, TorontoTime(2026, 10, 1, 2, 5));
        _now = TorontoTime(2026, 10, 10, 9);

        Assert.Equal(0, service.Sweep(TenantId.Local));
        Assert.Equal(0, service.Problems(TenantId.Local, null, includeClosed: true).Count);
    }

    // ---- test doubles -----------------------------------------------------------------------------------------

    private static CronJobDto Recurring() => new()
    {
        Name = "nightly",
        Enabled = true,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Toronto",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTime utcNow) => UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        public DateTime UtcNow { get; }
    }

    private sealed class NoSessionStarter : ICronSessionStarter
    {
        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct) =>
            Task.FromResult<(string?, string?, string?)>((null, null, "the machine is asleep"));
    }

    private sealed class OneSessionStarter : ICronSessionStarter
    {
        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct) =>
            Task.FromResult<(string?, string?, string?)>((Guid.NewGuid().ToString(), "dir-1", null));
    }

    private sealed class NoWorkList : ICronWorkListRunner
    {
        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("these schedules are seed jobs");
    }
}
