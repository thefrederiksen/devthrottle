using CcDirector.Core.ApiMode;
using CcDirector.Gateway.Contracts.ApiMode;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.UnitTests.ApiMode;

/// <summary>
/// API mode, phase 4 (issue #4070): <see cref="QueuedPrompts"/>, the line of a session's prompts still waiting for their
/// receipt, as its own events say, and <see cref="SessionEventStream"/> carrying that line on every batch. The pusher
/// re-announces it at the start of every new generation, so it must be the line the Gateway keeps from the same events.
/// </summary>
public sealed class QueuedPromptsTests
{
    private const string Sid = "8d1f7c2e-4b0a-4c55-9e3d-2a6f0b1c9d77";
    private static readonly DateTimeOffset At = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static PromptQueued Queued(long sequence, string promptId, PromptQueuedReason reason = PromptQueuedReason.TurnRunning, int position = 1) => new()
    {
        SessionId = Sid, Sequence = sequence, At = At.AddSeconds(sequence),
        PromptId = promptId, PromptText = $"  text of {promptId}\n", Reason = reason, Position = position,
    };

    private static PromptDelivered Delivered(long sequence, string promptId) => new()
    {
        SessionId = Sid, Sequence = sequence, At = At,
        PromptId = promptId, PromptText = $"  text of {promptId}\n", Disposition = PromptDisposition.Started, Acknowledgement = "{}",
    };

    private static PromptRefused Refused(long sequence, string promptId) => new()
    {
        SessionId = Sid, Sequence = sequence, At = At,
        PromptId = promptId, PromptText = $"  text of {promptId}\n", Reason = "busy", RefusedBy = PromptRefusedBy.Agent,
    };

    private static TurnStarted Started(long sequence, string promptId) => new()
    {
        SessionId = Sid, Sequence = sequence, At = At, TurnId = "turn-1", PromptIds = [promptId],
    };

    private static List<string> Ids(QueuedPrompts line) => line.Prompts.Select(p => p.PromptId).ToList();

    [Fact]
    public void After_QueuedDeliveredAndRefused_KeepsTheStillQueuedPromptsInTheOrderTheyWereQueued()
    {
        // Act
        var line = QueuedPrompts.Empty.After([
            Queued(1, "p1"), Queued(2, "p2"), Queued(3, "p3"), Started(4, "p0"), Delivered(5, "p1"), Refused(6, "p3"), Queued(7, "p4"),
        ]);

        // Assert
        Assert.Equal(["p2", "p4"], Ids(line));
    }

    [Fact]
    public void After_ARefusedPromptQueuedAgain_JoinsTheEndOfTheLine()
    {
        // Arrange
        var line = QueuedPrompts.Empty.After([Queued(1, "p1"), Queued(2, "p2"), Refused(3, "p1")]);

        // Act
        line = line.After(Queued(4, "p1"));

        // Assert
        Assert.Equal(["p2", "p1"], Ids(line));
    }

    [Fact]
    public void After_AnEventThatIsNotAPromptsOrAnEndForAPromptNotQueued_ReturnsTheSameLine()
    {
        // Arrange
        var line = QueuedPrompts.Empty.After(Queued(1, "p1"));

        // Act / Assert
        Assert.Same(line, line.After(Started(2, "p0")));
        Assert.Same(line, line.After(Delivered(3, "p0")));
    }

    [Fact]
    public void After_DoesNotChangeTheLineItWasCalledOn()
    {
        // Arrange
        var before = QueuedPrompts.Empty.After(Queued(1, "p1"));

        // Act
        _ = before.After(Delivered(2, "p1"));

        // Assert
        Assert.Equal(["p1"], Ids(before));
    }

    [Fact]
    public void Reannounce_TwoQueued_EachKeepsItsIdTextReasonAndTimeWithItsPlaceAndANewSequence()
    {
        // Arrange: p1 queued while Pi was starting, at position 2 behind a prompt since delivered.
        var line = QueuedPrompts.Empty.After([
            Queued(1, "p0"), Queued(2, "p1", PromptQueuedReason.AgentStarting, position: 2), Delivered(3, "p0"), Queued(4, "p2", position: 2),
        ]);

        // Act
        var events = line.Reannounce(firstSequence: 9);

        // Assert
        Assert.Collection(events,
            p1 =>
            {
                Assert.Equal("p1", p1.PromptId);
                Assert.Equal("  text of p1\n", p1.PromptText);
                Assert.Equal(PromptQueuedReason.AgentStarting, p1.Reason);
                Assert.Equal(At.AddSeconds(2), p1.At);
                Assert.Equal(1, p1.Position);
                Assert.Equal(9, p1.Sequence);
            },
            p2 =>
            {
                Assert.Equal("p2", p2.PromptId);
                Assert.Equal(PromptQueuedReason.TurnRunning, p2.Reason);
                Assert.Equal(2, p2.Position);
                Assert.Equal(10, p2.Sequence);
            });
    }

    [Fact]
    public void Reannounce_NothingQueued_IsEmpty()
    {
        // Act / Assert
        Assert.Empty(QueuedPrompts.Empty.After([Queued(1, "p1"), Delivered(2, "p1")]).Reannounce(1));
    }

    [Fact]
    public void Publish_EveryBatch_CarriesTheLineAsItStoodBeforeIt()
    {
        // Arrange
        var sink = new RecordingSink();
        var stream = new SessionEventStream(Sid, sink, new FakeTimeProvider(At));

        // Act
        stream.Publish([Queued(1, "p1"), Queued(2, "p2")]);
        stream.Publish([Delivered(3, "p1")]);
        stream.Publish([Started(4, "p1")]);

        // Assert
        Assert.Equal(3, sink.Batches.Count);
        Assert.Empty(Ids(sink.Batches[0].QueuedBefore));
        Assert.Equal(["p1", "p2"], Ids(sink.Batches[1].QueuedBefore));
        Assert.Equal(["p2"], Ids(sink.Batches[2].QueuedBefore));
        Assert.Equal(["p2"], Ids(stream.Queued));
    }
}
