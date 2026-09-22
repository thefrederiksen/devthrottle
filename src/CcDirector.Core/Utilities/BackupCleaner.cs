using System.Text.Json;
using CcDirector.Core.Background;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Periodically scans ~/.claude/backups/ and removes corrupted JSON backup files.
/// Claude Code has a known bug where concurrent writes to ~/.claude.json produce
/// corrupted backup/snapshot files that pile up quickly.
/// </summary>
public sealed class BackupCleaner : IDisposable
{
    private readonly string _backupsDir;
    private readonly Action<string>? _log;
    private readonly HashSet<string> _processedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _scanInterval;
    private readonly TimeSpan _minFileAge;
    private readonly BackgroundJobs _jobs;
    private BackgroundJob? _job;
    private bool _disposed;

    /// <summary>Raised when a corrupted backup file is successfully deleted.</summary>
    public Action<string>? OnCorruptedFileDeleted;

    /// <summary>Raised when deletion of a corrupted backup file fails.</summary>
    public Action<string, Exception>? OnDeletionFailed;

    /// <summary>
    /// Creates a BackupCleaner that scans the Claude backups directory.
    /// </summary>
    /// <param name="backupsDir">Override backups directory path (for testing). If null, uses ~/.claude/backups/.</param>
    /// <param name="scanInterval">Override scan interval (for testing). Default 60 seconds.</param>
    /// <param name="minFileAge">Override minimum file age before processing (for testing). Default 5 seconds.</param>
    /// <param name="log">Optional logging callback.</param>
    /// <param name="jobs">The scheduler the scan registers with; the process-wide one when null.</param>
    public BackupCleaner(
        string? backupsDir = null,
        TimeSpan? scanInterval = null,
        TimeSpan? minFileAge = null,
        Action<string>? log = null,
        BackgroundJobs? jobs = null)
    {
        _jobs = jobs ?? BackgroundJobs.Default;
        _backupsDir = backupsDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "backups");
        _scanInterval = scanInterval ?? TimeSpan.FromSeconds(60);
        _minFileAge = minFileAge ?? TimeSpan.FromSeconds(5);
        _log = log;
    }

    /// <summary>
    /// Starts the periodic scan timer.
    /// </summary>
    public void Start()
    {
        _log?.Invoke($"[BackupCleaner] Starting periodic scan of {_backupsDir} (interval={_scanInterval.TotalSeconds}s)");
        // A slow-and-steady job on the scheduler (docs/BackgroundWork.md): once at start, then every
        // scan interval, never overlapping, off the window's thread.
        _job = _jobs.Register(
            new BackgroundJobSpec("Backup cleaner", BackgroundJobTier.SlowAndSteady, _scanInterval, "the cleaner is disposed"),
            _ => { ScanOnce(); return Task.CompletedTask; });
        _job.StartTimer(TimeSpan.Zero);
    }

    /// <summary>
    /// Runs a single scan cycle. Called by the timer, but also exposed for testing.
    /// </summary>
    internal void ScanOnce()
    {
        if (!Directory.Exists(_backupsDir))
        {
            _log?.Invoke($"[BackupCleaner] Directory does not exist: {_backupsDir}");
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(_backupsDir);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[BackupCleaner] Failed to enumerate {_backupsDir}: {ex.Message}");
            return;
        }

        // Explicitly check for NUL file (Windows won't enumerate reserved device names)
        CheckForNulFile();

        foreach (var filePath in files)
        {
            var fileName = Path.GetFileName(filePath);

            if (_processedFiles.Contains(fileName))
                continue;

            try
            {
                ProcessFile(filePath, fileName);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[BackupCleaner] Error processing {fileName}: {ex.Message}");
            }
        }
    }

    private void CheckForNulFile()
    {
        var nulPath = Path.Combine(_backupsDir, "nul");
        if (_processedFiles.Contains("nul"))
            return;

        try
        {
            // Must use extended-length path to detect NUL files on Windows
            var extendedPath = NulFileWatcher.ToExtendedLengthPath(nulPath);
            if (File.Exists(extendedPath))
            {
                DeleteFile(nulPath, "NUL file");
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[BackupCleaner] Error checking for NUL file: {ex.Message}");
        }
    }

    private void ProcessFile(string filePath, string fileName)
    {
        // Skip Python scripts
        if (fileName.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            _processedFiles.Add(fileName);
            return;
        }

        // Check file age - skip files younger than threshold
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            return;

        var age = DateTime.UtcNow - fileInfo.LastWriteTimeUtc;
        if (age < _minFileAge)
            return;

        // Handle NUL files
        if (fileName.Equals("nul", StringComparison.OrdinalIgnoreCase))
        {
            DeleteFile(filePath, "NUL file");
            return;
        }

        // Files marked as corrupted by Claude itself
        if (fileName.Contains(".corrupted.", StringComparison.OrdinalIgnoreCase))
        {
            DeleteFile(filePath, "corrupted-marked");
            return;
        }

        // Validate JSON
        if (!IsValidJson(filePath))
        {
            DeleteFile(filePath, "invalid JSON");
            return;
        }

        // Valid JSON file - keep it
        _processedFiles.Add(fileName);
    }

    private static bool IsValidJson(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length == 0)
                return false;

            using var doc = JsonDocument.Parse(bytes);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            // File may be locked by another process - treat as not-yet-ready
            return true;
        }
    }

    private void DeleteFile(string filePath, string reason)
    {
        var fileName = Path.GetFileName(filePath);

        try
        {
            // Use extended-length path for NUL files on Windows
            if (fileName.Equals("nul", StringComparison.OrdinalIgnoreCase))
            {
                NulFileWatcher.TryDeleteNulFile(filePath);
            }
            else
            {
                File.Delete(filePath);
            }

            _log?.Invoke($"[BackupCleaner] Deleted {reason}: {filePath}");
            _processedFiles.Add(fileName);
            OnCorruptedFileDeleted?.Invoke(filePath);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[BackupCleaner] Delete FAILED ({reason}): {filePath} - {ex.Message}");
            OnDeletionFailed?.Invoke(filePath, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _job?.Dispose();
        _job = null;
    }
}
