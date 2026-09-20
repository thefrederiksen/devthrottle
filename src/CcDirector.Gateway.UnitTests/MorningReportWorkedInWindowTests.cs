using System;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Reports;
using CcDirector.Gateway.Supervision;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Whether the account did anything in the reported day (#3124). The owner's rule, 20 September 2026:
/// "If you didn't work in DevThrottle yesterday, I don't think we should send a daily email."
///
/// This flag decides whether somebody gets mail at all, so the tests here are mostly about the two ways
/// it can be wrong: saying no on a day they worked, which silently stops their report, and saying yes
/// because of something that happened on a different day or in a different account.
/// </summary>
public sealed class MorningReportWorkedInWindowTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly TenantId Bob = new("tenant-bob");
    private static readonly DateTime Now = new(2026, 9, 20, 11, 0, 0, DateTimeKind.Utc);

    // The reported day is 19 September, UTC: 2026-09-19T00:00Z inclusive to 2026-09-20T00:00Z exclusive.
    private static readonly MorningReportWindow Yesterday = MorningReportWindow.Resolve("2026-09-19", "UTC");

    public void Dispose() => _h.Dispose();

    private GatewayDatabase Db => _h.Open(new AsyncLocalTenantContext());

    private static void SeedTurn(GatewayDatabase db, TenantId tenant, DateTime at,
        string eventType = ActivityEventTypes.TurnSubmitted)
    {
        using var ctx = db.CreateContext(tenant);
        ctx.ActivityEvents.Add(new ActivityEventEntity
        {
            TenantId = tenant.Value,
            EventId = Guid.NewGuid(),
            OccurredUtc = at,
            RecordedUtc = at,
            DirectorId = "dir-1",
            SessionId = "s-1",
            EventType = eventType,
        });
        ctx.SaveChanges();
    }

    private static void SeedSessionState(GatewayDatabase db, TenantId tenant, string state, DateTime at)
    {
        using var ctx = db.CreateContext(tenant);
        ctx.GovernanceEvents.Add(new GovernanceEventEntity
        {
            TenantId = tenant.Value,
            SubjectKind = GovernanceEventSubject.Session,
            SessionId = "s-1",
            State = state,
            OccurredUtc = at,
            RecordedUtc = at,
        });
        ctx.SaveChanges();
    }

    private static bool Worked(GatewayDatabase db, TenantId tenant, MorningReportWindow? window = null) =>
        new MorningReportBuilder(db, utcNow: () => Now)
            .Build("someone@example.com", tenant, window ?? Yesterday)
            .WorkedInWindow;

    [Fact]
    public void A_day_with_no_trace_of_the_account_at_all_did_not_work()
    {
        Assert.False(Worked(Db, Alice));
    }

    [Fact]
    public void A_turn_submitted_in_the_window_is_work()
    {
        var db = Db;
        SeedTurn(db, Alice, Yesterday.StartUtc.AddHours(14));
        Assert.True(Worked(db, Alice));
    }

    [Fact]
    public void A_session_that_became_active_in_the_window_is_work_even_with_no_turn_recorded()
    {
        // The hosted case: a Director mid-reconnect produces state but no turn feed. Reporting "you did
        // nothing" for that day would stop the email of somebody who worked all day.
        var db = Db;
        SeedSessionState(db, Alice, GovernanceEventState.Active, Yesterday.StartUtc.AddHours(9));
        Assert.True(Worked(db, Alice));
    }

    [Fact]
    public void Work_on_a_different_day_is_not_work_on_this_one()
    {
        var db = Db;
        // The instant before the window opens, and the instant it closes. Both are other days.
        SeedTurn(db, Alice, Yesterday.StartUtc.AddTicks(-1));
        SeedTurn(db, Alice, Yesterday.EndUtc);
        SeedSessionState(db, Alice, GovernanceEventState.Active, Yesterday.StartUtc.AddDays(-3));

        Assert.False(Worked(db, Alice));

        // Positive control: one turn INSIDE the window flips it, so the falses above are the window
        // doing its job and not the seeding failing silently.
        SeedTurn(db, Alice, Yesterday.StartUtc);
        Assert.True(Worked(db, Alice));
    }

    [Fact]
    public void A_session_merely_sitting_in_some_other_state_is_not_work()
    {
        // Idle and waiting-on-human transitions happen to a session nobody is driving. Only becoming
        // ACTIVE says something ran.
        var db = Db;
        SeedSessionState(db, Alice, GovernanceEventState.Idle, Yesterday.StartUtc.AddHours(3));
        SeedSessionState(db, Alice, GovernanceEventState.WaitingOnHuman, Yesterday.StartUtc.AddHours(4));

        Assert.False(Worked(db, Alice));
    }

    [Fact]
    public void An_activity_record_that_is_not_a_submitted_turn_is_not_work()
    {
        var db = Db;
        SeedTurn(db, Alice, Yesterday.StartUtc.AddHours(2), ActivityEventTypes.SupervisorWaiting);
        SeedTurn(db, Alice, Yesterday.StartUtc.AddHours(3), ActivityEventTypes.SnoozeCreated);

        Assert.False(Worked(db, Alice));
    }

    [Fact]
    public void One_accounts_work_never_counts_as_anothers()
    {
        var db = Db;
        SeedTurn(db, Bob, Yesterday.StartUtc.AddHours(10));

        Assert.True(Worked(db, Bob));
        Assert.False(Worked(db, Alice));
    }

    [Fact]
    public void The_window_is_the_readers_own_day_not_UTCs()
    {
        // 2026-09-19 in Sydney starts at 2026-09-18T14:00Z. A turn at 15:00Z on the 18th is inside the
        // reader's 19th and outside UTC's, so the two windows must disagree about this one turn.
        var db = Db;
        SeedTurn(db, Alice, new DateTime(2026, 9, 18, 15, 0, 0, DateTimeKind.Utc));

        Assert.True(Worked(db, Alice, MorningReportWindow.Resolve("2026-09-19", "Australia/Sydney")));
        Assert.False(Worked(db, Alice, MorningReportWindow.Resolve("2026-09-19", "UTC")));
    }

    [Fact]
    public void On_the_wire_it_is_a_boolean_and_never_a_count()
    {
        // The owner ruled the daily is a briefing and not a scoreboard, and the stats key was removed
        // from this contract because of it. This flag exists to decide whether to SEND. If it ever
        // becomes a number it is that key returning under a new name.
        var db = Db;
        SeedTurn(db, Alice, Yesterday.StartUtc.AddHours(1));
        SeedTurn(db, Alice, Yesterday.StartUtc.AddHours(2));
        SeedTurn(db, Alice, Yesterday.StartUtc.AddHours(3));

        var json = System.Text.Json.JsonSerializer.Serialize(
            new MorningReportBuilder(db, utcNow: () => Now).Build("a@b.com", Alice, Yesterday),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"workedInWindow\":true", json);
        Assert.DoesNotContain("\"stats\"", json);
    }
}
