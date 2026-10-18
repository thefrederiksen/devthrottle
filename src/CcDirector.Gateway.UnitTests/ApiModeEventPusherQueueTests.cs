using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.ApiModeConversation;
using CcDirector.Gateway.ApiModeDisplay;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Receipts;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// API mode, phase 4 (issue #4070): a hole in the Director's push starts a new generation inside one Director process,
/// whose prompt queue survives it, while the Gateway takes a generation as one queue. So every new generation starts by
/// re-announcing each prompt still queued (the Delivery Lead's ruling on the review of pull request #4178, finding 1).
///
/// The Director side is real - the session's <see cref="SessionEventStream"/>, the sink that follows the Gateway setting,
/// and <see cref="ApiModeEventPusher"/> - and so is the Gateway side: the ingest, the buffer and the receipt store over
/// the harness database, read with the place in line exactly as the receipts route reads it. The hub between them is a
/// delegate call. No clock runs, no process starts, nothing sleeps and no port is opened.
/// </summary>
public sealed class ApiModeEventPusherQueueTests : IAsyncDisposable
{
    private const string Sid = "8d1f7c2e-4b0a-4c55-9e3d-2a6f0b1c9d77";
    private const string Director = "dir-devlinux";
    private static readonly TenantId Alice = new("aaaaaaaa-0000-4000-8000-00000000000a");
    private static readonly DateTimeOffset At = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly AsyncLocalTenantContext _ambient = new();
    private readonly HostedTenantBoundary _boundary;
    private readonly ApiModeReceiptStore _receipts;
    private readonly ApiModeEventIngest _ingest;
    private readonly List<ApiModeEventBatch> _received = [];
    private readonly List<ApiModeEventPusher> _pushers = [];
    private readonly LoggingSessionEventSink _gatewayOff = new();
    private ISessionEventSink _current;
    private bool _gatewayHasThePush = true;
    private readonly SessionEventStream _stream;

    public ApiModeEventPusherQueueTests()
    {
        var path = _harness.LegacyPath("devices.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _boundary = new HostedTenantBoundary(_ambient, new DeviceRegistry(_harness.Open(), path), hosted: false);
        _receipts = new ApiModeReceiptStore(_harness.Open(_ambient), () => At.UtcDateTime);
        _ingest = new ApiModeEventIngest(new ApiModeEventBuffer(), _receipts, new ApiModeConversationStore(_harness.Open(_ambient), () => At.UtcDateTime), _boundary);
        _current = _gatewayOff;
        // As the session manager wires it: the stream's sink is whichever the Director's setting makes current.
        _stream = new SessionEventStream(Sid, new ForwardingSessionEventSink(() => _current), new FakeTimeProvider(At));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pusher in _pushers)
            await pusher.DisposeAsync();
        _harness.Dispose();
    }

    private ApiModeEventPusher Pusher(int maxHeldEvents = ApiModeEventPusher.DefaultMaxHeldEventsPerSession)
    {
        var pusher = new ApiModeEventPusher(Push, () => _gatewayHasThePush, maxHeldEvents: maxHeldEvents);
        _pushers.Add(pusher);
        return pusher;
    }

    private Task<ApiModeEventWatermark?> Push(ApiModeEventBatch batch, CancellationToken ct)
    {
        _received.Add(batch);
        return Task.FromResult(_ingest.Accept(Alice, Director, batch));
    }

    /// <summary>Each receipt's place in line as the receipts route answers it: null when it has none.</summary>
    private Dictionary<string, int?> Places()
    {
        using (_boundary.EnterScope(Alice))
        {
            var stored = _receipts.ReadWithPositions(Sid);
            ApiModeLiveRuling.PlaceInLine(stored);
            return stored.ToDictionary(r => r.Receipt.PromptId, r => r.Receipt.QueuePosition);
        }
    }

    private string Words(string promptId)
    {
        using (_boundary.EnterScope(Alice))
        {
            var stored = _receipts.ReadWithPositions(Sid);
            ApiModeLiveRuling.PlaceInLine(stored);
            return ApiModeLiveRuling.ReceiptWords(Assert.Single(stored, r => r.Receipt.PromptId == promptId).Receipt);
        }
    }

    private long Next() => _stream.Numbering.NextSequence();

    private PromptQueued Queued(string promptId, int position) => new()
    {
        SessionId = Sid, Sequence = Next(), At = At,
        PromptId = promptId, PromptText = $"text of {promptId}", Reason = PromptQueuedReason.TurnRunning, Position = position,
    };

