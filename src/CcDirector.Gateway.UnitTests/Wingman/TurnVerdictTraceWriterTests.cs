using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// The writer that keeps the Wingman inspector's traces off the verdict path (devthrottle_internal#2029): handing it a
/// trace never waits on the database and never throws, a waiting trace is already cut to its row's ceilings, a write
/// that fails does not stop the next one, and a trace that is never written leaves a "lost" gap row saying so.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class TurnVerdictTraceWriterTests
{
    private static readonly TenantId Tenant = TenantId.Local;

    private static TurnVerdictTrace Trace(string id) => new()
    {
        TraceId = id,
        SessionId = "sid-" + id,
        RecordedAtUtc = DateTime.UtcNow,
        TurnEndObservedAtUtc = DateTime.UtcNow,
        Trigger = "turn-end",
        Outcome = TurnVerdictTraceOutcomes.Skipped,
    };

    [Fact]
    public async Task Enqueue_WhileAWriteIsStuckOnTheDatabase_ReturnsAtOnce_AndTheTracesAreWrittenInOrderAfterwards()
    {
        var release = new ManualResetEventSlim(false);
        var written = new List<string>();
        using var writer = new TurnVerdictTraceWriter((_, t) => { release.Wait(TimeSpan.FromSeconds(30)); lock (written) written.Add(t.TraceId); });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(writer.Enqueue(Tenant, Trace("first")));
        Assert.True(writer.Enqueue(Tenant, Trace("second")));
        clock.Stop();

        // The write is held for up to thirty seconds; the two enqueues did not wait for it.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"enqueue waited {clock.Elapsed}");
        Assert.Empty(written);

        release.Set();
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "first", "second" }, written);
        Assert.Equal(2, writer.Written);
    }

    [Fact]
    public async Task ATrace_IsCutToItsRowsCeilings_BeforeItWaitsInTheQueue()
    {
        // Found in review: the queue was bounded by count while each waiting trace still held its whole prompt, so a
        // stuck database could hold the Gateway's memory hostage. The append below only sees what was queued.
        var release = new ManualResetEventSlim(false);
        var seen = new List<TurnVerdictTrace>();
        using var writer = new TurnVerdictTraceWriter((_, t) => { release.Wait(TimeSpan.FromSeconds(30)); lock (seen) seen.Add(t); });

        Assert.True(writer.Enqueue(Tenant, Trace("huge") with
        {
            Prompt = new string('p', TurnVerdictTraceStore.MaxPromptChars * 3),
            RawReply = new string('r', TurnVerdictTraceStore.MaxRawReplyChars * 3),
        }));
        release.Set();
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var queued = Assert.Single(seen);
        Assert.Equal(TurnVerdictTraceStore.MaxPromptChars, queued.Prompt!.Length);
        Assert.True(queued.PromptTruncated);
        Assert.Equal(TurnVerdictTraceStore.MaxRawReplyChars, queued.RawReply!.Length);
        Assert.True(queued.RawReplyTruncated);
    }

    [Fact]
    public async Task AWriteThatThrows_IsCounted_TheNextTraceIsStillWritten_AndTheLostOneLeavesAGapRow()
    {
        var written = new List<TurnVerdictTrace>();
        using var writer = new TurnVerdictTraceWriter((_, t) =>
        {
            if (t.TraceId == "bad") throw new IOException("the database file is locked");
            lock (written) written.Add(t);
        });

        Assert.True(writer.Enqueue(Tenant, Trace("bad")));
        Assert.True(writer.Enqueue(Tenant, Trace("good")));
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, writer.Failed);
        Assert.Contains(written, t => t.TraceId == "good");
        var gap = Assert.Single(written, t => t.Outcome == TurnVerdictTraceOutcomes.Lost);
        Assert.Equal("sid-bad", gap.SessionId);
        Assert.Equal($"{TurnVerdictTraceWriter.WriteFailedCause}:{TurnVerdictTraceOutcomes.Skipped}", gap.Cause);
        Assert.Null(gap.Prompt);
        Assert.Null(gap.Package);
    }

    [Fact]
    public async Task AFullQueue_DropsTheTracesPastItsCapacity_CountsEveryOne_AndLeavesAGapRowForEach()
    {
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var written = new List<TurnVerdictTrace>();
        using var writer = new TurnVerdictTraceWriter((_, t) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            lock (written) written.Add(t);
        });

        // The reader takes the first trace and is stuck writing it, so the queue itself is empty and holds Capacity.
        Assert.True(writer.Enqueue(Tenant, Trace("held")));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        var accepted = 0;
        for (var i = 0; i < TurnVerdictTraceWriter.Capacity + 3; i++)
            if (writer.Enqueue(Tenant, Trace($"t{i}"))) accepted++;

        Assert.Equal(TurnVerdictTraceWriter.Capacity, accepted);
        Assert.Equal(3, writer.Dropped);

        release.Set();
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var gaps = written.Where(t => t.Outcome == TurnVerdictTraceOutcomes.Lost).ToList();
        var dropped = Enumerable.Range(TurnVerdictTraceWriter.Capacity, 3).Select(i => $"sid-t{i}").ToHashSet();
        Assert.Equal(3, gaps.Count);
        Assert.All(gaps, g => Assert.Contains(g.SessionId, dropped));
        Assert.All(gaps, g => Assert.Equal($"{TurnVerdictTraceWriter.QueueFullCause}:{TurnVerdictTraceOutcomes.Skipped}", g.Cause));
        Assert.Equal(TurnVerdictTraceWriter.Capacity + 1 + 3, writer.Written);
    }

    [Fact]
    public async Task AfterTheWriterIsClosed_EnqueueAnswersFalse_AndDoesNotThrow()
    {
        using var writer = new TurnVerdictTraceWriter((_, _) => { });
        await writer.CompleteAsync();

        Assert.False(writer.Enqueue(Tenant, Trace("late")));
        Assert.Equal(1, writer.Dropped);
    }
}
