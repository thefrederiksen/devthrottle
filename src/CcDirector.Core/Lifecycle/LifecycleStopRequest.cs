namespace CcDirector.Core.Lifecycle;

/// <summary>
/// THE ONE RECORD THAT SOMETHING OUTSIDE THIS PROCESS HAS ASKED IT TO STOP - raised by the
/// <see cref="LifecycleSignalNames.DirectorShutdown"/> listener and read by anything that must tell
/// "I am being stopped" apart from "the thing I asked for failed".
///
/// WHY IT EXISTS (product issue 3257). The launcher's way of ACCEPTING a restart is to stop the very
/// Director that asked for it, so the awaited request dies together with the process that made it. Read
/// as an error, that cancellation is indistinguishable from a launcher that refused - and twice out of
/// two a Director restarted correctly and told the person on the screen that its restart had been
/// REFUSED, which is the contract's word for a restart that did NOT happen. The engine cannot tell the
/// two apart from the exception, because both arrive as one; it can tell them apart from THIS, because
/// only one of them is accompanied by this process being asked to stop.
///
/// IT IS THE SIGNAL AND NOTHING ELSE. A window closed by the person is also a stop, and it is
/// deliberately NOT recorded here: the launcher stops a Director by raising the named signal
/// (<c>DirectorSupervisor.StopAsync</c>), so the signal - and only the signal - is evidence that
/// something outside this process acted on the ask. Widening this to "anything that stops us" would
/// turn a person closing the window mid-restart into a launcher's acceptance.
///
/// THE FIRST REQUEST WINS AND NOTHING EVER CLEARS IT. A process is asked to stop once and then stops;
/// a second raise is the same fact arriving twice, and forgetting the first would hide it.
/// </summary>
public static class LifecycleStopRequest
{
    private static long _madeAtTicks;

    /// <summary>
    /// Record that this process has been asked to stop. Safe to call from any thread and from any number
    /// of callers: the first moment is kept.
    /// </summary>
    /// <param name="utcNow">The moment the request arrived, in universal time.</param>
    public static void Record(DateTime utcNow)
    {
        var ticks = utcNow.ToUniversalTime().Ticks;
        Interlocked.CompareExchange(ref _madeAtTicks, ticks == 0 ? 1 : ticks, 0);
    }

    /// <summary>Has this process been asked to stop?</summary>
    public static bool WasMade => Interlocked.Read(ref _madeAtTicks) != 0;

    /// <summary>When the request arrived, in universal time, or null when none has.</summary>
    public static DateTime? MadeAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _madeAtTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }
}
