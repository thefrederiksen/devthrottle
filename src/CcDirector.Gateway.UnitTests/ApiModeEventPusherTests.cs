using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.ApiMode;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// API mode, phase 4 (issue #4070): <see cref="ApiModeEventPusher"/>, the Director's sink that pushes API-mode events to
/// the Gateway, against a fake Gateway that answers as the contract (<see cref="ApiModeEventBatch"/>) says the real one
/// does. No hub, no port, no clock: events are published while the fake Gateway lacks the capability, so they are only
/// held, and the test then awaits the push run itself.
/// </summary>
public sealed class ApiModeEventPusherTests : IAsyncDisposable
{
    private const string SessionId = "8d1f7c2e-4b0a-4c55-9e3d-2a6f0b1c9d77";
    private const string Generation = "gen-a";
    private static readonly DateTime GenerationStarted = new(2026, 10, 16, 13, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset At = new(2026, 10, 16, 13, 0, 5, TimeSpan.Zero);

    private readonly List<ApiModeEventPusher> _pushers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var pusher in _pushers)
            await pusher.DisposeAsync();
    }

    /// <summary>A Gateway that records every batch and, unless told otherwise, stores it: watermark = the batch's last
    /// sequence in its generation.</summary>
    private sealed class FakeGateway
    {
        public readonly List<ApiModeEventBatch> Received = [];
        public bool Supports;
        public Func<ApiModeEventBatch, ApiModeEventWatermark?>? Answer;
        public Exception? Throw;

        public Task<ApiModeEventWatermark?> Push(ApiModeEventBatch batch, CancellationToken ct)
        {
            lock (Received)
                Received.Add(batch);
            if (Throw is { } ex)
                throw ex;
            var answer = Answer is null
                ? new ApiModeEventWatermark { SessionId = batch.SessionId, Generation = batch.Generation, Sequence = Sequences(batch).Last() }
                : Answer(batch);
            return Task.FromResult(answer);
        }
    }

    private ApiModeEventPusher Build(FakeGateway gateway, int maxBatchEvents = ApiModeEventBatch.MaxEvents,
        int maxBatchCharacters = ApiModeEventBatch.MaxCharacters, int maxHeldEvents = ApiModeEventPusher.DefaultMaxHeldEventsPerSession)
    {
        var pusher = new ApiModeEventPusher(gateway.Push, () => gateway.Supports, maxBatchEvents, maxBatchCharacters, maxHeldEvents);
        _pushers.Add(pusher);
        return pusher;
    }

    private static SessionEvent Event(long sequence, string text = "x") => new MessageText
    {
        SessionId = SessionId, Sequence = sequence, At = At,
        TurnId = "turn-1", MessageId = "turn-1-message-1", Channel = MessageTextChannel.Reply, Kind = MessageTextKind.Delta, Text = text,
    };

    private static SessionEventBatch Batch(string generation, params SessionEvent[] events) =>
        new(SessionId, generation, GenerationStarted, events);

    private static List<long> Sequences(ApiModeEventBatch batch) =>
        batch.Events.Select(json => JsonSerializer.Deserialize<SessionEvent>(json, SessionEventJson.Options)!.Sequence).ToList();

    [Fact]
    public async Task PushSessionAsync_HeldEvents_ArePushedInOrderWithGenerationAndSequence()
    {
        // Arrange
        var gateway = new FakeGateway();
        var pusher = Build(gateway);
        pusher.Publish(Batch(Generation, Event(1), Event(2)));
        pusher.Publish(Batch(Generation, Event(3)));
        gateway.Supports = true;

        // Act
        await pusher.PushSessionAsync(SessionId);

        // Assert: one batch, in order, each event its own JSON text in the event model's own form.
        var batch = Assert.Single(gateway.Received);
        Assert.Equal(SessionId, batch.SessionId);
        Assert.Equal(Generation, batch.Generation);
        Assert.Equal(GenerationStarted, batch.GenerationStartedUtc);
        Assert.Equal([1L, 2L, 3L], Sequences(batch));
        Assert.Equal(JsonSerializer.Serialize(Event(1), SessionEventJson.Options), batch.Events[0]);
        Assert.Equal(0, pusher.HeldCount(SessionId));
    }

    [Fact]
    public async Task PushSessionAsync_MoreEventsThanABatchHolds_IsSplitByCount()
    {
        // Arrange
        var gateway = new FakeGateway();
        var pusher = Build(gateway, maxBatchEvents: 2);
        pusher.Publish(Batch(Generation, Enumerable.Range(1, 5).Select(i => Event(i)).ToArray()));
        gateway.Supports = true;

        // Act
        await pusher.PushSessionAsync(SessionId);

        // Assert
        Assert.Equal([[1L, 2L], [3L, 4L], [5L]], gateway.Received.Select(Sequences));
        Assert.Equal(0, pusher.HeldCount(SessionId));
    }

    [Fact]
    public async Task PushSessionAsync_EventsLargerTogetherThanABatchHolds_IsSplitByCharacters()
    {
        // Arrange: each event's JSON is a little over 1,000 characters; a batch may carry 2,500.
        var gateway = new FakeGateway();
        var pusher = Build(gateway, maxBatchCharacters: 2_500);
        var text = new string('a', 1_000);
        pusher.Publish(Batch(Generation, Event(1, text), Event(2, text), Event(3, text)));
        gateway.Supports = true;

        // Act
        await pusher.PushSessionAsync(SessionId);

        // Assert
        Assert.Equal([[1L, 2L], [3L]], gateway.Received.Select(Sequences));
        Assert.All(gateway.Received, b => Assert.True(b.Events.Sum(e => e.Length) <= 2_500));
    }

    [Fact]
    public async Task PushSessionAsync_AWatermarkShortOfTheBatch_DropsOnlyWhatItCoversAndSendsTheRestNextTime()
    {
        // Arrange: the Gateway holds up to sequence 2 of the 4 sent.
        var gateway = new FakeGateway
        {
            Answer = b => new ApiModeEventWatermark { SessionId = b.SessionId, Generation = b.Generation, Sequence = 2 },
        };
        var pusher = Build(gateway);
        pusher.Publish(Batch(Generation, Event(1), Event(2), Event(3), Event(4)));
        gateway.Supports = true;

        // Act
        await pusher.PushSessionAsync(SessionId);
        gateway.Answer = null;
        await pusher.PushSessionAsync(SessionId);

        // Assert
        Assert.Equal([[1L, 2L, 3L, 4L], [3L, 4L]], gateway.Received.Select(Sequences));
        Assert.Equal(0, pusher.HeldCount(SessionId));
    }

    [Fact]
    public async Task PushSessionAsync_AWatermarkThatCoversNone_StopsTheRunAndKeepsTheEvents()
    {
        // Arrange: the Gateway answers its generation with a sequence below everything sent (it lost what it held).
        var gateway = new FakeGateway
        {
            Answer = b => new ApiModeEventWatermark { SessionId = b.SessionId, Generation = b.Generation, Sequence = 0 },
        };
        var pusher = Build(gateway);
        pusher.Publish(Batch(Generation, Event(1), Event(2)));
        gateway.Supports = true;

        // Act: one run, which must end rather than send the same batch for ever.
        await pusher.PushSessionAsync(SessionId);
        var sentByThatRun = gateway.Received.Count;
        gateway.Answer = null;
        await pusher.PushSessionAsync(SessionId);

        // Assert
        Assert.Equal(1, sentByThatRun);
        Assert.Equal(gateway.Received[0].Events, gateway.Received[1].Events);
        Assert.Equal(0, pusher.HeldCount(SessionId));
    }

    [Fact]
    public async Task PushSessionAsync_RefusedForNow_KeepsTheEventsAndSendsTheSameBatchAgain()
    {
        // Arrange: the empty-generation answer.
        var gateway = new FakeGateway
        {
            Answer = b => new ApiModeEventWatermark { SessionId = b.SessionId, Generation = "", Sequence = -1 },
        };
        var pusher = Build(gateway);
        pusher.Publish(Batch(Generation, Event(1), Event(2)));
        gateway.Supports = true;

        // Act: refused for now, then the next trigger.
        await pusher.PushSessionAsync(SessionId);
        var heldAfterRefusal = pusher.HeldCount(SessionId);
        gateway.Answer = null;
        await pusher.PushSessionAsync(SessionId);

        // Assert: the same batch twice, the second stored.
        Assert.Equal(2, heldAfterRefusal);
        Assert.Equal(2, gateway.Received.Count);
        Assert.Equal(gateway.Received[0].Events, gateway.Received[1].Events);
        Assert.Equal(0, pusher.HeldCount(SessionId));
    }

    [Fact]
    public async Task PushSessionAsync_RefusedForGood_DropsTheBatchAndNeverSendsItAgain()
    {
        // Arrange: the null answer.
        var gateway = new FakeGateway { Answer = _ => null };
        var pusher = Build(gateway);
        pusher.Publish(Batch(Generation, Event(1), Event(2)));
        gateway.Supports = true;

        // Act
        await pusher.PushSessionAsync(SessionId);
        await pusher.PushSessionAsync(SessionId);
        await pusher.ResendAllAsync();

        // Assert
        Assert.Single(gateway.Received);
        Assert.Equal(0, pusher.HeldCount(SessionId));
    }

    [Fact]
    public async Task PushSessionAsync_TheHubThrows_KeepsTheEventsAndSendsThemAgain()
    {
        // Arrange
        var gateway = new FakeGateway { Throw = new InvalidOperationException("the Gateway stream is not connected") };
        var pusher = Build(gateway);
        pusher.Publish(Batch(Generation, Event(1), Event(2)));
        gateway.Supports = true;

        // Act
        await pusher.PushSessionAsync(SessionId);
        var heldAfterFailure = pusher.HeldCount(SessionId);
        gateway.Throw = null;
        await pusher.PushSessionAsync(SessionId);

        // Assert
        Assert.Equal(2, heldAfterFailure);
        Assert.Equal(gateway.Received[0].Events, gateway.Received[1].Events);
        Assert.Equal(0, pusher.HeldCount(SessionId));
    }

    [Fact]
    public async Task ResendAllAsync_AfterAGatewayRestart_ResendsEverythingPastTheWatermark()
    {
        // Arrange: the Gateway stored up to sequence 2 of a turn, then went away (restart) while the turn went on.
        var gateway = new FakeGateway
        {
            Answer = b => new ApiModeEventWatermark { SessionId = b.SessionId, Generation = b.Generation, Sequence = 2 },
        };
        var pusher = Build(gateway);
        pusher.Publish(Batch(Generation, Event(1), Event(2), Event(3)));
        gateway.Supports = true;
        await pusher.PushSessionAsync(SessionId);
        gateway.Answer = null;
        gateway.Throw = new InvalidOperationException("the Gateway stream is not connected");
        gateway.Supports = false;
        pusher.Publish(Batch(Generation, Event(4), Event(5)));
        await pusher.PushSessionAsync(SessionId);

        // Act: the Gateway is back, and its Hello on the new connection triggers the resend.
        gateway.Throw = null;
        gateway.Supports = true;
        await pusher.ResendAllAsync();

        // Assert: everything the Gateway had not acknowledged went up again, in order, and nothing it had.
        Assert.Equal([3L, 4L, 5L], Sequences(gateway.Received[^1]));
        Assert.Equal(0, pusher.HeldCount(SessionId));
    }

    [Fact]
    public async Task PushSessionAsync_ANewDirectorGeneration_StartsAtSequenceOneInItsOwnBatch()
    {
        // Arrange: events of the old generation still held, then the session's stream started afresh (a Director
        // restart) with a later generation whose sequence starts again at 1.
        var gateway = new FakeGateway();
        var pusher = Build(gateway);
        pusher.Publish(Batch("gen-old", Event(7), Event(8)));
        pusher.Publish(Batch("gen-new", Event(1), Event(2)));
        gateway.Supports = true;

        // Act
        await pusher.PushSessionAsync(SessionId);

        // Assert: never one batch across two generations; the new one starts at the contract's first sequence.
        Assert.Equal(["gen-old", "gen-new"], gateway.Received.Select(b => b.Generation));
        Assert.Equal(ApiModeEventBatch.FirstSequence, Sequences(gateway.Received[1])[0]);
        Assert.Equal(0, pusher.HeldCount(SessionId));
    }

    [Fact]
    public async Task PushSessionAsync_TheGatewayLacksTheCapability_PushesNothingAndHoldsTheEvents()
    {
        // Arrange
        var gateway = new FakeGateway { Supports = false };
        var pusher = Build(gateway);
        pusher.Publish(Batch(Generation, Event(1), Event(2)));

        // Act
        await pusher.PushSessionAsync(SessionId);
        await pusher.ResendAllAsync();

        // Assert
        Assert.Empty(gateway.Received);
        Assert.Equal(2, pusher.HeldCount(SessionId));
    }

    [Fact]
    public async Task Publish_PastTheMemoryBound_DropsTheOldestAndKeepsTheNewest()
    {
        // Arrange: at most three events held per session while the Gateway cannot take them.
        var gateway = new FakeGateway();
        var pusher = Build(gateway, maxHeldEvents: 3);

        // Act
        pusher.Publish(Batch(Generation, Enumerable.Range(1, 5).Select(i => Event(i)).ToArray()));
        gateway.Supports = true;
        await pusher.PushSessionAsync(SessionId);

        // Assert: the two oldest were dropped (and logged by sequence); the newest three went up.
        Assert.Equal([3L, 4L, 5L], Sequences(Assert.Single(gateway.Received)));
    }

    [Fact]
    public async Task PushSessionAsync_AnEventTooLargeForAnyBatch_IsDroppedByNameAndTheRestGoUp()
    {
        // Arrange
        var gateway = new FakeGateway();
        var pusher = Build(gateway, maxBatchCharacters: 500);
        pusher.Publish(Batch(Generation, Event(1), Event(2, new string('b', 1_000)), Event(3)));
        gateway.Supports = true;

        // Act
        await pusher.PushSessionAsync(SessionId);

        // Assert
        Assert.Equal([[1L], [3L]], gateway.Received.Select(Sequences));
        Assert.Equal(0, pusher.HeldCount(SessionId));
    }
}
