using CcDirector.Core.Machine;
using CcDirector.Core.Machine.ResourceSummaries;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The application's part of the Resource Monitor (issue #2811): one sampler for the life of the Director,
/// feeding the toolbar meter, the 24-hour recording and the five-minute Gateway summary.
/// </summary>
public partial class App
{
    /// <summary>
    /// The resource monitor, started with the Director's services. Null only when it failed to start - the
    /// failure is logged with its reason, and the meter then shows dashes rather than figures.
    /// </summary>
    public ResourceMonitorHost? ResourceMonitor { get; private set; }

    /// <summary>
    /// Creates and starts the resource monitor. Called after the control services are constructed, because the
    /// five-minute summary goes out through their outbox.
    /// </summary>
    private void StartResourceMonitor(Action<string> log)
    {
        if (ControlApiHost is null)
        {
            log("Resource monitor NOT started: the Director's control services did not start, so the summary has no outbox");
            return;
        }

        try
        {
            var time = TimeProvider.System;
            var sampler = new ResourceSampler(ResourceProbes.ForThisMachine(), ResourcePressure.Evaluate, time);
            ResourceMonitor = new ResourceMonitorHost(
                sampler,
                DirectorSessions,
                // The machine name the Gateway's Hello carries, so a summary row lands on the right machine.
                new ResourceSummaryRecorder(ControlApiHost.ResourceSummaryOutbox, Environment.MachineName),
                new ResourceRecorder(CcStorage.ResourceRecording(), time),
                time);
            sampler.Start();
            log("Resource monitor started: sampling every 5 seconds");
        }
        catch (Exception ex)
        {
            log($"Resource monitor FAILED to start: {ex}");
            ResourceMonitor = null;
        }
    }

    /// <summary>
    /// This Director's running sessions, as the resource monitor needs them. Read on the sampler's thread every five
    /// seconds; <see cref="CcDirector.Core.Sessions.SessionManager.ListSessions"/> returns a copy.
    /// </summary>
    private IReadOnlyList<DirectorSessionInfo> DirectorSessions()
    {
        var sessions = SessionManager.ListSessions();
        var result = new List<DirectorSessionInfo>(sessions.Count);
        foreach (var session in sessions)
        {
            // Zero when the session has no process of its own (not started, or a remote backend); the host leaves
            // such a session out, because it has no process tree on this machine to measure.
            var pid = session.ProcessId;
            var name = session.CustomName ?? session.MissionName ?? session.Id.ToString();
            var label = session.Number is { } number ? $"{number} ({name})" : name;
            result.Add(new DirectorSessionInfo(session.Id.ToString(), pid, label, session.Factory));
        }
        return result;
    }

    /// <summary>
    /// Stops the resource monitor: the sampler first (waiting for a read in progress), then the recording. Called
    /// first thing at shutdown, so nothing is written to the recording while it closes.
    /// </summary>
    private void StopResourceMonitor(Action<string> log)
    {
        var monitor = ResourceMonitor;
        if (monitor is null) return;
        try
        {
            monitor.Dispose();
            log("Resource monitor stopped");
        }
        catch (Exception ex)
        {
            log($"Resource monitor stop FAILED: {ex.Message}");
        }
    }
}
