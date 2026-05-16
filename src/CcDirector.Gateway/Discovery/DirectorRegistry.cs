using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// Watches the shared instances directory for Director registration files,
/// parses them, and keeps an in-memory map of live Directors.
///
/// Directory: %LOCALAPPDATA%\cc-director\config\director\instances\{guid}.json
/// </summary>
public sealed class DirectorRegistry : IDisposable
{
    public static string InstancesDirectory { get; } =
        Path.Combine(CcStorage.Config(), "director", "instances");

    private readonly ConcurrentDictionary<string, DirectorDto> _directors = new();
    private FileSystemWatcher? _watcher;
    private Timer? _sweeper;
    private bool _disposed;

    /// <summary>Raised when a Director appears (file created or updated).</summary>
    public event Action<DirectorDto>? OnDirectorAdded;

    /// <summary>Raised when a Director disappears (file removed or sweeper detected stale).</summary>
    public event Action<string>? OnDirectorRemoved;

    /// <summary>Begin watching. Loads any pre-existing registration files.</summary>
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

        // Stale sweeper - every 30 s, drop entries whose files no longer exist
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
        if (_directors.TryRemove(id, out _))
            OnDirectorRemoved?.Invoke(id);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        var oldId = Path.GetFileNameWithoutExtension(e.OldName ?? "");
        if (!string.IsNullOrEmpty(oldId) && _directors.TryRemove(oldId, out _))
            OnDirectorRemoved?.Invoke(oldId);
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

            dto.LastSeen = DateTime.UtcNow;
            _directors[dto.DirectorId] = dto;
            OnDirectorAdded?.Invoke(dto);
            FileLog.Write($"[DirectorRegistry] Added: id={dto.DirectorId}, endpoint={dto.ControlEndpoint}");
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
            foreach (var kv in _directors.ToArray())
            {
                var f = Path.Combine(InstancesDirectory, $"{kv.Key}.json");

                // Path 1: registration file is gone -> remove
                if (!File.Exists(f))
                {
                    if (_directors.TryRemove(kv.Key, out _))
                    {
                        FileLog.Write($"[DirectorRegistry] Sweeper removed orphan (file gone): {kv.Key}");
                        OnDirectorRemoved?.Invoke(kv.Key);
                    }
                    continue;
                }

                // Path 2: PID is gone -> remove the file too
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
