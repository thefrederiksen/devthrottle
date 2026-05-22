using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;

namespace CcDirector.Core.Scheduler;

/// <summary>
/// Per-tick body of the scheduler. Holds the runner registry and, on each tick,
/// decides which runners should fire and invokes them.
///
/// Decision order per runner:
///   1. If a previous fire is still in-flight, skip.
///   2. If the per-runner cooldown (<see cref="RunnerRegistration.MinIntervalBetweenFires"/>)
///      has not elapsed, skip.
///   3. If the runner's <see cref="Schedule"/> says no, skip.
///   4. Count matching items via the runner's QueueFilter. If zero, skip.
///   5. Otherwise launch the runner. With <see cref="RunnerRegistration.RespectHumanCadence"/>
///      true, the launch is delayed by a uniform 0-60min jitter; the runner is
///      considered "in-flight" for that whole window so no second tick can
///      double-fire it.
///
/// The scheduler never touches DB state itself. Marking items posted or failed
/// is the runner's responsibility (typically via `cc-comm-queue mark-posted`).
/// </summary>
public sealed class CommQueueScheduler
{
    private readonly string _dbPath;
    private readonly string? _statePath;
    private readonly List<RunnerRegistration> _runners = new();
    private readonly Dictionary<string, RunnerState> _state = new();
    private readonly Random _jitter = new();
    private readonly object _gate = new();
    private readonly object _stateFileLock = new();

    public CommQueueScheduler(string? dbPath = null, string? statePath = null)
    {
        _dbPath = dbPath ?? CcStorage.CommQueueDb();
        _statePath = statePath;
    }

    public string DbPath => _dbPath;
    public string? StatePath => _statePath;

    public void RegisterRunner(RunnerRegistration runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        lock (_gate)
        {
            _runners.Add(runner);
            if (!_state.ContainsKey(runner.Name))
                _state[runner.Name] = new RunnerState();
        }
        FileLog.Write($"[CommQueueScheduler] Registered runner '{runner.Name}' cmd='{runner.Command}'");
    }

