using System.Collections.Concurrent;
using System.ComponentModel;
using CcDirector.Core.Utilities;
using CcDirector.Engine.Jobs;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Triggers;

/// <summary>Runs one trigger check and says what the process produced. Production is
/// <see cref="DirectorTriggerRunner.RunCheckAsync"/>; tests pass a fake.</summary>
public delegate Task<TriggerCheckReport> TriggerCheckRunner(TriggerAssignmentDto trigger, CancellationToken ct);

/// <summary>
/// THE DIRECTOR RUNS THE CHECK (the Website Business Factory mission, product track). The check is a local command -
/// a business tool on this machine - so the hosted Gateway cannot run it. This Director asks the Gateway which
/// triggers it runs (<see cref="PollInterval"/>), runs each one's check when its interval is up, with a timeout, and
/// reports what the process produced. It decides NOTHING from the result: the Gateway reads it under the check
/// contract and decides whether a session starts.
///
/// HOW IT LEARNS ITS TRIGGERS: an HTTP pull, the same shape as the skill store refresh beside it
/// (<c>GET /directors/{id}/triggers</c> with the Director's own key), not the hub. A pull is enough - a new trigger
/// only has to be noticed within one poll, and a trigger has an interval of a minute or more - and it keeps the
/// switch where it belongs: a Gateway with factory agents off does not map the route, so the Director is handed
/// nothing and runs nothing.
///
/// A check is never run twice at once: a trigger whose previous check is still running is not started again until
/// it has finished, and its next check is due one interval after that.
/// </summary>
public sealed class DirectorTriggerRunner
{
    /// <summary>How often the Director asks the Gateway for its triggers.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>How much of a check's output travels to the Gateway. The contract needs a small JSON object; the
    /// rest is only ever quoted in a failure reason.</summary>
    public const int OutputCap = 8192;

