namespace CcDirectorClient.Voice;

/// <summary>
/// Decides whether a network-backed action may proceed, given the device's current
/// connectivity. Kept MAUI-free (a plain bool in, a verdict out) so the page maps
/// Connectivity.Current.NetworkAccess to the bool and this stays unit-testable off-device.
///
/// Issue #147: a button press must never sink into a disabled "busy" state waiting on a
/// network call that cannot succeed. When there is no connection the page calls this first,
/// gets an immediate verdict, shows the message, and stays responsive instead of hanging
/// behind the 5-minute HTTP timeout. The recording / "stop talking" parts are local and
/// run regardless; only the upload/advance step is gated here.
/// </summary>
public static class OfflineGuard
{
    /// <summary>
    /// The outcome of a connectivity gate: whether to proceed, and what to tell the user
    /// when not. <see cref="Message"/> is empty when <see cref="Allowed"/> is true.
    /// </summary>
    public sealed record Verdict(bool Allowed, string Message);

    /// <summary>
    /// Gate a network action. <paramref name="online"/> is the device-level "can reach the
    /// network" state (the page passes Connectivity.Current.NetworkAccess == NetworkAccess.Internet).
    /// <paramref name="action"/> is a short verb phrase used in the message, e.g.
    /// "send your answer" or "load your sessions".
    /// </summary>
    public static Verdict Check(bool online, string action)
    {
        if (online)
            return new Verdict(true, "");

        var what = string.IsNullOrWhiteSpace(action) ? "do that" : action.Trim();
        return new Verdict(false, $"No connection - can't {what} right now. Check your signal and try again.");
    }
}
