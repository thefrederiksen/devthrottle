using System;
using System.Collections.Generic;
using System.Linq;
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
/// The daily report's usage-limit row (#3124). The row is a claim about a person's work - "this session
/// stopped because your plan ran out, and it is still stopped" - so each test pins one way that claim could
/// be false: the stop was something else, the session already resumed, the session is closed, the stop is
/// old, or it is somebody else's session.
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

    private static UsageLimitStopsAttentionDto? Row(GatewayDatabase db, TenantId tenant) =>
        new MorningReportBuilder(db, utcNow: () => Now)
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

        var row = Row(db, Alice);

        Assert.NotNull(row);
        Assert.Equal("usage-limit-stops", row!.Type);
        Assert.Equal(3, TerminatingFaultClassifier.UsageLimitSignatures.Count);
        Assert.Equal(new[] { "s-0", "s-1", "s-2" }, row.Sessions.Select(s => s.Session));
        Assert.Equal(Now.AddHours(-10), row.Sessions[0].StoppedUtc);
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

        Assert.Null(Row(db, Alice));
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

        Assert.Equal(new[] { "s-still" }, Row(db, Alice)!.Sessions.Select(s => s.Session));
    }

    [Fact]
    public void A_session_that_stopped_twice_is_listed_once_at_its_latest_stop()
    {
        var db = Db;
        SeedFault(db, Alice, "s-twice", "signature=hit your usage limit.", hoursAgo: 30);
        SeedState(db, Alice, "s-twice", GovernanceEventState.Active, hoursAgo: 20);
        SeedFault(db, Alice, "s-twice", "signature=hit your usage limit.", hoursAgo: 6);

        var stop = Assert.Single(Row(db, Alice)!.Sessions);
        Assert.Equal(Now.AddHours(-6), stop.StoppedUtc);
    }

    [Fact]
    public void A_stop_older_than_the_lookback_is_retired()
    {
        var db = Db;
        SeedFault(db, Alice, "s-old", "signature=hit your usage limit.", hoursAgo: MorningReportBuilder.UsageLimitLookback.TotalHours + 1);

        Assert.Null(Row(db, Alice));
    }

    [Fact]
    public void One_accounts_row_never_names_another_accounts_session()
    {
        var db = Db;
        SeedFault(db, Bob, "s-bob", "signature=hit your usage limit.", hoursAgo: 5);

        Assert.Null(Row(db, Alice));
        Assert.Equal(new[] { "s-bob" }, Row(db, Bob)!.Sessions.Select(s => s.Session));
    }

    [Fact]
    public void On_the_wire_the_row_is_camelCase_with_its_type()
    {
        var db = Db;
        SeedFault(db, Alice, "s-1", "signature=hit your usage limit.", hoursAgo: 5);

        var json = System.Text.Json.JsonSerializer.Serialize<MorningAttentionItemDto>(Row(db, Alice)!,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal("{\"type\":\"usage-limit-stops\",\"sessions\":[{\"session\":\"s-1\",\"stoppedUtc\":\"2026-09-20T06:00:00Z\"}]}", json);
    }
}
