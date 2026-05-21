using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// In-memory registry of live Directors. Two ingress paths feed it, both kept
/// permanently:
///
///   1. Filesystem watch on %LOCALAPPDATA%\cc-director\config\director\instances\.
///      Used by same-machine Directors. Same-machine Directors do not need any
///      Gateway URL configured to be discovered. See <see cref="InstancesDirectory"/>.
///
///   2. HTTP register / heartbeat / unregister. Used by Directors that have
///      <c>gateway.url</c> configured (typically cross-machine). The Director POSTs
///      <see cref="Upsert"/> on startup, calls <see cref="Heartbeat"/> every 15 s, and
///      DELETEs via <see cref="Remove"/> on graceful shutdown.
///
/// De-duplication: keys are <c>directorId</c>. If both paths report the same id,
/// the HTTP entry wins because it carries <see cref="DirectorDto.TailnetEndpoint"/>
/// which the FSW path cannot provide.
/// </summary>
public sealed class DirectorRegistry : IDisposable
{
    public static string InstancesDirectory { get; } =
        Path.Combine(CcStorage.Config(), "director", "instances");

    /// <summary>If an HTTP-registered Director has not heartbeat for this long, it gets swept.</summary>
    public static TimeSpan HttpHeartbeatTimeout { get; } = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, DirectorDto> _directors = new();
    private FileSystemWatcher? _watcher;
    private Timer? _sweeper;
    private bool _disposed;

    /// <summary>Raised when a Director appears (file created or HTTP register).</summary>
    public event Action<DirectorDto>? OnDirectorAdded;

    /// <summary>Raised when a Director disappears (file removed, HTTP unregister, or stale).</summary>
    public event Action<string>? OnDirectorRemoved;

    // ===== HTTP path =====

    /// <summary>
    /// Add or refresh an HTTP-registered Director. Idempotent. The dto is stamped
    /// with <c>Source="http"</c> and <c>LastSeen=UtcNow</c>. If an FSW entry already
    /// exists for the same id, the HTTP entry replaces it (it has the tailnet endpoint).
    /// </summary>
    public DirectorDto Upsert(DirectorRegistrationRequest req)
    {
        if (string.IsNullOrEmpty(req.DirectorId))
            throw new ArgumentException("directorId is required", nameof(req));

        var now = DateTime.UtcNow;
        var dto = new DirectorDto
        {
            DirectorId = req.DirectorId,
            Pid = req.Pid,
            StartedAt = req.StartedAt == default ? now : req.StartedAt,
            ControlEndpoint = req.TailnetEndpoint, // HTTP path: the tailnet endpoint IS the control endpoint
            TailnetEndpoint = req.TailnetEndpoint,
            MachineName = req.MachineName,
            User = req.User,
            Version = req.Version,
            SchemaVersion = 1,
            LastSeen = now,
            Source = "http",
        };

        var existed = _directors.TryGetValue(req.DirectorId, out _);
        _directors[req.DirectorId] = dto;
        FileLog.Write($"[DirectorRegistry] Upsert (http): id={dto.DirectorId}, endpoint={dto.TailnetEndpoint}, existed={existed}");
        if (!existed)
            OnDirectorAdded?.Invoke(dto);
        return dto;
    }

