using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Scheduler;

/// <summary>
/// Sidecar file that the scheduler leader writes on mutex acquisition so
/// followers can show "Following leader (exename pid N)" instead of just
/// "another Director". The mutex itself can't carry a payload.
///
/// File lives at <c>%LOCALAPPDATA%\cc-director\config\director\scheduler-leader.json</c>
/// and contains PID, exe path, and an acquired-at timestamp.
///
/// Stale-file handling: if a leader crashes, the file is left behind. Readers
/// (<see cref="Read"/>) verify the PID is alive via <see cref="Process.GetProcessById(int)"/>
/// and return null when the process is gone. Writers do not preemptively
/// delete stale files -- a fresh <see cref="Write"/> by the new leader simply
/// overwrites.
/// </summary>
public sealed class LeaderIdentityStore
{
    private readonly string _path;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public LeaderIdentityStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    /// <summary>Write our own identity to the file. Called when this process
    /// becomes leader.</summary>
    public void Write()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var pid = Environment.ProcessId;
            var exePath = Environment.ProcessPath ?? "";
            var record = new IdentityRecord
            {
                Pid = pid,
                ExePath = exePath,
                ExeName = string.IsNullOrEmpty(exePath)
                    ? "cc-director"
                    : System.IO.Path.GetFileNameWithoutExtension(exePath),
                AcquiredAtUtc = DateTime.UtcNow.ToString("o"),
            };

            var json = JsonSerializer.Serialize(record, JsonOptions);
            var tmp = _path + ".tmp";
            lock (_gate)
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            FileLog.Write($"[LeaderIdentity] Wrote {_path} pid={pid} exe={record.ExeName}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LeaderIdentity] Write FAILED: {ex.Message}");
        }
    }

    /// <summary>Delete the identity file. Called on clean release of leadership.</summary>
    public void Delete()
    {
        try
        {
            lock (_gate)
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            FileLog.Write($"[LeaderIdentity] Deleted {_path}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LeaderIdentity] Delete FAILED: {ex.Message}");
        }
    }

    /// <summary>Read and validate the current leader identity. Returns null
    /// when the file is missing OR the recorded PID is no longer alive
    /// (treat-as-stale). Callers that need to differentiate "missing" from
    /// "stale" can also check <see cref="File.Exists(string?)"/>.</summary>
    public IdentityRecord? Read()
    {
        try
        {
            string json;
            lock (_gate)
            {
                if (!File.Exists(_path)) return null;
                json = File.ReadAllText(_path);
            }

            var record = JsonSerializer.Deserialize<IdentityRecord>(json, JsonOptions);
            if (record == null || record.Pid <= 0) return null;

            if (!IsProcessAlive(record.Pid)) return null;

            return record;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LeaderIdentity] Read FAILED: {ex.Message}");
            return null;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public sealed class IdentityRecord
    {
        public int Pid { get; set; }
        public string ExePath { get; set; } = "";
        public string ExeName { get; set; } = "";
        public string AcquiredAtUtc { get; set; } = "";
    }
}
