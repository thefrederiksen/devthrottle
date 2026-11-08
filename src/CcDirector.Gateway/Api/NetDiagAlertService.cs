using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Events;
using CcDirector.Gateway.Inbox;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The drift-alert channels (Network Diagnostics mission, P5). It delivers the monitor's Decide-machine
/// outputs - which the machine already emits correctly and false-alert-proof - to two channels:
///   - an ambient DOORBELL event (an in-app/Cockpit "your home network is slow" indicator), and
///   - a message in the owner's YOUR INBOX, from "Network", with the specific fix, naming the drifted device,
///     and one push to the owner's phones (<see cref="SystemAlertInbox"/>).
///
/// NO EMAIL. Until the owner's ruling "Move them all to Your inbox, with a push to the phone" this emailed the
/// owner through the signed-in account. This class now holds no mail sender of any kind, so it cannot send one.
/// Your inbox needs no signed-in account, so the old "not signed in, doorbell only" path is gone: every drift
/// under the cap is told.
///
/// Safety, because this is the one place a mistake would put a false "your home is slow" in front of the
/// owner: it adds NO detection of its own (the machine is the sole source of truth); it caps the messages per
/// day so a broken-for-hours network cannot fill the inbox; it tells one drift per EPISODE (the machine's rising
/// edge is already one-shot); and it tells "recovered" ONLY for a device it actually warned about. It never
/// touches the needs-you badge. Plain English, ASCII, names the device, no fabrication.
/// </summary>
public sealed class NetDiagAlertService
{
    /// <summary>The most drift messages one day carries.</summary>
    public const int DefaultDailyMessageCap = 3;

    // A fixed ring key for these gateway-originated network events (they are not tied to a Director/session).
    private const string RingKey = "network";

    // MTR-01 (Codex round 1): these are same-machine, gateway-originated network signals with no per-account
    // credential, so - like the doorbell/discovery plane - they file under the Local tenant. The event ring is
    // now keyed by (tenant, id); recording under Local keeps self-host behavior identical and, on hosted, files
    // into a ring no account's tenant reads (the reader keys by the request tenant, never Local), so this can
    // never surface to another account. The monitor runs only on a self-hosted Gateway, whose one account is
    // Local, so the Your inbox message is that account's too.
    private static readonly TenantId RingTenant = TenantId.Local;

    private readonly DirectorEventLog _events;
    private readonly SystemAlertInbox _inbox;
    private readonly int _dailyMessageCap;
    private readonly Func<DateTime> _nowUtc;

    private readonly object _gate = new();
    private DateOnly _messageDay;
    private int _messagesToday;
    private readonly HashSet<string> _warnedEpisode = new(StringComparer.Ordinal);

    /// <param name="inbox">Writes the message into the owner's Your inbox and pushes it to the phones. Tests inject
    /// one built over a fake writer and a fake pusher.</param>
    public NetDiagAlertService(DirectorEventLog events, SystemAlertInbox inbox, int? dailyMessageCap = null, Func<DateTime>? nowUtc = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _dailyMessageCap = dailyMessageCap ?? DefaultDailyMessageCap;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>Persistent home drift for <paramref name="deviceName"/>: doorbell always; a Your inbox message and a
    /// push while under the daily cap.</summary>
    public void OnDrift(string deviceName)
    {
        try { _events.Record(RingTenant, RingKey, "", DoorbellEvents.NetworkDrift, deviceName); }
        catch (Exception ex) { FileLog.Write($"[NetDiagAlert] doorbell drift failed: {ex.Message}"); }

        bool underCap;
        lock (_gate)
        {
            RollDay();
            underCap = _messagesToday < _dailyMessageCap;
            if (underCap) _messagesToday++;
        }
        if (!underCap)
        {
            FileLog.Write($"[NetDiagAlert] drift for {deviceName}: daily message cap ({_dailyMessageCap}) reached - doorbell only");
            return;
        }

        // Mark the device "warned" only when the message is actually WRITTEN (inside TellAsync), so
        // resolve-only-if-warned holds even if the write fails.
        _ = TellAsync(DriftSubject(deviceName), DriftBodyText(deviceName), markWarnedDevice: deviceName);
    }

    /// <summary>Observed recovery for <paramref name="deviceName"/>: doorbell always; a "recovered" message ONLY if a
    /// drift message was written for it.</summary>
    public void OnResolve(string deviceName)
    {
        try { _events.Record(RingTenant, RingKey, "", DoorbellEvents.NetworkRecovered, deviceName); }
        catch (Exception ex) { FileLog.Write($"[NetDiagAlert] doorbell recovered failed: {ex.Message}"); }

        bool weWarned;
        lock (_gate) { weWarned = _warnedEpisode.Remove(deviceName); }
        if (!weWarned) return; // never send an unsolicited "recovered" - only close the loop we opened

        _ = TellAsync(RecoverySubject(deviceName), RecoveryBodyText(deviceName), markWarnedDevice: null);
    }

    // Writes the Your inbox message and pushes it; once written, marks markWarnedDevice as "warned" so a later
    // resolve tells a recovery only for a drift the owner actually received (null for the recovery itself).
    private async Task TellAsync(string subject, string bodyText, string? markWarnedDevice)
    {
        try
        {
            var written = await _inbox.TellAsync(RingTenant, YourInboxWords.NetworkSeat, subject, bodyText, CancellationToken.None)
                .ConfigureAwait(false);
            if (markWarnedDevice is not null)
                lock (_gate) { _warnedEpisode.Add(markWarnedDevice); }
            FileLog.Write($"[NetDiagAlert] told the owner in Your inbox: message={written.Id:D}");
        }
        catch (Exception ex)
        {
            // The boundary of a fire-and-forget alert: a failed write is logged, never thrown into the monitor.
            FileLog.Write($"[NetDiagAlert] Your inbox message FAILED: {ex.Message}");
        }
    }

    private void RollDay()
    {
        var today = DateOnly.FromDateTime(_nowUtc());
        if (today != _messageDay) { _messageDay = today; _messagesToday = 0; }
    }

    // ----- pure message content (unit-tested) -----

    public static string DriftSubject(string deviceName) => $"Your home network is slow ({deviceName})";

    public static string DriftBodyText(string deviceName) =>
        $"{deviceName} has been relaying through a distant server instead of a direct connection on your home network for several minutes.\n\n" +
        "This only fires when it persists - a brief slow start when you open the app is normal and is not reported.\n\n" +
        "To get back to a fast, direct connection:\n" +
        $"- Keep the DevThrottle app open on {deviceName}.\n" +
        "- In the Tailscale app on that device, make sure it has local-network access.\n" +
        "- Make sure no Tailscale exit node is routing your home traffic.\n" +
        "- On your router, enable UPnP and do not isolate wireless clients, so a direct path can form.\n\n" +
        "You will get a follow-up message when it is back on a direct path.";

    public static string RecoverySubject(string deviceName) => $"Home network recovered ({deviceName})";

    public static string RecoveryBodyText(string deviceName) =>
        $"{deviceName} is back on a direct connection on your home network. No action needed.";
}
