using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Reports;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Supervision;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The daily report's usage-limit row (#3124). The row is a claim about a person's work - "this session
/// stopped because your plan ran out, and it is still stopped" - so each test pins one way that claim could
/// be false: the stop was something else, the session already resumed, the session is closed, the stop is
/// old, or it is somebody else's session.
///
/// EVERY TEST NOW SEEDS A LIVE ROSTER, and that is the point of the 20 September change rather than an
/// incidental tidy-up. The row used to print a session's identifier whenever the Gateway could not see it
/// live, and these tests seeded no roster at all - so they asserted the identifier and called it a name.
/// The owner's own report carried six raw identifiers under "6 sessions stopped on a usage limit", which is
/// precisely what he said was worthless: he is asked to go and resume each one, and an identifier is not
/// something a person can search for or click. A stop we cannot name is counted now, never listed.
/// </summary>
public sealed class MorningReportUsageLimitStopsTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly TenantId Bob = new("tenant-bob");
    private static readonly DateTime Now = new(2026, 9, 20, 11, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private GatewayDatabase Db => _h.Open(new AsyncLocalTenantContext());

    private static void SeedFault(GatewayDatabase db, TenantId tenant, string sessionId, string detail, double hoursAgo,
        string eventType = ActivityEventTypes.SupervisorFaultDetected, string cause = ActivityCauses.NonRecoverable)
    {
        using var ctx = db.CreateContext(tenant);
        var at = Now.AddHours(-hoursAgo);
        ctx.ActivityEvents.Add(new ActivityEventEntity
        {
            TenantId = tenant.Value,
            EventId = Guid.NewGuid(),
            OccurredUtc = at,
            RecordedUtc = at,
            DirectorId = "gateway",
            SessionId = sessionId,
            EventType = eventType,
            Cause = cause,
            Detail = detail,
        });
        ctx.SaveChanges();
    }

    private static void SeedState(GatewayDatabase db, TenantId tenant, string sessionId, string state, double hoursAgo)
    {
        using var ctx = db.CreateContext(tenant);
        var at = Now.AddHours(-hoursAgo);
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

    /// <summary>A Director connected for this account, pushing exactly these sessions.</summary>
    private static PushedSessionStore Live(TenantId tenant, params SessionDto[] sessions)
    {
        var store = new PushedSessionStore(() => Now);
        store.RegisterConnection(tenant, "dir-1", "conn-1");
        Assert.True(store.ApplySnapshot(tenant, "dir-1", "conn-1", 0, sessions.ToList()));
        return store;
    }

    /// <summary>Sessions named "s-0", "s-1", ... as the Gateway would see them live.</summary>
    private static SessionDto[] Named(params string[] ids) =>
        ids.Select((id, i) => new SessionDto { SessionId = id, Name = $"Session {id}", Number = 100 + i }).ToArray();

    private static UsageLimitStopsAttentionDto? Row(GatewayDatabase db, TenantId tenant, PushedSessionStore? live = null) =>
        new MorningReportBuilder(db, live, utcNow: () => Now)
            .Build("someone@example.com", tenant, MorningReportWindow.Resolve("2026-09-19", "UTC"))
            .Attention.OfType<UsageLimitStopsAttentionDto>().SingleOrDefault();

    [Fact]
    public void Every_usage_limit_block_the_classifier_knows_is_reported_and_in_the_order_they_stopped()
    {
        var db = Db;
        // The detail is written exactly as the supervisor writes it: "signature=" + the classifier's own string.
        var i = 0;
        foreach (var signature in TerminatingFaultClassifier.UsageLimitSignatures)
            SeedFault(db, Alice, $"s-{i}", $"signature={signature}", hoursAgo: 10 - i++);

        var row = Row(db, Alice, Live(Alice, Named("s-0", "s-1", "s-2")));

        Assert.NotNull(row);
        Assert.Equal("usage-limit-stops", row!.Type);
        Assert.Equal(3, TerminatingFaultClassifier.UsageLimitSignatures.Count);
        Assert.Equal(new[] { "Session s-0", "Session s-1", "Session s-2" }, row.Sessions.Select(s => s.Session));
        Assert.Equal(new[] { "s-0", "s-1", "s-2" }, row.Sessions.Select(s => s.SessionId));
        Assert.Equal(Now.AddHours(-10), row.Sessions[0].StoppedUtc);
        Assert.Null(row.LostContactCount);
    }

    [Fact]
    public void NO_ROW_EVER_CARRIES_AN_IDENTIFIER_WHERE_A_NAME_BELONGS()
    {
        // THE MEASURED FAILURE, 20 September 2026: the owner's own report said "6 sessions stopped on a
        // usage limit" and then printed six raw identifiers. He is asked to open each one and tell it to
        // continue; an identifier cannot be searched for, clicked, or recognised.
        var db = Db;
        foreach (var id in new[] { "ghost-1", "ghost-2", "ghost-3" })
            SeedFault(db, Alice, id, "signature=hit your usage limit.", hoursAgo: 5);

        var row = Row(db, Alice, Live(Alice)); // a Director is connected, but it has never seen these

        Assert.NotNull(row);
        Assert.Empty(row!.Sessions);
        Assert.Equal(3, row.LostContactCount);
        Assert.DoesNotContain(row.Sessions, s => s.Session == s.SessionId);
    }

    [Fact]
    public void A_stop_we_can_name_and_one_we_cannot_are_never_mixed_into_one_number()
    {
        // The named ones are the work; the rest is a count. Collapsing the two would either hide real
        // sessions or inflate the list with things nobody can act on.
        var db = Db;
        SeedFault(db, Alice, "s-seen", "signature=hit your usage limit.", hoursAgo: 5);
        SeedFault(db, Alice, "s-ghost", "signature=hit your usage limit.", hoursAgo: 4);

        var row = Row(db, Alice, Live(Alice, Named("s-seen")))!;

        Assert.Equal("Session s-seen", Assert.Single(row.Sessions).Session);
        Assert.Equal(1, row.LostContactCount);
    }

    [Fact]
    public void The_row_carries_what_a_person_needs_to_act_on_it()
    {
        var db = Db;
        SeedFault(db, Alice, "s-1", "signature=hit your usage limit.", hoursAgo: 5);

        var live = Live(Alice, new SessionDto
        {
            SessionId = "s-1",
            Name = "Email Improvements - Developer",
            Number = 131,
            RepoName = "thefrederiksen/devthrottle_internal",
            RepoPath = "D:/ReposFred/devthrottle_internal",
        });
        var stop = Assert.Single(Row(db, Alice, live)!.Sessions);

        Assert.Equal("Email Improvements - Developer", stop.Session);
        Assert.Equal("s-1", stop.SessionId);
        Assert.Equal(131, stop.Number);
        // The SHORT repository name, never the path off somebody's disk.
        Assert.Equal("thefrederiksen/devthrottle_internal", stop.Repo);
    }

    [Fact]
    public void A_snoozed_session_is_not_something_to_go_and_restart()
    {
        // Snoozing is the owner saying "not now". Telling him to go and resume it contradicts his own
        // decision, which is how a report earns being ignored.
        var db = Db;
        SeedFault(db, Alice, "s-snoozed", "signature=hit your usage limit.", hoursAgo: 5);

        // HoldStates.Held is the wire value; "Snoozed" is only what the Cockpit prints. Using the constant
        // means a rename of the state breaks this test rather than silently un-guarding the row.
        var live = Live(Alice, new SessionDto { SessionId = "s-snoozed", Name = "Parked", HoldState = HoldStates.Held });

        Assert.Null(Row(db, Alice, live));
    }

    [Fact]
    public void The_signatures_the_row_reads_are_ones_the_classifier_really_emits()
    {
        // The seam: the row matches recovery-log text the classifier produced. If the two ever spell a
        // block differently the row goes silent for ever and no other test notices.
        foreach (var signature in TerminatingFaultClassifier.UsageLimitSignatures)
        {
            var fault = TerminatingFaultClassifier.Classify(new[] { $"You've {signature} resets 3pm" });
            Assert.Equal(SessionFaultClass.NonRecoverable, fault.Class);
            Assert.Equal(signature, fault.Signature);
            Assert.True(MorningReportBuilder.IsUsageLimitDetail($"signature={fault.Signature}"));
        }
    }

    [Fact]
    public void A_stop_for_any_other_reason_is_not_called_a_usage_limit()
    {
        var db = Db;
        SeedFault(db, Alice, "s-badkey", "signature=invalid api key", hoursAgo: 5);
        SeedFault(db, Alice, "s-credits", "signature=out of credits", hoursAgo: 5);
        SeedFault(db, Alice, "s-ratelimit", "signature=rate_limit_error", hoursAgo: 5, cause: ActivityCauses.NonRecoverable);
        SeedFault(db, Alice, "s-model", "signature=needs-human (model verdict)", hoursAgo: 5);
        SeedFault(db, Alice, "s-prose", "the agent said it hit your usage limit. in passing", hoursAgo: 5);
        // Same signature, but not a fault-detected line: a recovery record about it is not a second stop.
        SeedFault(db, Alice, "s-recovered", "signature=hit your usage limit.", hoursAgo: 5, eventType: ActivityEventTypes.SupervisorRecovered);

        // Every one of them IS live, so a null here is the signature filter doing its job and not the
        // lost-contact filter quietly emptying the row for a different reason.
        var live = Live(Alice, Named("s-badkey", "s-credits", "s-ratelimit", "s-model", "s-prose", "s-recovered"));

        Assert.Null(Row(db, Alice, live));
    }

    [Fact]
    public void A_session_that_worked_again_after_the_stop_is_not_news()
    {
        var db = Db;
        SeedFault(db, Alice, "s-resumed", "signature=hit your weekly limit", hoursAgo: 9);
        SeedState(db, Alice, "s-resumed", GovernanceEventState.Active, hoursAgo: 2);
        // Active BEFORE the stop is the turn that hit the limit, not a resumption.
        SeedState(db, Alice, "s-still", GovernanceEventState.Active, hoursAgo: 10);
        SeedFault(db, Alice, "s-still", "signature=hit your weekly limit", hoursAgo: 9);
        SeedState(db, Alice, "s-still", GovernanceEventState.Idle, hoursAgo: 8.9);

        var row = Row(db, Alice, Live(Alice, Named("s-resumed", "s-still")))!;

        Assert.Equal(new[] { "Session s-still" }, row.Sessions.Select(s => s.Session));
        // The resumed one is GONE, not moved into the count: it is not news in either form.
        Assert.Null(row.LostContactCount);
    }

    [Fact]
    public void A_session_that_stopped_twice_is_listed_once_at_its_latest_stop()
    {
        var db = Db;
        SeedFault(db, Alice, "s-twice", "signature=hit your usage limit.", hoursAgo: 30);
        SeedState(db, Alice, "s-twice", GovernanceEventState.Active, hoursAgo: 20);
        SeedFault(db, Alice, "s-twice", "signature=hit your usage limit.", hoursAgo: 6);

        var stop = Assert.Single(Row(db, Alice, Live(Alice, Named("s-twice")))!.Sessions);
        Assert.Equal(Now.AddHours(-6), stop.StoppedUtc);
    }

    [Fact]
    public void A_stop_older_than_the_lookback_is_retired()
    {
        var db = Db;
        SeedFault(db, Alice, "s-old", "signature=hit your usage limit.", hoursAgo: MorningReportBuilder.UsageLimitLookback.TotalHours + 1);

        // Live, so a null is the lookback and nothing else.
        Assert.Null(Row(db, Alice, Live(Alice, Named("s-old"))));
    }

    [Fact]
    public void A_closed_session_has_nothing_left_to_resume()
    {
        var db = Db;
        SeedFault(db, Alice, "s-exited", "signature=hit your usage limit.", hoursAgo: 5);

        var live = Live(Alice, new SessionDto { SessionId = "s-exited", Name = "Finished", ActivityState = "Exited" });

        Assert.Null(Row(db, Alice, live));
    }

    [Fact]
    public void One_accounts_row_never_names_another_accounts_session()
    {
        var db = Db;
        SeedFault(db, Bob, "s-bob", "signature=hit your usage limit.", hoursAgo: 5);

        // Alice has no stop of her own, and Bob's roster is Bob's: hers stays silent either way.
        Assert.Null(Row(db, Alice, Live(Alice, Named("s-bob"))));
        Assert.Equal(new[] { "Session s-bob" }, Row(db, Bob, Live(Bob, Named("s-bob")))!.Sessions.Select(s => s.Session));
    }

    [Fact]
    public void On_the_wire_the_row_is_camelCase_with_its_type()
    {
        var db = Db;
        SeedFault(db, Alice, "s-1", "signature=hit your usage limit.", hoursAgo: 5);

        var live = Live(Alice, new SessionDto { SessionId = "s-1", Name = "Nightly", Number = 7, RepoName = "acme/app" });
        var json = System.Text.Json.JsonSerializer.Serialize<MorningAttentionItemDto>(Row(db, Alice, live)!,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal(
            "{\"type\":\"usage-limit-stops\",\"sessions\":[{\"session\":\"Nightly\",\"sessionId\":\"s-1\"," +
            "\"repo\":\"acme/app\",\"number\":7,\"stoppedUtc\":\"2026-09-20T06:00:00Z\"}]}",
            json);
    }

    [Fact]
    public void A_count_of_none_is_ABSENT_from_the_wire_never_a_zero()
    {
        var db = Db;
        SeedFault(db, Alice, "s-1", "signature=hit your usage limit.", hoursAgo: 5);

        var json = System.Text.Json.JsonSerializer.Serialize<MorningAttentionItemDto>(
            Row(db, Alice, Live(Alice, Named("s-1")))!,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.DoesNotContain("lostContactCount", json);
    }
}
