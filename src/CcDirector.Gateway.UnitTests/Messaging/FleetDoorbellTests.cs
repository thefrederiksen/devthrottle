using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Messaging;

/// <summary>
/// The doorbell's Gateway half (the Message Load mission, slice 2): WHEN a ring is asked for, what is counted,
/// when a message is stuck, who is told, and that a read undoes it. Real EF store on a throwaway SQLite file,
/// a fake clock the test moves, and a fake Director that records every ring and answers as the test says.
/// </summary>
public sealed class FleetDoorbellTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId Tenant = new("acct-a");
    private const string Manager = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Worker = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string Director = "dddddddd-0000-0000-0000-000000000009";
    private static readonly DateTime T0 = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    private DateTime _now = T0;
    private string _activity = "WaitingForInput";
    private bool _connected = true;
    private bool _locateThrows;
    private FleetRingResponse? _answer = new() { Outcome = FleetRingOutcomes.Rung };
    private readonly List<(string Sid, int Unread, DateTime At)> _rings = new();

    private sealed class Rig
    {
        public required FleetMessageStore Store { get; init; }
        public required FleetMessageService Service { get; init; }
        public required FleetDoorbell Doorbell { get; init; }
    }

    private Rig NewRig(FleetMessageLimits? limits = null)
    {
        var store = new FleetMessageStore(_harness.Open());
        var service = new FleetMessageService(store, limits, () => _now);
        var doorbell = new FleetDoorbell(
            store,
            service,
            locate: (_, sid) => _locateThrows
                ? throw new InvalidOperationException("the roster could not be read")
                : _connected ? new FleetRingTarget(Director, _activity, sid == Worker ? "worker-one" : null) : null,
            ring: (_, _, sid, unread, _) =>
            {
                _rings.Add((sid, unread, _now));
                return Task.FromResult(_answer);
            },
            forEachTenant: (pass, _) => pass(Tenant),
            limits: limits,
            clock: () => _now);
        return new Rig { Store = store, Service = service, Doorbell = doorbell };
    }

    private static string Send(Rig rig, string text = "please look at the build")
    {
        var outcome = rig.Service.Send(Tenant,
            new FleetParty(Manager, null, "manager", "mac"),
            new FleetParty(Worker, Manager, "worker-one", "mac"),
            text, FleetMessageKinds.Message);
        Assert.Equal("queued", outcome.Response.Status);
        return outcome.Response.MessageId!;
    }

    private FleetMessageEntity Peek(string id)
    {
        using var ctx = _harness.Open().CreateContext(Tenant);
        return ctx.FleetMessages.Single(m => m.MessageId == id);
    }

    private void Advance(TimeSpan by) => _now += by;

    // Once a message is stuck, the notice sits in the SENDER's inbox and the sender is rung for it too - so the
    // worker's schedule is read from the worker's rings only.
    private List<(string Sid, int Unread, DateTime At)> WorkerRings => _rings.Where(r => r.Sid == Worker).ToList();

    // ---------- The schedule ----------

    [Fact]
    public async Task A_new_message_is_rung_at_once_with_the_unread_count()
    {
        var rig = NewRig();
        var id = Send(rig);

        var attempt = await rig.Doorbell.RingSessionAsync(Tenant, Worker, "test", CancellationToken.None);

        Assert.Equal(FleetRingAttempt.Rung, attempt);
        var ring = Assert.Single(_rings);
        Assert.Equal(Worker, ring.Sid);
        Assert.Equal(1, ring.Unread);
        var row = Peek(id);
        Assert.Equal(1, row.RingCount);
        Assert.Equal(T0, row.LastRungAtUtc);
        Assert.Null(row.ReadAtUtc);
    }

    [Fact]
    public async Task No_second_ring_inside_the_grace()
    {
        var rig = NewRig();
        var id = Send(rig);
        await rig.Doorbell.RingSessionAsync(Tenant, Worker, "test", CancellationToken.None);

        Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        var attempt = await rig.Doorbell.RingSessionAsync(Tenant, Worker, "test", CancellationToken.None);

        Assert.Equal(FleetRingAttempt.NotDue, attempt);
        Assert.Single(_rings);
        Assert.Equal(1, Peek(id).RingCount);
    }

    [Fact]
    public async Task A_second_ring_once_the_grace_has_passed()
    {
        var rig = NewRig();
        var id = Send(rig);
        await rig.Doorbell.RingSessionAsync(Tenant, Worker, "test", CancellationToken.None);

        Advance(TimeSpan.FromMinutes(5));
        var attempt = await rig.Doorbell.RingSessionAsync(Tenant, Worker, "test", CancellationToken.None);

        Assert.Equal(FleetRingAttempt.Rung, attempt);
        Assert.Equal(2, _rings.Count);
        Assert.Equal(T0.AddMinutes(5), _rings[1].At);
        Assert.Equal(2, Peek(id).RingCount);
        Assert.Equal(T0.AddMinutes(5), Peek(id).LastRungAtUtc);
    }

    [Fact]
    public async Task Three_rings_five_minutes_apart_then_stuck_and_the_sender_is_told_once()
    {
        var rig = NewRig();
        var id = Send(rig);

        // Drive the heartbeat every 15 seconds for 16 minutes: the whole life of an unanswered message.
        for (var t = TimeSpan.Zero; t <= TimeSpan.FromMinutes(16); t += TimeSpan.FromSeconds(15))
        {
            _now = T0 + t;
            await rig.Doorbell.SweepAsync();
        }

        Assert.Equal(new[] { T0, T0.AddMinutes(5), T0.AddMinutes(10) }, WorkerRings.Select(r => r.At).ToArray());
        var row = Peek(id);
        Assert.Equal(3, row.RingCount);
        Assert.Equal(T0.AddMinutes(15), row.StuckAtUtc);
        Assert.Null(row.ReadAtUtc);

        // The sender's inbox holds exactly one notice, from the Gateway, naming the message.
        var senderInbox = rig.Store.ReadInbox(Tenant, Manager, _now, includeRecent: false);
        var notice = Assert.Single(senderInbox.Unread);
        Assert.Null(notice.SenderSessionId);
        Assert.Equal(FleetMessageKinds.System, notice.Kind);
        Assert.Contains(id, notice.Text);
        Assert.Contains("worker-one", notice.Text);
        Assert.Contains("rang 3 times", notice.Text);
    }

    [Fact]
    public async Task The_stuck_message_is_not_rung_a_fourth_time()
    {
        var rig = NewRig();
        Send(rig);
        for (var t = TimeSpan.Zero; t <= TimeSpan.FromMinutes(40); t += TimeSpan.FromSeconds(15))
        {
            _now = T0 + t;
            await rig.Doorbell.SweepAsync();
        }

        Assert.Equal(3, WorkerRings.Count);
        // The sender was rung for its stuck notice - the notice is a message like any other.
        Assert.Equal(T0.AddMinutes(15), _rings.First(r => r.Sid == Manager).At);
    }

    [Fact]
    public async Task The_settled_edge_never_rings_a_fourth_time_even_before_the_heartbeat_marks_stuck()
    {
        var rig = NewRig();
        var id = Send(rig);
        foreach (var minutes in new[] { 0, 5, 10, 15 })
        {
            _now = T0.AddMinutes(minutes);
            await rig.Doorbell.RingSessionAsync(Tenant, Worker, "settled", CancellationToken.None);
        }

        Assert.Equal(3, WorkerRings.Count);
        Assert.Equal(3, Peek(id).RingCount);
        Assert.Null(Peek(id).StuckAtUtc); // only the heartbeat marks stuck
    }

    [Fact]
    public async Task Not_stuck_after_two_rings_whatever_the_wait()
    {
        var rig = NewRig();
        var id = Send(rig);
        foreach (var minutes in new[] { 0, 5 })
        {
            _now = T0.AddMinutes(minutes);
            await rig.Doorbell.RingSessionAsync(Tenant, Worker, "settled", CancellationToken.None);
        }

        _now = T0.AddHours(2);
        Assert.Empty(rig.Doorbell.MarkStuckAndNotify(Tenant));
        Assert.Null(Peek(id).StuckAtUtc);
    }

    [Fact]
    public async Task Not_stuck_one_second_before_the_grace_after_the_third_ring()
    {
        var rig = NewRig();
        var id = Send(rig);
        foreach (var minutes in new[] { 0, 5, 10 })
        {
            _now = T0.AddMinutes(minutes);
            await rig.Doorbell.SweepAsync();
        }

        _now = T0.AddMinutes(15) - TimeSpan.FromSeconds(1);
        Assert.Empty(rig.Doorbell.MarkStuckAndNotify(Tenant));
        Assert.Null(Peek(id).StuckAtUtc);

        _now = T0.AddMinutes(15);
        Assert.Single(rig.Doorbell.MarkStuckAndNotify(Tenant));
    }

    [Fact]
    public async Task A_read_undoes_stuck_and_nothing_further_is_rung_or_sent()
    {
        var rig = NewRig();
        var id = Send(rig);
        for (var t = TimeSpan.Zero; t <= TimeSpan.FromMinutes(15); t += TimeSpan.FromSeconds(15))
        {
            _now = T0 + t;
            await rig.Doorbell.SweepAsync();
        }
        Assert.NotNull(Peek(id).StuckAtUtc);

        Advance(TimeSpan.FromMinutes(1));
        var read = rig.Store.ReadInbox(Tenant, Worker, _now, includeRecent: false);

        var m = Assert.Single(read.Unread);
        Assert.Equal(id, m.MessageId);
        var row = Peek(id);
        Assert.Null(row.StuckAtUtc);
        Assert.Equal(_now, row.ReadAtUtc);

        var ringsBefore = WorkerRings.Count;
        Advance(TimeSpan.FromMinutes(30));
        await rig.Doorbell.SweepAsync();
        Assert.Equal(ringsBefore, WorkerRings.Count);
        // One notice only: the read did not add a second one.
        Assert.Single(rig.Store.ReadInbox(Tenant, Manager, _now, includeRecent: false).Unread);
    }

    [Fact]
    public async Task A_message_read_before_the_grace_ends_is_never_stuck()
    {
        var rig = NewRig();
        var id = Send(rig);
        foreach (var minutes in new[] { 0, 5, 10 })
        {
            _now = T0.AddMinutes(minutes);
            await rig.Doorbell.SweepAsync();
        }
        _now = T0.AddMinutes(14);
        rig.Store.ReadInbox(Tenant, Worker, _now, includeRecent: false);

        _now = T0.AddMinutes(20);
        await rig.Doorbell.SweepAsync();

        Assert.Null(Peek(id).StuckAtUtc);
        Assert.Empty(rig.Store.ReadInbox(Tenant, Manager, _now, includeRecent: false).Unread);
    }

    // ---------- Inspection 4, ruling 4: stuck and its notice are one write ----------

    /// <summary>Ring the worker three times at the product grace and move to the moment it is due stuck.</summary>
    private async Task<string> RungThreeTimesAndDueStuck(Rig rig)
    {
        var id = Send(rig);
        foreach (var minutes in new[] { 0, 5, 10 })
        {
            _now = T0.AddMinutes(minutes);
            await rig.Doorbell.RingSessionAsync(Tenant, Worker, "settled", CancellationToken.None);
        }
        _now = T0.AddMinutes(15);
        return id;
    }

    private int SystemNoticesFor(string sid)
    {
        using var ctx = _harness.Open().CreateContext(Tenant);
        return ctx.FleetMessages.Count(m => m.RecipientSessionId == sid && m.SenderSessionId == null
                                            && m.Kind == FleetMessageKinds.System);
    }

    [Fact]
    public async Task A_failure_between_the_stuck_mark_and_its_notice_persists_neither()
    {
        var rig = NewRig();
        var id = await RungThreeTimesAndDueStuck(rig);
        rig.Store.BeforeStuckSave = () => throw new InvalidOperationException("the process stopped here");

        Assert.Throws<InvalidOperationException>(() => rig.Doorbell.MarkStuckAndNotify(Tenant));

        Assert.Null(Peek(id).StuckAtUtc);
        Assert.Equal(0, SystemNoticesFor(Manager));
    }

    [Fact]
    public async Task A_notice_that_cannot_be_written_leaves_the_message_open()
    {
        // The notice's text needs the recipient's roster name; the roster read throws. That is the "send threw
        // after the mark" case of inspection 4: before the fix the mark was already saved and the notice lost.
        var rig = NewRig();
        var id = await RungThreeTimesAndDueStuck(rig);
        _locateThrows = true;

        Assert.Throws<InvalidOperationException>(() => rig.Doorbell.MarkStuckAndNotify(Tenant));

        Assert.Null(Peek(id).StuckAtUtc);
        Assert.Equal(0, SystemNoticesFor(Manager));
    }

    [Fact]
    public async Task The_sweep_after_a_crash_marks_and_notifies_exactly_once()
    {
        var rig = NewRig();
        var id = await RungThreeTimesAndDueStuck(rig);
        _locateThrows = true;
        Assert.Throws<InvalidOperationException>(() => rig.Doorbell.MarkStuckAndNotify(Tenant));
        _locateThrows = false;

        Advance(TimeSpan.FromSeconds(15));
        var marked = rig.Doorbell.MarkStuckAndNotify(Tenant);
        Advance(TimeSpan.FromSeconds(15));
        var again = rig.Doorbell.MarkStuckAndNotify(Tenant);

        Assert.Equal(id, Assert.Single(marked).MessageId);
        Assert.Empty(again);
        Assert.Equal(T0.AddMinutes(15).AddSeconds(15), Peek(id).StuckAtUtc);
        Assert.Equal(1, SystemNoticesFor(Manager));
    }

    [Fact]
    public async Task The_mark_and_the_notice_carry_the_same_moment()
    {
        var rig = NewRig();
        var id = await RungThreeTimesAndDueStuck(rig);

        rig.Doorbell.MarkStuckAndNotify(Tenant);

        using var ctx = _harness.Open().CreateContext(Tenant);
        var notice = ctx.FleetMessages.Single(m => m.RecipientSessionId == Manager && m.SenderSessionId == null);
        Assert.Equal(Peek(id).StuckAtUtc, notice.CreatedAtUtc);
        Assert.Equal(FleetMessageKinds.System, notice.Kind);
        Assert.Contains(id, notice.Text);
    }

    // ---------- What is and is not counted as a ring ----------

    [Fact]
    public async Task A_deferred_ring_types_nothing_and_does_not_move_the_message_towards_stuck()
    {
        var rig = NewRig();
        var id = Send(rig);
        _answer = new FleetRingResponse { Outcome = FleetRingOutcomes.Deferred, Reason = FleetRingDeferReasons.ComposerHoldsText };

        for (var t = TimeSpan.Zero; t <= TimeSpan.FromMinutes(30); t += TimeSpan.FromSeconds(15))
        {
            _now = T0 + t;
            await rig.Doorbell.SweepAsync();
        }

        Assert.True(_rings.Count > 100, "a deferred ring is asked again on every heartbeat");
        var row = Peek(id);
        Assert.Equal(0, row.RingCount);
        Assert.Null(row.LastRungAtUtc);
        Assert.Null(row.StuckAtUtc);
    }

    [Fact]
    public async Task An_unreachable_director_is_not_a_ring()
    {
        var rig = NewRig();
        var id = Send(rig);
        _answer = null;

        var attempt = await rig.Doorbell.RingSessionAsync(Tenant, Worker, "test", CancellationToken.None);

        Assert.Equal(FleetRingAttempt.Unreachable, attempt);
        Assert.Equal(0, Peek(id).RingCount);
    }

    [Theory]
    [InlineData("Working", FleetRingAttempt.SkippedWorking)]
    [InlineData("Starting", FleetRingAttempt.SkippedWorking)]
    [InlineData("Exited", FleetRingAttempt.SkippedExited)]
    public async Task The_director_is_not_asked_when_the_roster_says_working_or_exited(string activity, FleetRingAttempt expected)
    {
        var rig = NewRig();
        var id = Send(rig);
        _activity = activity;

        var attempt = await rig.Doorbell.RingSessionAsync(Tenant, Worker, "test", CancellationToken.None);

        Assert.Equal(expected, attempt);
        Assert.Empty(_rings);
        Assert.Equal(0, Peek(id).RingCount);
    }

    [Fact]
    public async Task A_session_not_on_a_connected_director_is_not_rung()
    {
        var rig = NewRig();
        Send(rig);
        _connected = false;

        Assert.Equal(FleetRingAttempt.NotConnected,
            await rig.Doorbell.RingSessionAsync(Tenant, Worker, "test", CancellationToken.None));
        Assert.Empty(_rings);
    }

    [Fact]
    public async Task A_ring_counts_only_the_messages_that_were_due()
    {
        var rig = NewRig();
        var first = Send(rig, "first");
        await rig.Doorbell.RingSessionAsync(Tenant, Worker, "test", CancellationToken.None);

        // Two minutes later a second message arrives (a granted whole-account copy, so the ten-minute spacing
        // between one sender's messages does not refuse it). It is due at once; the first is inside its grace.
        Advance(TimeSpan.FromMinutes(2));
        var second = rig.Service.Send(Tenant, new FleetParty(Manager, null, null, null),
            new FleetParty(Worker, Manager, null, null), "second", FleetMessageKinds.Everyone, FleetMessageExemption.HumanGrant);
        Assert.Equal("queued", second.Response.Status);

        var attempt = await rig.Doorbell.RingSessionAsync(Tenant, Worker, "test", CancellationToken.None);

        Assert.Equal(FleetRingAttempt.Rung, attempt);
        Assert.Equal(2, _rings.Last().Unread);
        Assert.Equal(1, Peek(first).RingCount);
        Assert.Equal(T0, Peek(first).LastRungAtUtc);
        Assert.Equal(1, Peek(second.Response.MessageId!).RingCount);
    }

    [Fact]
    public async Task The_settled_edge_rings_without_waiting_for_the_heartbeat()
    {
        var rig = NewRig();
        Send(rig);

        rig.Doorbell.OnSettled(Tenant, Worker);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_rings.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Single(_rings);
    }

    [Fact]
    public async Task A_stuck_system_notice_tells_nobody()
    {
        var rig = NewRig();
        var notice = rig.Service.Send(Tenant, sender: null, new FleetParty(Worker, null, null, null),
            "a notice nobody reads", FleetMessageKinds.System, FleetMessageExemption.System);
        Assert.Equal("queued", notice.Response.Status);

        for (var t = TimeSpan.Zero; t <= TimeSpan.FromMinutes(16); t += TimeSpan.FromSeconds(15))
        {
            _now = T0 + t;
            await rig.Doorbell.SweepAsync();
        }

        Assert.NotNull(Peek(notice.Response.MessageId!).StuckAtUtc);
        using var ctx = _harness.Open().CreateContext(Tenant);
        Assert.Equal(1, ctx.FleetMessages.Count());
    }

    [Fact]
    public async Task A_shortened_grace_moves_the_whole_schedule()
    {
        var rig = NewRig(FleetMessageLimits.Default with { RingGrace = TimeSpan.FromMinutes(1) });
        var id = Send(rig);
        for (var t = TimeSpan.Zero; t <= TimeSpan.FromMinutes(4); t += TimeSpan.FromSeconds(15))
        {
            _now = T0 + t;
            await rig.Doorbell.SweepAsync();
        }

        Assert.Equal(new[] { T0, T0.AddMinutes(1), T0.AddMinutes(2) }, WorkerRings.Select(r => r.At).ToArray());
        Assert.Equal(T0.AddMinutes(3), Peek(id).StuckAtUtc);
    }

    // ---------- The store's own guards: a read that lands between a decision and its write wins ----------

    [Fact]
    public void Marking_stuck_never_touches_a_message_already_read_whatever_the_ruling_says()
    {
        var rig = NewRig();
        var id = Send(rig);
        rig.Store.MarkRung(Tenant, new[] { id }, T0);
        rig.Store.ReadInbox(Tenant, Worker, T0.AddMinutes(1), includeRecent: false);

        var marked = rig.Store.MarkStuck(Tenant, T0.AddHours(1), _ => true);

        Assert.Empty(marked);
        Assert.Null(Peek(id).StuckAtUtc);
    }

    [Fact]
    public void A_ring_is_not_recorded_on_a_message_already_read()
    {
        var rig = NewRig();
        var id = Send(rig);
        rig.Store.ReadInbox(Tenant, Worker, T0.AddMinutes(1), includeRecent: false);

        Assert.Equal(0, rig.Store.MarkRung(Tenant, new[] { id }, T0.AddMinutes(2)));
        Assert.Equal(0, Peek(id).RingCount);
    }

    [Fact]
    public void A_stuck_message_is_not_offered_for_ringing_but_still_counts_as_unread()
    {
        var rig = NewRig();
        var id = Send(rig);
        rig.Store.MarkRung(Tenant, new[] { id }, T0);
        Assert.Single(rig.Store.MarkStuck(Tenant, T0.AddMinutes(1), _ => true));

        Assert.Empty(rig.Store.OpenMessagesFor(Tenant, Worker));
        Assert.Empty(rig.Store.RecipientsWithOpenMessages(Tenant));
        Assert.Equal(1, rig.Store.CountUnread(Tenant, Worker));
    }

    // ---------- The words and the numbers ----------

    [Fact]
    public void The_product_rings_every_five_minutes_and_marks_stuck_after_three()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), FleetMessageLimits.Default.RingGrace);
        Assert.Equal(3, FleetMessageLimits.Default.StuckAfterRings);
        Assert.Equal(TimeSpan.FromSeconds(15), FleetDoorbell.HeartbeatInterval);
    }

    [Fact]
    public void The_stuck_notice_says_what_happened_and_what_to_do()
    {
        var row = new FleetMessageEntity { MessageId = "0123456789abcdef0123456789abcdef", RecipientSessionId = Worker, RingCount = 3 };

        var text = FleetDoorbell.StuckNoticeText(row, "worker-one", FleetMessageLimits.Default);

        Assert.Equal(
            "Your message 0123456789abcdef0123456789abcdef to worker-one (bbbbbbbb) is stuck: its doorbell rang 3 times, " +
            "5 minutes apart, and the session has not read its inbox. The message stays in that inbox and is delivered " +
            "if the session reads it. Do not send it again. If you needed an answer, carry on without it and say so in your report.",
            text);
    }
}