    /// <summary>
    /// Refresh the heartbeat timestamp on an existing HTTP-registered Director.
    /// Returns false if the id is unknown (caller can choose to ask the Director to re-register).
    /// </summary>
    public bool Heartbeat(string directorId)
    {
        if (string.IsNullOrEmpty(directorId)) return false;
        if (!_directors.TryGetValue(directorId, out var existing)) return false;
        existing.LastSeen = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Remove a Director from the registry (HTTP graceful shutdown). Returns true if
    /// it was present.
    /// </summary>
    public bool Remove(string directorId)
    {
        if (string.IsNullOrEmpty(directorId)) return false;
        if (_directors.TryRemove(directorId, out _))
        {
            FileLog.Write($"[DirectorRegistry] Remove (http): id={directorId}");
            OnDirectorRemoved?.Invoke(directorId);
            return true;
        }
        return false;
    }

    // ===== FSW path =====

    /// <summary>Begin watching the instances directory and start the stale sweeper.</summary>
    public void Start()
    {
        FileLog.Write($"[DirectorRegistry] Start: watching {InstancesDirectory}");
        Directory.CreateDirectory(InstancesDirectory);

        LoadExisting();

        _watcher = new FileSystemWatcher(InstancesDirectory, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Created += OnFileCreatedOrChanged;
        _watcher.Changed += OnFileCreatedOrChanged;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;

        // Stale sweeper - every 30s.
        // FSW entries: drop if file gone or PID dead.
        // HTTP entries: drop if LastSeen older than HttpHeartbeatTimeout.
        _sweeper = new Timer(_ => SweepStale(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>Snapshot of all currently-known Directors.</summary>
    public IReadOnlyCollection<DirectorDto> ListDirectors()
        => _directors.Values.ToList().AsReadOnly();

    /// <summary>Look up by Director ID. Null if unknown.</summary>
    public DirectorDto? Get(string directorId)
        => _directors.TryGetValue(directorId, out var d) ? d : null;

    private void LoadExisting()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(InstancesDirectory, "*.json"))
                TryParseAndAdd(f);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRegistry] LoadExisting FAILED: {ex.Message}");
        }
    }

    private void OnFileCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        FileLog.Write($"[DirectorRegistry] File created/changed: {e.Name}");
        // FileSystemWatcher fires before write is complete on some systems - retry briefly.
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (TryParseAndAdd(e.FullPath)) return;
                await Task.Delay(100);
            }
            FileLog.Write($"[DirectorRegistry] Could not parse {e.Name} after retries");
        });
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        FileLog.Write($"[DirectorRegistry] File deleted: {e.Name}");
        var id = Path.GetFileNameWithoutExtension(e.Name ?? "");
        if (string.IsNullOrEmpty(id)) return;
        // Only remove if the entry came from the FSW path. An HTTP entry must not be
        // wiped by a stray file delete - it lives by its own heartbeat lifecycle.
        if (_directors.TryGetValue(id, out var existing) && existing.Source == "file")
        {
            if (_directors.TryRemove(id, out _))
                OnDirectorRemoved?.Invoke(id);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        var oldId = Path.GetFileNameWithoutExtension(e.OldName ?? "");
        if (!string.IsNullOrEmpty(oldId)
            && _directors.TryGetValue(oldId, out var existing)
            && existing.Source == "file"
            && _directors.TryRemove(oldId, out _))
        {
            OnDirectorRemoved?.Invoke(oldId);
        }
        TryParseAndAdd(e.FullPath);
    }

    private bool TryParseAndAdd(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return false;

            var dto = JsonSerializer.Deserialize<DirectorDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (dto is null || string.IsNullOrEmpty(dto.DirectorId)) return false;

            // If an HTTP-registered entry exists for the same id, leave it alone.
            // HTTP carries the tailnet endpoint which FSW cannot supply.
            if (_directors.TryGetValue(dto.DirectorId, out var existing) && existing.Source == "http")
            {
                FileLog.Write($"[DirectorRegistry] Skipping FSW upsert for id={dto.DirectorId}: HTTP entry already present");
                return true;
            }

            dto.LastSeen = DateTime.UtcNow;
            dto.Source = "file";
            var wasNew = !_directors.ContainsKey(dto.DirectorId);
            _directors[dto.DirectorId] = dto;
            if (wasNew) OnDirectorAdded?.Invoke(dto);
            FileLog.Write($"[DirectorRegistry] Added (file): id={dto.DirectorId}, endpoint={dto.ControlEndpoint}");
            return true;
        }
        catch (IOException) { return false; /* file still being written */ }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRegistry] TryParseAndAdd FAILED for {path}: {ex.Message}");
            return false;
        }
    }

    private void SweepStale()
    {
        if (_disposed) return;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _directors.ToArray())
            {
                if (kv.Value.Source == "http")
                {
                    // HTTP entries: drop if no heartbeat for HttpHeartbeatTimeout.
                    var lastSeen = kv.Value.LastSeen ?? DateTime.MinValue;
                    if (now - lastSeen > HttpHeartbeatTimeout)
                    {
                        if (_directors.TryRemove(kv.Key, out _))
                        {
                            FileLog.Write($"[DirectorRegistry] Sweeper removed stale http entry: {kv.Key} (last heartbeat {(now - lastSeen).TotalSeconds:F0}s ago)");
                            OnDirectorRemoved?.Invoke(kv.Key);
                        }
                    }
                    continue;
                }

                // FSW path: file gone or PID dead.
                var f = Path.Combine(InstancesDirectory, $"{kv.Key}.json");
                if (!File.Exists(f))
                {
                    if (_directors.TryRemove(kv.Key, out _))
                    {
                        FileLog.Write($"[DirectorRegistry] Sweeper removed orphan (file gone): {kv.Key}");
                        OnDirectorRemoved?.Invoke(kv.Key);
                    }
                    continue;
                }

                var pid = kv.Value.Pid;
                if (pid > 0)
                {
                    try { System.Diagnostics.Process.GetProcessById(pid); }
                    catch (ArgumentException) // process not running
                    {
                        try { File.Delete(f); } catch { }
                        if (_directors.TryRemove(kv.Key, out _))
                        {
                            FileLog.Write($"[DirectorRegistry] Sweeper removed orphan (pid {pid} dead): {kv.Key}");
                            OnDirectorRemoved?.Invoke(kv.Key);
                        }
                    }
                    catch { /* permission errors etc - leave it for next pass */ }
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRegistry] SweepStale error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sweeper?.Dispose();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
