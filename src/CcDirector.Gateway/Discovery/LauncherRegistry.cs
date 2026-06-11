using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// In-memory registry of live cc-launcher processes.
///
/// A launcher POSTs <see cref="LauncherRegistrationRequest"/> to /launchers/register on
/// startup and heartbeats every 30 s. The Gateway relay uses the stored port + token to
/// forward lifecycle verbs to the remote launcher's loopback REST API.
///
/// Keys are machine names (case-insensitive). One machine -> one launcher entry.
///
/// Issue #331.
/// </summary>
public sealed class LauncherRegistry
{
    /// <summary>How long without a heartbeat before a launcher is considered stale and swept.</summary>
    public static TimeSpan HeartbeatTimeout { get; } = TimeSpan.FromSeconds(90);

    private sealed class Entry
    {
        public required LauncherDto Dto;
        /// <summary>The launcher's Bearer token (NOT exposed in <see cref="LauncherDto"/>).</summary>
        public required string Token;
        public DateTime LastSeenAt;
    }

    private readonly ConcurrentDictionary<string, Entry> _launchers =
        new(StringComparer.OrdinalIgnoreCase);

    private Timer? _sweepTimer;

    /// <summary>
    /// Register or refresh a launcher. Returns the stored <see cref="LauncherDto"/> for
    /// the 201/200 response body (no token exposed).
    /// </summary>
    public LauncherDto Upsert(LauncherRegistrationRequest req)
    {
        var now = DateTime.UtcNow;
        var dto = new LauncherDto
        {
            MachineName = req.MachineName,
            Port = req.Port,
            Pid = req.Pid,
            Version = req.Version,
            StartedAt = req.StartedAt,
            LastSeenAt = now,
        };

        _launchers[req.MachineName] = new Entry
        {
            Dto = dto,
            Token = req.Token,
            LastSeenAt = now,
        };

        FileLog.Write($"[LauncherRegistry] Upsert: machine={req.MachineName}, port={req.Port}, pid={req.Pid}, version={req.Version}");
        return dto;
    }

    /// <summary>
    /// Record a heartbeat from a known launcher. Returns true if the launcher was found;
    /// false (-> 410) if it is unknown (it should re-register).
    /// </summary>
    public bool Heartbeat(string machineName)
    {
        if (!_launchers.TryGetValue(machineName, out var entry))
        {
            FileLog.Write($"[LauncherRegistry] Heartbeat: unknown machine={machineName} -> 410");
            return false;
        }

        var now = DateTime.UtcNow;
        entry.LastSeenAt = now;
        entry.Dto.LastSeenAt = now;
        FileLog.Write($"[LauncherRegistry] Heartbeat: machine={machineName}");
        return true;
    }

    /// <summary>
    /// Remove a launcher entry (called on graceful launcher shutdown).
    /// </summary>
    public void Remove(string machineName)
    {
        if (_launchers.TryRemove(machineName, out _))
            FileLog.Write($"[LauncherRegistry] Remove: machine={machineName}");
    }

    /// <summary>
    /// Look up a launcher by machine name. Returns null if not registered.
    /// </summary>
    public LauncherDto? Get(string machineName)
    {
        return _launchers.TryGetValue(machineName, out var e) ? e.Dto : null;
    }

    /// <summary>
    /// Retrieve the relay token for a launcher. Returns null if not registered.
    /// </summary>
    public string? GetToken(string machineName)
    {
        return _launchers.TryGetValue(machineName, out var e) ? e.Token : null;
    }

    /// <summary>
    /// All registered launchers, as public DTOs (no tokens).
    /// </summary>
    public IReadOnlyList<LauncherDto> ListLaunchers()
    {
        return _launchers.Values.Select(e => e.Dto).ToList();
    }

    /// <summary>
    /// Start the periodic stale-entry sweep (removes launchers that have not heartbeat
    /// within <see cref="HeartbeatTimeout"/>).
    /// </summary>
    public void StartSweep()
    {
        _sweepTimer = new Timer(Sweep, null,
            dueTime: TimeSpan.FromSeconds(30),
            period: TimeSpan.FromSeconds(30));
        FileLog.Write("[LauncherRegistry] Sweep timer started (30s interval)");
    }

    private void Sweep(object? _)
    {
        var cutoff = DateTime.UtcNow - HeartbeatTimeout;
        foreach (var kv in _launchers)
        {
            if (kv.Value.LastSeenAt < cutoff)
            {
                if (_launchers.TryRemove(kv.Key, out var removed))
                    FileLog.Write($"[LauncherRegistry] Sweep: removed stale launcher machine={kv.Key} (last-seen={removed.LastSeenAt:o})");
            }
        }
    }

    public void Dispose()
    {
        _sweepTimer?.Dispose();
    }
}
