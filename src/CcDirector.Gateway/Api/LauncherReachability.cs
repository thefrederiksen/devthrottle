using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The ONE definition of how far a launcher is from being able to receive a command, from the two facts
/// the Gateway holds about it: its registration row and whether it holds a command stream.
///
/// WHY IT IS ITS OWN TYPE. This rule was written inline inside <see cref="LauncherLifecycleRelay"/>,
/// where it was reached only AFTER a command had failed to deliver - the right place to explain a
/// refusal, and the wrong place to ask the question in advance. The capability query has to ask it
/// BEFORE anything is sent, and a second spelling of it in the query would be a copy free to drift from
/// the one the refusals use: the day the heartbeat window changes, or a fourth state is added, one of the
/// two answers silently stops matching the other, and the query that exists to be trusted becomes the
/// one that is wrong. So the rule moved here and both callers read it.
///
/// THE THREE REFUSALS HAVE THREE DIFFERENT FIXES, WHICH IS THE WHOLE REASON THEY ARE SEPARATE. Install a
/// launcher; get a stopped launcher running again; update a launcher that is running perfectly. A single
/// "cannot reach it" would send two readers out of three to examine the wrong thing - and one of them to
/// check a network connection that is demonstrably carrying heartbeats.
/// </summary>
internal static class LauncherReachability
{
    /// <summary>
    /// Classify a launcher's reach.
    ///
    /// A LIVE STREAM IS THE WHOLE ANSWER WHEN IT IS THERE, and it outranks the registration row
    /// deliberately: delivery rides the stream and nothing else (remove-the-network-port mission, phase
    /// 6), so a launcher holding one is reachable even in the window before its registration lands or
    /// after the row has been swept. Reading the row first would report a reachable machine as
    /// unreachable on the strength of presence metadata that no command travels over.
    /// </summary>
    /// <param name="registered">The launcher's registration row, or null when this tenant has none.</param>
    /// <param name="streamConnected">Whether that (tenant, machine) holds a live command stream.</param>
    /// <param name="nowUtc">The clock, injected so the heartbeat window is testable.</param>
    public static LauncherReach Classify(LauncherDto? registered, bool streamConnected, DateTime nowUtc)
    {
        if (streamConnected) return LauncherReach.Connected;
        if (registered is null) return LauncherReach.NoLauncher;

        // Heartbeating and yet unreachable. The two facts together are the evidence: it can talk TO this
        // Gateway and this Gateway cannot talk to it, which is exactly what a launcher predating the
        // command stream looks like. It expected the Gateway to dial its own listener, and that relay is
        // deleted.
        var quietFor = nowUtc - registered.LastSeenAt;
        return quietFor < LauncherRegistry.HeartbeatTimeout
            ? LauncherReach.NotStreamCapable
            : LauncherReach.NotConnected;
    }

    /// <summary>How long since that launcher last registered or heartbeated, in whole seconds. 0 when no
    /// launcher is registered. Negative clock skew is reported as 0 rather than as a negative age, which
    /// would read as a heartbeat from the future.</summary>
    public static int QuietForSeconds(LauncherDto? registered, DateTime nowUtc)
    {
        if (registered is null) return 0;
        var quietFor = nowUtc - registered.LastSeenAt;
        return quietFor < TimeSpan.Zero ? 0 : (int)quietFor.TotalSeconds;
    }
}
