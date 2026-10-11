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
        // This is the OLD behavior, inlined: await Task.Delay with no ConfigureAwait(false) and no
        // Task.Run, so every continuation resumes on the (pinned) UI thread. Showing that THIS cannot
        // get past its first chunk until the pin is released proves the pin genuinely saturates the UI
        // thread - otherwise the first test would pass for the wrong reason (a pin that never bit).
        var text = new string('x', PasteLen);

        using var release = new ManualResetEventSlim(false);
        using var pinning = new ManualResetEventSlim(false);
        using var firstChunkSent = new ManualResetEventSlim(false);
        var sentWhilePinned = new ConcurrentQueue<bool>();

        var pin = PinUiThreadUntil(release, pinning);

        // The releaser runs off the UI thread: it opens the pin only once the pin is really held AND the
        // first chunk has gone out, so the second chunk's continuation has a pinned UI thread to wait on.
        var releaser = Task.Run(() =>
        {
            if (!pinning.Wait(HangGuard)) throw new TimeoutException("the UI thread was never pinned");
            if (!firstChunkSent.Wait(HangGuard)) throw new TimeoutException("the first chunk was never sent");
            release.Set();
        });

        await NaiveUiThreadPace(text, _ =>
        {
            sentWhilePinned.Enqueue(!release.IsSet);
            firstChunkSent.Set();
        });

        Assert.Equal(PinEnd.Signal, await pin);
        await releaser;

        var sent = sentWhilePinned.ToArray();
        Assert.True(sent.Length > 1, $"expected the paste to go out in several chunks, got {sent.Length}");
        Assert.True(sent[0], "the first chunk goes out synchronously, before the pacing ever awaits");
        Assert.All(sent.Skip(1), pinned => Assert.False(pinned,
            "a chunk after the first was sent while the UI thread was still pinned - the naive pacing did not need the UI thread, so the pin is not saturating it"));
    }

    /// <summary>The naive loop the fix replaced: chunked, but resuming on the UI thread.</summary>
    private static async Task NaiveUiThreadPace(string text, Action<byte[]> send)
    {
        const int chunk = 8;
        for (int i = 0; i < text.Length; i += chunk)
        {
            send(System.Text.Encoding.UTF8.GetBytes(text.Substring(i, Math.Min(chunk, text.Length - i))));
            await Task.Delay(15); // no ConfigureAwait(false): continuation returns to the UI thread
        }
    }

    private enum PinEnd { Signal, HangGuard }

    /// <summary>
    /// Post a job that spins the UI thread until <paramref name="until"/> is set, blocking the dispatcher
    /// completely. <paramref name="pinning"/>, when given, is set the moment the spin begins. The task
    /// says what ended the pin: the signal, or the hang guard.
    /// </summary>
    private static Task<PinEnd> PinUiThreadUntil(ManualResetEventSlim until, ManualResetEventSlim? pinning = null)
    {
        var tcs = new TaskCompletionSource<PinEnd>();
        Dispatcher.UIThread.Post(() =>
        {
            pinning?.Set();
            var deadline = Environment.TickCount64 + (long)HangGuard.TotalMilliseconds;
            var ended = PinEnd.HangGuard;
            while (Environment.TickCount64 < deadline)
            {
                if (until.IsSet) { ended = PinEnd.Signal; break; }
                // Pin the UI thread: no wait, no yield.
            }
            tcs.SetResult(ended);
        }, DispatcherPriority.Normal);
        return tcs.Task;
    }
}
