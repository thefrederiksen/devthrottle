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
    private readonly ResourceMonitorHost _host;
    private readonly IResourceSampler _sampler;
    private readonly ResourceMeter _meter;
    private readonly ResourceBannerStrip _banners;
    private readonly string _machineName;
    private readonly Func<int> _runningSessions;
    private readonly Action _openDetail;
    private readonly ResourceBannerBoard _board = new();

    /// <summary>Wires the sampler to the two controls. Call on the user-interface thread.</summary>
    /// <param name="host">The Director's resource monitor: its sampler, and each read with its session figures.</param>
    /// <param name="meter">The toolbar meter.</param>
    /// <param name="banners">The banner strip under the toolbar.</param>
    /// <param name="machineName">The machine's name, for the tooltip and the banner headline.</param>
    /// <param name="runningSessions">How many sessions this Director runs, read at each sample.</param>
    /// <param name="openDetail">Opens the detail screen (phase 2b). Must show its window at once, even empty.</param>
    public ResourceMonitorPresenter(
        ResourceMonitorHost host,
        ResourceMeter meter,
        ResourceBannerStrip banners,
        string machineName,
        Func<int> runningSessions,
        Action openDetail)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(banners);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        ArgumentNullException.ThrowIfNull(runningSessions);
        ArgumentNullException.ThrowIfNull(openDetail);

        _host = host;
        _sampler = host.Sampler;
        _meter = meter;
        _banners = banners;
        _machineName = machineName;
        _runningSessions = runningSessions;
        _openDetail = openDetail;

        _host.Updated += OnUpdated;
        _sampler.Crossed += OnCrossed;
        _meter.Click += OnMeterClick;
        _banners.ShowResourcesRequested += OnShowResources;
        _banners.DismissRequested += OnDismiss;

        _meter.Render(BuildMeter(_sampler.Current, null));
        FileLog.Write($"[ResourceMonitorPresenter] wired: machine={machineName}");
    }

    private ResourceMeterView BuildMeter(ResourceSamplerState state, IReadOnlyList<ResourceSessionInput>? sessions) =>
        ResourceMeterFold.Build(state, _machineName, _runningSessions(), Combined(sessions), _sampler.LatestAge);

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

    private void OnUpdated(object? sender, ResourceMonitorUpdate update)
    {
        var view = BuildMeter(update.State, update.Sessions);
        Dispatcher.UIThread.Post(() => _meter.Render(view));
    }

    private void OnCrossed(object? sender, ResourceCrossingEventArgs e)
    {
        var words = e.Crossing.ShowsBanner
            // Crossed follows the host's handling of the same read, so Latest carries this read's session figures.
            ? ResourceBannerFold.Build(e.Crossing, e.State.Latest, _machineName, _host.Latest?.Sessions)
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
        _host.Updated -= OnUpdated;
        _sampler.Crossed -= OnCrossed;
        _meter.Click -= OnMeterClick;
        _banners.ShowResourcesRequested -= OnShowResources;
        _banners.DismissRequested -= OnDismiss;
        FileLog.Write("[ResourceMonitorPresenter] disposed");
    }
}
