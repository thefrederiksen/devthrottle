using CcDirector.ControlApi;
using CcDirector.Core.History;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Phase 1 of the turn-push mission, Director side: <see cref="TurnPusher"/> against a fake conversation
/// and a fake Gateway. Pins: a first push sends everything from ordinal zero; a later push sends only what
/// the watermark says is missing; a changed source starts a new generation; the Gateway's watermark (not
/// the Director's count) is the truth after every push; a refusal stops the run; a tunnel failure stops the
/// run without losing anything; a seeded watermark resumes; an unsupported agent sends its head once; a
/// changed history state refreshes the head; runs for one session never overlap.
/// </summary>
public sealed class TurnPusherTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 21, 0, 0, DateTimeKind.Utc);

    /// <summary>A Gateway that stores nothing but remembers every batch and answers like the real store
    /// would for the ordinary case: watermark = start + count, generation = what was sent.</summary>
    private sealed class FakeGateway
    {
        public readonly List<TurnPushBatch> Received = new();
        public Func<TurnPushBatch, TurnWatermark?>? Answer;
        public Exception? ThrowNext;
        private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = new();

        /// <summary>Completes once <paramref name="count"/> batches have been received - the event a test awaits.</summary>
        public Task WhenReceived(int count)
        {
            lock (_waiters)
            {
                if (Received.Count >= count) return Task.CompletedTask;
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, reached));
                return reached.Task;
            }
        }

        public Task<TurnWatermark?> Push(TurnPushBatch b, CancellationToken ct)
        {
            if (ThrowNext is { } ex) { ThrowNext = null; throw ex; }
            lock (_waiters)
            {
                Received.Add(b);
                foreach (var waiter in _waiters.Where(w => Received.Count >= w.Count)) waiter.Reached.TrySetResult();
            }
            // A configured Answer that returns null IS the refusal; only an unconfigured fake answers by default.
            var answer = Answer is null
                ? new TurnWatermark { SessionId = b.SessionId, Generation = b.Generation, Count = b.StartOrdinal + b.Turns.Count }
                : Answer(b);
            return Task.FromResult(answer);
        }
    }

    private static PushedTurn T(int i) => new() { Ordinal = i, Role = i % 2 == 0 ? "User" : "Assistant", Parts = { new HistoryPartDto { Kind = "Text", Text = "m" + i } } };

    private static TurnSnapshot Snap(Guid sid, string generation, int count, string? state = null, bool supported = true) =>
        new(sid.ToString(), generation, "ClaudeCode", supported, false, state, Enumerable.Range(0, count).Select(T).ToList());

    private static (TurnPusher pusher, FakeGateway gw) Build(Guid sid, Func<TurnSnapshot?> snapshot, bool canPush = true)
    {
        var gw = new FakeGateway();
        var pusher = new TurnPusher(() => new[] { sid }, _ => snapshot(), gw.Push, () => canPush, () => Now);
        return (pusher, gw);
    }

    [Fact]
    public async Task AFirstRun_PushesEverythingFromOrdinalZero_InBoundedBatches()
    {
        var sid = Guid.NewGuid();
        var (pusher, gw) = Build(sid, () => Snap(sid, @"C:\t\a.jsonl", 1200));

        await pusher.PushSessionAsync(sid);

        Assert.Equal(3, gw.Received.Count);
        Assert.Equal(new[] { 0, 500, 1000 }, gw.Received.Select(b => b.StartOrdinal));
        Assert.Equal(new[] { 500, 500, 200 }, gw.Received.Select(b => b.Turns.Count));
        Assert.All(gw.Received, b => Assert.Equal(1200, b.TotalCount));
        Assert.All(gw.Received, b => Assert.Equal(Now, b.GenerationStartedUtc));
        Assert.All(gw.Received, b => Assert.Equal(@"C:\t\a.jsonl", b.Generation));
    }

    [Fact]
    public async Task ALaterRun_PushesOnlyWhatIsMissing()
    {
        var sid = Guid.NewGuid();
        var count = 3;
        var (pusher, gw) = Build(sid, () => Snap(sid, "g", count));
        await pusher.PushSessionAsync(sid);
        gw.Received.Clear();

        count = 5;
        await pusher.PushSessionAsync(sid);

        var b = Assert.Single(gw.Received);
        Assert.Equal(3, b.StartOrdinal);
        Assert.Equal(new[] { 3, 4 }, b.Turns.Select(t => t.Ordinal));
    }

    [Fact]
    public async Task NothingNew_PushesNothing()
    {
        var sid = Guid.NewGuid();
        var (pusher, gw) = Build(sid, () => Snap(sid, "g", 3));
        await pusher.PushSessionAsync(sid);
        gw.Received.Clear();

        await pusher.PushSessionAsync(sid);

        Assert.Empty(gw.Received);
    }

    [Fact]
    public async Task AChangedSource_StartsANewGeneration_FromOrdinalZero_WithANewStart()
    {
        // /clear, or the transcript moved into a worktree.
        var sid = Guid.NewGuid();
        var generation = "old";
        var (pusher, gw) = Build(sid, () => Snap(sid, generation, 4));
        await pusher.PushSessionAsync(sid);
        gw.Received.Clear();

        generation = "new";
        await pusher.PushSessionAsync(sid);

        var b = Assert.Single(gw.Received);
        Assert.Equal("new", b.Generation);
        Assert.Equal(0, b.StartOrdinal);
        Assert.Equal(4, b.Turns.Count);
    }

    [Fact]
    public async Task TheGatewaysWatermark_IsTheTruth_NotTheDirectorsCount()
    {
        // The Gateway answers a LOWER watermark than what was sent (an earlier batch was lost, so the prefix
        // stops short). The Director must resume from the Gateway's number, not its own.
        var sid = Guid.NewGuid();
        var (pusher, gw) = Build(sid, () => Snap(sid, "g", 10));
        var answers = 0;
        gw.Answer = b => new TurnWatermark { SessionId = b.SessionId, Generation = b.Generation, Count = answers++ == 0 ? 4 : b.StartOrdinal + b.Turns.Count };

        await pusher.PushSessionAsync(sid);

        // First push 0..9 answered 4; the run goes round again and resends from 4.
        Assert.Equal(2, gw.Received.Count);
        Assert.Equal(0, gw.Received[0].StartOrdinal);
        Assert.Equal(4, gw.Received[1].StartOrdinal);
    }

    [Fact]
    public async Task TheGatewayOnALaterGeneration_StopsTheRun_AndAdoptsItsView()
    {
        var sid = Guid.NewGuid();
        var (pusher, gw) = Build(sid, () => Snap(sid, "stale-read", 3));
        gw.Answer = _ => new TurnWatermark { SessionId = sid.ToString(), Generation = "later-source", Count = 7 };

        await pusher.PushSessionAsync(sid);

        // One push, then it stopped (it did not keep pushing "stale-read" batches at a Gateway that has moved on).
        Assert.Single(gw.Received);
    }

    [Fact]
    public async Task ARefusedBatch_StopsTheRun()
    {
        var sid = Guid.NewGuid();
        var (pusher, gw) = Build(sid, () => Snap(sid, "g", 1200));
        gw.Answer = _ => null;

        await pusher.PushSessionAsync(sid);

        Assert.Single(gw.Received);
    }

    [Fact]
    public async Task ATunnelFailure_StopsTheRunWithoutThrowing_AndTheNextRunResumes()
    {
        var sid = Guid.NewGuid();
        var (pusher, gw) = Build(sid, () => Snap(sid, "g", 700));
        gw.ThrowNext = new InvalidOperationException("tunnel down");

        await pusher.PushSessionAsync(sid);           // must not throw
        Assert.Empty(gw.Received);

        await pusher.PushSessionAsync(sid);
        Assert.Equal(new[] { 0, 500 }, gw.Received.Select(b => b.StartOrdinal));
    }

    [Fact]
    public async Task ASeededWatermark_ResumesFromIt_OnTheSameGeneration()
    {
        // The Gateway said on Hello: session s, generation g, 3 held. The Director sends from 3.
        var sid = Guid.NewGuid();
        var gw = new FakeGateway();
        var pusher = new TurnPusher(() => new[] { sid }, _ => Snap(sid, "g", 5), gw.Push, () => true, () => Now, sweepInterval: TimeSpan.FromHours(1));

        await pusher.SeedWatermarks(new[] { new TurnWatermark { SessionId = sid.ToString(), Generation = "g", Count = 3 } });   // the seed's sweep

        var b = gw.Received.First();
        Assert.Equal(3, b.StartOrdinal);
        Assert.Equal(2, b.Turns.Count);
    }

    [Fact]
    public async Task ASeededWatermark_OnAnotherGeneration_IsReplacedByAFullPush()
    {
        var sid = Guid.NewGuid();
        var gw = new FakeGateway();
        var pusher = new TurnPusher(() => new[] { sid }, _ => Snap(sid, "current", 2), gw.Push, () => true, () => Now, sweepInterval: TimeSpan.FromHours(1));

        await pusher.SeedWatermarks(new[] { new TurnWatermark { SessionId = sid.ToString(), Generation = "previous", Count = 9 } });

        var b = gw.Received.First();
        Assert.Equal("current", b.Generation);
        Assert.Equal(0, b.StartOrdinal);
        Assert.Equal(2, b.Turns.Count);
    }

    [Fact]
    public async Task AnUnsupportedAgent_SendsItsHeadOnce_AndNoTurns()
    {
        var sid = Guid.NewGuid();
        var (pusher, gw) = Build(sid, () => Snap(sid, "session:" + sid, 0, supported: false));

        await pusher.PushSessionAsync(sid);
        await pusher.PushSessionAsync(sid);

        var b = Assert.Single(gw.Received);
        Assert.False(b.IsSupported);
        Assert.Empty(b.Turns);
    }

    [Fact]
    public async Task AChangedHistoryState_WithNoNewTurns_RefreshesTheHead()
    {
        var sid = Guid.NewGuid();
        var state = "Idle";
        var (pusher, gw) = Build(sid, () => Snap(sid, "g", 2, state));
        await pusher.PushSessionAsync(sid);
        gw.Received.Clear();

        state = "BackgroundRunning";
        await pusher.PushSessionAsync(sid);

        var b = Assert.Single(gw.Received);
        Assert.Empty(b.Turns);
        Assert.Equal(2, b.StartOrdinal);
        Assert.Equal("BackgroundRunning", b.HistoryState);
    }

    [Fact]
    public async Task AGatewayWithoutPushTurns_IsNeverPushedAt()
    {
        var sid = Guid.NewGuid();
        var (pusher, gw) = Build(sid, () => Snap(sid, "g", 3), canPush: false);

        await pusher.PushSessionAsync(sid);
        await pusher.SweepAsync();

        Assert.Empty(gw.Received);
    }

    [Fact]
    public async Task RunsForOneSession_NeverOverlap_ButATriggerDuringARun_IsNotLost()
    {
        var sid = Guid.NewGuid();
        var count = 2;
        var gate = new TaskCompletionSource();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gw = new FakeGateway();
        var entered = 0;
        var pusher = new TurnPusher(() => new[] { sid }, _ => Snap(sid, "g", count),
            async (b, ct) =>
            {
                if (Interlocked.Increment(ref entered) == 1)
                {
                    firstEntered.SetResult();
                    await gate.Task;   // hold the first push open
                }
                return await gw.Push(b, ct);
            },
            () => true, () => Now);

        var first = pusher.PushSessionAsync(sid);
        await firstEntered.Task;
        count = 3;                                    // a new turn lands while the first run is mid-push
        var second = pusher.PushSessionAsync(sid);    // must return at once, marking the run pending
        Assert.True(second.IsCompleted);
        gate.SetResult();
        await first;

        // The first run went round again and pushed the third message; nothing overlapped.
        Assert.Equal(2, gw.Received.Count);
        Assert.Equal(new[] { 0, 2 }, gw.Received.Select(b => b.StartOrdinal));
    }

    [Fact]
    public async Task AHelloThatDoesNotListASession_ResetsIt_SoItIsPushedFromTheStart()
    {
        // The Gateway was replaced or lost its rows: its Hello lists nothing for this session. The Director must
        // not go on believing its old watermark (found in review).
        var sid = Guid.NewGuid();
        var gw = new FakeGateway();
        var pusher = new TurnPusher(() => new[] { sid }, _ => Snap(sid, "g", 4), gw.Push, () => true, () => Now, sweepInterval: TimeSpan.FromHours(1));
        await pusher.PushSessionAsync(sid);
        Assert.Equal(4, gw.Received.Single().Turns.Count);
        gw.Received.Clear();

        await pusher.SeedWatermarks(Array.Empty<TurnWatermark>());

        var b = gw.Received.First();
        Assert.Equal(0, b.StartOrdinal);
        Assert.Equal(4, b.Turns.Count);
    }

    [Fact]
    public async Task AnOldSourceReReadLater_KeepsItsFirstSeenStamp_SoItCannotOutrankTheNewerOne()
    {
        // The stale-read case (found in review): the Director read old source A, the Gateway answered "I am on
        // B", and then the Director happens to read A again. A must NOT be re-stamped with a later time than
        // B's, or the Gateway would switch back to it. First-seen stamps are stable for the process.
        var sid = Guid.NewGuid();
        var clockNow = Now;
        var source = "A";
        var gw = new FakeGateway();
        var pusher = new TurnPusher(() => new[] { sid }, _ => Snap(sid, source, 2), gw.Push, () => true, () => clockNow);

        await pusher.PushSessionAsync(sid);                 // A first seen at Now
        var stampA = gw.Received.Single().GenerationStartedUtc;
        clockNow = Now.AddMinutes(1);
        source = "B";
        await pusher.PushSessionAsync(sid);                 // B first seen at Now+1
        var stampB = gw.Received.Last().GenerationStartedUtc;
        clockNow = Now.AddMinutes(2);
        source = "A";
        gw.Answer = b => new TurnWatermark { SessionId = sid.ToString(), Generation = "B", Count = 2 };
        await pusher.PushSessionAsync(sid);                 // a stale re-read of A

        var late = gw.Received.Last();
        Assert.Equal("A", late.Generation);
        Assert.Equal(stampA, late.GenerationStartedUtc);    // NOT Now+2
        Assert.True(stampB > late.GenerationStartedUtc);    // so the Gateway keeps B
    }

    [Fact]
    public async Task ARefusedGeneration_IsNotReSentEverySweep()
    {
        var sid = Guid.NewGuid();
        var (pusher, gw) = Build(sid, () => Snap(sid, "g", 3));
        gw.Answer = _ => null;

        await pusher.PushSessionAsync(sid);
        await pusher.SweepAsync();
        await pusher.PushSessionAsync(sid);

        Assert.Single(gw.Received);
    }

    [Fact]
    public async Task AConversationThatKeepsGrowingMidRun_IsAllPushed_AndNoCallRunsForever()
    {
        // Each push makes more work arrive. One CALL is capped at MaxRoundsPerCall rounds, so it returns to
        // its caller rather than holding the session (and the sweep) - and because the last round reached the
        // Gateway, it hands the remainder to a fresh call at once instead of leaving the trigger to wait out
        // the minute-long sweep (found in review). The whole conversation still lands.
        var sid = Guid.NewGuid();
        var count = 1;
        var grown = 0;
        var gw = new FakeGateway();
        TurnPusher? pusher = null;
        pusher = new TurnPusher(() => new[] { sid }, _ => Snap(sid, "g", count), async (b, ct) =>
        {
            if (Interlocked.Increment(ref grown) <= 8) { count++; pusher!.Trigger(sid); }
            return await gw.Push(b, ct);
        }, () => true, () => Now);

        await pusher.PushSessionAsync(sid).WaitAsync(TimeSpan.FromSeconds(30));   // the call returns; the work it could not finish is handed on

        // The hand-off chain carries the rest. The watchdog only turns a hang into a failure; it never
        // decides a pass, because the event awaited is the ninth batch arriving, not time passing.
        await gw.WhenReceived(9).WaitAsync(TimeSpan.FromSeconds(30));
        // More pushes than one call's round cap allows, so the hand-off chain carried the rest.
        Assert.True(gw.Received.Count >= 9, $"expected the whole conversation to land, saw {gw.Received.Count} push(es)");
        Assert.True(gw.Received.Count > TurnPusher.MaxRoundsPerCall, "the remainder must be handed on, not left waiting for the sweep");
    }

    [Fact]
    public async Task ABackfillBiggerThanOneRoundsBudget_FinishesInTheSameCall()
    {
        // A round stops after MaxBatchesPerRound. The rest of the conversation must carry on in the next
        // round of the SAME call, not wait a minute for the sweep (found in review).
        var sid = Guid.NewGuid();
        var overOneRound = TurnPusher.MaxBatchesPerRound * TurnPusher.BatchSize + TurnPusher.BatchSize;
        var (pusher, gw) = Build(sid, () => Snap(sid, "g", overOneRound));

        await pusher.PushSessionAsync(sid);

        Assert.Equal(TurnPusher.MaxBatchesPerRound + 1, gw.Received.Count);
        Assert.Equal(overOneRound, gw.Received.Sum(b => b.Turns.Count));
    }

    [Fact]
    public async Task ASessionTheSweepPruned_PushesAgainWhenItComesBack()
    {
        // The sweep retires and removes the state of a session that is no longer on the roster. A caller that
        // was holding that state must not go on using it (nothing can find it any more) - it takes a fresh
        // one, which pushes from ordinal zero. The store is idempotent, so re-pushing costs nothing.
        var sid = Guid.NewGuid();
        var gw = new FakeGateway();
        IReadOnlyCollection<Guid> ids = new[] { sid };
        var pusher = new TurnPusher(() => ids, _ => Snap(sid, "g", 2), gw.Push, () => true, () => Now);
        await pusher.PushSessionAsync(sid);
        gw.Received.Clear();

        ids = Array.Empty<Guid>();
        await pusher.SweepAsync();          // gone from the roster: its state is retired and removed
        ids = new[] { sid };
        await pusher.PushSessionAsync(sid); // back again: a fresh state, so it pushes from the start

        var b = Assert.Single(gw.Received);
        Assert.Equal(0, b.StartOrdinal);
        Assert.Equal(2, b.Turns.Count);
    }

    [Fact]
    public async Task ATriggerThatArrivesWhileARoundIsFailing_IsNotLost()
    {
        // A turn ends while a push is failing on a dropped tunnel. Returning from the failed round would leave
        // that trigger set with nobody to consume it, and the turn would wait for the minute-long sweep (found
        // in review). The call goes round again instead, bounded by the round cap.
        var sid = Guid.NewGuid();
        var gw = new FakeGateway();
        TurnPusher? pusher = null;
        var attempt = 0;
        pusher = new TurnPusher(() => new[] { sid }, _ => Snap(sid, "g", 2), async (b, ct) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                pusher!.Trigger(sid);                       // the turn-end edge fires mid-push
                throw new InvalidOperationException("tunnel dropped mid-push");
            }
            return await gw.Push(b, ct);
        }, () => true, () => Now);

        await pusher.PushSessionAsync(sid);

        var b = Assert.Single(gw.Received);
        Assert.Equal(2, b.Turns.Count);
    }

    [Fact]
    public async Task TheSweep_DoesNotForgetASessionThatIsMidRun()
    {
        // The sweep prunes state for sessions that no longer exist. Pruning one that is mid-push would lose its
        // watermark, and the next run would re-push the whole conversation from ordinal zero (found in review).
        var sid = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gw = new FakeGateway();
        IReadOnlyCollection<Guid> ids = new[] { sid };
        var pusher = new TurnPusher(() => ids, _ => Snap(sid, "g", 2),
            async (b, ct) => { inFlight.TrySetResult(); await gate.Task; return await gw.Push(b, ct); }, () => true, () => Now);

        var run = pusher.PushSessionAsync(sid);
        await inFlight.Task;
        ids = Array.Empty<Guid>();          // the roster stops listing it while its push is in flight
        await pusher.SweepAsync();
        gate.SetResult();
        await run;
        ids = new[] { sid };
        await pusher.PushSessionAsync(sid);  // its watermark survived, so there is nothing left to send

        Assert.Single(gw.Received);
    }

    [Fact]
    public async Task EachNewSource_GetsAStrictlyLaterStamp_EvenWhenTheClockDoesNotMove()
    {
        // The Gateway switches only to a LATER generation. A clock that has not ticked between two sources -
        // or has gone backwards - would mint a stamp that could never win, wedging the session on the old
        // conversation (found in review).
        var sid = Guid.NewGuid();
        var source = "A";
        var frozen = Now;
        var gw = new FakeGateway();
        var pusher = new TurnPusher(() => new[] { sid }, _ => Snap(sid, source, 1), gw.Push, () => true, () => frozen);

        await pusher.PushSessionAsync(sid);
        source = "B";
        await pusher.PushSessionAsync(sid);
        frozen = Now.AddMinutes(-5);        // the clock goes BACKWARDS
        source = "C";
        await pusher.PushSessionAsync(sid);

        var stamps = gw.Received.Select(b => b.GenerationStartedUtc).ToList();
        Assert.Equal(3, stamps.Count);
        Assert.True(stamps[1] > stamps[0], "the second source must start later than the first");
        Assert.True(stamps[2] > stamps[1], "the third source must start later than the second");
    }

    [Fact]
    public async Task ARefusedGeneration_StaysRefused_AcrossAHelloThatListsTheSameGeneration()
    {
        // A refusal means the batch was malformed - a bug on this side - so a reconnect must not start
        // re-sending it every sweep (found in review).
        var sid = Guid.NewGuid();
        var (pusher, gw) = Build(sid, () => Snap(sid, "g", 3));
        gw.Answer = _ => null;
        await pusher.PushSessionAsync(sid);
        Assert.Single(gw.Received);

        await pusher.SeedWatermarks(new[] { new TurnWatermark { SessionId = sid.ToString(), Generation = "g", Count = 0 } });
        await pusher.PushSessionAsync(sid);

        Assert.Single(gw.Received);
    }

    [Fact]
    public void Map_ShapesMessagesAsOrdinalTurns_WithTheSamePartsChatRenders()
    {
        var messages = new List<ConversationMessage>
        {
            new(ConversationRole.User, new[] { new ConversationPart(ConversationPartKind.Text, "hello") }, new DateTimeOffset(Now)),
            new(ConversationRole.Assistant, new[]
            {
                new ConversationPart(ConversationPartKind.Thinking, "hmm"),
                new ConversationPart(ConversationPartKind.ToolUse, "{}", ToolName: "Read", ToolId: "t1"),
                new ConversationPart(ConversationPartKind.Text, "hi"),
            }, ContextId: "ctx-1", IsMeta: false, IsSidechain: false),
        };

        var turns = TurnPushBuilder.Map(messages);

        Assert.Equal(new[] { 0, 1 }, turns.Select(t => t.Ordinal));
        Assert.Equal(new[] { "User", "Assistant" }, turns.Select(t => t.Role));
        Assert.Equal(new[] { "Thinking", "ToolUse", "Text" }, turns[1].Parts.Select(p => p.Kind));
        Assert.Equal("Read", turns[1].Parts[1].ToolName);
        Assert.Equal("ctx-1", turns[1].ContextId);
        Assert.Equal(new DateTimeOffset(Now), turns[0].Timestamp);
    }
}