    private PromptDelivered Delivered(string promptId) => new()
    {
        SessionId = Sid, Sequence = Next(), At = At,
        PromptId = promptId, PromptText = $"text of {promptId}", Disposition = PromptDisposition.Started, Acknowledgement = "{}",
    };

    private MessageText Text() => new()
    {
        SessionId = Sid, Sequence = Next(), At = At,
        TurnId = "turn-1", MessageId = "turn-1-message-1", Channel = MessageTextChannel.Reply, Kind = MessageTextKind.Delta, Text = "x",
    };

    private ToolCallStarted Tool() => new()
    {
        SessionId = Sid, Sequence = Next(), At = At,
        TurnId = "turn-1", ToolCallId = "call-" + Guid.NewGuid().ToString("N"), ToolName = "bash", Kind = ToolCallKind.Execute, Input = "ls",
    };

    private TurnStarted TurnStarts(params string[] promptIds) => new()
    {
        SessionId = Sid, Sequence = Next(), At = At, TurnId = "turn-1", PromptIds = promptIds,
    };

    private static List<SessionEvent> Events(ApiModeEventBatch batch) =>
        batch.Events.Select(json => JsonSerializer.Deserialize<SessionEvent>(json, SessionEventJson.Options)!).ToList();

    [Fact]
    public async Task Publish_AHoleWhileTheGatewayWasOff_PromptsQueuedBeforeAndAfterItKeepTheirPlaces()
    {
        // Arrange: p1 and p2 queued and on the Gateway, 1st and 2nd in line.
        var pusher = Pusher();
        _current = pusher;
        _stream.Publish([Queued("p1", 1), Queued("p2", 2)]);
        await pusher.PushSessionAsync(Sid);
        Assert.Equal(new Dictionary<string, int?> { ["p1"] = 1, ["p2"] = 2 }, Places());

        // Act: the Gateway is turned off in the settings while the turn writes text, then on again; then p3 is queued.
        _current = _gatewayOff;
        _stream.Publish([Text()]);
        _current = pusher;
        _stream.Publish([Queued("p3", 3)]);
        await pusher.PushSessionAsync(Sid);

        // Assert: p3 went up in a new generation, and every prompt still has the place the Director gives it.
        Assert.NotEqual(_stream.Generation, _received[^1].Generation);
        Assert.Equal(new Dictionary<string, int?> { ["p1"] = 1, ["p2"] = 2, ["p3"] = 3 }, Places());
        Assert.StartsWith("Queued, 3rd in line", Words("p3"));

        // And the line keeps moving: p1 is delivered, so p2 and p3 move up.
        _stream.Publish([Delivered("p1")]);
        await pusher.PushSessionAsync(Sid);
        Assert.Equal(new Dictionary<string, int?> { ["p1"] = null, ["p2"] = 1, ["p3"] = 2 }, Places());
    }

    [Fact]
    public async Task Publish_TheMemoryBoundDropsTheQueuedEventsThemselves_TheirPromptsStillReachTheGatewayInLine()
    {
        // Arrange: the Gateway cannot take the push yet, and at most three events are held.
        _gatewayHasThePush = false;
        var pusher = Pusher(maxHeldEvents: 3);
        _current = pusher;

        // Act: p1 and p2 queued, then four text events push both PromptQueued events past the bound; then p3 is queued,
        // and the Gateway can take the push.
        _stream.Publish([Queued("p1", 1), Queued("p2", 2)]);
        _stream.Publish([Text(), Text(), Text(), Text()]);
        _stream.Publish([Queued("p3", 3)]);
        _gatewayHasThePush = true;
        await pusher.PushSessionAsync(Sid);

        // Assert: the Gateway never saw p1's or p2's own event, and still holds all three in the Director's order.
        Assert.Equal(new Dictionary<string, int?> { ["p1"] = 1, ["p2"] = 2, ["p3"] = 3 }, Places());
    }

    [Fact]
    public async Task Publish_AHoleWithNothingQueued_TheNewGenerationReannouncesNothing()
    {
        // Arrange: p1 was queued and delivered before the hole.
        var pusher = Pusher();
        _current = pusher;
        _stream.Publish([Queued("p1", 1), Delivered("p1")]);
        await pusher.PushSessionAsync(Sid);

        // Act
        _current = _gatewayOff;
        _stream.Publish([Text()]);
        _current = pusher;
        var after = Text();
        _stream.Publish([after]);
        await pusher.PushSessionAsync(Sid);

        // Assert: the new generation is the event after the hole alone, at sequence 1.
        var batch = _received[^1];
        Assert.NotEqual(_stream.Generation, batch.Generation);
        var sent = Assert.Single(Events(batch));
        Assert.IsType<MessageText>(sent);
        Assert.Equal(1, sent.Sequence);
        Assert.Equal(new Dictionary<string, int?> { ["p1"] = null }, Places());
    }

