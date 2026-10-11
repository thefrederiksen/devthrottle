using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// Regression guard for the slow-paste fix. Pasting into the terminal used to type the clipboard one
/// chunk at a time while awaiting back onto the Avalonia UI thread. On a Director hosting many
/// sessions the UI thread is saturated with rendering, so the per-chunk delay continuations sat
/// unscheduled for seconds: a real 92-character claude /login code took 22.6s on this machine, with
/// 11 chunks stalling over 800ms each (one for 7.3s), while the same paste was near-instant on an
/// idle machine. The fix runs the pacing loop on the thread pool (Task.Run + ConfigureAwait(false)),
/// so paste speed no longer depends on UI-thread availability.
///
/// These tests reproduce the exact condition - a fully blocked UI thread - and decide the outcome by
/// ORDER, never by the clock. The UI thread is pinned until a signal the test controls, not for a
/// number of milliseconds: the production pacing (<see cref="TerminalControl.HarnessPaceChunksAsync"/>)
/// must finish the whole paste while the pin is still held, whereas the old naive UI-thread-resuming
/// loop cannot send its second chunk until the pin is released. The contrast is the point: if a change
/// makes the production pacing resume on the UI thread again, the first test fails - and it fails by
/// its hang guard, naming the cause, rather than passing on a fast machine. (The earlier version of
/// this file measured the span in milliseconds and read red under load: 1343 ms against a 1000 ms
/// limit on 10 October 2026, with the fix intact.)
/// </summary>
public sealed class PasteOffUiThreadTests
{
    // The paste that bit the user: a 92-character single-line code, no newlines.
    private const int PasteLen = 92;

    /// <summary>
    /// How long a pin may be held before the test is called hung. This is a hang guard, not an
    /// assertion: it can only turn a test that would never finish into one that fails with a cause.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [AvaloniaFact]
    public async Task Production_pacing_finishes_the_whole_paste_while_the_ui_thread_is_pinned()
    {
        var terminal = new TerminalControl();
        var text = new string('x', PasteLen);

        var chunks = new ConcurrentQueue<byte[]>();
        using var pasteDone = new ManualResetEventSlim(false);

        // Pin the UI thread until the paste reports done, then run the REAL pacing. Because the pacing
        // runs on the thread pool, it finishes while the pin is held. If it ever resumed on the UI
        // thread it could not finish until the pin was released, and the pin is released only when it
        // finishes - so the guard is what ends the pin, and the test fails naming that.
        var pin = PinUiThreadUntil(pasteDone);
        var paste = terminal.HarnessPaceChunksAsync(text, chunks.Enqueue);
        _ = paste.ContinueWith(_ => pasteDone.Set(), TaskScheduler.Default);

        var endedBy = await pin;
        await paste;

        Assert.Equal(PinEnd.Signal, endedBy);
        Assert.True(chunks.Count > 1, $"expected the paste to go out in several chunks, got {chunks.Count}");
        Assert.Equal(PasteLen, chunks.Sum(c => c.Length));
    }

