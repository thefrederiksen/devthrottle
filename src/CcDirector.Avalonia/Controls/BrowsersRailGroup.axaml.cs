using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Core.Browsers;
using CcDirector.Core.Utilities;
using CcDirector.Core.ErrorReports;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The pinned "Browser profiles" entry in the left rail: ONE clickable row that opens Settings &gt;
/// Browsers, showing the profile count and a dot when any profile is up. Everything it shows is the
/// Core fold (<see cref="AutomationBrowserViewFold.FoldRail"/>) rendered verbatim - this control
/// decides layout, never meaning.
///
/// It is deliberately not expandable and offers no per-profile action. The rail is a navigation strip:
/// Repositories holds seventeen things in one row, and a browsers group that unfolded into four
/// two-line rows with their own Start links was both a third of the rail and a control panel in the
/// wrong place. Starting, signing in and attaching live on the Browsers settings screen, which has the
/// room to report them honestly - and in normal use an agent brings a profile up itself through Browser
/// Harness, so nobody needs to press Start first.
///
/// When Browser Harness is not installed the row stays visible and wears a Setup nudge, so the feature
/// advertises itself instead of hiding; clicking it lands on the settings screen, where the install runs.
/// </summary>
public partial class BrowsersRailGroup : UserControl
{
    /// <summary>Raised when the user clicks the row: open the management surface (Settings &gt; Browsers).</summary>
    public event EventHandler? ManageRequested;

    /// <summary>Raised with a short user-facing message (a failure) for the host window's notification
    /// strip.</summary>
    public event EventHandler<string>? Notified;

    private readonly DispatcherTimer _refreshTimer;
    private IReadOnlyList<AutomationBrowserView> _views = Array.Empty<AutomationBrowserView>();
    private bool _harnessInstalled;
    private bool _refreshing;

    /// <summary>Bumped by every refresh. A status probe carries the generation it started under and
    /// drops its result if a newer refresh has replaced the list underneath it.</summary>
    private int _refreshGeneration;

    public BrowsersRailGroup()
    {
        InitializeComponent();

        // Status is a live port probe, so it can drift (the user closes a browser window by hand);
        // a slow background re-poll keeps the running dot honest without hammering the ports.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();

        AttachedToVisualTree += (_, _) =>
        {
            _refreshTimer.Start();
            _ = RefreshAsync();
        };
        DetachedFromVisualTree += (_, _) => _refreshTimer.Stop();
    }

    /// <summary>
    /// Re-read the registry and repaint the row, then find out which browsers are running.
    ///
    /// The count comes from local files and paints immediately; each browser's running/stopped answer
    /// costs a probe of its debug port and arrives after, on its own. The row keeps its last known
    /// running dot while the fresh probe runs - this method is also driven by a timer, and dropping the
    /// dot on every tick would read as instability.
    ///
    /// Safe to call from anywhere; overlapping calls collapse into one.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        var generation = ++_refreshGeneration;
        try
        {
            var previous = _views;
            var harnessInstalled = false;
            IReadOnlyList<AutomationBrowserView> views = Array.Empty<AutomationBrowserView>();
            await Task.Run(() =>
            {
                harnessInstalled = AutomationBrowserViewFold.IsHarnessInstalled();
                views = AutomationBrowserViewFold.ListPending(previous);
            });

            _views = views;
            _harnessInstalled = harnessInstalled;
            Render(views, harnessInstalled);
        }
        catch (Exception ex)
        {
            Notified?.Invoke(this, ShownError.Report("browsers rail", "read the browsers list", $"Could not read the browsers list: {ex.Message}", ex));
            return;
        }
        finally
        {
            _refreshing = false;
        }

        await ProbeStatusesAsync(generation);
    }

    /// <summary>
    /// Probe every listed browser CONCURRENTLY and repaint as each answer arrives. Results from a
    /// superseded refresh are dropped: by then they would be updating a list that no longer exists.
    /// </summary>
    private async Task ProbeStatusesAsync(int generation)
    {
        var listed = _views;
        if (listed.Count == 0) return;

        await Task.WhenAll(listed.Select(async pending =>
        {
            AutomationBrowserView probed;
            try
            {
                probed = await Task.Run(() => AutomationBrowserViewFold.FoldAsync(AutomationBrowserRegistry.Get(pending.Id)));
            }
            catch (Exception ex)
            {
                // One browser that cannot be probed stays on its last known status and takes nothing
                // else down with it.
                FileLog.Write($"[BrowsersRailGroup] ProbeStatusesAsync: id={pending.Id} FAILED (non-fatal): {ex.Message}");
                return;
            }

            if (generation != _refreshGeneration) return;
            _views = _views.Select(v => v.Id == probed.Id ? probed : v).ToList();
            Render(_views, _harnessInstalled);
        }));
    }

    private void Render(IReadOnlyList<AutomationBrowserView> views, bool harnessInstalled)
    {
        var rail = AutomationBrowserViewFold.FoldRail(views, harnessInstalled);

        SetupLabel.IsVisible = rail.ShowSetup;
        CountBadge.IsVisible = rail.ShowCount;
        CountText.Text = rail.CountText;
        RunningDot.IsVisible = rail.RunningDotColor is not null;
        if (rail.RunningDotColor is not null)
            RunningDot.Fill = StatusPalette.BrushFor(rail.RunningDotColor);
        ToolTip.SetTip(HeaderButton, rail.ToolTip);
    }

    /// <summary>
    /// The whole interaction: open the Browsers settings. That screen manages the profiles AND runs the
    /// harness install (issue #1012) - the rail is a narrow strip with no room to report a multi-step
    /// install honestly, so it hands over to the screen that has.
    /// </summary>
    private void HeaderButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[BrowsersRailGroup] HeaderButton_Click -> manage");
        ManageRequested?.Invoke(this, EventArgs.Empty);
    }
}