    private readonly string _directorId;
    private readonly Func<ITriggerGateway?> _gateway;
    private readonly TriggerCheckRunner _runCheck;
    private readonly Func<DateTime> _nowUtc;
    private readonly ConcurrentDictionary<string, DateTime> _nextDueUtc = new(StringComparer.OrdinalIgnoreCase);
    // The triggers whose check is running now. Marked BEFORE the check starts and cleared when it ends, so a check
    // that finishes at once cannot clear the mark before it is set and leave its trigger marked forever.
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.OrdinalIgnoreCase);
    private TriggerFetchKind? _lastFetchKind;

    /// <param name="gateway">Read on every poll, so a settings change that replaces the Gateway client is picked up.
    /// Null means no Gateway: nothing runs.</param>
    public DirectorTriggerRunner(string directorId, Func<ITriggerGateway?> gateway, TriggerCheckRunner runCheck,
        Func<DateTime> nowUtc)
    {
        if (string.IsNullOrWhiteSpace(directorId)) throw new ArgumentException("a Director id is required", nameof(directorId));
        _directorId = directorId;
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _runCheck = runCheck ?? throw new ArgumentNullException(nameof(runCheck));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    /// <summary>Poll on <see cref="PollInterval"/> until cancelled. The first poll is immediate.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        FileLog.Write($"[DirectorTriggerRunner] RunAsync: director={_directorId}, polling every {PollInterval.TotalSeconds:0}s");
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                try
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The loop is an entry point: one bad poll is logged and the next one runs.
                    FileLog.Write($"[DirectorTriggerRunner] poll FAILED: {ex.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[DirectorTriggerRunner] RunAsync: stopped");
        }
    }

    /// <summary>
    /// One poll: ask the Gateway for this Director's triggers and start every check that is due and not already
    /// running. Returns the checks it started, so a test can await them; the loop does not wait for them.
    /// </summary>
    public async Task<IReadOnlyList<Task>> TickAsync(CancellationToken ct)
    {
        var gateway = _gateway();
        if (gateway is null) return Array.Empty<Task>();

        var fetch = await gateway.FetchTriggersAsync(_directorId, ct).ConfigureAwait(false);
        if (fetch.Kind != _lastFetchKind)
        {
            FileLog.Write($"[DirectorTriggerRunner] fetch: {fetch.Kind}, triggers={fetch.Triggers.Count}{(fetch.Detail is null ? "" : $", {fetch.Detail}")}");
            _lastFetchKind = fetch.Kind;
        }
        if (fetch.Kind != TriggerFetchKind.Assigned)
        {
            if (fetch.Kind == TriggerFetchKind.Off) _nextDueUtc.Clear();
            return Array.Empty<Task>();
        }

        // Forget the schedule of a trigger this Director no longer runs, so it is due at once if it comes back.
        var assigned = new HashSet<string>(fetch.Triggers.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var id in _nextDueUtc.Keys.Where(id => !assigned.Contains(id)).ToList())
            _nextDueUtc.TryRemove(id, out _);

        var started = new List<Task>();
        var now = _nowUtc();
        foreach (var trigger in fetch.Triggers)
        {
            if (_nextDueUtc.TryGetValue(trigger.Id, out var due) && now < due) continue;
            if (!_running.TryAdd(trigger.Id, 0)) continue;

            started.Add(CheckAndReportAsync(gateway, trigger, ct));
        }
        return started;
    }

    private async Task CheckAndReportAsync(ITriggerGateway gateway, TriggerAssignmentDto trigger, CancellationToken ct)
    {
        try
        {
            FileLog.Write($"[DirectorTriggerRunner] check: trigger={trigger.Name}, command={trigger.CheckCommand}");
            var report = await _runCheck(trigger, ct).ConfigureAwait(false);
            var refused = await gateway.ReportTriggerCheckAsync(_directorId, trigger.Id, report, ct).ConfigureAwait(false);
            FileLog.Write(refused is null
                ? $"[DirectorTriggerRunner] reported: trigger={trigger.Name}, exit={report.ExitCode?.ToString() ?? "none"}, timedOut={report.TimedOut}"
                : $"[DirectorTriggerRunner] report NOT recorded: trigger={trigger.Name}, {refused}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[DirectorTriggerRunner] check FAILED: trigger={trigger.Name}, {ex.Message}");
        }
        finally
        {
            _nextDueUtc[trigger.Id] = _nowUtc() + TimeSpan.FromSeconds(trigger.IntervalSeconds);
            _running.TryRemove(trigger.Id, out _);
        }
    }

    /// <summary>
    /// Run one check the way the Engine runs a job (<see cref="ProcessJob"/>: the shell, the timeout, the exit code,
    /// the output), in the trigger's repository folder. A folder that does not exist, or a shell that will not start,
    /// is reported as a check that could not start - never skipped - so it turns the trigger red on the Gateway.
    /// </summary>
    public static async Task<TriggerCheckReport> RunCheckAsync(TriggerAssignmentDto trigger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (!Directory.Exists(trigger.RepoPath))
            return new TriggerCheckReport
            {
                CheckedAtUtc = DateTime.UtcNow,
                StartError = $"the folder '{trigger.RepoPath}' does not exist on this machine",
            };

        try
        {
            var job = new ProcessJob($"trigger:{trigger.Name}", trigger.CheckCommand, trigger.RepoPath, trigger.TimeoutSeconds);
            var result = await job.ExecuteAsync(ct).ConfigureAwait(false);
            return new TriggerCheckReport
            {
                CheckedAtUtc = DateTime.UtcNow,
                ExitCode = result.TimedOut ? null : result.ExitCode,
                TimedOut = result.TimedOut,
                Output = Cap(result.Output),
                ErrorOutput = Cap(result.ErrorOutput),
            };
        }
        catch (Win32Exception ex)
        {
            return new TriggerCheckReport { CheckedAtUtc = DateTime.UtcNow, StartError = ex.Message };
        }
    }

    private static string Cap(string? text)
    {
        var value = text ?? "";
        return value.Length <= OutputCap ? value : value[..OutputCap];
    }
}
