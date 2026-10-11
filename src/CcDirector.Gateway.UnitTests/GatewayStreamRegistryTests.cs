using System.Runtime.CompilerServices;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (up-stream): acceptance tests for <see cref="GatewayStreamRegistry"/> -
/// the Gateway's receive side of the tunnel up-stream. Covers the three Architect refinements that live in the
/// registry: pull-then-forward backpressure (ruling 1), and the two close-stream lifecycle races plus the
/// StreamUp-never-arrives timeout (ruling 3). Framing order and completion are covered too.
/// </summary>
public sealed class GatewayStreamRegistryTests
{
    // Issue #1923: every stream now records the identity that owns it. These lifecycle tests are all
    // single-owner, so they register and consume as the same identity - the ownership CHECK itself is proved
    // in StreamOwnershipTests.
    private static readonly StreamOwner Owner = new(TenantId.Local, "dir-1");

    // A sink that records the frames it is handed, optionally blocking each write on a supplied gate so a test
    // can hold the pull and observe backpressure.
    private sealed class RecordingSink : IStreamSink
    {
        private readonly Func<Task>? _gate;
        public RecordingSink(Func<Task>? gate = null) => _gate = gate;
        public List<DirectorStreamFrame> Frames { get; } = new();
        public bool Completed { get; private set; }
        public string? CompletedReason { get; private set; }

        public async Task WriteFrameAsync(DirectorStreamFrame frame, CancellationToken cancellationToken)
        {
            if (_gate is not null) await _gate();
            Frames.Add(frame);
        }

        public Task CompleteAsync(string? reason)
        {
            Completed = true;
            CompletedReason = reason;
            return Task.CompletedTask;
        }
    }

    private static DirectorStreamFrame Size(string s) => new() { StreamId = s, Kind = DirectorStreamFrameType.Size, Cols = 80, Rows = 24 };
    private static DirectorStreamFrame Bin(string s, byte b) => new() { StreamId = s, Kind = DirectorStreamFrameType.Binary, Data = new[] { b } };
    private static DirectorStreamFrame Closed(string s, string reason) => new() { StreamId = s, Kind = DirectorStreamFrameType.Closed, Reason = reason };

    private static async IAsyncEnumerable<DirectorStreamFrame> SizeBinaryClosed(string s, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return Size(s);
        yield return Bin(s, 1);
        await Task.Yield();
        yield return Closed(s, "eof");
    }

    [Fact]
    public async Task ConsumeAsync_RoutesFramesToSinkInOrder_ThenTearsDown()
    {
        var registry = new GatewayStreamRegistry();
        var sink = new RecordingSink();
        registry.Register("s1", Owner, sink);

        await registry.ConsumeAsync("s1", Owner, SizeBinaryClosed("s1"), CancellationToken.None);

        Assert.Equal(3, sink.Frames.Count);
        Assert.Equal(DirectorStreamFrameType.Size, sink.Frames[0].Kind);
        Assert.Equal(DirectorStreamFrameType.Binary, sink.Frames[1].Kind);
        Assert.Equal(DirectorStreamFrameType.Closed, sink.Frames[2].Kind);
        Assert.True(sink.Completed);
        Assert.Equal("eof", sink.CompletedReason);
        Assert.Equal(0, registry.LiveStreamCount); // torn down after completion
    }

