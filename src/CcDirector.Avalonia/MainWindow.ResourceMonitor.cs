using CcDirector.Avalonia.Controls;
using CcDirector.Core.Machine;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The window's part of the Resource Monitor (issue #2811): attaching the sampler to the toolbar meter and the
/// and keeping the meter from pushing Cockpit and Settings off a narrow toolbar (issue #2812).
/// </summary>
public partial class MainWindow
{
    // Room left between the two toolbar groups before the meter gives up its bars. Small, so the meter keeps its
    // bars for as long as they genuinely fit.
    private const double ToolbarSlack = 16;

    private ResourceMonitorPresenter? _resourceMonitor;
    private double _resourceMeterFullWidth;
    private bool _resourceMeterFitWired;

    /// <summary>The last fit decision's figures, for tests and the log: what the toolbar needed and had.</summary>
    internal (double Needed, double Available) LastResourceMeterFit { get; private set; }

    /// <summary>
    /// Attaches the resource sampler to the meter. Called once, by the start-up wiring once the
    /// platform probe and ResourcePressure exist, and by the headless tests with a fake probe.
    /// </summary>
    internal void AttachResourceMonitor(
        ResourceMonitorHost host,
        Func<int> runningSessions,
        Action openDetail)
    {
        FileLog.Write("[MainWindow] AttachResourceMonitor");
        if (_resourceMonitor is not null)
            throw new InvalidOperationException("The resource monitor is already attached to this window.");

        _resourceMonitor = new ResourceMonitorPresenter(
            host, ResourceMeterControl, Environment.MachineName, runningSessions, openDetail);
        WireResourceMeterFit();
        Closed += (_, _) => _resourceMonitor?.Dispose();
    }

    /// <summary>
    /// What clicking the meter does - the only way in now that the banner and its "Show resources" link are gone.
    /// THE ONE SEAM FOR THE DETAIL SCREEN: phase 2b
    /// replaces this body with opening its window, which must show at once even while empty (CLAUDE.md rule 1).
    /// Until then the click opens the meter's own figures in place, so it still answers within 100 milliseconds
    /// and never does nothing.
    /// </summary>
    internal void OpenResourceDetail()
    {
        FileLog.Write("[MainWindow] OpenResourceDetail: the detail screen is not built yet (phase 2b); showing the meter's figures");
        global::Avalonia.Controls.ToolTip.SetIsOpen(ResourceMeterControl, true);
    }

    /// <summary>
    /// Re-decides the meter's form whenever the toolbar or its groups change size. Wired on its own so the narrow
    /// behaviour works before a sampler is attached too.
    /// </summary>
    internal void WireResourceMeterFit()
    {
        if (_resourceMeterFitWired) return;
        _resourceMeterFitWired = true;
        MainToolbar.SizeChanged += (_, _) => UpdateResourceMeterFit();
        ToolbarLeftGroup.SizeChanged += (_, _) => UpdateResourceMeterFit();
        ToolbarRightGroup.SizeChanged += (_, _) => UpdateResourceMeterFit();
    }

    /// <summary>
    /// Compact when the bars would not fit: the left group, the right group without the meter, and the meter at its
    /// full width must all fit in the toolbar. Measured rather than a fixed width, because the Director's name and
    /// the left group's buttons differ from machine to machine.
    /// </summary>
    internal void UpdateResourceMeterFit()
    {
        var meter = ResourceMeterControl;
        if (!meter.IsCompact && meter.DesiredSize.Width > 0)
            _resourceMeterFullWidth = meter.DesiredSize.Width;
        if (_resourceMeterFullWidth <= 0) return;

        var available = MainToolbar.Bounds.Width - MainToolbar.Padding.Left - MainToolbar.Padding.Right;
        if (available <= 0) return;

        // NATURAL widths, worked out from the children. Neither the groups' bounds nor their desired sizes will do:
        // the left group is the toolbar's last child, so its bounds stretch to whatever is left, and a stack panel's
        // desired size is clipped to the room it was offered - a squeezed group always reports that it fits. A
        // horizontal stack panel measures its children with unlimited width, so their desired sizes are honest.
        var rightWithoutMeter = NaturalWidth(ToolbarRightGroup) - meter.DesiredSize.Width;
        var needed = NaturalWidth(ToolbarLeftGroup) + rightWithoutMeter + _resourceMeterFullWidth + ToolbarSlack;
        var compact = needed > available;
        LastResourceMeterFit = (needed, available);
        if (compact != meter.IsCompact)
            FileLog.Write($"[MainWindow] UpdateResourceMeterFit: needed={needed:F0} available={available:F0} compact={compact}");
        meter.IsCompact = compact;
    }

    private static double NaturalWidth(global::Avalonia.Controls.StackPanel group)
    {
        double width = 0;
        var visible = 0;
        foreach (var child in group.Children)
        {
            if (!child.IsVisible) continue;
            width += child.DesiredSize.Width;
            visible++;
        }
        if (visible > 1) width += group.Spacing * (visible - 1);
        return width + group.Margin.Left + group.Margin.Right;
    }
}
