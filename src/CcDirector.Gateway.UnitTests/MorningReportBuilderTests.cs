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
/// The morning report's assembly (issue #2119). The claims under test are the ones an EMAIL depends on:
///
///  - NO YESTERDAY-STATS AT ALL, BY OWNER RULING (2026-09-20, issue #3124): the daily report answers WHAT
///    NEEDS YOU TODAY, and the scoreboard moved to a future weekly personal-stats surface. The stats tests
///    went with the stats.
///  - The waiting-session and hygiene rows are honest: a feed the Gateway has not heard from says nothing
///    rather than something plausible (the silence bar, the lookback, the repo-state freshness).
///  - THE WINDOW BOUNDS: inclusive start, EXCLUSIVE end, so a day's last event is not counted twice.
///  - TENANT ISOLATION: one account's report can never contain another account's rows.
/// </summary>
public sealed class MorningReportBuilderTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly TenantId Bob = new("tenant-bob");

    /// <summary>Noon UTC on the day the report is built - safely after the reported window closes.</summary>
    private static readonly DateTime Now = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The reported day: 23 July 2026 in Toronto = 2026-07-23T04:00Z .. 2026-07-24T04:00Z.</summary>
    private static MorningReportWindow Window() => MorningReportWindow.Resolve("2026-07-23", "America/Toronto");

    public void Dispose() => _h.Dispose();

    /// <summary>A database whose AMBIENT tenant is <paramref name="tenant"/> - what the seeding stores write as.</summary>
    private GatewayDatabase DbAs(TenantId tenant) => _h.Open(new FixedTenantContext(tenant));

    private MorningReportBuilder NewBuilder(GatewayDatabase db, PushedSessionStore? sessions = null) =>
        new(db, sessions, TimeSpan.FromMinutes(5), () => Now);

    // ---- seeding ---------------------------------------------------------------------------------------

    /// <summary>A live roster for <paramref name="tenant"/>. A waiting row is only produced for a session
    /// the Gateway can SEE (#3124), so most tests here need one.</summary>
    private static PushedSessionStore Live(TenantId tenant, params SessionDto[] sessions)
    {
        var store = new PushedSessionStore(() => Now);
        store.RegisterConnection(tenant, "dir-1", "conn-1");
        Assert.True(store.ApplySnapshot(tenant, "dir-1", "conn-1", 0, sessions.ToList()));
        return store;
    }

    private static void SeedSessionEvent(GatewayDatabase db, TenantId tenant, string sessionId, string state, DateTime occurredUtc)
    {
        using var ctx = db.CreateContext(tenant);
        ctx.GovernanceEvents.Add(new GovernanceEventEntity
        {
            TenantId = tenant.Value,
            SubjectKind = GovernanceEventSubject.Session,
            SessionId = sessionId,
            State = state,
            OccurredUtc = occurredUtc,
            RecordedUtc = occurredUtc,
        });
        ctx.SaveChanges();
    }

    // ---- the waiting-session attention rows -------------------------------------------------------------

    [Fact]
    public void A_session_whose_LAST_recorded_state_is_waiting_is_reported_with_a_real_age()
    {
        var db = DbAs(Alice);
        var waitingSince = Now.AddHours(-6);
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.Active, waitingSince.AddHours(-1));
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.WaitingOnHuman, waitingSince);

        var live = Live(Alice, new SessionDto { SessionId = "s1", Name = "Email - Developer" });
        var report = NewBuilder(db, live).Build("alice@example.com", Alice, Window());

        var item = Assert.IsType<WaitingSessionAttentionDto>(Assert.Single(report.Attention));
        Assert.Equal(MorningAttentionTypes.WaitingSession, item.Type);
        Assert.Equal(waitingSince, item.WaitingSinceUtc);
        Assert.Equal(6.0, item.AgeHours);
        // THE AGE STILL COMES FROM THE LEDGER, never from the live roster - the roster only labels.
        Assert.Equal("Email - Developer", item.Session);
        Assert.Equal("s1", item.SessionId);
    }

    [Fact]
    public void A_session_that_came_BACK_is_not_reported_as_waiting()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.WaitingOnHuman, Now.AddHours(-6));
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.Active, Now.AddHours(-2));

        var report = NewBuilder(db).Build("alice@example.com", Alice, Window());

        // The ledger's LAST word wins. A report that read any waiting event ever recorded would nag the
        // owner every morning about work they finished weeks ago.
        Assert.Empty(report.Attention);
    }

    [Fact]
    public void Waiting_rows_are_ordered_longest_wait_first()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "recent", GovernanceEventState.WaitingOnHuman, Now.AddHours(-2));
        SeedSessionEvent(db, Alice, "oldest", GovernanceEventState.WaitingOnHuman, Now.AddHours(-30));
        SeedSessionEvent(db, Alice, "middle", GovernanceEventState.WaitingOnPermission, Now.AddHours(-9));

        var live = Live(Alice,
            new SessionDto { SessionId = "recent", Name = "recent" },
            new SessionDto { SessionId = "oldest", Name = "oldest" },
            new SessionDto { SessionId = "middle", Name = "middle" });
        var report = NewBuilder(db, live).Build("alice@example.com", Alice, Window());

        var names = report.Attention.Cast<WaitingSessionAttentionDto>().Select(i => i.Session).ToList();
        Assert.Equal(new[] { "oldest", "middle", "recent" }, names);
    }

    [Fact]
    public void A_live_roster_supplies_the_friendly_name_and_the_repository_path()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.WaitingOnHuman, Now.AddHours(-3));

        var sessions = new PushedSessionStore(() => Now);
        sessions.RegisterConnection(Alice, "dir-1", "conn-1");
        Assert.True(sessions.ApplySnapshot(Alice, "dir-1", "conn-1", 0, new List<SessionDto>
        {
            new() { SessionId = "s1", Name = "Morning report - Developer", RepoName = "devthrottle",
                    RepoPath = "D:/ReposFred/devthrottle", MachineName = "SOREN_NORTH", Number = 131,
                    ControllerSessionId = "lead-9", ActivityState = "WaitingForInput" },
        }));

        var report = NewBuilder(db, sessions).Build("alice@example.com", Alice, Window());

        var item = Assert.IsType<WaitingSessionAttentionDto>(Assert.Single(report.Attention));
        Assert.Equal("Morning report - Developer", item.Session);
        // THE SHORT NAME, NOT THE PATH. "D:/ReposFred/devthrottle" in a sentence about a session is
        // noise; the reader knows their repositories by name.
        Assert.Equal("devthrottle", item.Repo);
        Assert.Equal("SOREN_NORTH", item.Machine);
        Assert.Equal(131, item.Number);
        Assert.Equal("lead-9", item.ControllerSessionId);
        // The AGE still comes from the durable ledger, never from the live roster.
        Assert.Equal(3.0, item.AgeHours);
    }

    [Fact]
    public void A_session_the_owner_has_SNOOZED_is_not_this_mornings_problem()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.WaitingOnHuman, Now.AddHours(-3));

        var sessions = new PushedSessionStore(() => Now);
        sessions.RegisterConnection(Alice, "dir-1", "conn-1");
        Assert.True(sessions.ApplySnapshot(Alice, "dir-1", "conn-1", 0, new List<SessionDto>
        {
            new() { SessionId = "s1", Name = "parked", HoldState = HoldStates.Held },
        }));

        var report = NewBuilder(db, sessions).Build("alice@example.com", Alice, Window());

        // The owner deliberately parked it. Putting it in the 7am email defeats the snooze.
        Assert.Empty(report.Attention);
    }

    [Fact]
    public void A_session_that_has_EXITED_is_not_waiting_on_anybody()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.WaitingOnHuman, Now.AddHours(-3));

        var sessions = new PushedSessionStore(() => Now);
        sessions.RegisterConnection(Alice, "dir-1", "conn-1");
        Assert.True(sessions.ApplySnapshot(Alice, "dir-1", "conn-1", 0, new List<SessionDto>
        {
            new() { SessionId = "s1", Name = "gone", ActivityState = "Exited" },
        }));

        var report = NewBuilder(db, sessions).Build("alice@example.com", Alice, Window());

        Assert.Empty(report.Attention);
    }

    [Fact]
    public void An_open_wait_older_than_the_silence_bar_is_not_reported()
    {
        // The defect the first real hosted call exposed: nine rows aged 69-129 hours, every one a true
        // statement about the ledger and a false statement about the owner's morning. A session that stops
        // without an exit event leaves its wait open forever, so the age grows without bound and never
        // resolves. After the bar the Gateway says nothing rather than something plausible.
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "long-gone", GovernanceEventState.WaitingOnHuman,
            Now - MorningReportBuilder.WaitingReportMaxAge - TimeSpan.FromHours(1));

        var report = NewBuilder(db).Build("alice@example.com", Alice, Window());

        Assert.Empty(report.Attention);
    }

    [Fact]
    public void A_wait_just_INSIDE_the_silence_bar_is_still_reported()
    {
        // The bar has two failure directions. Set it wrong the other way and every genuine multi-day wait
        // vanishes from the email, which is the same silence dressed as tidiness.
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "still-waiting", GovernanceEventState.WaitingOnHuman,
            Now - MorningReportBuilder.WaitingReportMaxAge + TimeSpan.FromHours(1));

        var live = Live(Alice, new SessionDto { SessionId = "still-waiting", Name = "still-waiting" });
        var report = NewBuilder(db, live).Build("alice@example.com", Alice, Window());

        var item = Assert.IsType<WaitingSessionAttentionDto>(Assert.Single(report.Attention));
        Assert.Equal("still-waiting", item.Session);
    }

    [Fact]
    public void A_session_older_than_the_lookback_horizon_is_not_reported()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "ancient", GovernanceEventState.WaitingOnHuman,
            Now.AddDays(-(MorningReportBuilder.WaitingLookbackDays + 1)));

        var report = NewBuilder(db).Build("alice@example.com", Alice, Window());

        // The Gateway will not assert the CURRENT state of something it has not heard about in a month.
        Assert.Empty(report.Attention);
    }

    // ---- the hygiene sections are absent until the repo-state feed exists -------------------------------

    [Fact]
    public void No_hygiene_items_are_emitted_while_there_is_no_repo_state_store()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.WaitingOnHuman, Now.AddHours(-3));

        var report = NewBuilder(db).Build("alice@example.com", Alice, Window());

        // This is what makes this slice mergeable and shippable BEFORE the snapshot feed lands: no
        // repo-state data means no stale-worktree / unmerged-branch rows at all - not empty ones.
        Assert.DoesNotContain(report.Attention, i => i.Type == MorningAttentionTypes.StaleWorktrees);
        Assert.DoesNotContain(report.Attention, i => i.Type == MorningAttentionTypes.UnmergedBranches);
    }

    // ---- tenant isolation ------------------------------------------------------------------------------

    [Fact]
    public void One_accounts_report_never_contains_another_accounts_rows()
    {
        // Two tenants, ONE database file - exactly the hosted shape.
        var aliceDb = DbAs(Alice);
        var bobDb = DbAs(Bob);
        var inWindow = new DateTime(2026, 7, 23, 15, 0, 0, DateTimeKind.Utc);

        SeedSessionEvent(aliceDb, Alice, "alice-1", GovernanceEventState.Active, inWindow);
        SeedSessionEvent(aliceDb, Alice, "alice-2", GovernanceEventState.WaitingOnHuman, Now.AddHours(-4));

        SeedSessionEvent(bobDb, Bob, "bob-1", GovernanceEventState.Active, inWindow);
        SeedSessionEvent(bobDb, Bob, "bob-3", GovernanceEventState.WaitingOnHuman, Now.AddHours(-40));

        // Each tenant's own live roster, so each has a row to be isolated in the first place.
        var live = new PushedSessionStore(() => Now);
        live.RegisterConnection(Alice, "dir-a", "conn-a");
        Assert.True(live.ApplySnapshot(Alice, "dir-a", "conn-a", 0,
            new List<SessionDto> { new() { SessionId = "alice-2", Name = "alice-2" } }));
        live.RegisterConnection(Bob, "dir-b", "conn-b");
        Assert.True(live.ApplySnapshot(Bob, "dir-b", "conn-b", 0,
            new List<SessionDto> { new() { SessionId = "bob-3", Name = "bob-3" } }));

        // Build BOTH reports through the SAME builder instance, to prove the tenant argument - not some
        // remembered ambient state - is what scopes the read.
        var builder = NewBuilder(aliceDb, live);
        var aliceReport = builder.Build("alice@example.com", Alice, Window());
        var bobReport = builder.Build("bob@example.com", Bob, Window());

        // alice-2 has been waiting since after the reported day closed; bob-3 since before it - each
        // report carries exactly its own tenant's wait and never the other's.
        var aliceWaiting = Assert.IsType<WaitingSessionAttentionDto>(Assert.Single(aliceReport.Attention));
        Assert.Equal("alice-2", aliceWaiting.Session);

        var bobWaiting = Assert.IsType<WaitingSessionAttentionDto>(Assert.Single(bobReport.Attention));
        Assert.Equal("bob-3", bobWaiting.Session);
    }

    [Fact]
    public void A_tenant_with_no_rows_of_its_own_reports_ABSENT_even_when_another_tenant_has_plenty()
    {
        var aliceDb = DbAs(Alice);
        var inWindow = new DateTime(2026, 7, 23, 15, 0, 0, DateTimeKind.Utc);
        SeedSessionEvent(aliceDb, Alice, "alice-1", GovernanceEventState.Active, inWindow);
        SeedSessionEvent(aliceDb, Alice, "alice-2", GovernanceEventState.WaitingOnHuman, Now.AddHours(-2));

        // Alice must genuinely HAVE a row for this to test anything. Without a live roster she has none
        // either, and "Bob's report is empty" would pass because nobody has rows - a test that proves
        // isolation by proving there is nothing to isolate.
        var live = Live(Alice, new SessionDto { SessionId = "alice-2", Name = "alice-2" });
        Assert.NotEmpty(NewBuilder(aliceDb, live).Build("alice@example.com", Alice, Window()).Attention);

        var bobReport = NewBuilder(aliceDb, live).Build("bob@example.com", Bob, Window());

        // Every read the report makes must itself be tenant-scoped. If it were not, Bob would get Alice's
        // waiting rows in his email - a claim made entirely out of Alice's data.
        Assert.Empty(bobReport.Attention);
    }

    [Fact]
    public void A_live_roster_from_another_tenant_never_labels_this_tenants_row()
    {
        var db = DbAs(Alice);
        SeedSessionEvent(db, Alice, "shared-id", GovernanceEventState.WaitingOnHuman, Now.AddHours(-3));

        // Bob happens to run a session with the SAME raw id - a collision the tenant partition must absorb.
        var sessions = new PushedSessionStore(() => Now);
        sessions.RegisterConnection(Bob, "dir-bob", "conn-bob");
        Assert.True(sessions.ApplySnapshot(Bob, "dir-bob", "conn-bob", 0, new List<SessionDto>
        {
            new() { SessionId = "shared-id", Name = "BOB'S SECRET PROJECT", RepoPath = "D:/bob/private" },
        }));

        var report = NewBuilder(db, sessions).Build("alice@example.com", Alice, Window());

        // STRONGER THAN IT USED TO BE. This once asserted the row appeared bearing the raw id rather
        // than Bob's name. Now an unseeable session produces NO row at all, so Bob's label cannot leak
        // even in principle - and the count says one wait was dropped rather than hiding it.
        Assert.Empty(report.Attention);
        Assert.Equal(1, report.LostContactCount);
    }

    // ---- argument discipline ---------------------------------------------------------------------------

    [Fact]
    public void An_invalid_tenant_is_refused_rather_than_read_as_something()
    {
        var db = DbAs(Alice);
        Assert.Throws<ArgumentException>(() => NewBuilder(db).Build("a@b.c", default, Window()));
    }
}
