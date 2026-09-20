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
/// A waiting row is only produced for a session the Gateway can SEE (#3124).
///
/// THIS IS THE FIX FOR A REAL, MEASURED FAILURE. On 20 September 2026 the owner's daily report named
/// 93 sessions waiting on him. Cross-referenced against the sessions that existed, NONE of them
/// matched - while 15 that really were waiting went unmentioned. The cause was here: a row was built
/// from the durable ledger whether or not the session could still be seen, and the two checks that
/// would have dismissed it (snoozed, exited) can only run on a session we CAN see. So an unseeable
/// session was reported as waiting purely because nothing contradicted it, under a name that was
/// really its identifier.
///
/// The owner's ruling, 20 September 2026: "Do not list it. Count it in one line at most."
/// </summary>
public sealed class MorningReportLostContactTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly DateTime Now = new(2026, 9, 20, 11, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private GatewayDatabase DbAs(TenantId t) => _h.Open(new FixedTenantContext(t));

    private static MorningReportWindow Window() => MorningReportWindow.Resolve("2026-09-19", "UTC");

    private static void SeedWaiting(GatewayDatabase db, TenantId tenant, string sessionId, DateTime at)
    {
        using var ctx = db.CreateContext(tenant);
        ctx.GovernanceEvents.Add(new GovernanceEventEntity
        {
            TenantId = tenant.Value,
            SubjectKind = GovernanceEventSubject.Session,
            SessionId = sessionId,
            State = GovernanceEventState.WaitingOnHuman,
            OccurredUtc = at,
            RecordedUtc = at,
        });
        ctx.SaveChanges();
    }

    private static PushedSessionStore Live(TenantId tenant, params SessionDto[] sessions)
    {
        var store = new PushedSessionStore(() => Now);
        store.RegisterConnection(tenant, "dir-1", "conn-1");
        Assert.True(store.ApplySnapshot(tenant, "dir-1", "conn-1", 0, sessions.ToList()));
        return store;
    }

    private MorningReportDto Build(GatewayDatabase db, PushedSessionStore? live) =>
        new MorningReportBuilder(db, live, TimeSpan.FromMinutes(5), () => Now)
            .Build("alice@example.com", Alice, Window());

    [Fact]
    public void A_session_the_gateway_cannot_see_is_NOT_listed()
    {
        var db = DbAs(Alice);
        SeedWaiting(db, Alice, "ghost", Now.AddHours(-9));

        var report = Build(db, Live(Alice));   // an empty roster: the Director is gone

        Assert.Empty(report.Attention);
    }

    [Fact]
    public void It_is_COUNTED_rather_than_silently_dropped()
    {
        var db = DbAs(Alice);
        SeedWaiting(db, Alice, "ghost-1", Now.AddHours(-9));
        SeedWaiting(db, Alice, "ghost-2", Now.AddHours(-4));

        var report = Build(db, Live(Alice));

        // "Count it in one line at most." A count is answerable; silence is not.
        Assert.Equal(2, report.LostContactCount);
    }

    [Fact]
    public void The_count_is_ABSENT_when_nothing_was_lost()
    {
        var db = DbAs(Alice);
        SeedWaiting(db, Alice, "s1", Now.AddHours(-3));

        var report = Build(db, Live(Alice, new SessionDto { SessionId = "s1", Name = "a real one" }));

        // A section with no data is absent here, never zero-filled - the rule the whole report follows.
        Assert.Single(report.Attention);
        Assert.Null(report.LostContactCount);
    }

    [Fact]
    public void THE_MEASURED_FAILURE_ghosts_no_longer_crowd_out_the_real_ones()
    {
        // The shape of 20 September: many ledger rows whose sessions are gone, and a couple that are
        // real. Before the fix the email carried the ghosts and not the real ones.
        var db = DbAs(Alice);
        for (var i = 0; i < 20; i++)
            SeedWaiting(db, Alice, "ghost-" + i, Now.AddHours(-10));
        SeedWaiting(db, Alice, "real-1", Now.AddHours(-2));
        SeedWaiting(db, Alice, "real-2", Now.AddHours(-1));

        var report = Build(db, Live(Alice,
            new SessionDto { SessionId = "real-1", Name = "One Repo List - Delivery Lead" },
            new SessionDto { SessionId = "real-2", Name = "Wingman Error - Delivery Lead" }));

        var names = report.Attention.Cast<WaitingSessionAttentionDto>().Select(i => i.Session).ToList();
        Assert.Equal(new[] { "One Repo List - Delivery Lead", "Wingman Error - Delivery Lead" }, names);
        Assert.Equal(20, report.LostContactCount);
    }

    [Fact]
    public void NO_ROW_EVER_CARRIES_AN_IDENTIFIER_WHERE_A_NAME_BELONGS()
    {
        // The guard that stops this coming back. Every row's display name must differ from its id -
        // an identifier in that field is what put 93 unreadable rows in front of the owner.
        var db = DbAs(Alice);
        SeedWaiting(db, Alice, "1c3174ba-cd90-4506-8639-2f1ed108f1c9", Now.AddHours(-5));
        SeedWaiting(db, Alice, "s2", Now.AddHours(-2));

        var report = Build(db, Live(Alice,
            new SessionDto { SessionId = "1c3174ba-cd90-4506-8639-2f1ed108f1c9", Name = "Named seat" },
            new SessionDto { SessionId = "s2", Name = "Another seat" }));

        Assert.NotEmpty(report.Attention);
        foreach (var item in report.Attention.Cast<WaitingSessionAttentionDto>())
        {
            Assert.NotEqual(item.SessionId, item.Session);
            Assert.False(Guid.TryParse(item.Session, out _), item.Session + " is an identifier, not a name");
        }
    }

    [Fact]
    public void A_snoozed_or_exited_session_is_still_dismissed_and_is_NOT_counted_as_lost()
    {
        var db = DbAs(Alice);
        SeedWaiting(db, Alice, "parked", Now.AddHours(-6));
        SeedWaiting(db, Alice, "gone", Now.AddHours(-6));

        var report = Build(db, Live(Alice,
            new SessionDto { SessionId = "parked", Name = "parked", HoldState = HoldStates.Held },
            new SessionDto { SessionId = "gone", Name = "gone", ActivityState = "Exited" }));

        // We CAN see both, so neither is lost contact - they are dismissed on what we can see.
        Assert.Empty(report.Attention);
        Assert.Null(report.LostContactCount);
    }

    [Fact]
    public void The_owner_filter_field_is_carried_so_the_reader_can_tell_whose_problem_it_is()
    {
        var db = DbAs(Alice);
        SeedWaiting(db, Alice, "lead", Now.AddHours(-3));
        SeedWaiting(db, Alice, "seat", Now.AddHours(-3));

        var report = Build(db, Live(Alice,
            new SessionDto { SessionId = "lead", Name = "Delivery Lead" },
            new SessionDto { SessionId = "seat", Name = "Developer", ControllerSessionId = "lead" }));

        var rows = report.Attention.Cast<WaitingSessionAttentionDto>().ToDictionary(r => r.SessionId);
        Assert.Null(rows["lead"].ControllerSessionId);      // nothing drives it - the owner's own problem
        Assert.Equal("lead", rows["seat"].ControllerSessionId);
    }
}
