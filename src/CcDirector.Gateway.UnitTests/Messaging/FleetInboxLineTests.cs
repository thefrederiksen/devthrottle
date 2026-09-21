using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Messaging;

/// <summary>
/// THE ROW LINE (Message Load mission, slice 4, ruling 12). The pure fold's every shape, pinned as literals; the
/// store's one grouped count over a real SQLite file; the stamp in both directions; and the fold's read count,
/// measured on the framework's own command events the way slice 2 measured the doorbell.
/// </summary>
public sealed class FleetInboxLineTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId TenantA = new("acct-a");
    private static readonly TenantId TenantB = new("acct-b");

    private const string Manager = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string WorkerA = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string WorkerB = "cccccccc-0000-0000-0000-000000000003";

    private static readonly DateTime T0 = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    private static FleetInboxCounts Counts(int waiting = 0, int replies = 0, int notices = 0, int stuck = 0,
        DateTime? oldestStuck = null) => new(waiting, replies, notices, stuck, oldestStuck);

    // ---------- The pure fold: every shape ----------

    [Fact]
    public void Nothing_waiting_folds_to_null()
    {
        Assert.Null(FleetInboxLineFold.Fold(null, T0));
        Assert.Null(FleetInboxLineFold.Fold(FleetInboxCounts.None, T0));
    }

    [Theory]
    [InlineData(1, "1 message waiting")]
    [InlineData(2, "2 messages waiting")]
    [InlineData(17, "17 messages waiting")]
    public void Waiting_messages_are_counted_with_the_right_plural(int n, string expected)
        => Assert.Equal(expected, FleetInboxLineFold.Fold(Counts(waiting: n), T0));

    [Theory]
    [InlineData(1, "1 reply waiting")]
    [InlineData(3, "3 replies waiting")]
    public void Replies_are_their_own_part(int n, string expected)
        => Assert.Equal(expected, FleetInboxLineFold.Fold(Counts(replies: n), T0));

    [Theory]
    [InlineData(1, "1 notice from the Gateway waiting")]
    [InlineData(2, "2 notices from the Gateway waiting")]
    public void Notices_say_who_wrote_them(int n, string expected)
        => Assert.Equal(expected, FleetInboxLineFold.Fold(Counts(notices: n), T0));

    [Fact]
    public void One_stuck_message_says_how_long_it_has_been_unread()
        => Assert.Equal("1 message stuck, unread for 20 minutes",
            FleetInboxLineFold.Fold(Counts(stuck: 1, oldestStuck: T0.AddMinutes(-20)), T0));

    [Fact]
    public void Several_stuck_messages_give_the_oldest_age()
        => Assert.Equal("3 messages stuck, the oldest unread for 2 hours",
            FleetInboxLineFold.Fold(Counts(stuck: 3, oldestStuck: T0.AddMinutes(-150)), T0));

    [Fact]
    public void Every_part_together_leads_with_stuck_and_is_separated_by_semicolons()
        => Assert.Equal(
            "1 message stuck, unread for 45 minutes; 2 messages waiting; 1 reply waiting; 1 notice from the Gateway waiting",
            FleetInboxLineFold.Fold(Counts(waiting: 2, replies: 1, notices: 1, stuck: 1, oldestStuck: T0.AddMinutes(-45)), T0));

    [Fact]
    public void A_zero_part_is_left_out()
        => Assert.Equal("1 message waiting; 2 notices from the Gateway waiting",
            FleetInboxLineFold.Fold(Counts(waiting: 1, notices: 2), T0));

    [Theory]
    [InlineData(0, "less than a minute")]
    [InlineData(59, "less than a minute")]
    [InlineData(60, "1 minute")]
    [InlineData(119, "1 minute")]
    [InlineData(120, "2 minutes")]
    [InlineData(7199, "119 minutes")]
    [InlineData(7200, "2 hours")]
    [InlineData(3 * 3600 + 59 * 60, "3 hours")]
    [InlineData(47 * 3600 + 3599, "47 hours")]
    [InlineData(48 * 3600, "2 days")]
    [InlineData(10 * 86400 + 3600, "10 days")]
    public void The_age_is_rounded_down_in_minutes_then_hours_then_days(int seconds, string expected)
        => Assert.Equal(expected, FleetInboxLineFold.Age(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void A_write_time_in_the_future_reads_as_less_than_a_minute()
        => Assert.Equal("1 message stuck, unread for less than a minute",
            FleetInboxLineFold.Fold(Counts(stuck: 1, oldestStuck: T0.AddMinutes(5)), T0));

    [Fact]
    public void A_stuck_count_without_its_write_time_is_refused()
        => Assert.Throws<ArgumentException>(() => FleetInboxLineFold.Fold(Counts(stuck: 1), T0));

    [Fact]
    public void A_negative_count_is_refused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => FleetInboxLineFold.Fold(Counts(waiting: -1), T0));

    [Fact]
    public void The_separator_is_pinned()
        => Assert.Equal("; ", FleetInboxLineFold.PartSeparator);

    // ---------- The store's one grouped count ----------

    private static FleetMessageVerdict Allow(FleetMessageHistory _) => new(FleetMessageOutcome.Queued, "");

    private static FleetMessageDraft Draft(string? from, string to, string kind, string text) =>
        new(to, from, null, null, kind, text);

    private static string Enqueue(FleetMessageStore store, TenantId tenant, string? from, string to, string kind,
        string text, DateTime at)
    {
        var (verdict, written) = store.TryEnqueue(tenant, Draft(from, to, kind, text), at, TimeSpan.FromHours(1), Allow);
        Assert.True(verdict.Queued);
        return written!.MessageId;
    }

    /// <summary>Only a rung message can be marked stuck, so ring these once, then mark exactly them.</summary>
    private static void MarkStuck(FleetMessageStore store, string recipient, IReadOnlyCollection<string> ids, DateTime at)
    {
        Assert.Equal(ids.Count, store.MarkRung(TenantA, recipient, ids, at.AddMinutes(-1), ringCap: 3));
        Assert.Equal(ids.Count, store.MarkStuck(TenantA, at, m => ids.Contains(m.MessageId)).Count);
    }

    [Fact]
    public void The_store_counts_unread_by_recipient_kind_and_stuck_mark()
    {
        var store = new FleetMessageStore(_harness.Open());
        var stuckOld = Enqueue(store, TenantA, Manager, WorkerA, FleetMessageKinds.Message, "old", T0.AddMinutes(-40));
        var stuckNew = Enqueue(store, TenantA, Manager, WorkerA, FleetMessageKinds.Report, "newer", T0.AddMinutes(-30));
        Enqueue(store, TenantA, Manager, WorkerA, FleetMessageKinds.Message, "one", T0.AddMinutes(-5));
        Enqueue(store, TenantA, Manager, WorkerA, FleetMessageKinds.Team, "two", T0.AddMinutes(-4));
        Enqueue(store, TenantA, Manager, WorkerA, FleetMessageKinds.Everyone, "three", T0.AddMinutes(-3));
        Enqueue(store, TenantA, Manager, WorkerA, FleetMessageKinds.Report, "four", T0.AddMinutes(-2));
        Enqueue(store, TenantA, null, WorkerA, FleetMessageKinds.System, "notice", T0.AddMinutes(-2));
        Enqueue(store, TenantA, WorkerB, Manager, FleetMessageKinds.Reply, "answer", T0.AddMinutes(-1));
        MarkStuck(store, WorkerA, new[] { stuckOld, stuckNew }, T0.AddMinutes(-10));

        var counts = store.UnreadCountsByRecipient(TenantA);

        Assert.Equal(2, counts.Count);
        Assert.Equal(new FleetInboxCounts(4, 0, 1, 2, T0.AddMinutes(-40)), counts[WorkerA]);
        Assert.Equal(new FleetInboxCounts(0, 1, 0, 0, null), counts[Manager]);
        Assert.Equal(DateTimeKind.Utc, counts[WorkerA].OldestStuckWrittenUtc!.Value.Kind);
    }

    [Fact]
    public void A_read_message_is_not_counted_and_a_read_inbox_is_absent()
    {
        var store = new FleetMessageStore(_harness.Open());
        Enqueue(store, TenantA, Manager, WorkerA, FleetMessageKinds.Message, "hello", T0);
        store.ReadInbox(TenantA, WorkerA, T0.AddMinutes(1), includeRecent: false);

        Assert.Empty(store.UnreadCountsByRecipient(TenantA));
    }

    [Fact]
    public void The_count_is_partitioned_by_account()
    {
        var store = new FleetMessageStore(_harness.Open());
        Enqueue(store, TenantA, Manager, WorkerA, FleetMessageKinds.Message, "hello", T0);

        Assert.Empty(store.UnreadCountsByRecipient(TenantB));
        Assert.Single(store.UnreadCountsByRecipient(TenantA));
    }

    // ---------- The held counts (devthrottle_internal#2199: this ran on every roster fold) ----------

    [Fact]
    public void The_counts_asked_again_with_no_write_do_not_ask_the_database()
    {
        var store = new FleetMessageStore(_harness.Open());
        Enqueue(store, TenantA, Manager, WorkerA, FleetMessageKinds.Message, "hello", T0);
        Assert.Single(store.UnreadCountsByRecipient(TenantA));

        using var counter = new ReaderCommandCounter(_harness.DbPath);
        for (var i = 0; i < 5; i++) Assert.Equal(Counts(waiting: 1), store.UnreadCountsByRecipient(TenantA)[WorkerA]);

        Assert.Equal(0, counter.Reads);
    }

    /// <summary>Every write that changes what is counted reaches the next fold at once: a new message, a stuck
    /// mark, a read inbox, and the purge. A ring changes nothing counted and does not cost a read.</summary>
    [Fact]
    public void Every_write_that_changes_a_count_is_seen_at_once_and_a_ring_costs_nothing()
    {
        var store = new FleetMessageStore(_harness.Open());
        var first = Enqueue(store, TenantA, Manager, WorkerA, FleetMessageKinds.Message, "one", T0.AddMinutes(-30));
        Assert.Equal(Counts(waiting: 1), store.UnreadCountsByRecipient(TenantA)[WorkerA]);

        Enqueue(store, TenantA, Manager, WorkerA, FleetMessageKinds.Message, "two", T0.AddMinutes(-20));
        Assert.Equal(Counts(waiting: 2), store.UnreadCountsByRecipient(TenantA)[WorkerA]);

        using (var counter = new ReaderCommandCounter(_harness.DbPath))
        {
            Assert.Equal(1, store.MarkRung(TenantA, WorkerA, new[] { first }, T0.AddMinutes(-15), ringCap: 3));
            var reads = counter.Reads;   // the ring reads its own rows; only the fold's reads are asserted
            Assert.Equal(Counts(waiting: 2), store.UnreadCountsByRecipient(TenantA)[WorkerA]);
            Assert.Equal(reads, counter.Reads);
        }

        Assert.Single(store.MarkStuck(TenantA, T0.AddMinutes(-10), m => m.MessageId == first));
        Assert.Equal(Counts(waiting: 1, stuck: 1, oldestStuck: T0.AddMinutes(-30)), store.UnreadCountsByRecipient(TenantA)[WorkerA]);

        store.ReadInbox(TenantA, WorkerA, T0, includeRecent: false);
        Assert.Empty(store.UnreadCountsByRecipient(TenantA));

        Enqueue(store, TenantA, Manager, WorkerB, FleetMessageKinds.Message, "three", T0.AddMinutes(-5));
        Assert.Single(store.UnreadCountsByRecipient(TenantA));
    }

    /// <summary>A message another Gateway process wrote (a deploy overlap) is counted once the held counts are older
    /// than their ceiling, and not before.</summary>
    [Fact]
    public void Another_processs_message_is_counted_after_the_ceiling()
    {
        var now = T0;
        var db = _harness.Open();
        var store = new FleetMessageStore(db, () => now);
        var other = new FleetMessageStore(db, () => now);
        Assert.Empty(store.UnreadCountsByRecipient(TenantA));

        Enqueue(other, TenantA, Manager, WorkerA, FleetMessageKinds.Message, "hello", T0);

        now += FleetMessageStore.CountsMaxAge - TimeSpan.FromSeconds(1);
        Assert.Empty(store.UnreadCountsByRecipient(TenantA));
        now += TimeSpan.FromSeconds(1);
        Assert.Single(store.UnreadCountsByRecipient(TenantA));
    }

    // ---------- The stamp ----------

    private sealed class FakeSource : IFleetInboxLineSource
    {
        public Dictionary<string, FleetInboxCounts> Counts { get; } = new(StringComparer.Ordinal);
        public int Reads { get; private set; }

        public IReadOnlyDictionary<string, FleetInboxCounts> UnreadCountsByRecipient(TenantId tenant)
        {
            Reads++;
            return Counts;
        }
    }

    [Fact]
    public void The_stamp_writes_each_rows_own_line_and_clears_the_rest()
    {
        var source = new FakeSource();
        source.Counts[WorkerA] = Counts(waiting: 2);
        var a = new SessionDto { SessionId = WorkerA.ToUpperInvariant() };
        var b = new SessionDto { SessionId = WorkerB, InboxLine = "1 message waiting" }; // a line from an earlier fold

        FleetInboxLineStamp.Stamp(new[] { a, b }, source, TenantA, T0);

        Assert.Equal("2 messages waiting", a.InboxLine);
        Assert.Null(b.InboxLine);
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public void An_empty_inbox_clears_a_line_an_earlier_fold_stamped()
    {
        var source = new FakeSource();
        var a = new SessionDto { SessionId = WorkerA, InboxLine = "2 messages waiting" };

        FleetInboxLineStamp.Stamp(new[] { a }, source, TenantA, T0);

        Assert.Null(a.InboxLine);
    }

    [Fact]
    public void No_source_or_no_account_stamps_null_and_reads_nothing()
    {
        var source = new FakeSource();
        source.Counts[WorkerA] = Counts(waiting: 1);
        var a = new SessionDto { SessionId = WorkerA, InboxLine = "stale" };
        FleetInboxLineStamp.Stamp(new[] { a }, null, TenantA, T0);
        Assert.Null(a.InboxLine);

        a.InboxLine = "stale";
        FleetInboxLineStamp.Stamp(new[] { a }, source, null, T0);
        Assert.Null(a.InboxLine);
        Assert.Equal(0, source.Reads);
    }

    // ---------- Through the one fold, and what it costs ----------

    private static List<SessionDto> Fleet(int n) =>
        Enumerable.Range(0, n)
            .Select(i => new SessionDto
            {
                SessionId = $"dddddddd-0000-0000-0000-{i:D12}",
                ActivityState = "WaitingForInput",
                Status = "Running",
            })
            .ToList();

    [Fact]
    public void The_shared_fold_stamps_the_line_with_its_one_moment()
    {
        var source = new FakeSource();
        var fleet = Fleet(2);
        source.Counts[fleet[1].SessionId] = Counts(stuck: 1, oldestStuck: T0.AddMinutes(-20));

        GatewayEndpoints.StampFleetRolesAndFold(fleet, fleet, tenant: TenantA, nowUtc: T0, inboxLines: source);

        Assert.Null(fleet[0].InboxLine);
        Assert.Equal("1 message stuck, unread for 20 minutes", fleet[1].InboxLine);
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public void The_line_does_not_change_the_colour_the_label_or_the_bucket()
    {
        var quiet = Fleet(1);
        var waiting = Fleet(1);
        var source = new FakeSource();
        source.Counts[waiting[0].SessionId] = Counts(waiting: 3, stuck: 1, oldestStuck: T0.AddHours(-3));

        GatewayEndpoints.StampFleetRolesAndFold(quiet, quiet, tenant: TenantA, nowUtc: T0);
        GatewayEndpoints.StampFleetRolesAndFold(waiting, waiting, tenant: TenantA, nowUtc: T0, inboxLines: source);

        Assert.NotNull(waiting[0].InboxLine);
        Assert.Equal(quiet[0].EffectiveColor, waiting[0].EffectiveColor);
        Assert.Equal(quiet[0].StateLabel, waiting[0].StateLabel);
        Assert.Equal(quiet[0].TriageBucket, waiting[0].TriageBucket);
    }

    [Fact]
    public void One_fold_over_forty_sessions_is_one_query_against_the_inbox()
    {
        var store = new FleetMessageStore(_harness.Open());
        var fleet = Fleet(40);
        foreach (var s in fleet)
            Enqueue(store, TenantA, Manager, s.SessionId, FleetMessageKinds.Message, "hello " + s.SessionId, T0.AddMinutes(-1));
        MarkStuck(store, fleet[0].SessionId, store.OpenMessagesFor(TenantA, fleet[0].SessionId).Select(m => m.MessageId).ToList(), T0);

        using var counter = new DatabaseCommandCounter(_harness.DbPath);
        GatewayEndpoints.StampFleetRolesAndFold(fleet, fleet, tenant: TenantA, nowUtc: T0, inboxLines: store);
        var readers = counter.Readers;
        var others = counter.Others;

        Assert.Equal("1 message stuck, unread for 1 minute", fleet[0].InboxLine);
        Assert.All(fleet.Skip(1), s => Assert.Equal("1 message waiting", s.InboxLine));
        Assert.Equal(1, readers);
        Assert.Equal(0, others);
    }
}
