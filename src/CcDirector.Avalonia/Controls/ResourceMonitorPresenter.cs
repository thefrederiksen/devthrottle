using Avalonia.Threading;
using CcDirector.Core.Machine;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Joins the resource sampler to the toolbar meter and the warning banners (the Resource Monitor mission,
/// issue #2811). It decides nothing about what a reading means: the folds in Core do that. It moves the
/// sampler's events onto the user-interface thread (CLAUDE.md rule 6), keeps the banner board, and turns the
/// two clicks into the detail screen opening.
///
/// The folds run on the sampler's own thread, so the user-interface thread only assigns finished views.
/// </summary>
public sealed class ResourceMonitorPresenter : IDisposable
{
    private readonly IResourceSampler _sampler;
    private readonly ResourceMeter _meter;
    private readonly ResourceBannerStrip _banners;
    private readonly string _machineName;
    private readonly Func<int> _runningSessions;
    private readonly Func<IReadOnlyList<ResourceSessionInput>?> _sessions;
    private readonly Action _openDetail;
    private readonly ResourceBannerBoard _board = new();

    /// <summary>Wires the sampler to the two controls. Call on the user-interface thread.</summary>
    /// <param name="sampler">The Director's sampler.</param>
    /// <param name="meter">The toolbar meter.</param>
    /// <param name="banners">The banner strip under the toolbar.</param>
    /// <param name="machineName">The machine's name, for the tooltip and the banner headline.</param>
    /// <param name="runningSessions">How many sessions this Director runs, read at each sample.</param>
    /// <param name="sessions">
    /// This Director's sessions with their process-tree usage, or null while that is not known (the per-session
    /// rollup is phase 1's). Null is never treated as "no sessions": nothing is claimed about ownership.
    /// </param>
    /// <param name="openDetail">Opens the detail screen (phase 2b). Must show its window at once, even empty.</param>
    public ResourceMonitorPresenter(
        IResourceSampler sampler,
        ResourceMeter meter,
        ResourceBannerStrip banners,
        string machineName,
        Func<int> runningSessions,
        Func<IReadOnlyList<ResourceSessionInput>?> sessions,
        Action openDetail)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(banners);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        ArgumentNullException.ThrowIfNull(runningSessions);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(openDetail);

        _sampler = sampler;
        _meter = meter;
        _banners = banners;
        _machineName = machineName;
        _runningSessions = runningSessions;
        _sessions = sessions;
        _openDetail = openDetail;

        _sampler.Sampled += OnSampled;
        _sampler.Crossed += OnCrossed;
        _meter.Click += OnMeterClick;
        _banners.ShowResourcesRequested += OnShowResources;
        _banners.DismissRequested += OnDismiss;

        _meter.Render(BuildMeter(_sampler.Current));
        FileLog.Write($"[ResourceMonitorPresenter] wired: machine={machineName}");
    }

    private ResourceMeterView BuildMeter(ResourceSamplerState state) =>
        ResourceMeterFold.Build(state, _machineName, _runningSessions(), Combined(_sessions()), _sampler.LatestAge);

    // What this Director's sessions cost together. Null when there are none to add up, so the tooltip leaves the
    // line out rather than claiming the sessions cost nothing.
    private static SessionUsage? Combined(IReadOnlyList<ResourceSessionInput>? sessions)
    {
        if (sessions is null || sessions.Count == 0) return null;
        return new SessionUsage(
            "all",
            0,
            sessions.Sum(s => s.Usage.CpuPercent),
            (ulong)sessions.Sum(s => (decimal)s.Usage.MemoryBytes),
            sessions.SelectMany(s => s.Usage.Pids).ToList(),
            sessions.Sum(s => s.Usage.ProcessesWithUnknownProcessor),
            sessions.Sum(s => s.Usage.ProcessesWithUnknownMemory));
    }

    private void OnSampled(object? sender, ResourceSamplerState state)
    {
        var view = BuildMeter(state);
        Dispatcher.UIThread.Post(() => _meter.Render(view));
    }

    private void OnCrossed(object? sender, ResourceCrossingEventArgs e)
    {
        var words = e.Crossing.ShowsBanner
            ? ResourceBannerFold.Build(e.Crossing, e.State.Latest, _machineName, _sessions())
            : null;
        Dispatcher.UIThread.Post(() =>
        {
            if (_board.Apply(e.Crossing, words)) _banners.Render(_board.Visible);
        });
    }

    private void OnMeterClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[ResourceMonitorPresenter] meter clicked: opening the detail screen");
            _openDetail();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ResourceMonitorPresenter] meter click FAILED: {ex}");
        }
    }

    private void OnShowResources(object? sender, ResourceKind kind)
    {
        try
        {
            FileLog.Write($"[ResourceMonitorPresenter] Show resources from the {kind} banner: opening the detail screen");
            _openDetail();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ResourceMonitorPresenter] Show resources FAILED: {ex}");
        }
    }

    private void OnDismiss(object? sender, ResourceKind kind)
    {
        if (_board.Dismiss(kind)) _banners.Render(_board.Visible);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sampler.Sampled -= OnSampled;
        _sampler.Crossed -= OnCrossed;
        _meter.Click -= OnMeterClick;
        _banners.ShowResourcesRequested -= OnShowResources;
        _banners.DismissRequested -= OnDismiss;
        FileLog.Write("[ResourceMonitorPresenter] disposed");
    }
}
