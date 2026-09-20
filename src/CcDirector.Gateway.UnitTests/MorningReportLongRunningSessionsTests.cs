using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Reports;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The daily report's running-too-long row (#3124). The owner's rule, 20 September 2026: still working
/// after three hours AND at least ten times the account's own usual. An email that cries wolf stops being
/// read, so most of these tests are about when the row must say NOTHING.
/// </summary>
public sealed class MorningReportLongRunningSessionsTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly TenantId Bob = new("tenant-bob");
    private static readonly DateTime Now = new(2026, 9, 20, 11, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private GatewayDatabase Db => _h.Open(new AsyncLocalTenantContext());

    private static void SeedState(GatewayDatabase db, TenantId tenant, string sessionId, string state, DateTime at)
    {
        using var ctx = db.CreateContext(tenant);
        ctx.GovernanceEvents.Add(new GovernanceEventEntity
        {
            TenantId = tenant.Value,
            SubjectKind = GovernanceEventSubject.Session,
            SessionId = sessionId,
            State = state,
            OccurredUtc = at,
            RecordedUtc = at,
        });
        ctx.SaveChanges();
    }

    /// <summary><paramref name="count"/> finished working stretches of <paramref name="minutes"/> each, a week ago.</summary>
    private static void SeedHistory(GatewayDatabase db, TenantId tenant, int count, double minutes)
    {
        using var ctx = db.CreateContext(tenant);
        for (var i = 0; i < count; i++)
        {
            var start = Now.AddDays(-7).AddHours(i);
            foreach (var (state, at) in new[] { (GovernanceEventState.Active, start), (GovernanceEventState.Idle, start.AddMinutes(minutes)) })
            {
                ctx.GovernanceEvents.Add(new GovernanceEventEntity
                {
                    TenantId = tenant.Value,
                    SubjectKind = GovernanceEventSubject.Session,
                    SessionId = $"history-{i}",
                    State = state,
                    OccurredUtc = at,
                    RecordedUtc = at,
                });
            }
        }
        ctx.SaveChanges();
    }

    private static PushedSessionStore Live(TenantId tenant, params (string Id, string Name, string State)[] sessions)
    {
        var store = new PushedSessionStore(() => Now);
        store.RegisterConnection(tenant, "dir-1", "conn-1");
        store.ApplySnapshot(tenant, "dir-1", "conn-1", 1,
            sessions.Select(s => new SessionDto { SessionId = s.Id, Name = s.Name, ActivityState = s.State }).ToList());
        return store;
    }

    private static LongRunningSessionsAttentionDto? Row(GatewayDatabase db, TenantId tenant, PushedSessionStore? live) =>
        new MorningReportBuilder(db, live, TimeSpan.FromMinutes(5), () => Now)
            .Build("someone@example.com", tenant, MorningReportWindow.Resolve("2026-09-19", "UTC"))
            .Attention.OfType<LongRunningSessionsAttentionDto>().SingleOrDefault();

    [Fact]
    public void A_session_working_far_longer_than_this_accounts_usual_is_named_with_how_long_and_the_usual()
    {
        var db = Db;
        SeedHistory(db, Alice, count: 20, minutes: 6);
        SeedState(db, Alice, "s-stuck", GovernanceEventState.Active, Now.AddHours(-9));

        var row = Row(db, Alice, Live(Alice, ("s-stuck", "website - Developer", "Working")));

        Assert.NotNull(row);
        Assert.Equal("long-running-sessions", row!.Type);
        Assert.Equal(6, row.UsualMinutes);
        var s = Assert.Single(row.Sessions);
        Assert.Equal("website - Developer", s.Session);
        Assert.Equal(9, s.RunningHours);
    }

    [Fact]
    public void Under_three_hours_is_never_flagged_however_short_the_usual()
    {
        var db = Db;
        SeedHistory(db, Alice, count: 20, minutes: 1); // ten times the usual is ten minutes
        SeedState(db, Alice, "s-build", GovernanceEventState.Active, Now.AddHours(-2.9));

        Assert.Null(Row(db, Alice, Live(Alice, ("s-build", "build", "Working"))));
    }

    [Fact]
    public void Somebody_whose_sessions_routinely_run_for_hours_is_not_nagged()
    {
        var db = Db;
        SeedHistory(db, Alice, count: 20, minutes: 45); // ten times the usual is seven and a half hours
        SeedState(db, Alice, "s-long", GovernanceEventState.Active, Now.AddHours(-5));
        SeedState(db, Alice, "s-longer", GovernanceEventState.Active, Now.AddHours(-8));

        var row = Row(db, Alice, Live(Alice, ("s-long", "five hours", "Working"), ("s-longer", "eight hours", "Working")));

        Assert.Equal(new[] { "eight hours" }, row!.Sessions.Select(s => s.Session));
    }

    [Fact]
    public void Too_little_history_to_know_the_usual_says_nothing_rather_than_guess()
    {
        var db = Db;
        SeedHistory(db, Alice, count: MorningReportBuilder.LongRunningMinimumHistory - 1, minutes: 6);
        SeedState(db, Alice, "s-stuck", GovernanceEventState.Active, Now.AddHours(-9));
        var live = Live(Alice, ("s-stuck", "stuck", "Working"));

        Assert.Null(Row(db, Alice, live));

        // Positive control: one more finished stretch and the same session IS reported, so the absence above
        // is the missing history and nothing else.
        SeedHistory(db, Bob, count: 0, minutes: 0);
        SeedState(db, Alice, "history-extra", GovernanceEventState.Active, Now.AddDays(-3));
        SeedState(db, Alice, "history-extra", GovernanceEventState.Idle, Now.AddDays(-3).AddMinutes(6));
        Assert.NotNull(Row(db, Alice, live));
    }

    [Fact]
    public void A_session_the_ledger_calls_active_but_the_Gateway_cannot_see_working_is_not_claimed_to_be_running()
    {
        // The emitter writes nothing when an active session simply vanishes - the lid was shut - so the ledger
        // says "active" for ever. Only a session seen Working right now is reported.
        var db = Db;
        SeedHistory(db, Alice, count: 20, minutes: 6);
        SeedState(db, Alice, "s-vanished", GovernanceEventState.Active, Now.AddHours(-9));
        SeedState(db, Alice, "s-idle-now", GovernanceEventState.Active, Now.AddHours(-9));
        SeedState(db, Alice, "s-real", GovernanceEventState.Active, Now.AddHours(-9));

        var row = Row(db, Alice, Live(Alice, ("s-idle-now", "idle now", "Idle"), ("s-real", "real", "Working")));
        Assert.Equal(new[] { "real" }, row!.Sessions.Select(s => s.Session));

        Assert.Null(Row(db, Alice, live: null));
    }

    [Fact]
    public void A_session_that_stopped_is_not_running_too_long()
    {
        var db = Db;
        SeedHistory(db, Alice, count: 20, minutes: 6);
        SeedState(db, Alice, "s-done", GovernanceEventState.Active, Now.AddHours(-9));
        SeedState(db, Alice, "s-done", GovernanceEventState.WaitingOnHuman, Now.AddHours(-1));

        // Live still says Working (a stale roster): the ledger's later transition wins, there is no open stretch.
        Assert.Null(Row(db, Alice, Live(Alice, ("s-done", "done", "Working"))));
    }

    [Fact]
    public void The_usual_is_the_MEDIAN_so_one_marathon_does_not_move_it()
    {
        var db = Db;
        SeedHistory(db, Alice, count: 20, minutes: 6);
        SeedState(db, Alice, "history-marathon", GovernanceEventState.Active, Now.AddDays(-2));
        SeedState(db, Alice, "history-marathon", GovernanceEventState.Idle, Now.AddDays(-2).AddHours(30));
        SeedState(db, Alice, "s-stuck", GovernanceEventState.Active, Now.AddHours(-4));

        var row = Row(db, Alice, Live(Alice, ("s-stuck", "stuck", "Working")));

        Assert.Equal(6, row!.UsualMinutes); // a mean would be about 91 minutes, and ten times that would hide this session
        Assert.Single(row.Sessions);
    }

    [Fact]
    public void One_accounts_usual_and_sessions_never_reach_anothers_row()
    {
        var db = Db;
        SeedHistory(db, Alice, count: 20, minutes: 6);
        SeedState(db, Alice, "s-alice", GovernanceEventState.Active, Now.AddHours(-9));
        var live = Live(Alice, ("s-alice", "alice", "Working"));

        Assert.NotNull(Row(db, Alice, live));
        Assert.Null(Row(db, Bob, live));
    }

    [Fact]
    public void On_the_wire_the_row_is_camelCase_with_its_type()
    {
        var db = Db;
        SeedHistory(db, Alice, count: 20, minutes: 6);
        SeedState(db, Alice, "s-stuck", GovernanceEventState.Active, Now.AddHours(-9));

        var json = System.Text.Json.JsonSerializer.Serialize<MorningAttentionItemDto>(
            Row(db, Alice, Live(Alice, ("s-stuck", "stuck", "Working")))!,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal("{\"type\":\"long-running-sessions\",\"usualMinutes\":6,\"sessions\":[{\"session\":\"stuck\",\"runningHours\":9}]}", json);
    }
}
