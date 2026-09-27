using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Messaging;

/// <summary>
/// A message whose doorbell cannot ring is reported to its sender (issue 3289). A deferred ring is not a ring, so
/// before this a message to a session that was never idle and safe to type into never went stuck, and its sender
/// waited in silence. Real EF store on a throwaway SQLite file, a clock the test moves, and a Director that answers
/// as the test says.
/// </summary>
public sealed class FleetDoorbellUnreachableTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId Tenant = new("acct-a");
    private const string Manager = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Worker = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string Director = "dddddddd-0000-0000-0000-000000000009";
    private static readonly DateTime T0 = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private DateTime _now = T0;
    private string _activity = "WaitingForInput";
    private FleetRingResponse _answer = new()
    {
        Outcome = FleetRingOutcomes.Deferred,
        Reason = FleetRingDeferReasons.Working,
        Detail = "the screen shows \"esc to interrupt\"",
    };

    private (FleetMessageStore Store, FleetMessageService Service, FleetDoorbell Doorbell) NewRig()
    {
        var store = new FleetMessageStore(_harness.Open());
        var service = new FleetMessageService(store, null, () => _now);
        var doorbell = new FleetDoorbell(
            store,
            service,
            locate: (_, sid) => new FleetRingTarget(Director, _activity, sid == Worker ? "Tech Lead" : null),
            ring: (_, _, _, _, _) => Task.FromResult<FleetRingResponse?>(_answer),
            forEachTenant: (pass, _) => pass(Tenant),
            clock: () => _now);
        return (store, service, doorbell);
    }

    private string Send(FleetMessageService service)
    {
        var outcome = service.Send(Tenant,
            new FleetParty(Manager, null, "delivery lead", "mac"),
            new FleetParty(Worker, Manager, "Tech Lead", "mac"),
            "your developer handed back; carry on", FleetMessageKinds.Message);
        Assert.Equal("queued", outcome.Response.Status);
        return outcome.Response.MessageId!;
    }

    private FleetMessageEntity Peek(string id)
    {
        using var ctx = _harness.Open().CreateContext(Tenant);
        return ctx.FleetMessages.Single(m => m.MessageId == id);
    }

    private async Task HeartbeatsUntil(FleetDoorbell doorbell, TimeSpan end)
    {
        for (var t = TimeSpan.Zero; t <= end; t += FleetDoorbell.HeartbeatInterval)
        {
            _now = T0 + t;
            await doorbell.SweepAsync();
        }
    }

    [Fact]
    public async Task SweepAsync_DeferredForFifteenMinutes_TellsTheSenderOnceWithTheReason()
    {
        var (store, service, doorbell) = NewRig();
        var id = Send(service);

        await HeartbeatsUntil(doorbell, TimeSpan.FromMinutes(20));

        var row = Peek(id);
        Assert.Equal(0, row.RingCount);
        Assert.Null(row.StuckAtUtc);
        Assert.Equal(T0.AddMinutes(15), row.UnreachableNoticeAtUtc);

        var notice = Assert.Single(store.ReadInbox(Tenant, Manager, _now, includeRecent: false).Unread);
        Assert.Null(notice.SenderSessionId);
        Assert.Equal(FleetMessageKinds.System, notice.Kind);
        Assert.Contains(id, notice.Text);
        Assert.Contains("Tech Lead (bbbbbbbb)", notice.Text);
        Assert.Contains("could not ring once", notice.Text);
        Assert.Contains("esc to interrupt", notice.Text);
        Assert.Contains("cc-devthrottle session prompt bbbbbbbb", notice.Text);
    }

    [Fact]
    public async Task SweepAsync_JustUnderFifteenMinutes_TellsNobody()
    {
        var (store, service, doorbell) = NewRig();
        var id = Send(service);

        await HeartbeatsUntil(doorbell, TimeSpan.FromMinutes(15) - FleetDoorbell.HeartbeatInterval);

        Assert.Null(Peek(id).UnreachableNoticeAtUtc);
        Assert.Empty(store.ReadInbox(Tenant, Manager, _now, includeRecent: false).Unread);
    }

    [Fact]
    public async Task SweepAsync_AfterTheNotice_TheMessageStillRingsWhenTheSessionIsIdle()
    {
        var (_, service, doorbell) = NewRig();
        var id = Send(service);
        await HeartbeatsUntil(doorbell, TimeSpan.FromMinutes(16));
        Assert.NotNull(Peek(id).UnreachableNoticeAtUtc);

        _answer = new FleetRingResponse { Outcome = FleetRingOutcomes.Rung };
        _now = T0.AddMinutes(17);
        var attempt = await doorbell.RingSessionAsync(Tenant, Worker, "settled", CancellationToken.None);

        Assert.Equal(FleetRingAttempt.Rung, attempt);
        Assert.Equal(1, Peek(id).RingCount);
    }

    [Fact]
    public async Task SweepAsync_AWorkingSessionForFifteenMinutes_SaysItHasBeenWorking()
    {
        _activity = "Working";
        var (store, service, doorbell) = NewRig();
        Send(service);

        await HeartbeatsUntil(doorbell, TimeSpan.FromMinutes(16));

        var notice = Assert.Single(store.ReadInbox(Tenant, Manager, _now, includeRecent: false).Unread);
        Assert.Contains("the session has been working", notice.Text);
    }

    [Fact]
    public async Task SweepAsync_AMessageThatWasRung_IsNeverReportedUnreachable()
    {
        // A rung message that goes unanswered is the stuck path's to report, not this one's.
        _answer = new FleetRingResponse { Outcome = FleetRingOutcomes.Rung };
        var (_, service, doorbell) = NewRig();
        var id = Send(service);

        await HeartbeatsUntil(doorbell, TimeSpan.FromMinutes(20));

        Assert.Null(Peek(id).UnreachableNoticeAtUtc);
        Assert.NotNull(Peek(id).StuckAtUtc);
    }

    [Fact]
    public async Task SweepAsync_AMessageReadInTime_IsNeverReported()
    {
        var (store, service, doorbell) = NewRig();
        var id = Send(service);
        await HeartbeatsUntil(doorbell, TimeSpan.FromMinutes(10));
        store.ReadInbox(Tenant, Worker, _now, includeRecent: false);

        await HeartbeatsUntil(doorbell, TimeSpan.FromMinutes(20));

        Assert.Null(Peek(id).UnreachableNoticeAtUtc);
        Assert.Empty(store.ReadInbox(Tenant, Manager, _now, includeRecent: false).Unread);
    }

    [Fact]
    public void UnreachableNoticeText_LongerThanTheCap_KeepsTheWholeMessageId()
    {
        var message = new FleetMessageEntity { MessageId = new string('a', 32), RecipientSessionId = Worker };

        var text = FleetDoorbell.UnreachableNoticeText(message, new string('n', 500), "x", FleetMessageLimits.Default,
            FleetMessageLimits.MinTextLength);

        Assert.Equal(FleetDoorbell.StuckNoticePrefix + new string('a', 32), text);
    }
}