    [Fact]
    public async Task ConsumeAsync_IsPullThenForward_BackpressuresTheProducer()
    {
        // The backpressure invariant (ruling 1): the registry must await the sink write of one frame BEFORE it
        // pulls the next frame. A sink whose write is held therefore stops the producer from running ahead.
        var registry = new GatewayStreamRegistry();
        var gate = new TaskCompletionSource();
        var firstWriteHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingSink(gate: () => { firstWriteHeld.TrySetResult(); return gate.Task; });
        registry.Register("bp", Owner, sink);

        var yielded = 0;
        async IAsyncEnumerable<DirectorStreamFrame> Produce([EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < 5; i++)
            {
                Interlocked.Increment(ref yielded);
                yield return Bin("bp", (byte)i);
                await Task.Yield();
            }
            yield return Closed("bp", "eof");
        }

        var consume = registry.ConsumeAsync("bp", Owner, Produce(), CancellationToken.None);
        await firstWriteHeld.Task; // the consumer pulled the first frame and is blocked on the held sink write

        Assert.Equal(1, Volatile.Read(ref yielded)); // only the first frame was pulled; the next pull is blocked
        Assert.False(consume.IsCompleted);

        gate.SetResult(); // release the sink writes
        await consume;

        Assert.Equal(5, Volatile.Read(ref yielded)); // all five binaries were pulled (the counter counts binaries)
        Assert.Equal(6, sink.Frames.Count); // and the sink received all five binaries plus the closed frame
    }

    [Fact]
    public async Task ConsumeAsync_CloseCancelsTheStream_EndsTheEnumerableAndTearsDown()
    {
        var registry = new GatewayStreamRegistry();
        var sink = new RecordingSink();
        registry.Register("inf", Owner, sink);

        var flowing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<DirectorStreamFrame> Forever([EnumeratorCancellation] CancellationToken ct = default)
        {
            var i = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                yield return Bin("inf", (byte)(i++ & 0xFF));
                if (i == 3) flowing.TrySetResult();
                await Task.Yield();
            }
        }

        var consume = registry.ConsumeAsync("inf", Owner, Forever(), CancellationToken.None);
        await flowing.Task; // a few frames have flowed

        registry.Close("inf"); // browser disconnected
        await consume; // completes cleanly (cancellation swallowed), does not throw

        Assert.True(sink.Completed);
        Assert.Equal(0, registry.LiveStreamCount);
    }

    [Fact]
    public async Task ConsumeAsync_ForAStreamWhoseSinkIsGone_DropsImmediately()
    {
        // StreamUp-after-sink-gone (ruling 3): the browser disconnected before the frames arrived; there is no
        // registered sink, so consume returns at once without touching any sink.
        var registry = new GatewayStreamRegistry();
        var sink = new RecordingSink();
        registry.Register("gone", Owner, sink);
        registry.Close("gone"); // sink torn down before StreamUp arrives

        await registry.ConsumeAsync("gone", Owner, SizeBinaryClosed("gone"), CancellationToken.None);

        Assert.Empty(sink.Frames); // nothing was pumped into the gone sink
        Assert.Equal(0, registry.LiveStreamCount);
    }

    [Fact]
    public async Task Register_WhenStreamUpNeverArrives_TearsTheSinkDownAfterTheTimeout()
    {
        // StreamUp-never-arrives (ruling 3): the Director died mid-open, so no StreamUp ever comes; the sink
        // must not wait forever - the open timeout tears it down and fires the caller's token.
        //
        // The timeout runs on a fake clock. This once slept 400 ms past a 100 ms real timeout and failed on a
        // loaded runner (the v2.6.0 release check, 17 September 2026), then polled the real clock; now the test
        // moves the clock itself, and the timer fires on this thread when it does.
        var time = new FakeTimeProvider();
        var registry = new GatewayStreamRegistry(openTimeout: TimeSpan.FromSeconds(10), time: time);
        var sink = new RecordingSink();
        var token = registry.Register("late", Owner, sink);

        time.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Assert.False(sink.Completed); // not one tick early
        time.Advance(TimeSpan.FromTicks(1));

        Assert.True(sink.Completed);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(0, registry.LiveStreamCount);
    }

    [Fact]
    public void Register_DuplicateStreamId_ThrowsFailLoud()
    {
        var registry = new GatewayStreamRegistry();
        registry.Register("dup", Owner, new RecordingSink());
        Assert.Throws<InvalidOperationException>(() => registry.Register("dup", Owner, new RecordingSink()));
    }
}