    [AvaloniaFact]
    public async Task Naive_ui_thread_pacing_cannot_send_a_second_chunk_until_the_ui_thread_is_released()
    {
        // This is the OLD behavior, inlined: an await with no ConfigureAwait(false) and no Task.Run, so
        // every continuation resumes on the (pinned) UI thread. Showing that THIS cannot get past its
        // first chunk until the pin is released proves the pin genuinely saturates the UI thread -
        // otherwise the first test would pass for the wrong reason (a pin that never bit).
        //
        // The order is forced, not hoped for. The pacing's wait between chunks is a task the test completes
        // by hand, from off the UI thread, only once the pin is held: completing it posts the second chunk's
        // continuation onto the pinned dispatcher, and only then is the pin released, so the second chunk
        // goes out after the pin has ended. That the pin really held the dispatcher is proven by the pin job
        // itself, which refuses to spin anywhere but on the UI thread (see PinUiThreadUntil) - order alone
        // cannot prove it, because a pin that held nothing still ends a few microseconds before a freed UI
        // thread gets to the chunk. (A first version of this test leaned on the order alone, and the review
        // reproduced a pass with the UI thread left completely free.)
        var text = new string('x', PasteLen);

        using var release = new ManualResetEventSlim(false);
        using var pinStarted = new ManualResetEventSlim(false);
        using var pinEnded = new ManualResetEventSlim(false);
        using var firstChunkSent = new ManualResetEventSlim(false);
        var betweenChunks = new TaskCompletionSource();
        var sentAfterThePinEnded = new ConcurrentQueue<bool>();

        var pin = PinUiThreadUntil(release, pinStarted, pinEnded);

        var releaser = Task.Run(() =>
        {
            try
            {
                if (!pinStarted.Wait(HangGuard)) throw new TimeoutException("the pin job never started");
                if (!firstChunkSent.Wait(HangGuard)) throw new TimeoutException("the first chunk was never sent");
                // The pacing is now awaiting betweenChunks on the UI thread's context. Completing it queues the
                // continuation behind the pin job; the pin is opened only after that.
                betweenChunks.SetResult();
                release.Set();
            }
            finally
            {
                // Whatever stopped the releaser, the pacing must not be left waiting for ever on a task only
                // the releaser completes: a test that hangs reports nothing, a test that fails names its cause.
                betweenChunks.TrySetException(new InvalidOperationException("the releaser gave up before it let the pacing continue"));
                release.Set();
            }
        });

        await NaiveUiThreadPace(text, _ =>
        {
            sentAfterThePinEnded.Enqueue(pinEnded.IsSet);
            firstChunkSent.Set();
        }, () => betweenChunks.Task);

        Assert.Equal(PinEnd.Signal, await pin);
        await releaser;

        var sent = sentAfterThePinEnded.ToArray();
        Assert.True(sent.Length > 1, $"expected the paste to go out in several chunks, got {sent.Length}");
        Assert.False(sent[0], "the first chunk goes out synchronously, before the pin has even started");
        Assert.All(sent.Skip(1), afterPin => Assert.True(afterPin,
            "a chunk after the first was sent before the pin ended - its continuation did not need the UI thread, so the pin is not saturating it"));
    }

    /// <summary>
    /// The naive loop the fix replaced: chunked, but resuming on the UI thread. The wait between chunks is
    /// handed in so a test can decide the moment it ends; production used <c>Task.Delay(15)</c> there.
    /// </summary>
    private static async Task NaiveUiThreadPace(string text, Action<byte[]> send, Func<Task> waitBetweenChunks)
    {
        const int chunk = 8;
        for (int i = 0; i < text.Length; i += chunk)
        {
            send(System.Text.Encoding.UTF8.GetBytes(text.Substring(i, Math.Min(chunk, text.Length - i))));
            await waitBetweenChunks(); // no ConfigureAwait(false): continuation returns to the UI thread
        }
    }

    private enum PinEnd { Signal, HangGuard }

    /// <summary>
    /// Post a job that spins the UI thread until <paramref name="until"/> is set, blocking the dispatcher
    /// completely. <paramref name="started"/>, when given, is set the moment the job runs - whether it then
    /// pins or refuses to - and <paramref name="ended"/> the moment it stops, before the job returns the
    /// thread to the dispatcher. The task says what ended the pin: the signal, or the hang guard.
    ///
    /// <para>The job proves it IS the pin before it spins: it has to be running on the UI thread, because
    /// a dispatcher runs one job at a time on that one thread, so a job spinning there is the one fact that
    /// makes every other dispatcher job wait. A pin that found itself anywhere else fails both tests
    /// instead of pinning nothing - the review ran this very mutation (the spin moved to a pool thread,
    /// the UI thread left free) and the first version of this file passed it.</para>
    /// </summary>
    private static Task<PinEnd> PinUiThreadUntil(ManualResetEventSlim until, ManualResetEventSlim? started = null, ManualResetEventSlim? ended = null)
    {
        var tcs = new TaskCompletionSource<PinEnd>();
        Dispatcher.UIThread.Post(() =>
        {
            started?.Set();
            if (!Dispatcher.UIThread.CheckAccess())
            {
                ended?.Set();
                tcs.SetException(new InvalidOperationException(
                    "the pin is not running on the UI thread, so it pins nothing and neither test can prove anything"));
                return;
            }
            var deadline = Environment.TickCount64 + (long)HangGuard.TotalMilliseconds;
            var endedBy = PinEnd.HangGuard;
            while (Environment.TickCount64 < deadline)
            {
                if (until.IsSet) { endedBy = PinEnd.Signal; break; }
                // Pin the UI thread: no wait, no yield.
            }
            ended?.Set();
            tcs.SetResult(endedBy);
        }, DispatcherPriority.Normal);
        return tcs.Task;
    }
}
