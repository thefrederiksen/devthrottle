using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The desktop's last-known copy of what every session colour MEANS, read FROM the Gateway
/// (<c>GET /gateway/session-colours</c>).
///
/// Two readers, one read. The rail's colour hover needs the legend's NAME for a colour on every repaint,
/// and the "What do the colours mean?" window needs the whole legend when it opens. Neither may wait on a
/// network call - a hover and a window both owe feedback in under 100 milliseconds (CodingStyle.md) - so
/// both read <see cref="Current"/>, which never blocks and never throws. This mirrors the web client's
/// one shared legend read (<c>packages/client-core/src/sessions/sessionColours.ts</c>), for the same
/// reason: every surface showing the same words means every surface asked the same question once.
///
/// It warms itself when the Gateway connection goes green (<see cref="AttachTo"/>) - the first moment the
/// answer is gettable, and the moment a reconnect should re-read a legend that may have changed while
/// this Director was away, which is precisely the case of a Gateway that has been updated underneath an
/// older Director.
///
/// <see cref="Current"/> is null until the first successful read, and that is NOT a fallback. A null
/// means this desktop genuinely does not know what the colours mean, and its readers say less rather than
/// inventing words: the hover falls back to the Gateway's own stamped LABEL for the session, and the
/// window says why it is empty. There is no built-in copy of the words to fall back on, deliberately - a
/// stale copy is exactly how a legend comes to explain a colour the product no longer paints.
/// </summary>
public sealed class SessionColourLegendCache
{
    /// <summary>
    /// How long a read legend is trusted before the next read of <see cref="Current"/> triggers a
    /// background refresh. The words change only when the Gateway is redeployed, so this is generous;
    /// it exists so a Director that has been up for days is not explaining a Gateway that has moved on.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    private readonly Func<IGatewayColourLegend?> _gateway;
    private readonly object _lock = new();
    private SessionColourLegendDto? _current;
    private string? _error;
    private DateTime _fetchedAtUtc = DateTime.MinValue;
    private Task? _inFlight;

    /// <param name="gateway">Reads the seam lazily, so the cache follows a client replaced on a settings
    /// change rather than pinning the one that existed at construction.</param>
    public SessionColourLegendCache(Func<IGatewayColourLegend?> gateway) => _gateway = gateway;

    /// <summary>
    /// Raised when a fresh legend has been stored. The rail subscribes: a colour dot's hover is built from
    /// the legend, and the legend lands SECONDS after the rows are already on screen, so without this
    /// every hover would show the Gateway's label alone until some unrelated event repainted the row.
    /// Raised outside the lock, on the reading thread - the subscriber marshals to the user-interface
    /// thread itself, as the rest of the rail's refreshes do.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// The last-known legend, or null when this desktop has never successfully read one. Never blocks and
    /// never throws - safe to read from a binding getter. Reading a stale value starts a background
    /// refresh and returns the stale value for THIS read; the next one sees the fresh legend.
    /// </summary>
    public SessionColourLegendDto? Current
    {
        get
        {
            SessionColourLegendDto? current;
            bool stale;
            lock (_lock)
            {
                current = _current;
                stale = DateTime.UtcNow - _fetchedAtUtc > StaleAfter;
            }

            if (stale) BeginRefresh();
            return current;
        }
    }

    /// <summary>
    /// Why the last read failed, in words, or null when one has succeeded or none has finished. The
    /// window shows this instead of an empty list, because a legend window that is silently blank says
    /// the colours have no meanings.
    /// </summary>
    public string? Error
    {
        get { lock (_lock) return _error; }
    }

    /// <summary>
    /// Warm the cache whenever the Gateway connection goes green. Connecting is the first moment the
    /// answer is gettable, and a reconnect is when a Gateway that has been updated underneath this
    /// Director should be re-read.
    /// </summary>
    public void AttachTo(GatewayConnectionMonitor monitor)
    {
        monitor.Changed += () =>
        {
            if (monitor.Status == GatewayConnectionStatus.Connected)
            {
                FileLog.Write("[SessionColourLegendCache] Gateway connected: re-reading what the colours mean");
                BeginRefresh();
            }
        };
    }

    /// <summary>
    /// Start a read unless one is already running, and never let its failure escape - an unreachable
    /// Gateway must not crash the app or a hover. Fire-and-forget by design: the caller keeps whatever it
    /// already had. A caller that must WAIT for the answer awaits <see cref="ReadNowAsync"/> instead, which
    /// is the same single read.
    /// </summary>
    public void BeginRefresh() => _ = ReadNowAsync();

    /// <summary>
    /// Read now, or JOIN the read that is already running, and hand back the task that finishes when the
    /// answer has landed. This is what a caller awaits when it has nothing to show and must wait for
    /// something - the legend window on a cold Director.
    ///
    /// IT EXISTS SO A COLD WINDOW DOES NOT DIAL TWICE. The window reads <see cref="Current"/>, which starts
    /// a background refresh when the answer is stale or absent, and then had to wait for one - and awaiting
    /// <see cref="RefreshAsync"/> directly walked straight past the in-flight guard and opened a second
    /// request to the Gateway for the same words.
    /// </summary>
    public Task ReadNowAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_inFlight is { IsCompleted: false } running) return running;
            return _inFlight = Task.Run(() => RefreshAsync(ct), ct);
        }
    }

    /// <summary>
    /// Read the legend and store it. Swallows failure ON PURPOSE and says so in the log: this is the one
    /// place that decides an unreachable Gateway means "keep showing what we last read" rather than
    /// "break the rail". A failure leaves <see cref="Current"/> untouched - null, or the last real answer
    /// - and records <see cref="Error"/> so the window can say what happened. It never becomes a made-up
    /// legend.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var legend = await (_gateway() is { } gateway
                ? gateway.GetSessionColourLegendAsync(ct)
                : Task.FromResult<SessionColourLegendDto?>(null));

            if (legend is null)
            {
                FileLog.Write("[SessionColourLegendCache] RefreshAsync: no Gateway configured, nothing to read");
                return;
            }

            lock (_lock)
            {
                _current = legend;
                _error = null;
                _fetchedAtUtc = DateTime.UtcNow;
            }
            FileLog.Write($"[SessionColourLegendCache] RefreshAsync: read {legend.Entries.Count} colour(s)");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            lock (_lock) _error = ex.Message;
            FileLog.Write($"[SessionColourLegendCache] RefreshAsync FAILED (keeping the last-known words): {ex.Message}");
        }
    }
}