    [Fact]
    public async Task Publish_AHoleWithPromptsQueued_TheReannouncementsComeFirstInQueueOrder()
    {
        // Arrange: p1, p2 and p3 queued; p2 refused, so p1 and p3 are still waiting.
        var pusher = Pusher();
        _current = pusher;
        _stream.Publish([Queued("p1", 1), Queued("p2", 2), Queued("p3", 3)]);
        _stream.Publish([new PromptRefused
        {
            SessionId = Sid, Sequence = Next(), At = At,
            PromptId = "p2", PromptText = "text of p2", Reason = "busy", RefusedBy = PromptRefusedBy.Agent,
        }]);
        await pusher.PushSessionAsync(Sid);

        // Act: a hole, then the delivery of p1.
        _current = _gatewayOff;
        _stream.Publish([Text()]);
        _current = pusher;
        _stream.Publish([Delivered("p1")]);
        await pusher.PushSessionAsync(Sid);

        // Assert: the new generation opens with p1 then p3, each with its place, reason and text, numbered 1 and 2;
        // then the delivery, numbered 3.
        var events = Events(_received[^1]);
        Assert.Collection(events,
            e =>
            {
                var q = Assert.IsType<PromptQueued>(e);
                Assert.Equal(("p1", 1, 1L, PromptQueuedReason.TurnRunning, "text of p1"), (q.PromptId, q.Position, q.Sequence, q.Reason, q.PromptText));
            },
            e =>
            {
                var q = Assert.IsType<PromptQueued>(e);
                Assert.Equal(("p3", 2, 2L, PromptQueuedReason.TurnRunning, "text of p3"), (q.PromptId, q.Position, q.Sequence, q.Reason, q.PromptText));
            },
            e =>
            {
                var d = Assert.IsType<PromptDelivered>(e);
                Assert.Equal(("p1", 3L), (d.PromptId, d.Sequence));
            });
        Assert.Equal(new Dictionary<string, int?> { ["p1"] = null, ["p2"] = null, ["p3"] = 1 }, Places());
    }

    [Fact]
    public async Task Publish_TheMemoryBoundDropsPartOfAReannouncement_TheRestIsReplacedAndOneGenerationGoesUp()
    {
        // Arrange: the Gateway cannot take the push yet, at most two events are held, and p1 and p2 are queued.
        _gatewayHasThePush = false;
        var pusher = Pusher(maxHeldEvents: 2);
        _current = pusher;
        _stream.Publish([Queued("p1", 1), Queued("p2", 2)]);

        // Act: a hole while the Gateway is off, so the next event's generation opens by re-announcing p1 and p2; the bound
        // then drops p1's and p2's own events and p1's re-announcement, but not p2's (review of pull request #4190,
        // finding 1). Then the Gateway can take the push.
        _current = _gatewayOff;
        _stream.Publish([Text()]);
        _current = pusher;
        _stream.Publish([Text()]);
        _gatewayHasThePush = true;
        await pusher.PushSessionAsync(Sid);

        // Assert: p2's leftover re-announcement was replaced, not sent as a generation of its own: the Gateway gets one
        // generation, which re-announces the whole line and then the text.
        var batch = Assert.Single(_received);
        Assert.Collection(Events(batch),
            e => Assert.Equal(("p1", 1, 1L), (Assert.IsType<PromptQueued>(e).PromptId, ((PromptQueued)e).Position, e.Sequence)),
            e => Assert.Equal(("p2", 2, 2L), (Assert.IsType<PromptQueued>(e).PromptId, ((PromptQueued)e).Position, e.Sequence)),
            e => Assert.Equal(3L, Assert.IsType<MessageText>(e).Sequence));
        Assert.Equal(new Dictionary<string, int?> { ["p1"] = 1, ["p2"] = 2 }, Places());
    }

    // ----- a queued prompt whose ending fell in the hole (item 9; review of pull request #4209, finding 1) -----

