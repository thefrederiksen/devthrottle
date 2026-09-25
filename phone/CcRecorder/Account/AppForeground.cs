namespace CcRecorder.Account;

/// <summary>
/// Whether the recorder's window is on screen, and a way to wait until it is.
///
/// WHY: while the browser is in front for sign-in, Android cuts a backgrounded app off the network - on the
/// test emulator even the Gateway's host name would not resolve (EAI_NODATA) - so the trade of the account
/// sign-in for a device key has to happen once the user is back in the app, not the moment the browser
/// hands the sign-in back. <c>App</c> reports the window's Resumed and Stopped events here.
/// </summary>
public static class AppForeground
{
    private static readonly object Gate = new();
    private static bool _isForeground = true;
    private static TaskCompletionSource _returned = NewReturned(completed: true);

    /// <summary>True while the app's window is on screen.</summary>
    public static bool IsForeground
    {
        get { lock (Gate) return _isForeground; }
    }

    /// <summary>Called by the app when its window comes on screen (true) or leaves it (false).</summary>
    public static void Set(bool isForeground)
    {
        TaskCompletionSource? toComplete = null;
        lock (Gate)
        {
            if (_isForeground == isForeground) return;
            _isForeground = isForeground;
            if (isForeground)
                toComplete = _returned;
            else
                _returned = NewReturned(completed: false);
        }
        RecorderLog.Write($"[AppForeground] Set: foreground={isForeground}");
        toComplete?.TrySetResult();
    }

    /// <summary>Completes at once when the app is on screen, otherwise when it comes back.</summary>
    public static Task WaitAsync(CancellationToken ct)
    {
        Task returned;
        lock (Gate) returned = _returned.Task;
        return returned.WaitAsync(ct);
    }

    private static TaskCompletionSource NewReturned(bool completed)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) tcs.SetResult();
        return tcs;
    }
}