    /// <summary>Apply state previously written by <see cref="SaveStateToDisk"/>
    /// to registered runners whose in-memory state is still untouched
    /// (LastFiredAt == DateTime.MinValue). Call after all
    /// <see cref="RegisterRunner"/> calls on startup, and again after a
    /// hot-reload to restore state for newly added runners. Existing runners
    /// keep their in-memory state, which is at-least-as-recent as disk.</summary>
    public void LoadPersistedState()
    {
        if (_statePath == null) return;
        if (!File.Exists(_statePath)) return;

        try
        {
            string json;
            lock (_stateFileLock) json = File.ReadAllText(_statePath);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("runners", out var runners)) return;

            int applied = 0;
            lock (_gate)
            {
                foreach (var r in runners.EnumerateArray())
                {
                    if (!r.TryGetProperty("name", out var nameEl)) continue;
                    var name = nameEl.GetString();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!_state.TryGetValue(name, out var s)) continue;
                    if (s.LastFiredAt != DateTime.MinValue) continue;
                    if (r.TryGetProperty("lastFiredAtUtc", out var t) && t.TryGetDateTime(out var dt))
                    {
                        // The file always stores UTC. TryGetDateTime can return
                        // Unspecified for strings without a clear timezone marker;
                        // treat that as UTC (don't ToUniversalTime, which would
                        // assume Local and apply the TZ offset).
                        s.LastFiredAt = dt.Kind switch
                        {
                            DateTimeKind.Utc => dt,
                            DateTimeKind.Local => dt.ToUniversalTime(),
                            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                        };
                        applied++;
                    }
                }
            }
            FileLog.Write($"[CommQueueScheduler] Loaded persisted state ({applied} runner(s)) from {_statePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CommQueueScheduler] LoadPersistedState FAILED: {ex.Message}");
        }
    }

    /// <summary>Replace the registered-runners list. State is preserved by
    /// name: a runner that survives the swap keeps its LastFiredAt and last
    /// invocation results, a new runner gets a fresh state, and a removed
    /// runner's state is dropped. Any fire-and-forget task that was started
    /// for a removed runner runs to completion but its state-update becomes
    /// an orphan (no disk write).</summary>
    public void ReloadRunners(IReadOnlyList<RunnerRegistration> newRunners)
    {
        ArgumentNullException.ThrowIfNull(newRunners);
        int added, removed, kept;
        lock (_gate)
        {
            var oldStateByName = new Dictionary<string, RunnerState>(_state);
            var newNames = new HashSet<string>(newRunners.Select(r => r.Name));

            _runners.Clear();
            _state.Clear();
            foreach (var r in newRunners)
            {
                _runners.Add(r);
                _state[r.Name] = oldStateByName.TryGetValue(r.Name, out var prev) ? prev : new RunnerState();
            }
            added = newNames.Except(oldStateByName.Keys).Count();
            removed = oldStateByName.Keys.Except(newNames).Count();
            kept = newNames.Intersect(oldStateByName.Keys).Count();
        }
        FileLog.Write($"[CommQueueScheduler] ReloadRunners: added={added}, removed={removed}, kept={kept}");
    }

    private void SaveStateToDisk()
    {
        if (_statePath == null) return;
        try
        {
            List<(string Name, DateTime LastFiredAt)> snapshot;
            lock (_gate)
            {
                snapshot = _state
                    .Where(kvp => kvp.Value.LastFiredAt != DateTime.MinValue)
                    .Select(kvp => (kvp.Key, kvp.Value.LastFiredAt))
                    .ToList();
            }

            var entries = snapshot.Select(s => new StatePersistedEntry
            {
                Name = s.Name,
                LastFiredAtUtc = s.LastFiredAt.ToString("o"),
            }).ToList();
            var payload = new StatePersistedFile { Runners = entries };

            var json = JsonSerializer.Serialize(payload, JsonSerializerOptionsForState);
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Atomic write: temp file + rename. Avoids torn writes if the
            // process dies mid-save.
            var tmp = _statePath + ".tmp";
            lock (_stateFileLock)
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, _statePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CommQueueScheduler] SaveStateToDisk FAILED: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonSerializerOptionsForState = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class StatePersistedFile
    {
        public List<StatePersistedEntry> Runners { get; set; } = new();
    }

    private sealed class StatePersistedEntry
    {
        public string Name { get; set; } = "";
        public string LastFiredAtUtc { get; set; } = "";
    }

    public IReadOnlyList<RunnerRegistration> Runners
    {
        get { lock (_gate) return _runners.ToArray(); }
    }

    /// <summary>One tick. Invoked by SchedulerService while this process is leader.</summary>
    public void Tick(DateTime now)
    {
        if (!File.Exists(_dbPath))
        {
            // No queue DB yet -- the comm-queue tool hasn't been used on this
            // machine. Nothing to do; not an error.
            return;
        }

        RunnerRegistration[] snapshot;
        lock (_gate) snapshot = _runners.ToArray();

        foreach (var runner in snapshot)
        {
            try
            {
                EvaluateRunner(runner, now);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[CommQueueScheduler] EvaluateRunner('{runner.Name}') FAILED: {ex.Message}");
            }
        }
    }

    private void EvaluateRunner(RunnerRegistration runner, DateTime now)
    {
        RunnerState state;
        lock (_gate)
        {
            state = _state[runner.Name];
            if (state.IsFiring) return;
            if (now - state.LastFiredAt < runner.MinIntervalBetweenFires) return;
            if (!runner.Schedule.ShouldFire(state.LastFiredAt, now)) return;
            state.IsFiring = true;
        }

        int pendingCount;
        try
        {
            pendingCount = CountPendingItems(runner);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CommQueueScheduler] CountPendingItems('{runner.Name}') FAILED: {ex.Message}");
            lock (_gate) state.IsFiring = false;
            return;
        }

        if (pendingCount == 0)
        {
            lock (_gate) state.IsFiring = false;
            return;
        }

        var jitterMinutes = runner.RespectHumanCadence ? _jitter.Next(0, 60) : 0;
        var jitterDelay = TimeSpan.FromMinutes(jitterMinutes);
        FileLog.Write(
            $"[CommQueueScheduler] '{runner.Name}' has {pendingCount} pending item(s); " +
            $"firing after {jitterDelay.TotalMinutes:F0}min jitter");

        _ = Task.Run(async () =>
        {
            RunInvocationResult? result = null;
            try
            {
                if (jitterDelay > TimeSpan.Zero)
                    await Task.Delay(jitterDelay).ConfigureAwait(false);
                result = await InvokeRunnerAsync(runner).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[CommQueueScheduler] '{runner.Name}' invocation FAILED: {ex.Message}");
                result = new RunInvocationResult(-1, "", $"Invocation FAILED: {ex.Message}", DateTime.UtcNow);
            }
            finally
            {
                MarkFireCompleted(state, result);
            }
        });
    }

    /// <summary>Clear the IsFiring flag, stamp LastFiredAt, record the last
    /// invocation result for the UI, and persist state. State persistence
    /// runs outside the gate lock to avoid holding it during disk I/O.</summary>
    private void MarkFireCompleted(RunnerState state, RunInvocationResult? lastRun)
    {
        lock (_gate)
        {
            state.IsFiring = false;
            state.LastFiredAt = DateTime.UtcNow;
            if (lastRun != null)
            {
                state.LastExitCode = lastRun.ExitCode;
                state.LastStdoutTail = lastRun.StdoutTail;
                state.LastStderrTail = lastRun.StderrTail;
                state.LastFinishedAtUtc = lastRun.FinishedAtUtc;
            }
        }
        SaveStateToDisk();
    }

    private int CountPendingItems(RunnerRegistration runner)
    {
        var connStr = $"Data Source={_dbPath};Mode=ReadOnly";
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM communications WHERE {runner.QueueFilter}";
        var result = cmd.ExecuteScalar();
        return Convert.ToInt32(result ?? 0);
    }

    private const int OutputTailMaxChars = 4000;

    private static async Task<RunInvocationResult> InvokeRunnerAsync(RunnerRegistration runner)
    {
        var psi = new ProcessStartInfo
        {
            FileName = runner.Command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in runner.Args) psi.ArgumentList.Add(arg);

        FileLog.Write($"[CommQueueScheduler] Invoking '{runner.Name}': {psi.FileName} {string.Join(' ', runner.Args)}");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {runner.Command}");

        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        FileLog.Write($"[CommQueueScheduler] '{runner.Name}' exit={process.ExitCode}");
        LogProcessOutput(runner.Name, "stdout", stdout);
        LogProcessOutput(runner.Name, "stderr", stderr);

        return new RunInvocationResult(
            process.ExitCode,
            TruncateTail(stdout, OutputTailMaxChars),
            TruncateTail(stderr, OutputTailMaxChars),
            DateTime.UtcNow);
    }

    private static string TruncateTail(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
        return "[...truncated, showing last " + maxChars + " chars...]\n"
            + text.Substring(text.Length - maxChars);
    }

    private sealed record RunInvocationResult(
        int ExitCode,
        string StdoutTail,
        string StderrTail,
        DateTime FinishedAtUtc);

    private static void LogProcessOutput(string runnerName, string stream, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            FileLog.Write($"[CommQueueScheduler] '{runnerName}' {stream}: {trimmed}");
        }
    }

    /// <summary>UI snapshot of one runner. Pending count is a live DB read,
    /// hence may carry an error string instead of a number. Last-run fields
    /// are null until the runner has completed at least one invocation since
    /// the Director started.</summary>
    public sealed record RunnerSnapshot(
        string Name,
        string Command,
        IReadOnlyList<string> Args,
        string ScheduleDescription,
        bool RespectHumanCadence,
        TimeSpan MinIntervalBetweenFires,
        DateTime LastFiredAtUtc,
        bool IsFiring,
        int PendingItemCount,
        string? PendingCountError,
        int? LastExitCode,
        string? LastStdoutTail,
        string? LastStderrTail,
        DateTime? LastFinishedAtUtc);

    public sealed record RunNowResult(bool Started, string Message);

    /// <summary>Snapshot of every registered runner. Issues one DB read per
    /// runner to populate the pending count; safe to call on a polling timer.</summary>
    public IReadOnlyList<RunnerSnapshot> GetSnapshot()
    {
        List<(RunnerRegistration runner, RunnerState state)> entries;
        lock (_gate)
        {
            entries = new List<(RunnerRegistration, RunnerState)>(_runners.Count);
            foreach (var r in _runners) entries.Add((r, _state[r.Name]));
        }

        var dbExists = File.Exists(_dbPath);
        var result = new List<RunnerSnapshot>(entries.Count);
        foreach (var (runner, state) in entries)
        {
            int count = 0;
            string? err = null;
            if (dbExists)
            {
                try { count = CountPendingItems(runner); }
                catch (Exception ex) { err = ex.Message; }
            }
            else
            {
                err = "comm-queue DB not found";
            }

            // Snapshot last-run fields under the lock so we don't tear values
            // that are being concurrently updated by MarkFireCompleted.
            DateTime lastFired;
            bool isFiring;
            int? lastExit;
            string? lastStdout;
            string? lastStderr;
            DateTime? lastFinished;
            lock (_gate)
            {
                lastFired = state.LastFiredAt;
                isFiring = state.IsFiring;
                lastExit = state.LastExitCode;
                lastStdout = state.LastStdoutTail;
                lastStderr = state.LastStderrTail;
                lastFinished = state.LastFinishedAtUtc;
            }

            result.Add(new RunnerSnapshot(
                runner.Name,
                runner.Command,
                runner.Args,
                runner.Schedule.Describe(),
                runner.RespectHumanCadence,
                runner.MinIntervalBetweenFires,
                lastFired,
                isFiring,
                count,
                err,
                lastExit,
                lastStdout,
                lastStderr,
                lastFinished));
        }
        return result;
    }

    /// <summary>Manual trigger from the UI. Bypasses schedule and cooldown but
    /// respects the per-runner IsFiring guard so the same runner cannot be
    /// invoked twice concurrently in this process. Fire-and-forget: returns
    /// immediately after the background task is scheduled.</summary>
    public RunNowResult RunNow(string runnerName)
    {
        RunnerRegistration runner;
        RunnerState state;
        lock (_gate)
        {
            var idx = _runners.FindIndex(r => r.Name == runnerName);
            if (idx < 0) return new RunNowResult(false, $"Runner '{runnerName}' is not registered");
            runner = _runners[idx];
            state = _state[runner.Name];
            if (state.IsFiring) return new RunNowResult(false, "Runner is already running");
            state.IsFiring = true;
        }

        FileLog.Write($"[CommQueueScheduler] RunNow '{runnerName}' (manual trigger)");

        _ = Task.Run(async () =>
        {
            RunInvocationResult? result = null;
            try { result = await InvokeRunnerAsync(runner).ConfigureAwait(false); }
            catch (Exception ex)
            {
                FileLog.Write($"[CommQueueScheduler] RunNow '{runnerName}' FAILED: {ex.Message}");
                result = new RunInvocationResult(-1, "", $"Invocation FAILED: {ex.Message}", DateTime.UtcNow);
            }
            finally
            {
                MarkFireCompleted(state, result);
            }
        });

        return new RunNowResult(true, "Started");
    }

    private sealed class RunnerState
    {
        public DateTime LastFiredAt = DateTime.MinValue;
        public bool IsFiring;
        public int? LastExitCode;
        public string? LastStdoutTail;
        public string? LastStderrTail;
        public DateTime? LastFinishedAtUtc;
    }
}
