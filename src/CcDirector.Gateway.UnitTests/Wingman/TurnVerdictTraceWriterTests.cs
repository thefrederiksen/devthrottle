using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// The writer that keeps the Wingman inspector's traces off the verdict path (devthrottle_internal#2029): handing it a
/// trace never waits on the database and never throws, a waiting trace is already cut to its row's ceilings, a write
/// that fails does not stop the next one, a trace that is not kept is counted, and an abandoned writer stops writing.
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
    public async Task ATrace_IsCutToItsRowsCeilings_AndAnOversizedPackageDropped_BeforeItWaitsInTheQueue()
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
            Package = new TurnVerdictPackage { RecentTurns = new string('t', TurnVerdictTraceStore.MaxPackageJsonChars + 1) },
            Verdict = new TurnVerdictDto { Failed = true, FailureReason = new string('w', TurnVerdictTraceStore.MaxFailureReasonChars * 3) },
        }));
        release.Set();
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var queued = Assert.Single(seen);
        Assert.Equal(TurnVerdictTraceStore.MaxPromptChars, queued.Prompt!.Length);
        Assert.True(queued.PromptTruncated);
        Assert.Equal(TurnVerdictTraceStore.MaxRawReplyChars, queued.RawReply!.Length);
        Assert.True(queued.RawReplyTruncated);
        Assert.Null(queued.Package);
        Assert.True(queued.PackageOmitted);
        Assert.Equal(TurnVerdictTraceStore.MaxFailureReasonChars + TurnVerdictTraceStore.CutMarker.Length, queued.Verdict!.FailureReason!.Length);
    }

    [Fact]
    public async Task AWriteThatThrows_IsCounted_AndTheNextTraceIsStillWritten()
    {
        var written = new List<string>();
        using var writer = new TurnVerdictTraceWriter((_, t) =>
        {
            if (t.TraceId == "bad") throw new IOException("the database file is locked");
            lock (written) written.Add(t.TraceId);
        });

        Assert.True(writer.Enqueue(Tenant, Trace("bad")));
        Assert.True(writer.Enqueue(Tenant, Trace("good")));
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, writer.Failed);
        Assert.Equal(new[] { "good" }, written);
    }

    [Fact]
    public async Task AColourFoldThatThrows_LosesTheTraceUnderItsOwnName_NotAsAFailedWrite_AndTheNextTraceIsStillWritten()
    {
        // Inspection round 2, finding 3: the stamp ran inside the write's try, so a fold fault was counted and logged as
        // "write failed" - the database refusing a write. The trace is still not written without its colour; the loss
        // now says what it was.
        var written = new List<string>();
        using var writer = new TurnVerdictTraceWriter(
            (_, t) => { lock (written) written.Add(t.TraceId); },
            stamp: (_, t) => t.TraceId == "bad"
                ? throw new InvalidOperationException("the roster fold broke")
                : t with { RowColour = "cyan", RowLabel = "Finished" });

        Assert.True(writer.Enqueue(Tenant, Trace("bad")));
        Assert.True(writer.Enqueue(Tenant, Trace("good")));
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, writer.ColourFoldFailed);
        Assert.Equal(0, writer.Failed);
        Assert.Equal(1, writer.Written);
        Assert.Equal(new[] { "good" }, written);
        Assert.Equal("colour fold failed: System.InvalidOperationException: the roster fold broke", writer.LastFailure);
    }

    [Fact]
    public async Task AWriteThatThrowsAfterTheColourFold_IsAFailedWrite_NotAColourFoldFailure()
    {
        using var writer = new TurnVerdictTraceWriter(
            (_, _) => throw new IOException("the database file is locked"),
            stamp: (_, t) => t with { RowColour = "cyan", RowLabel = "Finished" });

        Assert.True(writer.Enqueue(Tenant, Trace("one")));
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, writer.Failed);
        Assert.Equal(0, writer.ColourFoldFailed);
        Assert.Equal("write failed: System.IO.IOException: the database file is locked", writer.LastFailure);
    }

    [Fact]
    public async Task AFullQueue_RefusesTheTracesPastItsCapacity_AndCountsEveryOne()
    {
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        using var writer = new TurnVerdictTraceWriter((_, _) => { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); });

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
        Assert.Equal(TurnVerdictTraceWriter.Capacity + 1, writer.Written);
    }

    [Fact]
    public async Task AnAbandonedWriter_WritesNothingThatWasStillQueued_AndCountsIt()
    {
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var written = new List<string>();
        using var writer = new TurnVerdictTraceWriter((_, t) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            lock (written) written.Add(t.TraceId);
        });

        Assert.True(writer.Enqueue(Tenant, Trace("in-the-database-call")));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(writer.Enqueue(Tenant, Trace("queued-1")));
        Assert.True(writer.Enqueue(Tenant, Trace("queued-2")));

        writer.Abandon();

        // Found in review: the queued traces were counted only once the stuck write returned, which at a shutdown that
        // timed out may be never. They are counted the moment the writer is abandoned, while that write is still stuck.
        Assert.Equal(2, writer.Abandoned);
        Assert.Empty(written);

        release.Set();
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));
        // The write already inside the database call finishes on its own; nothing that was queued is written.
        Assert.Equal(new[] { "in-the-database-call" }, written);
        Assert.Equal(2, writer.Abandoned);
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
