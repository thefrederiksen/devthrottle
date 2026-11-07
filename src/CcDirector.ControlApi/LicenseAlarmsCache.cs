using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The desktop's last-known license alarm banner (mission "Licenses - Always Ready, Loud When Not", step 2). The owner,
/// 6 November 2026: a machine that cannot switch is shown "across the top of every Cockpit, Director and phone screen".
/// The Director's bar reads <see cref="Current"/>, which never blocks and never throws, and draws the Gateway's
/// <see cref="LicenseAlarmsResponse.Summary"/>, <see cref="LicenseAlarmsResponse.LinkText"/> and
/// <see cref="LicenseAlarmsResponse.Tone"/> as given: it decides nothing.
///
/// It reads when the Gateway connection goes green and then every <see cref="PollInterval"/> while connected - the
/// Cockpit banner's own cadence. A failed read keeps the last answer and records <see cref="Error"/>; it never invents
/// one, and it never hides a banner because a read failed.
/// </summary>
public sealed class LicenseAlarmsCache : IDisposable
{
    /// <summary>How often the open alarms are read again while the Gateway is connected.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly Func<IGatewayLicenseAlarms?> _gateway;
    private readonly object _lock = new();
    private LicenseAlarmsResponse? _current;
    private string? _error;
    private Task? _inFlight;
    private Timer? _timer;
    private GatewayConnectionMonitor? _monitor;

    /// <param name="gateway">Reads the seam lazily, so the cache follows a client replaced on a settings change.</param>
    public LicenseAlarmsCache(Func<IGatewayLicenseAlarms?> gateway) => _gateway = gateway;

    /// <summary>Raised when the banner's words or tone changed. Raised on the reading thread; the subscriber marshals to
    /// the user-interface thread itself.</summary>
    public event Action? Changed;

    /// <summary>The last answer read, or null when none has been read. Never blocks, never throws.</summary>
    public LicenseAlarmsResponse? Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Why the last read failed, in words, or null when the last read succeeded.</summary>
    public string? Error
    {
        get { lock (_lock) return _error; }
    }

    /// <summary>Read on connect, then every <see cref="PollInterval"/> while the connection stays green.</summary>
    public void AttachTo(GatewayConnectionMonitor monitor)
    {
        _monitor = monitor;
        monitor.Changed += () =>
        {
            if (monitor.Status == GatewayConnectionStatus.Connected)
            {
                FileLog.Write("[LicenseAlarmsCache] Gateway connected: reading the license alarms");
                BeginRefresh();
            }
        };
        _timer = new Timer(_ =>
        {
            if (_monitor?.Status == GatewayConnectionStatus.Connected) BeginRefresh();
        }, null, PollInterval, PollInterval);
    }

    /// <summary>Start a read unless one is running. Fire-and-forget; a failure never escapes.</summary>
    public void BeginRefresh() => _ = ReadNowAsync();

    /// <summary>Read now, or join the read already running.</summary>
    public Task ReadNowAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_inFlight is { IsCompleted: false } running) return running;
            return _inFlight = Task.Run(() => RefreshAsync(ct), ct);
        }
    }

    /// <summary>Read the open alarms. A failure keeps the last answer and records why.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var answer = await (_gateway() is { } gateway
                ? gateway.GetLicenseAlarmsAsync(ct)
                : Task.FromResult<LicenseAlarmsResponse?>(null));
            if (answer is null) return;

            bool changed;
            lock (_lock)
            {
                changed = !SameBanner(_current, answer) || _error is not null;
                _current = answer;
                _error = null;
            }
            if (changed)
            {
                FileLog.Write($"[LicenseAlarmsCache] RefreshAsync: {answer.Count} alarm(s) open, tone={answer.Tone ?? "none"}");
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            lock (_lock) _error = ex.Message;
            FileLog.Write($"[LicenseAlarmsCache] RefreshAsync FAILED (keeping the last answer): {ex.Message}");
        }
    }

    private static bool SameBanner(LicenseAlarmsResponse? a, LicenseAlarmsResponse b)
        => a is not null && a.Count == b.Count && a.Summary == b.Summary && a.LinkText == b.LinkText && a.Tone == b.Tone;

    public void Dispose() => _timer?.Dispose();
}
