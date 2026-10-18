using CcDirector.Core.Tenancy;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Receipts;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.ApiModeEvents.ApiModeEventTestData;

namespace CcDirector.Gateway.Tests.ApiModeEvents;

/// <summary>
/// The Gateway's ingest of API-mode session events (mission #4070, phase 2), driven with a REAL receipt store over the
/// harness database, the REAL buffer, and a REAL hosted tenant boundary (two accounts over one database). Each of the
/// five ways a prompt can go is replayed as the event sequence the event model makes of its recorded Pi fixture, and
/// what is asserted is what a reader would see: the receipt (durable) and the held events (live). Then the properties
/// the ingest promises: a duplicate or late batch stores nothing new, an older generation is refused, a later one
/// starts its sequence afresh, a malformed batch stores nothing, and two accounts never see each other's events.
/// </summary>
public sealed class ApiModeEventIngestTests : IDisposable
{
    private static readonly TenantId Alice = new("aaaaaaaa-0000-4000-8000-00000000000a");
    private static readonly TenantId Bob = new("bbbbbbbb-0000-4000-8000-00000000000b");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly AsyncLocalTenantContext _ambient = new();
    private readonly HostedTenantBoundary _boundary;
    private readonly ApiModeReceiptStore _receipts;
    private readonly ApiModeEventBuffer _buffer = new();
    private readonly ApiModeEventIngest _ingest;

