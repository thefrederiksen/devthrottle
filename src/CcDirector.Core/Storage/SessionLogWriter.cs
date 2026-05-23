using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Storage;

/// <summary>
/// Phase 5: persistent per-session log writer. One instance per <see cref="Session"/>.
///
/// Append-only JSONL streams, one per record kind:
///  - <c>raw.jsonl</c>              every chunk of bytes the terminal emitted
///                                  (base64-encoded so binary ANSI doesn't break JSON)
///  - <c>turns.jsonl</c>            every completed <see cref="TurnSummary"/>
///  - <c>wingman-events.jsonl</c> every color change the SessionStatusWingman wrote
///  - <c>agent-view.jsonl</c>       (reserved - hooked up by the agent-view layer in a later slice)
///
/// Hot-path safety: incoming events go through a bounded <see cref="Channel{T}"/>
/// and a single dedicated writer task. If the channel fills (slow disk, bad path,
/// runaway producer) the writer drops oldest records and logs a warning. The Session's
/// PTY drain loop is never blocked.
///
/// Restart safety: streams are opened in <see cref="FileMode.Append"/> so a Director
/// restart reattaches to the existing directory and continues appending.
/// </summary>
public sealed class SessionLogWriter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        // Keep one record per line: no indent.
        WriteIndented = false,
    };

    private readonly Guid _sessionId;
    private readonly Session _session;

    // Bounded so a runaway producer can't OOM us. 4096 records ~= a few seconds of busy
    // terminal output even at very high throughput; if we're behind that long, drop.
    private readonly Channel<LogEntry> _channel =
        Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private Task? _writerTask;
    private bool _started;
    private bool _disposed;

    // File handles, opened lazily on first record per kind.
    private StreamWriter? _rawStream;
    private StreamWriter? _turnsStream;
    private StreamWriter? _wingmanStream;
    private StreamWriter? _agentViewStream;

    // Producer-side subscriptions we own so we can unhook on Dispose.
    private Action<byte[]>? _onBytes;
    private Action<string, string, string>? _onColor;

    public SessionLogWriter(Session session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _sessionId = session.Id;
    }

    /// <summary>Begin watching the session and writing to disk. Idempotent.</summary>
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        try
        {
            Directory.CreateDirectory(SessionLogPaths.SessionDir(_sessionId));
            WriteMetaJson();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionLogWriter] Start failed for {_sessionId}: {ex.Message}");
            return;
        }

        _writerTask = Task.Run(RunWriterAsync);

        // Subscribe to producers. Each handler does the minimum work and enqueues.
        if (_session.Buffer is not null)
        {
            _onBytes = bytes => Enqueue(new LogEntry(LogKind.Raw, new
            {
                ts = DateTime.UtcNow,
                len = bytes.Length,
                b64 = Convert.ToBase64String(bytes),
            }));
            _session.Buffer.OnBytesWritten += _onBytes;
        }

        // Note: TurnSummary is added by TurnSummaryCache after Haiku finishes. We
        // log the SUMMARY (not the raw TurnData) via WriteTurnSummary, called from
        // the cache. So no OnTurnCompleted subscription here - it would log the raw
        // turn before Haiku has labelled it, which isn't useful.

        _onColor = (oldColor, newColor, reason) => Enqueue(new LogEntry(LogKind.WingmanEvent, new
        {
            ts = DateTime.UtcNow,
            oldColor,
            newColor,
            reason,
        }));
        _session.OnStatusColorChanged += _onColor;
    }

    /// <summary>
    /// Called by <see cref="Wingman.TurnSummaryCache"/> after Haiku returns and
    /// the summary has landed in the in-memory cache. We persist the summary so it
    /// survives Director restart and is replayable by the wingman.
    /// </summary>
    public void WriteTurnSummary(TurnSummary summary)
    {
        if (summary is null) return;
        Enqueue(new LogEntry(LogKind.Turn, summary));
    }

    /// <summary>
    /// Hook for future agent-view widget logging. The agent-view layer (currently
    /// rendered on read from Claude's JSONL) will call this with each widget so we
    /// own a self-contained record of what the user saw.
    /// </summary>
    public void WriteAgentViewWidget(object widget)
    {
        if (widget is null) return;
        Enqueue(new LogEntry(LogKind.AgentView, widget));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe producers FIRST so no new records arrive while we drain.
        if (_session.Buffer is not null && _onBytes is not null)
            _session.Buffer.OnBytesWritten -= _onBytes;
        if (_onColor is not null)
            _session.OnStatusColorChanged -= _onColor;

        // Complete the channel; the writer task processes remaining queued records
        // and the foreach exits naturally when the channel is empty + completed.
        _channel.Writer.TryComplete();
        try { _writerTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) { FileLog.Write($"[SessionLogWriter] writer drain failed for {_sessionId}: {ex.Message}"); }

        try { _rawStream?.Dispose(); } catch { }
        try { _turnsStream?.Dispose(); } catch { }
        try { _wingmanStream?.Dispose(); } catch { }
        try { _agentViewStream?.Dispose(); } catch { }
    }

    // ---------- internals ----------

    private void WriteMetaJson()
    {
        var meta = new
        {
            sessionId = _sessionId,
            repoPath = _session.RepoPath,
            agent = _session.AgentKind.ToString(),
            createdAt = _session.CreatedAt.UtcDateTime,
            schema = 1,
            writtenAt = DateTime.UtcNow,
        };
        File.WriteAllText(SessionLogPaths.MetaJson(_sessionId),
            JsonSerializer.Serialize(meta, JsonOpts), Encoding.UTF8);
    }

    private void Enqueue(LogEntry entry)
    {
        if (_disposed) return;
        // TryWrite never blocks; if the channel is full the bounded policy
        // (DropOldest) silently evicts the oldest unwritten record.
        _channel.Writer.TryWrite(entry);
    }

    private async Task RunWriterAsync()
    {
        try
        {
            var reader = _channel.Reader;
            await foreach (var entry in reader.ReadAllAsync())
            {
                try { WriteOne(entry); }
                catch (Exception ex)
                {
                    FileLog.Write($"[SessionLogWriter] write failed for {_sessionId}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionLogWriter] writer loop crashed for {_sessionId}: {ex}");
        }
    }

    private void WriteOne(LogEntry entry)
    {
        var stream = entry.Kind switch
        {
            LogKind.Raw             => GetOrOpen(ref _rawStream, SessionLogPaths.RawJsonl(_sessionId)),
            LogKind.Turn            => GetOrOpen(ref _turnsStream, SessionLogPaths.TurnsJsonl(_sessionId)),
            LogKind.WingmanEvent => GetOrOpen(ref _wingmanStream, SessionLogPaths.WingmanEventsJsonl(_sessionId)),
            LogKind.AgentView       => GetOrOpen(ref _agentViewStream, SessionLogPaths.AgentViewJsonl(_sessionId)),
            _ => null,
        };
        if (stream is null) return;
        stream.WriteLine(JsonSerializer.Serialize(entry.Payload, JsonOpts));
        // Flush every record so a Director crash never silently loses the most recent
        // events. The single writer task is the only thread touching this stream, so
        // there's no contention; flush cost is one syscall and is well under the
        // bounded channel's drain rate.
        stream.Flush();
    }

    private static StreamWriter GetOrOpen(ref StreamWriter? slot, string path)
    {
        if (slot is not null) return slot;
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        slot = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
        return slot;
    }

    private enum LogKind { Raw, Turn, WingmanEvent, AgentView }

    private sealed record LogEntry(LogKind Kind, object Payload);
}
