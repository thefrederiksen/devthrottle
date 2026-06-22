using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Centralization Phase 1 (issue #629): the durable, bounded, restart-surviving RETRY QUEUE
/// that sits BEHIND the login-telemetry relay (issue #628). The relay no longer forwards inline; it
/// hands every accepted event to this queue, which owns delivery to the backend.
///
/// Behaviour:
/// <list type="bullet">
///   <item>FIFO: events flush in the order they were enqueued (best-effort FIFO, at-least-once).</item>
///   <item>Retry with backoff: a failed or unreachable forward leaves the event at the HEAD of the
///     queue and the flusher waits the retry interval before trying again, so a backend outage queues
///     events instead of dropping them.</item>
///   <item>Bounded: the queue never grows past <see cref="MaxSize"/>; when full, the OLDEST event is
///     evicted (dropped) with a logged WARNING so there is no unbounded growth.</item>
///   <item>Durable: the whole queue is persisted to one JSON file (the WorkListStore precedent: atomic
///     temp + rename write-through, reload on construction, corrupt-file quarantine) under the Gateway
///     config directory, so queued events survive a Gateway restart.</item>
/// </list>
///
/// Security (issue #628 property preserved): a queued payload carries the inbound access token (the
/// Bearer) in memory and on disk so it can be replayed, but the token value is NEVER written to the
/// Gateway log on any path - every log line records only the target URL, the queue depth, and the
/// outcome.
/// </summary>
public sealed class TelemetryRetryQueue : IAsyncDisposable
{
    /// <summary>The default maximum number of queued events before the oldest is evicted.</summary>
    public const int DefaultMaxSize = 1000;

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly HttpClient _client;
    private readonly TimeSpan _retryInterval;
    private readonly IGatewayTelemetryTokenSource? _gatewayTokenSource;
    private readonly LinkedList<QueuedEvent> _events = new();

    private readonly CancellationTokenSource _flushCts = new();
    private Task? _flushLoop;
    private bool _disposed;

    /// <summary>The maximum number of events the queue holds before evicting the oldest.</summary>
    public int MaxSize { get; }

    /// <summary>The current number of queued events awaiting delivery.</summary>
    public int Depth
    {
        get { lock (_gate) return _events.Count; }
    }

    /// <param name="path">
    /// The JSON file the queue persists to. REQUIRED so no caller silently lands on the real user's
    /// file: production (<see cref="GatewayHost"/>) passes telemetry-queue.json in the Gateway config
    /// directory; tests pass an isolated temp path.
    /// </param>
    /// <param name="client">The HttpClient used to forward queued events to the backend.</param>
    /// <param name="retryInterval">
    /// How long the flusher waits between drain passes when the backend is unreachable (also the
    /// idle poll interval when the queue is empty).
    /// </param>
    /// <param name="maxSize">The bound; the oldest event is evicted once the queue exceeds this.</param>
    /// <param name="gatewayTokenSource">
    /// Gateway Centralization Phase 2 (issue #639): the source of the GATEWAY's own account token,
    /// attached at FORWARD time when the Gateway acts as the single egress to the cloud. When supplied:
    /// the Gateway's token is attached and any per-event stored <see cref="QueuedEvent.Bearer"/> (a
    /// leftover inbound Director token) is IGNORED; and when the Gateway is NOT signed in the forward is
    /// deferred (the event stays queued, FIFO preserved, and flushes once the Gateway signs in). When
    /// null (a host with no credential service, or Phase 1 callers) the queue falls back to the stored
    /// per-event Bearer - the original #628/#629 behaviour, unchanged.
    /// </param>
    /// <exception cref="ArgumentException">The path is null/empty/whitespace.</exception>
    /// <exception cref="ArgumentNullException">The client is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">maxSize or retryInterval is not positive.</exception>
    public TelemetryRetryQueue(
        string path,
        HttpClient client,
        TimeSpan retryInterval,
        int maxSize = DefaultMaxSize,
        IGatewayTelemetryTokenSource? gatewayTokenSource = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("queue path is required", nameof(path));
        if (client is null)
            throw new ArgumentNullException(nameof(client));
        if (maxSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSize), "maxSize must be positive");
        if (retryInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryInterval), "retryInterval must be positive");

        _path = path;
        _client = client;
        _retryInterval = retryInterval;
        _gatewayTokenSource = gatewayTokenSource;
        MaxSize = maxSize;
        Load();
    }

    /// <summary>
    /// Start the background flush loop. Called once by the host after construction. A second call is
    /// a no-op so the loop is never double-started.
    /// </summary>
    public void StartFlushing()
    {
        lock (_gate)
        {
            if (_flushLoop is not null)
                return;
            _flushLoop = Task.Run(() => FlushLoopAsync(_flushCts.Token));
        }
        FileLog.Write($"[TelemetryRetryQueue] StartFlushing: retryInterval={_retryInterval.TotalSeconds}s, maxSize={MaxSize}, depth={Depth}");
    }

    /// <summary>
    /// Enqueue one accepted telemetry event for durable delivery. The body and Bearer are stored
    /// verbatim so they replay UNCHANGED. When the queue is full the OLDEST event is evicted first
    /// (logged WARNING). The token value is never logged.
    /// </summary>
    /// <param name="targetUrl">The backend URL to forward to.</param>
    /// <param name="body">The event JSON, forwarded unchanged.</param>
    /// <param name="bearer">The inbound access token, replayed unchanged; NEVER logged.</param>
    /// <exception cref="ArgumentException">targetUrl is null/empty/whitespace.</exception>
    public void Enqueue(string targetUrl, string body, string? bearer)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
            throw new ArgumentException("targetUrl is required", nameof(targetUrl));

        var item = new QueuedEvent
        {
            Id = Guid.NewGuid().ToString("N"),
            EnqueuedAtUtc = DateTime.UtcNow,
            TargetUrl = targetUrl,
            Body = body ?? string.Empty,
            Bearer = bearer,
        };

        int depth;
        bool evicted = false;
        lock (_gate)
        {
            _events.AddLast(item);
            while (_events.Count > MaxSize)
            {
                var oldest = _events.First;
                if (oldest is null)
                    break;
                _events.RemoveFirst();
                evicted = true;
                FileLog.Write($"[TelemetryRetryQueue] WARNING bound exceeded (maxSize={MaxSize}); evicted OLDEST event id={oldest.Value.Id} enqueuedAt={oldest.Value.EnqueuedAtUtc:O} target={oldest.Value.TargetUrl} (dropped, not delivered)");
            }
            depth = _events.Count;
            Save();
        }

        FileLog.Write($"[TelemetryRetryQueue] Enqueue: target={targetUrl} (bearerPresent={(bearer is not null)}), depth={depth}{(evicted ? " (oldest evicted)" : "")}");
    }

    /// <summary>
    /// Try to drain the queue once, head-first, in FIFO order. Each event is forwarded; on success it
    /// is removed from the head and the next is attempted; on the FIRST failure the pass stops and the
    /// failing event stays at the head (so order is preserved and it is retried next pass). Returns the
    /// number of events delivered this pass. Public so a test can trigger a deterministic drain without
    /// waiting on the timer.
    /// </summary>
    public async Task<int> FlushOnceAsync(CancellationToken cancellationToken = default)
    {
        var delivered = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            QueuedEvent head;
            lock (_gate)
            {
                if (_events.First is null)
                    break;
                head = _events.First.Value;
            }

            var ok = await TryForwardAsync(head, cancellationToken);
            if (!ok)
                break; // leave it at the head; retry next pass (FIFO preserved)

            lock (_gate)
            {
                // The head may only be removed if it is still the same event (it always is here:
                // a single flusher drains, and Enqueue only appends to the tail).
                if (_events.First is not null && _events.First.Value.Id == head.Id)
                {
                    _events.RemoveFirst();
                    Save();
                }
            }
            delivered++;
        }
        return delivered;
    }

    /// <summary>
    /// Forward one event to its backend URL with the stored body and the token to attach. Returns true
    /// on a 2xx, false on any non-2xx or transport failure - and false WITHOUT forwarding when a Gateway
    /// token source is configured but the Gateway is not signed in, so the event stays queued for a later
    /// pass (issue #639). The token value is never logged.
    /// </summary>
    private async Task<bool> TryForwardAsync(QueuedEvent item, CancellationToken cancellationToken)
    {
        // Issue #639: when a Gateway token source is wired, the Gateway attaches its OWN account token
        // and the per-event stored Bearer (a leftover inbound Director token) is ignored. If the Gateway
        // is not signed in, the forward is DEFERRED (event stays queued) - never sent without the token.
        string? tokenToAttach;
        if (_gatewayTokenSource is not null)
        {
            if (!_gatewayTokenSource.TryGetAccessToken(out tokenToAttach) || tokenToAttach is null)
            {
                FileLog.Write($"[TelemetryRetryQueue] forward DEFERRED (gateway not signed in): {item.TargetUrl}, id={item.Id} (kept queued, will flush after sign-in)");
                return false;
            }
        }
        else
        {
            // Phase 1 / no-credential-service host: fall back to the stored per-event Bearer unchanged.
            tokenToAttach = item.Bearer;
        }

        try
        {
            using var forward = new HttpRequestMessage(HttpMethod.Post, item.TargetUrl)
            {
                Content = new StringContent(item.Body, System.Text.Encoding.UTF8, "application/json"),
            };
            if (tokenToAttach is not null)
                forward.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenToAttach);

            using var resp = await _client.SendAsync(forward, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[TelemetryRetryQueue] forward OK: {item.TargetUrl} -> {(int)resp.StatusCode}, id={item.Id}");
                return true;
            }

            FileLog.Write($"[TelemetryRetryQueue] forward FAILED (backend status): {item.TargetUrl} -> {(int)resp.StatusCode}, id={item.Id} (will retry)");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown - not a delivery failure to log as an error; just leave it queued.
            return false;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TelemetryRetryQueue] forward FAILED (unreachable): {item.TargetUrl} -> {ex.Message}, id={item.Id} (will retry)");
            return false;
        }
    }

    /// <summary>
    /// The background flush loop: drains the queue head-first, then waits the retry interval before
    /// the next pass. A pass that delivers everything still waits the interval before polling again,
    /// so an empty queue costs one timer wakeup per interval and nothing more.
    /// </summary>
    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (Depth > 0)
                {
                    var delivered = await FlushOnceAsync(cancellationToken);
                    if (delivered > 0)
                        FileLog.Write($"[TelemetryRetryQueue] flush pass delivered {delivered}, remaining depth={Depth}");
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TelemetryRetryQueue] flush loop error: {ex.Message}");
            }

            try { await Task.Delay(_retryInterval, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ---- persistence (issue #629; WorkListStore precedent, issue #301) -----------------------

    /// <summary>One queued telemetry event, persisted verbatim so it replays unchanged.</summary>
    public sealed class QueuedEvent
    {
        public string Id { get; set; } = string.Empty;
        public DateTime EnqueuedAtUtc { get; set; }
        public string TargetUrl { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;

        /// <summary>The inbound access token, replayed unchanged. On disk, never logged.</summary>
        public string? Bearer { get; set; }
    }

    /// <summary>The on-disk shape: one document holding the ordered queue.</summary>
    private sealed class QueueFile
    {
        public List<QueuedEvent> Events { get; set; } = new();
    }

    /// <summary>
    /// Load the queue written by a previous Gateway run. Called once from the constructor. A missing
    /// file is the normal first boot (empty queue, logged), never an error. A corrupt file is
    /// quarantined (renamed next to the original with a timestamp suffix) so its bytes are preserved
    /// for the operator and never silently overwritten, and the queue then starts empty so the
    /// Gateway still boots. The token values are NOT logged on any load path.
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[TelemetryRetryQueue] Load: no queue file at {_path}; starting empty");
            return;
        }

        QueueFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<QueueFile>(File.ReadAllText(_path), FileJsonOptions);
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
            return;
        }

        if (parsed is null)
        {
            Quarantine("file deserialized to null (no queue document)");
            return;
        }

        foreach (var ev in parsed.Events)
        {
            if (string.IsNullOrWhiteSpace(ev.Id) || string.IsNullOrWhiteSpace(ev.TargetUrl))
            {
                Quarantine("a persisted event has an empty id or targetUrl");
                _events.Clear();
                return;
            }
            _events.AddLast(ev);
        }

        FileLog.Write($"[TelemetryRetryQueue] Load: restored {_events.Count} queued event(s) from {_path}");
    }

    /// <summary>
    /// Preserve an unreadable queue file as "&lt;path&gt;.corrupt-&lt;stamp&gt;" and log loudly. The
    /// original path is then free for the next write-through. The move is not allowed to fail silently:
    /// if even the quarantine fails, the exception propagates and the Gateway does not start half-blind.
    /// </summary>
    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[TelemetryRetryQueue] Load FAILED: queue file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty.");
    }

    /// <summary>
    /// Write-through: serialize the whole queue and atomically replace the file (temp + rename), so a
    /// concurrent reader or a crash mid-write never sees a half-written queue. Called inside the lock by
    /// every mutation. A failed save is a LOGGED error that propagates - never a silent skip.
    /// </summary>
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var file = new QueueFile { Events = _events.ToList() };
            var json = JsonSerializer.Serialize(file, FileJsonOptions);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TelemetryRetryQueue] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Stop the background flush loop. The persisted file already holds every undelivered event (it is
    /// written through on every mutation), so a stop never loses queued events - they reload on the
    /// next Gateway start.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        FileLog.Write($"[TelemetryRetryQueue] DisposeAsync: stopping flush loop, depth={Depth}");
        _flushCts.Cancel();
        Task? loop;
        lock (_gate) loop = _flushLoop;
        if (loop is not null)
        {
            try { await loop; }
            catch (Exception ex) { FileLog.Write($"[TelemetryRetryQueue] flush loop stop error: {ex.Message}"); }
        }
        _flushCts.Dispose();
    }
}
