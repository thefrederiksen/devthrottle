using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The desktop's last-known list of WAITING requests from sessions that want to talk to another session (issue #3851),
/// the same list the Cockpit polls. The rail shows an amber phone badge on the asking session's row and its header a
/// chip that opens Approve / Deny; both read <see cref="Current"/>, which never blocks and never throws.
///
/// It reads when the Gateway connection goes green and then every <see cref="PollInterval"/> while connected, the
/// Cockpit's cadence: a request is not urgent, the asking session carries on meanwhile. A failed read keeps the last
/// list and records <see cref="Error"/>; it never invents one.
/// </summary>
public sealed class LinkRequestsCache : IDisposable
{
    /// <summary>How often the waiting requests are read again while the Gateway is connected.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly Func<IGatewayLinkRequests?> _gateway;
    private readonly object _lock = new();
    private IReadOnlyList<FleetMessageLinkRequestDto> _current = Array.Empty<FleetMessageLinkRequestDto>();
    private string? _error;
    private Task? _inFlight;
    private Timer? _timer;
    private GatewayConnectionMonitor? _monitor;

    /// <param name="gateway">Reads the seam lazily, so the cache follows a client replaced on a settings change.</param>
    public LinkRequestsCache(Func<IGatewayLinkRequests?> gateway) => _gateway = gateway;

    /// <summary>Raised when the waiting list changed. Raised on the reading thread; the subscriber marshals to the
    /// user-interface thread itself.</summary>
    public event Action? Changed;

    /// <summary>The waiting requests, or empty when none wait or none have been read. Never blocks, never throws.</summary>
    public IReadOnlyList<FleetMessageLinkRequestDto> Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Why the last read failed, in words, or null when the last read succeeded.</summary>
    public string? Error
    {
        get { lock (_lock) return _error; }
    }

    /// <summary>The waiting requests made BY <paramref name="sessionId"/>, the session that asked.</summary>
    public IReadOnlyList<FleetMessageLinkRequestDto> From(string sessionId)
        => Current.Where(r => string.Equals(r.RequesterSessionId, sessionId, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Read on connect, then every <see cref="PollInterval"/> while the connection stays green.</summary>
    public void AttachTo(GatewayConnectionMonitor monitor)
    {
        _monitor = monitor;
        monitor.Changed += () =>
        {
            if (monitor.Status == GatewayConnectionStatus.Connected)
            {
                FileLog.Write("[LinkRequestsCache] Gateway connected: reading the requests to talk to another session");
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

    /// <summary>Read the list and keep the waiting ones. A failure keeps the last list and records why.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var all = await (_gateway() is { } gateway
                ? gateway.ListLinkRequestsAsync(ct)
                : Task.FromResult<List<FleetMessageLinkRequestDto>?>(null));
            if (all is null) return;

            var waiting = all.Where(r => r.Status == "pending").ToList();
            bool changed;
            lock (_lock)
            {
                changed = !SameIds(_current, waiting) || _error is not null;
                _current = waiting;
                _error = null;
            }
            if (changed)
            {
                FileLog.Write($"[LinkRequestsCache] RefreshAsync: {waiting.Count} request(s) waiting");
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            lock (_lock) _error = ex.Message;
            FileLog.Write($"[LinkRequestsCache] RefreshAsync FAILED (keeping the last list): {ex.Message}");
        }
    }

    /// <summary>
    /// Approve what the session asked for, or say no. On success the request leaves the list at once and the list is
    /// read again. THROWS with the Gateway's own words on a refusal, so the chip can show it - an answer that quietly
    /// failed would look exactly like one that worked.
    /// </summary>
    public async Task AnswerAsync(string requestId, bool approve, CancellationToken ct = default)
    {
        var gateway = _gateway() ?? throw new InvalidOperationException(
            "This Director is not connected to a Gateway, so a request cannot be answered from here.");
        FileLog.Write($"[LinkRequestsCache] AnswerAsync: request={requestId} approve={approve}");
        await gateway.AnswerLinkRequestAsync(requestId, approve, ct);
        lock (_lock) _current = _current.Where(r => r.RequestId != requestId).ToList();
        Changed?.Invoke();
        BeginRefresh();
    }

    private static bool SameIds(IReadOnlyList<FleetMessageLinkRequestDto> a, IReadOnlyList<FleetMessageLinkRequestDto> b)
        => a.Count == b.Count && a.Select(r => r.RequestId).OrderBy(x => x).SequenceEqual(b.Select(r => r.RequestId).OrderBy(x => x));

    public void Dispose() => _timer?.Dispose();
}
