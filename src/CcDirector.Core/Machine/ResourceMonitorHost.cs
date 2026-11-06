using CcDirector.Core.Machine.ResourceSummaries;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>One of this Director's sessions, as the resource monitor needs to know it.</summary>
/// <param name="SessionId">The session's id.</param>
/// <param name="RootPid">The process the session started - the agent itself.</param>
/// <param name="Label">How the owner recognises it, for example "140 (Resource Monitor - Dev - Linux probe)".</param>
/// <param name="Factory">The session's Factory value, or null when it belongs to none.</param>
public sealed record DirectorSessionInfo(string SessionId, int RootPid, string Label, string? Factory);

/// <summary>
/// One read, with what this Director's sessions cost at it.
/// </summary>
/// <param name="State">The sampler's state after the read.</param>
/// <param name="Sessions">
/// Each session's process-tree usage. NULL WHEN NOT KNOWN - the process list could not be read - which is
/// never the same as an empty list (a Director running no sessions).
/// </param>
public sealed record ResourceMonitorUpdate(ResourceSamplerState State, IReadOnlyList<ResourceSessionInput>? Sessions);

/// <summary>
/// Everything the Director does with each five-second read, in one place and in one order (the Resource
/// Monitor mission, issue #2811): work out what each session's process tree costs, write the read to the
/// 24-hour recording (#4645), add it to the five-minute Gateway summary and close a window when it is due
/// (#4639), then tell the screen.
///
/// ONE PLACE, SO THE SESSION FIGURES ARE WORKED OUT ONCE. The recording, the summary and the meter all need
/// them for the same read; three subscribers working them out separately would cost three times and depend
/// on which subscribed first.
///
/// IT OWNS THE SAMPLER AND THE RECORDING, so it can stop them in the only safe order:
/// <see cref="Dispose"/> stops the sampler first - which waits for a read in progress - and only then
/// disposes the recording. Recording and disposing are not safe on two threads, and the Director shuts down
/// on a different thread from the sampler's timer.
/// </summary>
public sealed class ResourceMonitorHost : IDisposable
{
    private readonly IResourceSampler _sampler;
    private readonly Func<IReadOnlyList<DirectorSessionInfo>> _sessions;
    private readonly ResourceSummaryRecorder _summary;
    private readonly ResourceRecorder _recording;
    private readonly TimeProvider _time;
    private SessionProcessMembership _membership = SessionProcessMembership.Empty;
    private volatile ResourceMonitorUpdate? _latest;
    private bool _disposed;

    /// <summary>Subscribes to the sampler. Call <see cref="IResourceSampler.Start"/> afterwards.</summary>
    /// <param name="sampler">The sampler. Owned from here: disposed by <see cref="Dispose"/>.</param>
    /// <param name="sessions">This Director's running sessions, read once per read, on the sampler's thread.</param>
    /// <param name="summary">The five-minute summary recorder, over the Director's outbox, named with <c>Environment.MachineName</c>.</param>
    /// <param name="recording">The 24-hour recording. Owned from here: disposed by <see cref="Dispose"/> after the sampler stops.</param>
    /// <param name="time">The clock the summary's windows are closed against.</param>
    public ResourceMonitorHost(
        IResourceSampler sampler,
        Func<IReadOnlyList<DirectorSessionInfo>> sessions,
        ResourceSummaryRecorder summary,
        ResourceRecorder recording,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(time);
        _sampler = sampler;
        _sessions = sessions;
        _summary = summary;
        _recording = recording;
        _time = time;
        _sampler.Sampled += OnSampled;
        FileLog.Write("[ResourceMonitorHost] subscribed to the sampler");
    }

    /// <summary>The sampler this host drives - for the meter and the banner to subscribe to.</summary>
    public IResourceSampler Sampler => _sampler;

    /// <summary>The latest read with its session figures. Null before the first read.</summary>
    public ResourceMonitorUpdate? Latest => _latest;

    /// <summary>
    /// Raised after every read, on the sampler's thread, once the recording and the summary have it. A
    /// handler that touches the screen dispatches to the user-interface thread itself.
    /// </summary>
    public event EventHandler<ResourceMonitorUpdate>? Updated;

    private void OnSampled(object? sender, ResourceSamplerState state)
    {
        if (state.Latest is not { } sample) return;

        var sessions = SessionsAt(sample, _sessions());
        var update = new ResourceMonitorUpdate(state, sessions);
        _latest = update;

        var summarySample = ResourceSummarySampleFold.From(state, sessions ?? Array.Empty<ResourceSessionInput>());
        if (summarySample is not null) _summary.Add(summarySample);
        if (_summary.CloseIfDue(_time.GetUtcNow().UtcDateTime))
            FileLog.Write("[ResourceMonitorHost] OnSampled: a five-minute window closed and went to the outbox");

        var recorded = sessions is null
            ? Array.Empty<RecordedSession>()
            : sessions.Select(s => new RecordedSession(s.Usage, s.Label)).ToArray();
        _recording.Record(new ResourceMoment(sample, recorded, state.Verdict, Diagnosis: null));

        Updated?.Invoke(this, update);
    }

    // What each session's process tree costs at this read. Null when the process list could not be read: then
    // nothing is learned and nothing is forgotten, and nobody is told the sessions cost nothing.
    private IReadOnlyList<ResourceSessionInput>? SessionsAt(ResourceSample sample, IReadOnlyList<DirectorSessionInfo> sessions)
    {
        var started = sessions.Where(s => s.RootPid > 0).ToList();
        var roots = started.ToDictionary(s => s.SessionId, s => s.RootPid, StringComparer.Ordinal);
        _membership = SessionProcessMembership.Update(_membership, sample.Processes, roots);
        if (!sample.Processes.CouldRead) return null;

        var byId = started.ToDictionary(s => s.SessionId, StringComparer.Ordinal);
        return SessionUsageRollup.Sum(sample.Processes, _membership.Members(sample.Processes), roots)
            .Select(usage => new ResourceSessionInput(usage, byId[usage.SessionId].Label, byId[usage.SessionId].Factory))
            .ToList();
    }

    /// <summary>Stops the sampler (waiting for a read in progress), then closes the recording.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FileLog.Write("[ResourceMonitorHost] Dispose: stopping the sampler, then the recording");
        _sampler.Sampled -= OnSampled;
        _sampler.Dispose();
        _recording.Dispose();
        FileLog.Write("[ResourceMonitorHost] Dispose: stopped");
    }
}