    public ApiModeEventIngestTests()
    {
        var path = _harness.LegacyPath("devices.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _boundary = new HostedTenantBoundary(_ambient, new DeviceRegistry(_harness.Open(), path), hosted: false);
        _receipts = new ApiModeReceiptStore(_harness.Open(_ambient), () => T0.UtcDateTime);
        _ingest = new ApiModeEventIngest(_buffer, _receipts, _boundary);
    }

    public void Dispose() => _harness.Dispose();

    private ApiModeEventWatermark? Push(ApiModeEventBatch batch, TenantId? tenant = null) => _ingest.Accept(tenant ?? Alice, Director, batch);

    private IReadOnlyList<ApiModeReceiptDto> Receipts(TenantId? tenant = null)
    {
        using (_boundary.EnterScope(tenant ?? Alice))
            return _receipts.Read(Sid);
    }

    private ApiModeReceiptDto Receipt(string promptId, TenantId? tenant = null) => Assert.Single(Receipts(tenant), r => r.PromptId == promptId);

    private IEnumerable<long> HeldSequences(TenantId? tenant = null) => _buffer.Read(tenant ?? Alice, Sid).Events.Select(e => e.Event.Sequence);

    // ----- the recorded fixtures' outcomes -----

    [Fact]
    public void Accept_DeliveredThenDone_TheReceiptIsDeliveredAndDone_AndEveryEventIsHeld()
    {
        var mark = Push(A(DeliveredThenDone()));

        Assert.NotNull(mark);
        Assert.Equal("gen-a", mark.Generation);
        Assert.Equal(7, mark.Sequence);
        var r = Receipt("p1");
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Equal("started", r.Disposition);
        Assert.Equal(PlainAck, r.Acknowledgement);
        Assert.Equal("done", r.TurnOutcome);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7 }, HeldSequences());
        Assert.IsType<TurnEnded>(_buffer.Read(Alice, Sid).Events[^1].Event);
    }

    [Fact]
    public void Accept_RefusedByTheAgent_TheReceiptCarriesPisOwnWords()
    {
        Push(A(RefusedByTheAgent()));

        var refused = Receipt("p2");
        Assert.Equal(ApiModeReceiptState.Refused, refused.State);
        Assert.Equal("agent", refused.RefusedBy);
        Assert.Equal(RefusalBusy, refused.RefusalReason);
        Assert.Null(refused.TurnOutcome);
        var first = Receipt("p1");
        Assert.Equal(ApiModeReceiptState.Delivered, first.State);
        Assert.Equal("done", first.TurnOutcome);
    }

    [Fact]
    public void Accept_RefusedByTheDirectorBeforeAcknowledgement_TheReceiptIsRefusedAndNeverDelivered()
    {
        Push(A(RefusedByTheDirectorBeforeAcknowledgement()));

        var r = Receipt("p1");
        Assert.Equal(ApiModeReceiptState.Refused, r.State);
        Assert.Equal("director", r.RefusedBy);
        Assert.Equal("Pi exited before acknowledging", r.RefusalReason);
        Assert.Null(r.Acknowledgement);
        Assert.IsType<ConnectionEnded>(_buffer.Read(Alice, Sid).Events[^1].Event);
    }

    [Fact]
    public void Accept_DeliveredThenFailedMidTurn_TheReceiptShowsFailedBesideDelivered()
    {
        Push(A(DeliveredThenFailedMidTurn()));

        var r = Receipt("p1");
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Equal(ExitAck, r.Acknowledgement);
        Assert.Equal("failed", r.TurnOutcome);
        Assert.Equal("Pi exited during the turn (exit code 137)", r.TurnOutcomeReason);
    }

    [Fact]
    public void Accept_DeliveredThenCancelled_TheReceiptShowsCancelledBesideDelivered()
    {
        Push(A(DeliveredThenCancelled()));

        var r = Receipt("p1");
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Equal("cancelled", r.TurnOutcome);
        Assert.Equal("stopped by the user", r.TurnOutcomeReason);
    }

    [Fact]
    public void Accept_AQueuedPrompt_IsQueuedUntilDelivered()
    {
        Push(A(Queued(1, "p1", PromptQueuedReason.UserHasTheWheel)));
        Assert.Equal(ApiModeReceiptState.Queued, Receipt("p1").State);
        Assert.Equal("userHasTheWheel", Receipt("p1").QueueReason);

        Push(A(Delivered(2, "p1", PlainAck)));

        Assert.Equal(ApiModeReceiptState.Delivered, Receipt("p1").State);
    }

    [Fact]
    public void Accept_TheSameFixtureSplitAcrossBatches_EndsExactlyAsOneBatchWould()
    {
        var events = DeliveredThenFailedMidTurn();

        Push(A(events[..2]));
        Push(A(events[2..4]));
        Push(A(events[4..]));

        Assert.Equal("failed", Receipt("p1").TurnOutcome);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, HeldSequences());
    }

    // ----- duplicates, late batches, generations -----

    [Fact]
    public void Accept_TheSameBatchTwice_StoresNothingNewAndDeliversEachEventOnce()
    {
        var seen = new List<long>();
        using var sub = _buffer.Subscribe(Alice, Sid, null, e => seen.Add(e.Event.Sequence));
        Push(A(DeliveredThenDone()));

        var mark = Push(A(DeliveredThenDone()));

        Assert.Equal(7, mark!.Sequence);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7 }, seen);
        Assert.Equal(7, HeldSequences().Count());
    }

    [Fact]
    public void Accept_ALateBatchThatOverlaps_StoresOnlyTheEventsAboveTheHighestHeld()
    {
        var events = DeliveredThenDone();
        Push(A(events[..5]));                 // 1..5

        var mark = Push(A(events[3..]));      // 4..7: 4 and 5 are duplicates

        Assert.Equal(7, mark!.Sequence);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7 }, HeldSequences());
    }

    [Fact]
    public void Accept_ALateEarlierEventAfterTheTurnEnded_CannotMoveTheSessionBackwards()
    {
        Push(A(DeliveredThenDone()));

        // The queued event of p1 that the Director sent first, arriving late as its own batch.
        Push(A(Queued(1, "p1", PromptQueuedReason.AgentStarting)));

        var r = Receipt("p1");
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Null(r.QueueReason);
        Assert.Equal(7, HeldSequences().Count());
    }

    [Fact]
    public void Accept_AnOlderGeneration_IsRefusedAndNothingChanges()
    {
        Push(B(Ready(1), Queued(2, "p1")));

        var mark = Push(A(Ready(1), Delivered(2, "p1", PlainAck), Started(3, "t1", "p1")));

        Assert.Null(mark);
        Assert.Equal(ApiModeReceiptState.Queued, Receipt("p1").State);
        Assert.Equal("gen-b", _buffer.Read(Alice, Sid).Generation);
        Assert.Equal(new long[] { 1, 2 }, HeldSequences());
    }

    [Fact]
    public void Accept_ALaterGeneration_ReplacesTheSessionsAndCountsItsSequenceAfresh()
    {
        Push(A(Ready(1), Delivered(2, "p1", ExitAck), Started(3, "t1", "p1"), Text(4, "t1", "Once")));

        // The Director restarted: a new generation, counting from 0, closes the turn the old process left open.
        var mark = Push(B(Ready(1), Ended(2, "t1", TurnOutcome.Failed, "the Director restarted during the turn", null)));

        Assert.Equal("gen-b", mark!.Generation);
        Assert.Equal(2, mark.Sequence);
        Assert.Equal("failed", Receipt("p1").TurnOutcome);
        var held = _buffer.Read(Alice, Sid);
        Assert.Equal("gen-b", held.Generation);
        Assert.Equal(new long[] { 1, 2 }, held.Events.Select(e => e.Event.Sequence));
    }

    // ----- a Director older than promptText (the Gateway deploys before every Director updates) -----

    public static TheoryData<string> PromptEventsWithoutText => new() { "PromptQueued", "PromptDelivered", "PromptRefused" };

    [Theory]
    [MemberData(nameof(PromptEventsWithoutText))]
    public void Parse_APromptEventWithoutItsText_ReadsWithNullTextAndIsNotRefused(string type)
    {
        SessionEvent e = type switch
        {
            "PromptQueued" => Queued(1, "p1"),
            "PromptDelivered" => Delivered(1, "p1", PlainAck),
            _ => Refused(1, "p1", RefusalBusy, PromptRefusedBy.Agent),
        };
        var batch = A(e);
        batch.Events[0] = JsonWithoutPromptText(e);
        Assert.DoesNotContain("promptText", batch.Events[0]);

        var read = Assert.Single(ApiModeEventIngest.Parse(batch)).Event;

        Assert.Equal(type, read.GetType().Name);
        Assert.Null(read switch
        {
            PromptQueued q => q.PromptText,
            PromptDelivered d => d.PromptText,
            PromptRefused r => r.PromptText,
            _ => throw new Xunit.Sdk.XunitException($"read as {read.GetType().Name}"),
        });
    }

    [Fact]
    public void Accept_ABatchFromADirectorOlderThanPromptText_IsAcceptedWholeAndFeedsTheReceipts()
    {
        var events = new SessionEvent[]
        {
            Ready(1),
            Queued(2, "p1", PromptQueuedReason.AgentStarting),
            Delivered(3, "p1", RefusalFirstAck),
            Started(4, "t1", "p1"),
            Refused(5, "p2", RefusalBusy, PromptRefusedBy.Agent),
            Ended(6, "t1", TurnOutcome.Done, null, "stop"),
        };
        var batch = AFromAnOlderDirector(events);
        Assert.All(batch.Events, json => Assert.DoesNotContain("promptText", json));

        var mark = Push(batch);

        Assert.NotNull(mark);
        Assert.Equal(6, mark.Sequence);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, HeldSequences());
        var delivered = Receipt("p1");
        Assert.Equal(ApiModeReceiptState.Delivered, delivered.State);
        Assert.Equal(RefusalFirstAck, delivered.Acknowledgement);
        Assert.Equal("done", delivered.TurnOutcome);
        var refused = Receipt("p2");
        Assert.Equal(ApiModeReceiptState.Refused, refused.State);
        Assert.Equal(RefusalBusy, refused.RefusalReason);
    }

    // ----- a malformed batch stores nothing -----

    public static TheoryData<string> Faults => new()
    {
        "another-session", "sequence-not-rising", "failed-without-reason", "not-json", "unknown-type",
        "too-many", "too-many-characters", "no-generation", "no-start", "sequence-zero", "long-prompt-id",
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public void Accept_AMalformedBatch_IsRefusedWholeAndNothingIsStored(string fault)
    {
        var batch = A(Ready(1), Delivered(2, "p1", PlainAck), Started(3, "t1", "p1"));
        switch (fault)
        {
            case "another-session": batch.Events.Add(Json(Queued(4, "p2", sid: "another-session"))); break;
            case "sequence-not-rising": batch.Events.Add(Json(Queued(3, "p2"))); break;
            case "failed-without-reason": batch.Events.Add(Json(Ended(4, "t1", TurnOutcome.Failed, null, null))); break;
            case "not-json": batch.Events.Add("{ this is not json"); break;
            case "unknown-type": batch.Events.Add("""{"type":"PromptTeleported","sessionId":"x","sequence":9,"at":"2026-10-16T12:00:00Z"}"""); break;
            case "too-many": batch.Events.AddRange(Enumerable.Range(4, ApiModeEventBatch.MaxEvents).Select(i => Json(Text(i, "t1", "x")))); break;
            case "too-many-characters": batch.Events.Add(Json(Text(4, "t1", new string('x', ApiModeEventBatch.MaxCharacters)))); break;
            case "no-generation": batch.Generation = ""; break;
            case "no-start": batch.GenerationStartedUtc = default; break;
            case "sequence-zero": batch.Events.Insert(0, Json(Ready(0))); break;
            case "long-prompt-id": batch.Events.Add(Json(Queued(4, new string('p', ApiModeReceiptStore.MaxIdLength + 1)))); break;
        }

        Assert.ThrowsAny<ArgumentException>(() => Push(batch));

        Assert.Empty(Receipts());
        Assert.Empty(HeldSequences());
    }

    // ----- two accounts -----

    [Fact]
    public void Accept_TheSameSessionIdInTwoAccounts_NeverMeets()
    {
        var bobSaw = new List<ApiModeHeldEvent>();
        using var bobs = _buffer.Subscribe(Bob, Sid, null, bobSaw.Add);

        Push(A(DeliveredThenDone()), Alice);
        Push(A(RefusedByTheDirectorBeforeAcknowledgement()), Bob);

        Assert.Equal(ApiModeReceiptState.Delivered, Receipt("p1", Alice).State);
        Assert.Equal(ApiModeReceiptState.Refused, Receipt("p1", Bob).State);
        Assert.Equal(7, HeldSequences(Alice).Count());
        Assert.Equal(3, HeldSequences(Bob).Count());
        Assert.Equal(3, bobSaw.Count);
        Assert.All(bobSaw, e => Assert.IsNotType<PromptDelivered>(e.Event));
    }
}