    [Fact]
    public async Task Publish_AHoleSwallowsADelivery_ThePromptIsRuledUnknownAtTheFirstEventAfterTheReannouncements()
    {
        // Arrange: p1, p2 and p3 queued and on the Gateway.
        var pusher = Pusher();
        _current = pusher;
        _stream.Publish([Queued("p1", 1), Queued("p2", 2), Queued("p3", 3)]);
        await pusher.PushSessionAsync(Sid);

        // Act: p1 is delivered while the Gateway is turned off, so its delivery falls in the hole; then a reply streams
        // (which proves nothing), and then a tool runs.
        _current = _gatewayOff;
        _stream.Publish([Delivered("p1")]);
        _current = pusher;
        _stream.Publish([Text()]);
        await pusher.PushSessionAsync(Sid);
        var reannounced = Events(_received[^1]).OfType<PromptQueued>().Select(q => q.PromptId).ToList();
        var beforeTheProof = Words("p1");
        _stream.Publish([Tool()]);
        await pusher.PushSessionAsync(Sid);

        // Assert: the new generation re-announced p2 and p3 only; p1 read Queued until the first event after the
        // re-announcements that proves, and then the Unknown words; p2 and p3 moved up to 1st and 2nd.
        Assert.Equal(new[] { "p2", "p3" }, reannounced);
        Assert.StartsWith("Queued", beforeTheProof);
        Assert.Equal(ApiModeLostQueuedPrompt.Words, Words("p1"));
        Assert.StartsWith("Queued, 1st in line", Words("p2"));
        Assert.StartsWith("Queued, 2nd in line", Words("p3"));
    }

    [Fact]
    public async Task Publish_AHoleWhileThePromptStaysQueued_ItIsReannouncedAndStaysQueued()
    {
        // Arrange
        var pusher = Pusher();
        _current = pusher;
        _stream.Publish([Queued("p1", 1)]);
        await pusher.PushSessionAsync(Sid);

        // Act: a hole, then a tool runs.
        _current = _gatewayOff;
        _stream.Publish([Text()]);
        _current = pusher;
        _stream.Publish([Tool()]);
        await pusher.PushSessionAsync(Sid);

        // Assert
        Assert.NotEqual(_stream.Generation, _received[^1].Generation);
        Assert.StartsWith("Queued, 1st in line", Words("p1"));
    }

    [Fact]
    public async Task Publish_AHoleSwallowsADeliveryAndTheNextEventIsItsTurnsStart_ThePromptIsNotRuledUnknown()
    {
        // Arrange
        var pusher = Pusher();
        _current = pusher;
        _stream.Publish([Queued("p1", 1)]);
        await pusher.PushSessionAsync(Sid);

        // Act: p1's delivery falls in the hole; the generation after it opens with the turn that carries p1 (review of
        // pull request #4209, finding 2), and a tool runs in it.
        _current = _gatewayOff;
        _stream.Publish([Delivered("p1")]);
        _current = pusher;
        _stream.Publish([TurnStarts("p1"), Tool()]);
        await pusher.PushSessionAsync(Sid);

        // Assert: the turn's start is a record of p1, so it is not "no record of sending it".
        Assert.IsType<TurnStarted>(Assert.Single(Events(_received[^1]), e => e.Sequence == 1));
        Assert.NotEqual(ApiModeLostQueuedPrompt.Words, Words("p1"));
    }

    [Fact]
    public void DescribeDropped_OwnEventsAndAReannouncement_NamesTheReannouncementByItsPromptNotItsSequence()
    {
        // Arrange: the session's own events 1 and 2, then p1 re-announced at a hole, given sequence 2 to go up under.
        PromptQueued Own(long sequence) => new()
        {
            SessionId = Sid, Sequence = sequence, At = At,
            PromptId = $"own-{sequence}", PromptText = "t", Reason = PromptQueuedReason.TurnRunning, Position = 1,
        };
        var reannouncement = Own(2) with { PromptId = "p1" };

        // Act
        var words = ApiModeEventPusher.DescribeDropped([(Own(1), false), (Own(2), false), (reannouncement, true)]);

        // Assert
        Assert.Equal("sequences 1..2 (2 events) and the re-announcement of 1 queued prompt(s) [p1], not yet sent " +
                     "(made at a hole, not events of the session)", words);
    }

    [Fact]
    public void DescribeDropped_OnlyReannouncements_NamesNoSequence()
    {
        // Arrange
        var reannouncement = new PromptQueued
        {
            SessionId = Sid, Sequence = 4, At = At,
            PromptId = "p2", PromptText = "t", Reason = PromptQueuedReason.TurnRunning, Position = 2,
        };

        // Act
        var words = ApiModeEventPusher.DescribeDropped([(reannouncement, true)]);

        // Assert
        Assert.DoesNotContain("sequence", words);
        Assert.Contains("[p2]", words);
    }
}
