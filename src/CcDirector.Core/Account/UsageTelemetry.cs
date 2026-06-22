using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// One richer usage-telemetry event (issue #582): an event name and the time it occurred. This is
/// the user-controllable telemetry, distinct from the always-on authentication floor
/// (<see cref="AuthEvent"/>). The record never carries the access or refresh token, and never carries
/// anything about the user's actual work - only the event name and a timestamp.
/// </summary>
/// <param name="Name">The usage event name.</param>
/// <param name="At">When the event occurred (UTC).</param>
public sealed record UsageEvent(string Name, DateTime At);

/// <summary>
/// The richer, user-controllable usage-telemetry path (issue #582). <see cref="Record"/> appends a
/// usage event to the local usage sink ONLY while the usage-telemetry toggle is on
/// (<see cref="TelemetrySettings.IsEnabled"/>); when the toggle is off it is a no-op, so nothing
/// about the user's usage is recorded. This is deliberately separate from the always-on
/// authentication floor (<see cref="AuthEventLog"/>), which records login and logout regardless of
/// the toggle: turning telemetry off stops the richer usage events while the authentication events
/// keep flowing.
///
/// Telemetry is greenfield (issue #582): this introduces the toggle, the gate on the richer path, and
/// a local sink that proves the gate; the server-side usage pipeline is owned by the
/// internal-repository child. The sink records only an event name and a timestamp - never the token
/// and never the user's work.
/// </summary>
public sealed class UsageTelemetry
{
    private readonly Func<bool> _isEnabled;
    private readonly string _sinkPath;
    private readonly object _gate = new();

    /// <summary>
    /// Creates the usage-telemetry path. By default the on/off decision reads the persisted
    /// <c>telemetry.enabled</c> flag and the sink is the usage-events file under the Director config
    /// directory; tests inject an explicit flag function and a temporary sink path.
    /// </summary>
    /// <param name="isEnabled">Decides, at the moment of each record, whether the richer telemetry is on. Defaults to <see cref="TelemetrySettings.IsEnabled"/>.</param>
    /// <param name="sinkPath">Where richer usage events are written. Defaults to the Director usage-events log.</param>
    public UsageTelemetry(Func<bool>? isEnabled = null, string? sinkPath = null)
    {
        _isEnabled = isEnabled ?? TelemetrySettings.IsEnabled;
        _sinkPath = sinkPath ?? CcStorage.DevThrottleUsageEventsLog();
    }

    /// <summary>
    /// Builds a usage-telemetry path whose richer events are gated by the GATEWAY's fleet-wide consent
    /// (issue #649) AND the Director's local toggle. The Gateway consent is the authoritative,
    /// fleet-wide opt-out; the local <see cref="TelemetrySettings"/> toggle remains until the Director
    /// cleanup issue (#651) removes it. Both must be on for an event to be recorded, so turning the
    /// Gateway setting OFF stops the richer usage events on every Director (fleet-wide). The decision is
    /// taken at the moment of each record from the Gateway consent's last-known cached value (refreshed
    /// off-thread by <see cref="GatewayTelemetryConsent.RefreshAsync"/>), so the synchronous gate never
    /// blocks on the network. The always-on login/director-startup auth-floor events are recorded
    /// elsewhere and are NOT gated by either flag.
    /// </summary>
    /// <param name="gatewayConsent">The Director-side reader of the Gateway's fleet-wide consent.</param>
    /// <param name="localToggle">
    /// The Director's local opt-out (issue #582). Defaults to <see cref="TelemetrySettings.IsEnabled"/>.
    /// </param>
    /// <param name="sinkPath">Where richer usage events are written. Defaults to the Director usage-events log.</param>
    public static UsageTelemetry ForDirector(
        GatewayTelemetryConsent gatewayConsent,
        Func<bool>? localToggle = null,
        string? sinkPath = null)
    {
        if (gatewayConsent is null)
            throw new ArgumentNullException(nameof(gatewayConsent));

        var local = localToggle ?? TelemetrySettings.IsEnabled;
        // Both gates must be on for an event to record. The Gateway read is the authoritative fleet
        // decision, read synchronously from the last-known cache (no network on this hot path).
        Func<bool> composed = () => local() && gatewayConsent.IsConsentedCached();
        return new UsageTelemetry(composed, sinkPath);
    }

    /// <summary>
    /// Records a richer usage event, but ONLY when the usage-telemetry toggle is on. When the toggle
    /// is off this is a no-op and returns false, so the caller cannot accidentally report usage while
    /// the user has opted out. Returns true when the event was written.
    /// </summary>
    public bool Record(string eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
            throw new ArgumentException("Event name is required", nameof(eventName));

        if (!_isEnabled())
        {
            FileLog.Write($"[UsageTelemetry] Record: telemetry toggle is OFF -> not recording usage event '{eventName}'");
            return false;
        }

        FileLog.Write($"[UsageTelemetry] Record: telemetry toggle is ON -> recording usage event '{eventName}'");
        Append(new UsageEvent(eventName, DateTime.UtcNow));
        return true;
    }

    /// <summary>Reads every recorded usage event in the order it was written, oldest first.</summary>
    public IReadOnlyList<UsageEvent> ReadAll()
    {
        FileLog.Write($"[UsageTelemetry] ReadAll: sinkPath={_sinkPath}, exists={File.Exists(_sinkPath)}");
        if (!File.Exists(_sinkPath))
            return Array.Empty<UsageEvent>();

        var events = new List<UsageEvent>();
        foreach (var line in File.ReadAllLines(_sinkPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var parsed = JsonSerializer.Deserialize<UsageEvent>(line);
            if (parsed is not null)
                events.Add(parsed);
        }
        return events;
    }

    private void Append(UsageEvent usageEvent)
    {
        var dir = Path.GetDirectoryName(_sinkPath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path: {_sinkPath}");
        lock (_gate)
        {
            Directory.CreateDirectory(dir);
            File.AppendAllText(_sinkPath, JsonSerializer.Serialize(usageEvent) + Environment.NewLine);
        }
    }
}
