using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Microsoft.Extensions.Time.Testing;
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
    // A test that never sets it reads the start of time, in UTC like the real clock.
    private DateTime _now = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
    // How many factory activity writes still fail before they start working (a database outage, a refused row).
    private int _activityFailures;
    // How many factory activity writes still fail AFTER their write committed (the process stopped on the way back).
    private int _failuresAfterTheWrite;
    // The sessions each Director reports, by exact name, with a schedule's origin, in roster order (the heal by name).
    private readonly Dictionary<(string Director, string Name), List<string>> _named = new();
    // Every schedule last status the service corrected: (schedule, its fire, the status).
    private readonly List<(string JobId, DateTime FiredUtc, string Status)> _settled = new();

    public void Dispose() => _h.Dispose();

    private CcDirector.Gateway.Factory.Triggers.TriggerStore? _triggers;
    private CcDirector.Gateway.Factory.Triggers.TriggerStore Triggers => _triggers ??= new(Db);

    private static readonly IReadOnlyList<(CcDirector.Gateway.Data.Entities.TriggerEntity, IReadOnlyList<CcDirector.Gateway.Data.Entities.TriggerRunEntity>)> NoTriggers =
        Array.Empty<(CcDirector.Gateway.Data.Entities.TriggerEntity, IReadOnlyList<CcDirector.Gateway.Data.Entities.TriggerRunEntity>)>();

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
            writeActivity: (tenant, run, row, correction) =>
            {
                if (_activityFailures > 0)
                {
                    _activityFailures--;
                    throw new InvalidOperationException("the activity record is unavailable");
                }
                var written = runs.WriteActivityRow(tenant, run, row, correction);
                if (_failuresAfterTheWrite > 0)
                {
                    _failuresAfterTheWrite--;
                    throw new InvalidOperationException("the process stopped after the row was written");
                }
                return written;
            },
            nowUtc: () => _now,
            triggers: Triggers,
            scheduleSessionsByName: (_, director, name) =>
                _named.TryGetValue((director, name), out var s) ? s : (IReadOnlyList<string>)Array.Empty<string>(),
            settleLastStatus: (jobId, firedUtc, status) =>
            {
                _settled.Add((jobId, firedUtc, status));
                return true;
            });
        return (service, runs, activity);
    }

    private CronJobDto Job(string id, string? factory = null, string? seat = null)
    {
        var job = new CronJobDto { Id = id, Name = "Mail Desk " + id, Factory = factory, Seat = seat };
        _jobs[id] = job;
        return job;
    }

    // A run as the engine records it: with its schedule's factory link fixed on it when it is created.
    private CronRunRecord Linked(string jobId, CronRunRecord run)
    {
        if (_jobs.TryGetValue(jobId, out var job)) CronRunResultFold.StampFactoryLink(run, job);
        return run;
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
        runs.Append("cj_a", Linked("cj_a", Started("s1", _now.AddMinutes(-8))));

        var outcome = service.Report(TenantId.Local, "s1", "ok", null);

        Assert.NotNull(outcome.Answer);
        Assert.True(outcome.Answer!.CloseSession);
        var run = Assert.Single(runs.List("cj_a"));
        Assert.Equal(CronRunResults.Ok, run.Result);
        Assert.Null(run.Problem);
        Assert.Equal(_now, run.ResultUtc);
    }

    // ---- an ok run honours its schedule's close-itself setting --------------------------------------------------

    [Fact]
    public void Report_Ok_WhenTheScheduleIsSetToCloseItself_TellsTheSessionToClose()
    {
        var (service, runs, _) = Build();
        Job("cj_a").Action.AutoDismiss = true;
        _now = TorontoTime(2026, 10, 10, 9);
        runs.Append("cj_a", Linked("cj_a", Started("s1", _now.AddMinutes(-8))));

        var outcome = service.Report(TenantId.Local, "s1", "ok", null);

        Assert.True(outcome.Answer!.CloseSession);
        Assert.Equal("Recorded: the run of Mail Desk cj_a went ok. This session now closes itself.", outcome.Answer.Text);
    }

    [Fact]
    public void Report_Ok_WhenTheScheduleIsSetNotToCloseItself_IsRecorded_AndTheSessionStaysOpen()
    {
        var (service, runs, _) = Build();
        Job("cj_a").Action.AutoDismiss = false;
        _now = TorontoTime(2026, 10, 10, 9);
        runs.Append("cj_a", Linked("cj_a", Started("s1", _now.AddMinutes(-8))));

        var outcome = service.Report(TenantId.Local, "s1", "ok", null);

        Assert.False(outcome.Answer!.CloseSession);
        Assert.Equal("Recorded: the run of Mail Desk cj_a went ok. This session stays open, because its schedule is set not to close itself.",
            outcome.Answer.Text);
        // The result is recorded all the same: staying open is not a problem.
        var run = Assert.Single(runs.List("cj_a"));
        Assert.Equal(CronRunResults.Ok, run.Result);
        Assert.Null(run.Problem);
    }

    [Fact]
    public void Report_OkSentAgain_WhenTheScheduleIsSetNotToCloseItself_StillLeavesTheSessionOpen()
    {
        var (service, runs, _) = Build();
        Job("cj_a").Action.AutoDismiss = false;
        _now = TorontoTime(2026, 10, 10, 9);
        runs.Append("cj_a", Linked("cj_a", Started("s1", _now.AddMinutes(-8))));
        service.Report(TenantId.Local, "s1", "ok", null);

        var again = service.Report(TenantId.Local, "s1", "ok", null);

        Assert.False(again.Answer!.CloseSession);
        Assert.Equal("The run of Mail Desk cj_a is already recorded as ok. This session stays open, because its schedule is set not to close itself.",
            again.Answer.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Report_Problem_LeavesTheSessionOpen_WhateverTheCloseItselfSetting(bool closesItself)
    {
        var (service, runs, _) = Build();
        Job("cj_a").Action.AutoDismiss = closesItself;
        _now = TorontoTime(2026, 10, 10, 9);
        runs.Append("cj_a", Linked("cj_a", Started("s1", _now.AddMinutes(-8))));

        var outcome = service.Report(TenantId.Local, "s1", "problem", "the inbox would not load");

        Assert.False(outcome.Answer!.CloseSession);
        Assert.Equal("Recorded the problem on the run of Mail Desk cj_a. This session stays open for someone to look at.", outcome.Answer.Text);
        Assert.Equal(CronRunResults.Problem, Assert.Single(runs.List("cj_a")).Result);
    }

    [Fact]
    public void Report_Ok_WhenTheScheduleWasDeletedWhileItsRunWasGoing_ClosesAsTheDefaultDoes()
    {
        var (service, runs, _) = Build();
        Job("cj_a").Action.AutoDismiss = false;
        _now = TorontoTime(2026, 10, 10, 9);
        runs.Append("cj_a", Linked("cj_a", Started("s1", _now.AddMinutes(-8))));
        _jobs.Remove("cj_a");

        var outcome = service.Report(TenantId.Local, "s1", "ok", null);

        Assert.True(outcome.Answer!.CloseSession);
    }

    [Fact]
    public void Report_Problem_IsRecordedWithItsReason_AndTheSessionStaysOpen()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        _now = TorontoTime(2026, 10, 10, 9);
        runs.Append("cj_a", Linked("cj_a", Started("s1", _now.AddMinutes(-8))));

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
        runs.Append("cj_a", Linked("cj_a", Started("s1", _now)));

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
        runs.Append("cj_a", Linked("cj_a", Started("s1", _now)));

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
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 9))));

        Assert.Equal(400, service.Report(TenantId.Local, "s1", "fine", null).StatusCode);
    }

    // ---- what the Gateway records by itself ------------------------------------------------------------------

    [Fact]
    public void Sweep_ASessionThatEndedWithoutReporting_IsRecordedAsDidNotReport()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        var fired = TorontoTime(2026, 10, 10, 9);
        runs.Append("cj_a", Linked("cj_a", Started("s1", fired)));
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
        runs.Append("cj_a", Linked("cj_a", Started("s1", fired)));
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
        runs.Append("cj_a", Linked("cj_a", Started("s1", fired)));
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
        runs.Append("cj_a", Linked("cj_a", Started("s1", fired)));
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
        runs.Append("cj_a", Linked("cj_a", Started("s1", fired)));
        _now = fired.AddMinutes(1);

        Assert.Equal(0, service.Sweep(TenantId.Local));
    }

    [Fact]
    public void Report_AfterTheRunWentPastItsShift_IsKeptButTheRunStaysAProblem()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 15, 30))));
        _endings["s1"] = new SessionEndingFact(null, null);
        _now = TorontoTime(2026, 10, 10, 16, 5);
        service.Sweep(TenantId.Local);

        var outcome = service.Report(TenantId.Local, "s1", "ok", "sent 4 replies");

        // The run is still a problem, so its session stays open: the close follows what is STORED, never the word
        // asked for (review finding 2 - this test pinned the opposite before).
        Assert.False(outcome.Answer!.CloseSession);
        var run = runs.List("cj_a").Single();
        Assert.Equal(CronRunResults.Problem, run.Result);
        Assert.Equal(CronRunProblems.RanPastShift, run.Problem);
        Assert.Contains("the session then reported ok: sent 4 replies", run.ResultReason);

        // Its report is added once; a second one is refused.
        Assert.Equal(409, service.Report(TenantId.Local, "s1", "ok", "again").StatusCode);
    }

    [Fact]
    public void Report_OkAfterTheShiftEnded_BeforeTheSweep_IsRecordedAsRanPastItsShift()
    {
        // Third review, finding 1: the session is still running when its shift ends and reports ok before the sweep
        // gets there. A run still going at the end of its shift is a problem, so that is what is recorded.
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 15, 30))));
        _now = TorontoTime(2026, 10, 10, 16, 5);

        var outcome = service.Report(TenantId.Local, "s1", "ok", "sent 4 replies");

        Assert.False(outcome.Answer!.CloseSession);
        var run = runs.List("cj_a").Single();
        Assert.Equal(CronRunResults.Problem, run.Result);
        Assert.Equal(CronRunProblems.RanPastShift, run.Problem);
        Assert.Contains("still open when its morning shift", run.ResultReason);
        Assert.Contains("the session then reported ok: sent 4 replies", run.ResultReason);
    }

    [Fact]
    public void Report_OkBeforeTheShiftEnds_IsOk()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 15, 30))));
        _now = TorontoTime(2026, 10, 10, 15, 59);

        Assert.True(service.Report(TenantId.Local, "s1", "ok", null).Answer!.CloseSession);
        Assert.Equal(CronRunResults.Ok, runs.List("cj_a").Single().Result);
    }

    [Fact]
    public void Problems_AsOfBeforeALateReport_StillListTheProblem_RaisedWhenTheSweepFoundIt()
    {
        // Third review, finding 4: a late report only adds to a problem the sweep already recorded, so it keeps the
        // time the problem was raised, and the state as of an earlier instant does not change.
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 15, 30))));
        _endings["s1"] = new SessionEndingFact(null, null);
        var swept = TorontoTime(2026, 10, 10, 16, 5);
        _now = swept;
        service.Sweep(TenantId.Local);
        _now = TorontoTime(2026, 10, 10, 17, 0);
        service.Report(TenantId.Local, "s1", "ok", "late");

        var before = service.Problems(TenantId.Local, null, includeClosed: false, TorontoTime(2026, 10, 10, 16, 30));
        var problem = Assert.Single(before.Problems);
        Assert.Equal(swept, problem.RaisedAtUtc);
        Assert.Equal(swept, runs.List("cj_a").Single().ResultUtc);
    }

    // ---- a result is recorded once (review finding 1) ---------------------------------------------------------

    [Fact]
    public void Report_OkAfterTheRunReportedAProblem_IsRefused_AndTheProblemStands()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 9))));
        service.Report(TenantId.Local, "s1", "problem", "the inbox would not load");

        var outcome = service.Report(TenantId.Local, "s1", "ok", null);

        Assert.Equal(409, outcome.StatusCode);
        Assert.Contains("a result is recorded once", outcome.Refusal);
        var run = runs.List("cj_a").Single();
        Assert.Equal(CronRunResults.Problem, run.Result);
        Assert.Equal(1, service.Problems(TenantId.Local, null, includeClosed: false).Count);
    }

    [Fact]
    public void Report_AProblemAfterTheRunReportedOk_IsRefused()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 9))));
        service.Report(TenantId.Local, "s1", "ok", null);

        Assert.Equal(409, service.Report(TenantId.Local, "s1", "problem", "changed my mind").StatusCode);
        Assert.Equal(CronRunResults.Ok, runs.List("cj_a").Single().Result);
    }

    [Fact]
    public void Report_TheSameResultTwice_AnswersWithTheStoredOne_AndWritesNoSecondActivityRow()
    {
        var (service, runs, activity) = Build();
        Job("cj_a", "mail", "mail-desk");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 9))));
        _now = TorontoTime(2026, 10, 10, 9, 10);
        var first = service.Report(TenantId.Local, "s1", "ok", null);
        _now = TorontoTime(2026, 10, 10, 9, 20);

        var again = service.Report(TenantId.Local, "s1", "ok", null);

        Assert.True(again.Answer!.CloseSession);
        Assert.Equal(first.Answer!.Run.ResultUtc, again.Answer.Run.ResultUtc);
        Assert.Single(activity.Query(TenantId.Local, factory: "mail").Rows);
    }

    [Fact]
    public void RecordResult_IsConditional_SoTheSweepCannotOverwriteAReportThatLandedFirst()
    {
        // The sweep read the run as pending; the session's report landed before the sweep's write.
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 9))));
        var runId = Guid.Parse(runs.List("cj_a").Single().RunId);
        service.Report(TenantId.Local, "s1", "ok", null);

        var late = runs.RecordResult(TenantId.Local, runId, CronRunResults.Pending, null,
            CronRunResults.Problem, CronRunProblems.DidNotReport, "x", TorontoTime(2026, 10, 10, 10));

        Assert.False(late.Applied);
        Assert.Equal(CronRunResults.Ok, runs.List("cj_a").Single().Result);
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
    public void Load_EveryRunDueWhileTheGatewayWasDown_IsRecorded_InTheSameWriteThatMovesTheScheduleOn()
    {
        // An hourly schedule first missed five whole hours before the top of this hour: the Gateway was down for six
        // of its runs (that one, and the five on the hour after it).
        var jobsPath = _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json");
        var clock = new FakeTimeProvider(GatewayRestartedAt);
        var created = new CronJobStore(Db, jobsPath, timeProvider: clock).Create(Hourly());
        var firstMissed = TopOfTheRestartHour.AddHours(-5);
        using (var ctx = Db.CreateContext())
        {
            ctx.CronJobs.Single(e => e.Id == created.Id).NextRunUtc = firstMissed;
            ctx.SaveChanges();
        }

        // The Gateway starts again. No engine runs: the runs are on record the moment the load has moved the schedule on,
        // so there is nothing held in memory to lose (review findings 4 and 6).
        var reloaded = new CronJobStore(Db, jobsPath, timeProvider: clock);

        Assert.Equal(TopOfTheRestartHour.AddHours(1), reloaded.Get(created.Id)!.NextRunUtc);
        var recorded = NewRuns().List(created.Id).OrderBy(r => r.ScheduledUtc).ToList();
        Assert.Equal(6, recorded.Count);
        Assert.All(recorded, r =>
        {
            Assert.Equal(CronRunProblems.DidNotRun, r.Problem);
            Assert.Null(r.SessionId);
            Assert.Contains("the Gateway was not running", r.ResultReason);
        });
        Assert.Equal(firstMissed, recorded[0].ScheduledUtc, TimeSpan.FromSeconds(1));
        Assert.Equal(6, recorded.Select(r => r.ScheduledUtc).Distinct().Count());
    }

    [Fact]
    public async Task Engine_AScheduleReachedLate_FiresOnce_AndRecordsEveryOtherTimeItWasDue()
    {
        // A schedule the load did not move on - a hosted customer account's, which the System-scoped load cannot see -
        // is reached by its own account's sweep four hours late (review finding 3). It fires once, for its first due
        // time, and the three later times it skips are each recorded as a run that did not run.
        var store = new CronJobStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        var runs = NewRuns();
        var job = store.Create(Hourly());
        var firstDue = new DateTime(2026, 10, 10, 13, 0, 0, DateTimeKind.Utc);
        using (var ctx = Db.CreateContext())
        {
            ctx.CronJobs.Single(e => e.Id == job.Id).NextRunUtc = firstDue;
            ctx.SaveChanges();
        }
        var engine = new CronEngine(store, runs, new OneSessionStarter(), new NoWorkList(), new NullCronNotifier(),
            new FixedClock(firstDue.AddHours(3).AddMinutes(30)));

        await engine.EvaluateDueAsync(CancellationToken.None);

        var all = runs.List(job.Id).OrderBy(r => r.ScheduledUtc).ToList();
        Assert.Equal(4, all.Count);
        Assert.Equal(CronRunResults.Pending, all[0].Result); // the fire, for 13:00
        Assert.Equal(new[] { firstDue.AddHours(1), firstDue.AddHours(2), firstDue.AddHours(3) },
            all.Skip(1).Select(r => r.ScheduledUtc));
        Assert.All(all.Skip(1), r => Assert.Equal(CronRunProblems.DidNotRun, r.Problem));
    }

    [Fact]
    public async Task Engine_ADueTimeAlreadyOnRecord_IsNotRecordedTwice()
    {
        // A sweep that recorded a skipped time and then failed before moving the schedule on retries next sweep.
        var store = new CronJobStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        var runs = NewRuns();
        var job = store.Create(Hourly());
        var firstDue = new DateTime(2026, 10, 10, 13, 0, 0, DateTimeKind.Utc);
        runs.Append(job.Id, CronRunResultFold.DidNotRunRecord(job, firstDue.AddHours(1), firstDue, "earlier pass"));
        using (var ctx = Db.CreateContext())
        {
            ctx.CronJobs.Single(e => e.Id == job.Id).NextRunUtc = firstDue;
            ctx.SaveChanges();
        }
        var engine = new CronEngine(store, runs, new OneSessionStarter(), new NoWorkList(), new NullCronNotifier(),
            new FixedClock(firstDue.AddHours(1).AddMinutes(30)));

        await engine.EvaluateDueAsync(CancellationToken.None);

        Assert.Single(runs.List(job.Id), r => r.ScheduledUtc == firstDue.AddHours(1));
    }

    // ---- open, cleared, resolved -----------------------------------------------------------------------------

    [Fact]
    public void Problems_AProblemFromLastNight_IsStillOpenTheNextMorning()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        var fired = TorontoTime(2026, 10, 10, 2);
        runs.Append("cj_a", Linked("cj_a", Started("s1", fired)));
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
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2))));
        runs.Append("cj_b", Linked("cj_b", Started("other", TorontoTime(2026, 10, 10, 3))));
        _now = TorontoTime(2026, 10, 10, 2, 30);
        service.Report(TenantId.Local, "s1", "problem", "the site answered 500");

        // Another schedule's ok clears nothing.
        service.Report(TenantId.Local, "other", "ok", null);
        Assert.Single(service.Problems(TenantId.Local, null, includeClosed: false).Problems);

        // The next run of the SAME schedule reports ok.
        runs.Append("cj_a", Linked("cj_a", Started("s2", TorontoTime(2026, 10, 11, 2))));
        _now = TorontoTime(2026, 10, 11, 2, 30);
        service.Report(TenantId.Local, "s2", "ok", null);

        Assert.Equal(0, service.Problems(TenantId.Local, null, includeClosed: false).Count);
        var all = service.Problems(TenantId.Local, null, includeClosed: true);
        var cleared = Assert.Single(all.Problems);
        Assert.Equal(CronRunProblemStates.Cleared, cleared.State);
        Assert.Equal(runs.List("cj_a")[0].RunId, cleared.ClearedByRunId);
    }

    [Fact]
    public void Problems_CarryTheFinishedWords_AndWhenEachProblemWasRaised()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        var fired = TorontoTime(2026, 10, 10, 2);
        runs.Append("cj_a", Linked("cj_a", Started("s1", fired)));
        _now = fired.AddMinutes(20);
        service.Report(TenantId.Local, "s1", "problem", "the site answered 500");

        _now = TorontoTime(2026, 10, 11, 8);
        var open = service.Problems(TenantId.Local, factory: null, includeClosed: false);

        var problem = Assert.Single(open.Problems);
        Assert.Equal(_now, open.AsOfUtc);
        Assert.Equal(fired.AddMinutes(20), problem.RaisedAtUtc);
        Assert.Equal(DateTimeKind.Utc, problem.RaisedAtUtc.Kind);
        Assert.Equal("Reported a problem: the site answered 500.", problem.Sentence);
        Assert.Equal("Started Sat 10 Oct 02:00", problem.WhenText);
        Assert.Equal("open 29 hours", problem.OpenForText);
    }

    [Fact]
    public void Problems_AsOfAnEarlierInstant_ListThemAsTheyStoodThen()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2))));
        _now = TorontoTime(2026, 10, 10, 2, 30);
        service.Report(TenantId.Local, "s1", "problem", "the site answered 500");
        runs.Append("cj_a", Linked("cj_a", Started("s2", TorontoTime(2026, 10, 11, 2))));
        _now = TorontoTime(2026, 10, 11, 2, 30);
        service.Report(TenantId.Local, "s2", "ok", null);

        // Now: cleared by the next ok run.
        Assert.Equal(0, service.Problems(TenantId.Local, null, includeClosed: false).Count);

        // At midnight before that run, it was still open, and had been for 21 and a half hours.
        var atMidnight = service.Problems(TenantId.Local, null, includeClosed: false, TorontoTime(2026, 10, 11, 0));
        var open = Assert.Single(atMidnight.Problems);
        Assert.Equal(CronRunProblemStates.Open, open.State);
        Assert.Equal("open 21 hours", open.OpenForText);
        Assert.Equal(TorontoTime(2026, 10, 11, 0), atMidnight.AsOfUtc);

        // Before it was raised, there was nothing to list.
        Assert.Equal(0, service.Problems(TenantId.Local, null, includeClosed: true, TorontoTime(2026, 10, 10, 2, 10)).Count);
    }

    [Fact]
    public void Problems_AsOfAnInstantBeforeTheResolve_ListItAsOpen()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2))));
        _now = TorontoTime(2026, 10, 10, 2, 30);
        service.Report(TenantId.Local, "s1", "problem", "the site answered 500");
        _now = TorontoTime(2026, 10, 10, 9);
        service.Resolve(TenantId.Local, runs.List("cj_a").Single().RunId, "you", "the host is back");

        var before = Assert.Single(service.Problems(TenantId.Local, null, includeClosed: false, TorontoTime(2026, 10, 10, 8)).Problems);
        Assert.Equal(CronRunProblemStates.Open, before.State);
        Assert.Null(before.ResolvedBy);

        var after = Assert.Single(service.Problems(TenantId.Local, null, includeClosed: true).Problems);
        Assert.Equal(CronRunProblemStates.Resolved, after.State);
        Assert.Null(after.OpenForText);
    }

    [Fact]
    public void Problems_ARunThatNeverStarted_SaysWhenItWasDue()
    {
        var due = TorontoTime(2026, 10, 11, 2);
        var run = CronRunResultFold.DidNotRunRecord(Hourly(), due, due.AddHours(6), "its machine was offline when it was due");
        var runs = new Dictionary<string, IReadOnlyList<CronRunRecord>>(StringComparer.Ordinal) { ["cj_a"] = new[] { run } };

        var folded = CronRunResultFold.Problems(runs, new Dictionary<string, CronJobDto>(StringComparer.Ordinal), Toronto,
            factory: null, includeClosed: false, asOfUtc: due.AddHours(6), NoTriggers);

        var problem = Assert.Single(folded.Problems);
        Assert.Equal("Did not run: its machine was offline when it was due.", problem.Sentence);
        Assert.Equal("Due Sun 11 Oct 02:00", problem.WhenText);
        Assert.Equal("open less than a minute", problem.OpenForText);
    }

    [Theory]
    [InlineData("did-not-report", "the session ended without saying", "Did not report: the session ended without saying.")]
    [InlineData("ran-past-its-shift", "still open at 16:00.", "Ran past its shift: still open at 16:00.")]
    [InlineData("reported", "", "Reported a problem.")]
    public void SentenceOf_AKindAndAReason_IsOneFinishedSentence(string kind, string reason, string expected) =>
        Assert.Equal(expected, CronRunResultFold.SentenceOf(kind, reason));

    [Theory]
    [InlineData(0, "open less than a minute")]
    [InlineData(1, "open 1 minute")]
    [InlineData(59, "open 59 minutes")]
    [InlineData(60, "open 1 hour")]
    [InlineData(47 * 60 + 59, "open 47 hours")]
    [InlineData(48 * 60, "open 2 days")]
    public void OpenFor_ADuration_IsInWords(int minutes, string expected) =>
        Assert.Equal(expected, CronRunResultFold.OpenFor(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Problems_AnAsOfThatIsNotUtc_IsRefused() =>
        Assert.Throws<ArgumentException>(() => CronRunResultFold.Problems(
            new Dictionary<string, IReadOnlyList<CronRunRecord>>(StringComparer.Ordinal),
            new Dictionary<string, CronJobDto>(StringComparer.Ordinal), Toronto, null, false,
            new DateTime(2026, 10, 11, 8, 0, 0, DateTimeKind.Local), NoTriggers));

    [Fact]
    public void Resolve_WithAReason_ClosesTheProblem_AndKeepsTheReasonAndWhoDidIt()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2))));
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
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2))));
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
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2))));
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
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2))));
        runs.Append("cj_b", Linked("cj_b", Started("s2", TorontoTime(2026, 10, 10, 3))));
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
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2))));
        runs.Append("cj_a", Linked("cj_a", Started("s2", TorontoTime(2026, 10, 10, 9))));
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
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2))));
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
        var inFactory = Recurring();
        inFactory.Factory = "mail";
        inFactory.Seat = "mail-desk";
        var job = store.Create(inFactory);
        // The run takes its factory link from the schedule the engine fired.
        var linked = job;
        _jobs[job.Id] = linked;
        var engine = new CronEngine(store, runs, new NoSessionStarter(), new NoWorkList(), new NullCronNotifier(),
            new FixedClock(job.NextRunUtc!.Value.AddSeconds(30)),
            onResultRecorded: (tenant, _, run) => service.Announce(tenant, linked, run));

        await engine.EvaluateDueAsync(CancellationToken.None);

        var row = Assert.Single(activity.Query(TenantId.Local, factory: "mail").Rows);
        Assert.Equal(FactoryActivityOutcome.Failed, row.Outcome);
        Assert.Contains("did not run", row.What);
    }

    [Fact]
    public void AnActivityWriteThatFails_LeavesTheRowOwed_AndTheNextSweepWritesIt()
    {
        // Review finding 5: the result must never stand without its factory row. The write fails; the result is
        // recorded and the run is marked as owing the row; the next sweep writes it.
        var (service, runs, activity) = Build();
        Job("cj_a", "mail", "mail-desk");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 9))));
        _activityFailures = 1;

        var outcome = service.Report(TenantId.Local, "s1", "problem", "the site answered 500");

        Assert.NotNull(outcome.Answer);
        Assert.Empty(activity.Query(TenantId.Local, factory: "mail").Rows);
        Assert.Single(runs.ActivityDueRuns(TenantId.Local));

        service.Sweep(TenantId.Local);

        var row = Assert.Single(activity.Query(TenantId.Local, factory: "mail").Rows);
        Assert.Equal(FactoryActivityOutcome.Failed, row.Outcome);
        Assert.Empty(runs.ActivityDueRuns(TenantId.Local));

        // And the problem's resolve still finds the row to mark handled.
        service.Resolve(TenantId.Local, runs.List("cj_a").Single().RunId, "you", "the host is back");
        Assert.Contains(activity.Query(TenantId.Local, factory: "mail").Rows, r => r.CorrectsId == row.Id);
    }

    [Fact]
    public void AResolveWhoseCorrectingRowFails_IsWrittenByTheNextSweep()
    {
        var (service, runs, activity) = Build();
        Job("cj_a", "mail", "mail-desk");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 9))));
        service.Report(TenantId.Local, "s1", "problem", "x");
        var failed = activity.Query(TenantId.Local, factory: "mail").Rows.Single();
        _activityFailures = 1;

        service.Resolve(TenantId.Local, runs.List("cj_a").Single().RunId, "you", "fixed");
        Assert.DoesNotContain(activity.Query(TenantId.Local, factory: "mail").Rows, r => r.CorrectsId == failed.Id);

        service.Sweep(TenantId.Local);

        Assert.Single(activity.Query(TenantId.Local, factory: "mail").Rows, r => r.CorrectsId == failed.Id);
        Assert.Empty(runs.ActivityDueRuns(TenantId.Local));
    }

    [Fact]
    public void AnOwedRow_IsStillWrittenToItsFactory_AfterItsScheduleIsDeleted()
    {
        // Second review, finding 1: the write fails, the schedule is deleted, then the sweep runs. The run kept its
        // factory, seat and name when its result was recorded, so the row still reaches the factory.
        var (service, runs, activity) = Build();
        Job("cj_a", "mail", "mail-desk");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 9))));
        _activityFailures = 1;
        service.Report(TenantId.Local, "s1", "problem", "the site answered 500");

        _jobs.Remove("cj_a");
        service.Sweep(TenantId.Local);

        var row = Assert.Single(activity.Query(TenantId.Local, factory: "mail").Rows);
        Assert.Equal(FactoryActivityOutcome.Failed, row.Outcome);
        Assert.Equal("mail-desk", row.FactoryAgent);
        Assert.Equal("Mail Desk cj_a", row.Subject);
        Assert.Empty(runs.ActivityDueRuns(TenantId.Local));
    }

    [Fact]
    public async Task ARunStartedByTheEngine_StillOwesItsFactoryRow_WhenItsScheduleIsDeletedBeforeItReports()
    {
        // Third review, finding 2: the link is fixed on the run when the engine creates it, so a schedule deleted while
        // its session is still running cannot take the run out of its factory.
        var store = new CronJobStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        var (service, runs, activity) = Build();
        var inFactory = Recurring();
        inFactory.Factory = "mail";
        inFactory.Seat = "mail-desk";
        var job = store.Create(inFactory);
        var engine = new CronEngine(store, runs, new OneSessionStarter(), new NoWorkList(), new NullCronNotifier(),
            new FixedClock(job.NextRunUtc!.Value.AddSeconds(30)));
        await engine.EvaluateDueAsync(CancellationToken.None);
        var started = runs.List(job.Id).Single();
        Assert.Equal("mail", started.Factory);

        store.Delete(job.Id);
        _now = started.FiredUtc.AddMinutes(5);
        service.Report(TenantId.Local, started.SessionId, "problem", "the site answered 500");

        var row = Assert.Single(activity.Query(TenantId.Local, factory: "mail").Rows);
        Assert.Equal("mail-desk", row.FactoryAgent);
        Assert.Equal(job.Name, row.Subject);
        Assert.Equal("mail", Assert.Single(service.Problems(TenantId.Local, "mail", includeClosed: false).Problems).Factory);
    }

    [Fact]
    public void AProblem_StaysWithItsRunsFactory_WhenTheScheduleIsMovedOrDeleted()
    {
        // Third review, finding 3: the problem is listed under the factory the run was in.
        var (service, runs, _) = Build();
        Job("cj_a", "mail", "mail-desk");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 9))));
        service.Report(TenantId.Local, "s1", "problem", "x");

        Job("cj_a", "billing", "billing-desk");
        Assert.Single(service.Problems(TenantId.Local, "mail", includeClosed: false).Problems);
        Assert.Equal(0, service.Problems(TenantId.Local, "billing", includeClosed: false).Count);

        _jobs.Remove("cj_a");
        Assert.Equal("mail-desk", Assert.Single(service.Problems(TenantId.Local, "mail", includeClosed: false).Problems).Seat);
    }

    [Fact]
    public void AWriteThatStopsAfterItsRowCommitted_IsNotWrittenTwice()
    {
        // Second review, finding 2: the row is written and the process stops before the caller hears so. The row and
        // the run's note of it are one write, so the run no longer owes it and the next sweep writes nothing more.
        var (service, runs, activity) = Build();
        Job("cj_a", "mail", "mail-desk");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 9))));
        _failuresAfterTheWrite = 1;
        service.Report(TenantId.Local, "s1", "problem", "the site answered 500");

        service.Sweep(TenantId.Local);
        service.Sweep(TenantId.Local);

        var failed = Assert.Single(activity.Query(TenantId.Local, factory: "mail").Rows);
        Assert.Empty(runs.ActivityDueRuns(TenantId.Local));

        // The same for the row a resolve writes.
        _failuresAfterTheWrite = 1;
        service.Resolve(TenantId.Local, runs.List("cj_a").Single().RunId, "you", "the host is back");
        service.Sweep(TenantId.Local);

        Assert.Single(activity.Query(TenantId.Local, factory: "mail").Rows, r => r.CorrectsId == failed.Id);
        Assert.Equal(2, activity.Query(TenantId.Local, factory: "mail").Rows.Count);
    }

    [Fact]
    public void Load_AnOutageLongerThanTheLimit_RecordsTheLimit_AndCountsTheRestAsANumber()
    {
        // Second review, finding 3: 503 runs were due while the Gateway was down. 500 rows are written, counting the
        // first, and the last carries the other 3 as a number - which the problems answer adds up.
        var jobsPath = _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json");
        var clock = new FakeTimeProvider(GatewayRestartedAt);
        var created = new CronJobStore(Db, jobsPath, timeProvider: clock).Create(Hourly());
        _jobs[created.Id] = created;
        using (var ctx = Db.CreateContext())
        {
            ctx.CronJobs.Single(e => e.Id == created.Id).NextRunUtc = TopOfTheRestartHour.AddHours(-502);
            ctx.SaveChanges();
        }

        _ = new CronJobStore(Db, jobsPath, timeProvider: clock);

        var recorded = NewRuns().AllByJob(TenantId.Local)[created.Id].OrderBy(r => r.ScheduledUtc).ToList();
        Assert.Equal(CronSchedule.MaxMissedRunsRecorded, recorded.Count);
        Assert.Equal(3, recorded[^1].MissedNotListed);
        Assert.All(recorded.Take(recorded.Count - 1), r => Assert.Equal(0, r.MissedNotListed));

        var (service, _, _) = Build();
        _now = GatewayRestartedAt.UtcDateTime.AddMinutes(1);
        var problems = service.Problems(TenantId.Local, null, includeClosed: false);
        Assert.Equal(500, problems.Count);
        Assert.Equal(503, problems.Occurrences);
        Assert.Equal(4, problems.Problems.Max(p => p.Occurrences));
    }

    [Fact]
    public async Task Engine_AScheduleReachedFarTooLate_RecordsTheLimit_AndCountsTheRestAsANumber()
    {
        // Reached 600 due times late: it fires once, and the 600 times it skips are 500 rows, the last counting 100 more.
        var store = new CronJobStore(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        var runs = NewRuns();
        var job = store.Create(Hourly());
        var firstDue = new DateTime(2026, 9, 1, 13, 0, 0, DateTimeKind.Utc);
        using (var ctx = Db.CreateContext())
        {
            ctx.CronJobs.Single(e => e.Id == job.Id).NextRunUtc = firstDue;
            ctx.SaveChanges();
        }
        var engine = new CronEngine(store, runs, new OneSessionStarter(), new NoWorkList(), new NullCronNotifier(),
            new FixedClock(firstDue.AddHours(600).AddMinutes(30)));

        await engine.EvaluateDueAsync(CancellationToken.None);

        var missed = runs.AllByJob(TenantId.Local)[job.Id].Where(r => r.Problem == CronRunProblems.DidNotRun).ToList();
        Assert.Equal(CronSchedule.MaxMissedRunsRecorded, missed.Count);
        Assert.Equal(600, missed.Sum(r => 1 + r.MissedNotListed));
    }

    // ---- open problems outlive the history cap (review finding 7) ---------------------------------------------

    [Fact]
    public void AnOpenProblem_IsNeverPrunedByTheHistoryCap()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 1, 9))));
        service.Report(TenantId.Local, "s1", "problem", "the oldest problem");
        for (var i = 0; i < CronRunHistoryStore.MaxRecordsPerJob + 10; i++)
        {
            var later = Started("later-" + i, TorontoTime(2026, 10, 2, 9).AddHours(i));
            later.Result = CronRunResults.Untracked;
            runs.Append("cj_a", later);
        }

        var all = runs.List("cj_a");
        Assert.Equal(CronRunHistoryStore.MaxRecordsPerJob, all.Count);
        Assert.Contains(all, r => r.ResultReason == "the oldest problem");
        Assert.Contains(service.Problems(TenantId.Local, null, includeClosed: false).Problems, p => p.Reason == "the oldest problem");
    }

    [Fact]
    public void AProblemClearedByALaterOk_IsPrunedLikeAnyOtherOldRun()
    {
        var (service, runs, _) = Build();
        Job("cj_a");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 1, 9))));
        service.Report(TenantId.Local, "s1", "problem", "cleared later");
        runs.Append("cj_a", Linked("cj_a", Started("s2", TorontoTime(2026, 10, 1, 10))));
        service.Report(TenantId.Local, "s2", "ok", null);
        for (var i = 0; i < CronRunHistoryStore.MaxRecordsPerJob; i++)
        {
            var later = Started("later-" + i, TorontoTime(2026, 10, 2, 9).AddHours(i));
            later.Result = CronRunResults.Untracked;
            runs.Append("cj_a", later);
        }

        Assert.DoesNotContain(runs.List("cj_a"), r => r.ResultReason == "cleared later");
        Assert.Equal(CronRunHistoryStore.MaxRecordsPerJob, runs.List("cj_a").Count);
    }

    [Fact]
    public void ARunInNoFactory_WritesNoActivityRow()
    {
        var (service, runs, activity) = Build();
        Job("cj_a");
        runs.Append("cj_a", Linked("cj_a", Started("s1", TorontoTime(2026, 10, 10, 2))));

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

    private static CronJobDto Hourly() => new()
    {
        Name = "hourly",
        Enabled = true,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 * * * *",
        TimeZoneId = "America/Toronto",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    /// <summary>The moment the Gateway "starts again" in the two load tests: twenty past an hour, on a fake clock, so
    /// the runs that were due while it was down are the same ones on every machine and at every time of day.</summary>
    private static readonly DateTimeOffset GatewayRestartedAt = new(2026, 10, 10, 14, 20, 0, TimeSpan.Zero);
    private static readonly DateTime TopOfTheRestartHour = new(2026, 10, 10, 14, 0, 0, DateTimeKind.Utc);

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

    // ---- the heal by name: a start whose outcome was not known (review of P10) --------------------------------

    private const string RunName = "Eric - Mail Desk - 2026-10-16 09:09";
    private const string Unknown30 = "whether it started a session is not known: The Director did not answer within 30 seconds. It is not known whether the command was carried out.";

    // A run whose create was sent to a Director and whose outcome the Gateway never learned, as the engine records it.
    private static CronRunRecord StartUnknown(DateTime firedUtc, string? name = RunName, string director = "dir-7") => new()
    {
        ScheduledUtc = firedUtc,
        FiredUtc = firedUtc,
        Machine = "SOREN_NORTH",
        TargetDirectorId = director,
        SessionId = null,
        InfraStatus = CronEngine.StartUnknownStatus,
        TaskStatus = CronRunHistoryStore.TaskStatusUnknown,
        Result = CronRunResults.StartUnknown,
        ResultReason = Unknown30,
        ResultUtc = firedUtc,
        SessionName = name,
    };

    [Fact]
    public void SettleUnknownStarts_TheDirectorReportsASessionOfTheRunsName_TheRunTakesIt_AndOwesItsReport()
    {
        var (service, runs, _) = Build();
        Job("cj_mail", "eric", "mail-desk");
        var fired = TorontoTime(2026, 10, 16, 9, 9);
        runs.Append("cj_mail", Linked("cj_mail", StartUnknown(fired)));
        // Phase 7: the Director answered the create eight minutes after it was sent.
        _now = fired.AddMinutes(8);
        _named[("dir-7", RunName)] = new() { "sid-eric" };

        var settled = service.SettleUnknownStarts(TenantId.Local);

        Assert.Equal(1, settled);
        var run = Assert.Single(runs.List("cj_mail"));
        Assert.Equal(("sid-eric", "dir-7", "started"), (run.SessionId, run.TargetDirectorId, run.InfraStatus));
        Assert.Equal(CronRunResults.Pending, run.Result);
        Assert.Null(run.ResultReason);
        Assert.Equal(RunName, run.SessionName);
        Assert.Equal(("cj_mail", fired, "started"), Assert.Single(_settled));
    }

    [Fact]
    public void Report_TheSessionOfARunWhoseStartWasNotKnownReportsOk_IsAccepted()
    {
        var (service, runs, _) = Build();
        Job("cj_mail", "eric", "mail-desk");
        var fired = TorontoTime(2026, 10, 16, 9, 9);
        runs.Append("cj_mail", Linked("cj_mail", StartUnknown(fired)));
        _now = fired.AddMinutes(20);
        _named[("dir-7", RunName)] = new() { "sid-eric" };

        var outcome = service.Report(TenantId.Local, "sid-eric", "ok", "inbox read, two replies drafted");

        Assert.Null(outcome.Refusal);
        var run = Assert.Single(runs.List("cj_mail"));
        Assert.Equal(("sid-eric", "started", CronRunResults.Ok), (run.SessionId, run.InfraStatus, run.Result));
    }

    [Fact]
    public void Report_ASessionNoScheduleStarted_IsStillRefused_WhenARunWhoseStartWasNotKnownWaitsForAnotherName()
    {
        var (service, runs, _) = Build();
        Job("cj_mail");
        var fired = TorontoTime(2026, 10, 16, 9, 9);
        runs.Append("cj_mail", StartUnknown(fired));
        _now = fired.AddMinutes(20);

        var outcome = service.Report(TenantId.Local, "sid-someone-else", "ok", null);

        Assert.Equal(404, outcome.StatusCode);
        Assert.Contains("was not started by a schedule", outcome.Refusal);
        Assert.Equal(CronRunResults.StartUnknown, Assert.Single(runs.List("cj_mail")).Result);
    }

    [Fact]
    public void SettleUnknownStarts_NoSessionOfThatNameByTheEndOfItsShift_TheRunDidNotRun_SayingSo()
    {
        var (service, runs, _) = Build();
        Job("cj_mail");
        var fired = TorontoTime(2026, 10, 16, 9, 9);
        runs.Append("cj_mail", StartUnknown(fired));

        _now = TorontoTime(2026, 10, 16, 15, 59);
        Assert.Equal(0, service.SettleUnknownStarts(TenantId.Local));
        Assert.Equal(CronRunResults.StartUnknown, Assert.Single(runs.List("cj_mail")).Result);

        _now = TorontoTime(2026, 10, 16, 16, 0);
        Assert.Equal(1, service.SettleUnknownStarts(TenantId.Local));

        var run = Assert.Single(runs.List("cj_mail"));
        Assert.Equal(("not-started", CronRunResults.Problem, CronRunProblems.DidNotRun), (run.InfraStatus, run.Result, run.Problem));
        Assert.Equal($"whether it started a session was not known, and no session named '{RunName}' appeared on its Director by 2026-10-16 20:00 UTC, when its window closed", run.ResultReason);
        Assert.Null(run.SessionId);
        Assert.Equal(("cj_mail", fired, "not-started"), Assert.Single(_settled));
    }

    [Fact]
    public void SettleUnknownStarts_ARunThatFiredJustBeforeItsShiftEnded_IsGivenThirtyMinutes()
    {
        var (service, runs, _) = Build();
        Job("cj_mail");
        var fired = TorontoTime(2026, 10, 16, 15, 50);
        runs.Append("cj_mail", StartUnknown(fired));

        _now = TorontoTime(2026, 10, 16, 16, 19);
        Assert.Equal(0, service.SettleUnknownStarts(TenantId.Local));

        _now = TorontoTime(2026, 10, 16, 16, 20);
        Assert.Equal(1, service.SettleUnknownStarts(TenantId.Local));
        Assert.Equal(CronRunProblems.DidNotRun, Assert.Single(runs.List("cj_mail")).Problem);
    }

    [Fact]
    public void SettleUnknownStarts_ASessionOfThatNameAlreadyAnotherRuns_IsNotTaken()
    {
        var (service, runs, _) = Build();
        Job("cj_mail");
        Job("cj_other");
        var fired = TorontoTime(2026, 10, 16, 9, 9);
        runs.Append("cj_other", Started("sid-eric", fired));
        runs.Append("cj_mail", StartUnknown(fired));
        _now = fired.AddMinutes(8);
        _named[("dir-7", RunName)] = new() { "sid-eric" };

        Assert.Equal(0, service.SettleUnknownStarts(TenantId.Local));

        var run = Assert.Single(runs.List("cj_mail"));
        Assert.Null(run.SessionId);
        Assert.Equal(CronRunResults.StartUnknown, run.Result);
        Assert.Empty(_settled);
    }

    [Fact]
    public void SettleUnknownStarts_ARunThatRecordedNoName_LapsesWhenItsWindowCloses_SayingItHadNoNameToLookFor()
    {
        var (service, runs, _) = Build();
        Job("cj_mail");
        var fired = TorontoTime(2026, 10, 16, 9, 9);
        runs.Append("cj_mail", StartUnknown(fired, name: null));
        _now = TorontoTime(2026, 10, 16, 16, 0);

        Assert.Equal(1, service.SettleUnknownStarts(TenantId.Local));

        Assert.Equal("whether it started a session was not known, and it recorded no session name to look for by 2026-10-16 20:00 UTC, when its window closed",
            Assert.Single(runs.List("cj_mail")).ResultReason);
    }

    // ---- the heal by name, follow-up review of 4073 -------------------------------------------------------------

    [Fact]
    public void SettleUnknownStarts_TwoRunsOfOneNameInOneMinute_EachTakesItsOwnSession_AndBothReportsAreAccepted()
    {
        var (service, runs, _) = Build();
        Job("cj_mail", "eric", "mail-desk");
        var fired = TorontoTime(2026, 10, 16, 9, 9);
        // The scheduled fire and a run now in the same minute: one name, two creates, two sessions on the Director.
        runs.Append("cj_mail", Linked("cj_mail", StartUnknown(fired)));
        runs.Append("cj_mail", Linked("cj_mail", StartUnknown(fired.AddSeconds(20))));
        _now = fired.AddMinutes(8);
        _named[("dir-7", RunName)] = new() { "sid-1", "sid-2" };

        Assert.Equal(2, service.SettleUnknownStarts(TenantId.Local));

        var sessions = runs.List("cj_mail").Select(r => (r.SessionId, r.Result)).OrderBy(r => r.SessionId).ToList();
        Assert.Equal(new (string?, string)[] { ("sid-1", CronRunResults.Pending), ("sid-2", CronRunResults.Pending) }, sessions);
        Assert.Null(service.Report(TenantId.Local, "sid-1", "ok", null).Refusal);
        Assert.Null(service.Report(TenantId.Local, "sid-2", "ok", null).Refusal);
    }

    [Fact]
    public void Report_TheSecondSessionOfOneName_HealsItsOwnRun_WhenTheFirstIsAlreadyTaken()
    {
        var (service, runs, _) = Build();
        Job("cj_mail", "eric", "mail-desk");
        var fired = TorontoTime(2026, 10, 16, 9, 9);
        runs.Append("cj_mail", Linked("cj_mail", Started("sid-1", fired)));
        runs.Append("cj_mail", Linked("cj_mail", StartUnknown(fired.AddSeconds(20))));
        _now = fired.AddMinutes(8);
        _named[("dir-7", RunName)] = new() { "sid-1", "sid-2" };

        var outcome = service.Report(TenantId.Local, "sid-2", "ok", null);

        Assert.Null(outcome.Refusal);
        Assert.Contains(runs.List("cj_mail"), r => r.SessionId == "sid-2" && r.Result == CronRunResults.Ok);
    }

    [Fact]
    public void SettleUnknownStarts_UNDEFINED_FACTORY_ASessionOfThatNameOnAnotherDirector_IsNotTaken_AndEachRunTakesItsOwnDirectorsSession()
    {
        var (service, runs, _) = Build();
        Job("cj_north");
        Job("cj_south");
        var fired = TorontoTime(2026, 10, 16, 9, 9);
        // Two schedules of one name on two machines, firing in the same minute.
        runs.Append("cj_north", StartUnknown(fired, director: "dir-north"));
        runs.Append("cj_south", StartUnknown(fired, director: "dir-south"));
        _now = fired.AddMinutes(8);
        _named[("dir-south", RunName)] = new() { "sid-south" };

        Assert.Equal(1, service.SettleUnknownStarts(TenantId.Local));
        Assert.Equal(("sid-south", "dir-south"), (Assert.Single(runs.List("cj_south")).SessionId, Assert.Single(runs.List("cj_south")).TargetDirectorId));
        var north = Assert.Single(runs.List("cj_north"));
        Assert.Equal((null, CronRunResults.StartUnknown), (north.SessionId, north.Result));

        _named[("dir-north", RunName)] = new() { "sid-north" };
        Assert.Equal(1, service.SettleUnknownStarts(TenantId.Local));
        Assert.Equal(("sid-north", "dir-north"), (Assert.Single(runs.List("cj_north")).SessionId, Assert.Single(runs.List("cj_north")).TargetDirectorId));
    }

    [Fact]
    public void SettleUnknownStarts_ARunThatRecordedNoDirector_LapsesWhenItsWindowCloses_SayingItHadNoDirectorToLookOn()
    {
        var (service, runs, _) = Build();
        Job("cj_mail");
        var fired = TorontoTime(2026, 10, 16, 9, 9);
        runs.Append("cj_mail", StartUnknown(fired, director: ""));
        _now = TorontoTime(2026, 10, 16, 16, 0);

        Assert.Equal(1, service.SettleUnknownStarts(TenantId.Local));

        Assert.Equal($"whether it started a session was not known, and it recorded no Director to look on for a session named '{RunName}' by 2026-10-16 20:00 UTC, when its window closed",
            Assert.Single(runs.List("cj_mail")).ResultReason);
    }

    [Fact]
    public void AdoptStartUnknown_ASessionAnotherRunAlreadyHolds_IsRefused_AndTheRunStaysUnknown()
    {
        var (_, runs, _) = Build();
        Job("cj_mail");
        Job("cj_other");
        var fired = TorontoTime(2026, 10, 16, 9, 9);
        runs.Append("cj_other", Started("sid-eric", fired));
        runs.Append("cj_mail", StartUnknown(fired));
        var runId = Guid.Parse(Assert.Single(runs.List("cj_mail")).RunId);

        Assert.Null(runs.AdoptStartUnknown(TenantId.Local, runId, "sid-eric", "dir-7"));

        var run = Assert.Single(runs.List("cj_mail"));
        Assert.Equal((null, CronRunResults.StartUnknown), (run.SessionId, run.Result));
    }
}
