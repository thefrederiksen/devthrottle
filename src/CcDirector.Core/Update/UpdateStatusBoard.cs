using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Update;

/// <summary>
/// The one place anything asks "what is the update situation on this machine?" (issue #1030).
///
/// There is exactly one updater per Director process, so there is exactly one answer, and this holds
/// it: the running version, whether this build updates itself at all, whatever check is in flight
/// right now, and how to start a check or install a staged build. Callers get a finished
/// <see cref="UpdateStatusView"/> from <see cref="UpdateStatusFold"/> and render it verbatim.
///
/// It exists so the desktop window and the Control API cannot answer the question differently. Before
/// it, the phase events went straight from the update service to one window, which meant the answer
/// lived only in that window's memory: nothing else could be told, and the window itself forgot
/// everything the moment the check ended. That is a large part of why "up to date" and "has not
/// checked yet" were the same picture.
///
/// Registration happens once at startup, whether or not the build has an updater - a development
/// build that will never update still has to be able to SAY that, rather than looking like a machine
/// that is up to date.
/// </summary>
public static class UpdateStatusBoard
{
    private static readonly object Gate = new();

    private static UpdateService? _service;
    private static string _currentVersion = "0.0.0";
    private static bool _enabled;
    private static Func<int>? _runningSessionCount;
    private static UpdateProgress? _live;

    /// <summary>True once startup has registered the updater and the board can answer.</summary>
    public static bool Ready
    {
        get { lock (Gate) return _service is not null; }
    }

    /// <summary>
    /// Register the process's updater. <paramref name="runningSessionCount"/> is asked at the moment a
    /// status is folded, never cached, because whether an install can be offered turns on it.
    /// </summary>
    public static void Register(UpdateService service, string currentVersion, bool automaticUpdatesEnabled,
        Func<int> runningSessionCount)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(runningSessionCount);
        lock (Gate)
        {
            _service = service;
            _currentVersion = string.IsNullOrWhiteSpace(currentVersion) ? "0.0.0" : currentVersion;
            _enabled = automaticUpdatesEnabled;
            _runningSessionCount = runningSessionCount;
        }
        FileLog.Write($"[UpdateStatusBoard] registered: version={currentVersion}, automaticUpdates={automaticUpdatesEnabled}");
    }

    /// <summary>
    /// Record a phase event from the update service. Terminal phases clear the in-flight progress: once a
    /// check has concluded, the conclusion belongs to the persisted state, and leaving a stale "checking"
    /// in memory would outrank it forever.
    /// </summary>
    public static void ReportProgress(UpdateProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var inFlight = progress.Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Verifying;
        lock (Gate) _live = inFlight ? progress : null;
    }

    /// <summary>
    /// The current status, folded and finished, or null when startup has not registered an updater yet.
    /// Null rather than an invented answer: a caller that gets nothing can say nothing, whereas a caller
    /// handed a made-up "up to date" would repeat it.
    /// </summary>
    public static UpdateStatusView? Current(DateTimeOffset? now = null)
    {
        string version;
        bool enabled;
        Func<int>? sessions;
        UpdateProgress? live;
        lock (Gate)
        {
            if (_service is null) return null;
            version = _currentVersion;
            enabled = _enabled;
            sessions = _runningSessionCount;
            live = _live;
        }

        var facts = new UpdateStatusFacts(
            CurrentVersion: version,
            AutomaticUpdatesEnabled: enabled,
            State: UpdaterState.Load(),
            Live: live,
            RunningSessionCount: SafeSessionCount(sessions),
            LauncherRunning: LauncherDiscovery.IsRunning(LauncherDiscovery.Read()),
            Now: now ?? DateTimeOffset.UtcNow);

        return UpdateStatusFold.Fold(facts);
    }

    /// <summary>
    /// Run a check now, on demand. Returns what it concluded, or null when no updater is registered.
    /// The status the caller reads afterwards reflects it, because the check writes its conclusion down.
    /// </summary>
    public static async Task<UpdatePhase?> CheckNowAsync(CancellationToken ct = default)
    {
        UpdateService? service;
        lock (Gate) service = _service;
        if (service is null)
        {
            FileLog.Write("[UpdateStatusBoard] CheckNowAsync: no updater is registered yet");
            return null;
        }

        FileLog.Write("[UpdateStatusBoard] CheckNowAsync: on-demand check requested");
        return await service.CheckAndStageAsync(ct);
    }

    /// <summary>
    /// A session count that cannot take the status down. If the count cannot be read, report one session
    /// rather than zero: zero would offer an install that could end live work, and this is the one number
    /// in the fold where being wrong in one direction is much worse than the other.
    /// </summary>
    private static int SafeSessionCount(Func<int>? counter)
    {
        if (counter is null) return 1;
        try
        {
            return counter();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateStatusBoard] could not read the session count FAILED: {ex.Message}. Treating this Director "
                          + "as busy, so nothing offers to restart it.");
            return 1;
        }
    }
}
