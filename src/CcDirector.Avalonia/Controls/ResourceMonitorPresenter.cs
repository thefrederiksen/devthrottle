using Avalonia.Threading;
using CcDirector.Core.Machine;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Joins the resource sampler to the toolbar meter (the Resource Monitor mission, issue #2811). It decides
/// nothing about what a reading means: the folds in Core do that. It moves the sampler's events onto the
/// user-interface thread (CLAUDE.md rule 6) and turns a click on the meter into the detail screen opening.
///
/// THERE IS NO WARNING BANNER ANY MORE (the owner's ruling of 2026-11-07). A resource in trouble is shown by
/// its own bar going amber or red and by the meter's tooltip, and by nothing else. Nothing in the Director
/// may push a full-width strip into the window: that was the whole of the complaint.
///
/// The folds run on the sampler's own thread, so the user-interface thread only assigns finished views.
/// </summary>
public sealed class ResourceMonitorPresenter : IDisposable
{
    private readonly ResourceMonitorHost _host;
    private readonly IResourceSampler _sampler;
    private readonly ResourceMeter _meter;
    private readonly string _machineName;
    private readonly Func<int> _runningSessions;
    private readonly Action _openDetail;

    /// <summary>Wires the sampler to the two controls. Call on the user-interface thread.</summary>
    /// <param name="host">The Director's resource monitor: its sampler, and each read with its session figures.</param>
    /// <param name="meter">The toolbar meter.</param>
    /// <param name="machineName">The machine's name, for the tooltip.</param>
    /// <param name="runningSessions">How many sessions this Director runs, read once at attach on the user-interface thread.</param>
    /// <param name="openDetail">Opens the detail screen (phase 2b). Must show its window at once, even empty.</param>
    public ResourceMonitorPresenter(
        ResourceMonitorHost host,
        ResourceMeter meter,
        string machineName,
        Func<int> runningSessions,
        Action openDetail)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        ArgumentNullException.ThrowIfNull(runningSessions);
        ArgumentNullException.ThrowIfNull(openDetail);

        _host = host;
        _sampler = host.Sampler;
        _meter = meter;
        _machineName = machineName;
        _runningSessions = runningSessions;
        _openDetail = openDetail;

        _host.Updated += OnUpdated;
        _meter.Click += OnMeterClick;

        var latest = _host.Latest;
        var current = _sampler.Current;
        // On the user-interface thread here, so the window's own count is safe to read; every later render takes
        // the count the host made at the same read, off the sampler's thread.
        _meter.Render(BuildMeter(current, latest?.Sessions, latest?.RunningSessions ?? _runningSessions()));
        FileLog.Write($"[ResourceMonitorPresenter] wired: machine={machineName}");
    }

    private ResourceMeterView BuildMeter(ResourceSamplerState state, IReadOnlyList<ResourceSessionInput>? sessions, int runningSessions) =>
        ResourceMeterFold.Build(state, _machineName, runningSessions, sessions, _sampler.LatestAge);

    private void OnUpdated(object? sender, ResourceMonitorUpdate update)
    {
        var view = BuildMeter(update.State, update.Sessions, update.RunningSessions);
        Dispatcher.UIThread.Post(() => _meter.Render(view));
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



    /// <inheritdoc />
    public void Dispose()
    {
        _host.Updated -= OnUpdated;
        _meter.Click -= OnMeterClick;
        FileLog.Write("[ResourceMonitorPresenter] disposed");
    }
}
