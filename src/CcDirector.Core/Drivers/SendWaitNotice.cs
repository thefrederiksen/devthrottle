using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Drivers;

/// <summary>
/// ONE WAIT IN THE SEND PATH THAT SAYS WHAT IT IS WAITING FOR ONCE IT RUNS LONG (Voice Delivery mission, phase 3).
///
/// On 25 September 2026 at 09:05 the Director held a spoken prompt for 122 seconds before typing its Enter, and the log
/// said nothing between "bracketed paste submit" and the give-up line two minutes later. A wait that is silent reads
/// exactly like a hang. So every wait in the send path that can last longer than a few seconds carries one of these:
/// once the wait passes <see cref="NoticeAfter"/> it writes a line naming what it waits for and its limit, and when it
/// ends it writes a line saying how long it took and how it ended. A wait that ends quickly writes nothing, so the
/// ordinary send is no noisier than before.
/// </summary>
internal sealed class SendWaitNotice
{
    /// <summary>How long a wait runs before it says what it is waiting for. A healthy send finishes well inside it.</summary>
    internal static readonly TimeSpan NoticeAfter = TimeSpan.FromSeconds(5);

    /// <summary>Test seam: sees every line a notice writes, alongside the log. Null in the Director.</summary>
    internal static Action<string>? LineObserver { get; set; }

    private readonly string _tag;
    private readonly string _waitingFor;
    private readonly string _limit;
    private readonly Func<TimeSpan> _elapsed;
    private bool _announced;

    /// <param name="tag">Who is waiting - the log prefix, such as "ClaudeCode" or "Session".</param>
    /// <param name="waitingFor">What the wait is for, in words: "the agent to take in a 847-character paste".</param>
    /// <param name="limit">When it gives up, in words: "120s", or "until the send in front finishes".</param>
    /// <param name="elapsed">The clock; tests pass one they advance. Null reads real time from now.</param>
    internal SendWaitNotice(string tag, string waitingFor, string limit, Func<TimeSpan>? elapsed = null)
    {
        _tag = tag;
        _waitingFor = waitingFor;
        _limit = limit;
        if (elapsed is null)
        {
            var sw = Stopwatch.StartNew();
            _elapsed = () => sw.Elapsed;
        }
        else
        {
            _elapsed = elapsed;
        }
    }

    /// <summary>Called on every turn of the wait's loop: writes the "still waiting" line once, when the wait runs long.</summary>
    internal void Check()
    {
        if (_announced) return;
        var elapsed = _elapsed();
        if (elapsed < NoticeAfter) return;
        _announced = true;
        Write($"[{_tag}] WAITING {elapsed.TotalSeconds:F0}s so far for {_waitingFor} (limit {_limit})");
    }

    /// <summary>Called once when the wait ends. Writes a line only if the wait ran long enough to announce itself.</summary>
    internal void End(string outcome)
    {
        Check();
        if (!_announced) return;
        Write($"[{_tag}] WAIT ENDED after {_elapsed().TotalSeconds:F1}s for {_waitingFor}: {outcome}");
    }

    /// <summary>
    /// Await one task that is itself the wait - a semaphore, another send's end - saying what it waits for if it runs
    /// long. The task's own result or exception is passed through untouched.
    /// </summary>
    internal static async Task WatchAsync(Task waiting, string tag, string waitingFor, string limit)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        if (waiting.IsCompleted)
        {
            await waiting;
            return;
        }
        var notice = new SendWaitNotice(tag, waitingFor, limit);
        using var cts = new CancellationTokenSource();
        if (await Task.WhenAny(waiting, Task.Delay(NoticeAfter, cts.Token)) != waiting)
            notice.Check();
        cts.Cancel();
        try
        {
            await waiting;
            notice.End("done");
        }
        catch (Exception ex)
        {
            notice.End($"FAILED: {ex.Message}");
            throw;
        }
    }

    private static void Write(string line)
    {
        FileLog.Write(line);
        LineObserver?.Invoke(line);
    }
}
