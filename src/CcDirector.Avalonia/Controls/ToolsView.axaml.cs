using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Setup;
using CcDirector.Core.Tools;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The Tools catalog page: lists every cc-* tool with a built/PASS/FAIL chip, and a detail pane
/// (Overview / Commands / Tests / Skills / Logs) for the selected tool. Reads the Core
/// <see cref="ToolCatalogService"/>, runs checks via <see cref="ToolTestRunner"/>, and shows skill
/// links from <see cref="SkillToolLinker"/> - the same Core surface the Control API exposes.
///
/// Responsive-UI rule: the catalog loads asynchronously on first show; tests run off the UI thread
/// and the status chips update via INotifyPropertyChanged.
/// </summary>
public partial class ToolsView : UserControl
{
    private readonly ToolCatalogService _catalog = new();
    private readonly ToolTestRunner _runner = new();
    private readonly SkillToolLinker _linker = new();

    private readonly List<ToolItemViewModel> _allItems = new();
    private IReadOnlyList<SkillToolLink> _allLinks = Array.Empty<SkillToolLink>();
    private bool _loaded;

    // Supplied by the owning window with the fault verdict.
    private string? _expectedBinDir;
    private FleetToolCheck? _fleetToolCheck;
    private Func<Task>? _onFleetToolRepaired;
    private Func<Task<FleetToolCheck?>>? _refreshFleetToolCheck;

    public ToolsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        RenderToolCopySweep(DirectorToolCopySweep.Last);
        await LoadCatalogAsync();

        // Re-ask the reachability question now the page is up. The window's verdict can be minutes old
        // - a tool repair may have healed the machine since - and a banner reporting a fault that is
        // already fixed is indistinguishable, to the person reading it, from a fix that did not work.
        // Painted first, refreshed after: the page never waits on a probe that can take seconds.
        if (_refreshFleetToolCheck is { } refresh)
        {
            try
            {
                RenderFleetToolStatus(await refresh());
            }
            catch (Exception ex)
            {
                // A failed re-probe leaves the verdict we were handed standing. It must not read as a
                // pass, and it must not take the page down.
                FileLog.Write($"[ToolsView] fleet tool re-check FAILED: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// What the sweep that ran at this Director's start did with the superseded copies of the tools.
    ///
    /// EVERY STRING COMES FROM THE SWEEP AND IS RENDERED VERBATIM. This method reads a verdict and a
    /// reason and paints them; it never works out what a state means. The one thing it decides is
    /// layout - the colour behind the word the sweep chose - which is what a client is for.
    ///
    /// A REFUSAL IS STILL A REPORT. A sweep that did nothing because the master is empty, or because an
    /// install held the lock, has no rows and a summary that says exactly that, and it is shown. Only a
    /// Director that has not swept at all hides the panel, because there is then nothing true to say.
    ///
    /// Internal and taking the result as a parameter, rather than reaching for the static itself, so a
    /// test can drive every shape of report through it without a Director having started.
    /// </summary>
    internal void RenderToolCopySweep(ToolCopySweepResult? result)
    {
        if (result is null || string.IsNullOrWhiteSpace(result.Summary))
        {
            ToolCopySweepPanel.IsVisible = false;
            return;
        }

        ToolCopySweepSummary.Text = result.Summary;
        ToolCopySweepItems.ItemsSource = result.Looked
            .Select(d => new SweptDirectoryViewModel(d))
            .ToList();
        ToolCopySweepPanel.IsVisible = true;
    }

    /// <summary>One row of the sweep report. Holds the sweep's own words and a colour for the verdict.</summary>
    internal sealed class SweptDirectoryViewModel
    {
        public SweptDirectoryViewModel(SweptDirectory swept)
        {
            Path = swept.Path;
            // A delete that could not finish says so here rather than anywhere else, because "removed"
            // for a directory that still exists is the one thing this report must never say.
            Verdict = swept.Action == SweepAction.Delete
                ? (swept.Removed ? "REMOVED" : "LEFT")
                : "KEPT";
            Reason = swept.Action == SweepAction.Delete && !swept.Removed
                ? $"{swept.Reason} - but it could not be removed: {swept.RemovalFailure}. It is off the path, and the next start tries again."
                : swept.Reason;
        }

        public string Path { get; }
        public string Verdict { get; }
        public string Reason { get; }

        public IBrush VerdictBrush => Verdict switch
        {
            "REMOVED" => SolidColorBrush.Parse("#8FC97F"),
            "LEFT" => SolidColorBrush.Parse("#D8B15A"),
            _ => SolidColorBrush.Parse("#7E9478"),
        };
    }

    /// <summary>Reload the catalog and re-run the checks. Called after a repair changes what is
    /// installed (the Settings Tools tab hosts this view above its "Download and repair tools" button),
    /// so the status list reflects the freshly installed toolset without reopening the dialog.</summary>
    public Task ReloadAsync() => LoadCatalogAsync();

    /// <summary>
    /// Show (or hide) the PATH fault banner from a verdict the Director already reached.
    ///
    /// The verdict is PASSED IN rather than re-derived here, so the badge on the rail and this banner
    /// can never disagree about the same machine at the same moment - the disagreement between two
    /// surfaces describing one thing is the failure this whole area exists to prevent.
    /// </summary>
    /// <param name="check">The Director's latest reachability verdict, or null when it has none yet.
    /// Null hides the banner: no verdict is not a fault, and it is not a pass either.</param>
    /// <param name="onRepaired">Invoked after a successful repair so the owning window re-drives its
    /// own badge rather than being left showing a fault the user has just fixed.</param>
    /// <param name="refresh">Re-runs the Director's own check and returns its fresh verdict. Called
    /// once when the page loads, so an open panel cannot sit on a verdict the machine has outgrown,
    /// and again after a repair - the window owns the probe because it owns the credential the
    /// probe presents.</param>
    public void ShowFleetToolStatus(
        FleetToolCheck? check,
        Func<Task>? onRepaired = null,
        Func<Task<FleetToolCheck?>>? refresh = null)
    {
        _onFleetToolRepaired = onRepaired;
        _refreshFleetToolCheck = refresh;
        RenderFleetToolStatus(check);
    }

    /// <summary>
    /// Paint the fault, and say which of the TWO faults it is. They look identical from the outside -
    /// a session cannot reach this Director - and they have different repairs:
    ///
    ///   PATH resolves someone else's WORKING copy   -> repoint PATH (ours is ready and waiting)
    ///   we have no working copy of our own          -> install ours FIRST, then repoint
    ///
    /// Telling them apart is the whole fix. Offering "Repoint PATH" for the second one is what shipped:
    /// the button dutifully reordered PATH around a directory that was empty, resolution fell through
    /// it to the same stale install, and it reported the failure it was pressed to fix.
    /// </summary>
    private void RenderFleetToolStatus(FleetToolCheck? check)
    {
        _fleetToolCheck = check;

        if (check is not { Verdict: FleetToolVerdict.CannotReachGateway })
        {
            PathFaultBanner.IsVisible = false;
            return;
        }

        PathFaultBanner.IsVisible = true;
        _expectedBinDir = check.ExpectedBinDir;
        PathFaultResolved.Text = check.ResolvedPath ?? "(not resolved)";
        PathFaultExpected.Text = check.ExpectedBinDir ?? "(unknown)";

        if (check.OwnToolsAreMissingOrBroken)
        {
            PathFaultExplanation.Text =
                "This Director's own command-line tools are not installed and working, so PATH order is "
                + "not the problem - there is nothing here to point at yet. "
                + $"{check.OwnDetail} Installing them is the repair; putting an empty directory on PATH "
                + "would change nothing.";
            PathFaultFixButton.Content = "Install tools, then repoint PATH";
            PathFaultFixButton.IsVisible = true;
        }
        else if (check.CanRepairByRepointingPath)
        {
            PathFaultExplanation.Text =
                "The command line on your PATH belongs to another install, so agents in your sessions "
                + "report \"cannot connect to DevThrottle\" even though this Director's Gateway "
                + "connection is healthy. This Director's own copy is installed and works.";
            PathFaultFixButton.Content = "Repoint PATH to this install";
            PathFaultFixButton.IsVisible = true;
        }
        else
        {
            // Same install and still refused, or no install directory to compare against. Repointing
            // repairs neither, so state what was seen and offer nothing rather than a button that
            // cannot work.
            PathFaultExplanation.Text =
                $"The command line on your PATH could not reach the fleet through the Gateway: {check.Detail}";
            PathFaultFixButton.IsVisible = false;
        }

        PathFaultFixButton.IsEnabled = true;
        PathFaultProgress.Text = "";
    }

    private async void PathFaultFixButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Immediate feedback before any awaited work (responsive-UI rule), and it has to name the
            // step actually starting: installing the tools takes minutes, and "Repointing PATH..."
            // sitting there through all of it describes work that has not begun.
            var installFirst = _fleetToolCheck is { OwnToolsAreMissingOrBroken: true };
            PathFaultFixButton.IsEnabled = false;
            PathFaultProgress.Text = installFirst
                ? "Installing this Director's tools..."
                : "Repointing PATH...";

            // The directory the verdict named as ours. Re-deriving it here from a different source is how
            // the banner and the check come to disagree about which directory the repair is even for.
            var binDir = _expectedBinDir;
            if (string.IsNullOrWhiteSpace(binDir))
            {
                PathFaultProgress.Text = "No install directory to repoint to.";
                PathFaultFixButton.IsEnabled = true;
                return;
            }

            // When the fault is that we have no working tools of our own, install them BEFORE touching
            // PATH. Repointing first would put an empty directory in front and report success.
            if (installFirst)
            {
                var progress = new Progress<string>(message => PathFaultProgress.Text = message);
                var installed = await Task.Run(() =>
                    new CcDirector.Setup.Engine.ToolUpdater(CcDirector.Setup.Engine.InstallLayout.Default())
                        .RepairPythonToolsAsync(progress));

                FileLog.Write(
                    $"[ToolsView] PATH fault: tool install success={installed.Success}, {installed.Message}");
                if (!installed.Success)
                {
                    PathFaultProgress.Text = $"Could not install the tools: {installed.Message}";
                    PathFaultFixButton.IsEnabled = true;
                    return;
                }

                // The catalog behind this banner is now describing a toolset that no longer exists.
                await LoadCatalogAsync();
                PathFaultProgress.Text = "Repointing PATH...";
            }

            var repair = await Task.Run(() => FleetToolPathRepair.PutFirstOnPath(binDir));

            if (!repair.Succeeded)
            {
                PathFaultProgress.Text = repair.Detail;
                PathFaultFixButton.IsEnabled = true;
                return;
            }

            // Re-ask the question rather than assuming the repair worked. A button that reports its own
            // success without re-checking is how a fix that did nothing still looks like a fix. The
            // re-check goes through the owning window's delegate because the window owns the probe
            // credential (a freshly registered Gateway session key).
            if (_refreshFleetToolCheck is not { } recheckDelegate)
            {
                PathFaultProgress.Text = "PATH updated. Re-open Settings to re-check.";
                PathFaultFixButton.IsEnabled = true;
                return;
            }

            PathFaultProgress.Text = "Checking...";
            var recheck = await recheckDelegate();
            RenderFleetToolStatus(recheck);

            if (recheck is { Verdict: FleetToolVerdict.Working })
            {
                if (_onFleetToolRepaired is { } notify) await notify();
            }
            else
            {
                PathFaultProgress.Text =
                    $"PATH updated, but it still cannot reach the fleet: {recheck?.Detail ?? "no verdict"}";
                PathFaultFixButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ToolsView] PathFaultFixButton_Click FAILED: {ex.Message}");
            PathFaultProgress.Text = $"Could not repoint PATH: {ex.Message}";
            PathFaultFixButton.IsEnabled = true;
        }
    }

    private async Task LoadCatalogAsync()
    {
        FileLog.Write("[ToolsView] LoadCatalogAsync");
        try
        {
            ListSummary.Text = "Loading...";

            var (descriptors, links, unmanaged) = await Task.Run(() =>
            {
                var d = _catalog.GetCatalog();
                var l = _linker.BuildLinks();
                var u = _catalog.GetUnmanagedBinaries();
                return (d, l, u);
            });

            _allLinks = links;
            _allItems.Clear();
            foreach (var d in descriptors)
                _allItems.Add(new ToolItemViewModel(d));

            ApplyFilter();

            // Availability (PATH or bundled bin), not bin-only IsBuilt, is the user-facing signal (issue #448).
            var available = _allItems.Count(i => i.IsAvailable);
            var unavailable = _allItems.Count - available;
            SummaryText.Text = $"{_allItems.Count} tools   {available} available   {unavailable} unavailable";

            var unmanagedNote = unmanaged.Count > 0
                ? $"\nUnmanaged binaries (not in manifest): {string.Join(", ", unmanaged)}"
                : "";
            ListSummary.Text = $"{available}/{_allItems.Count} available.{unmanagedNote}";

            // Auto-run the checks once so built tools show PASS/FAIL right away instead of a wall of
            // "untested" the user has to clear manually (the screenshot complaint). Fire-and-forget;
            // chips update live off the UI thread, and the "Run All Tests" button reflects progress.
            _ = RunAllAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ToolsView] LoadCatalogAsync FAILED: {ex.Message}");
            ListSummary.Text = $"Failed to load catalog: {ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        IEnumerable<ToolItemViewModel> items = _allItems;
        if (query.Length > 0)
            items = _allItems.Where(i =>
                i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.Category.Contains(query, StringComparison.OrdinalIgnoreCase));
        ToolList.ItemsSource = items.ToList();
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void ToolList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolItemViewModel vm)
        {
            DetailPanel.IsVisible = false;
            DetailEmpty.IsVisible = true;
            return;
        }

        DetailEmpty.IsVisible = false;
        DetailPanel.IsVisible = true;
        PopulateDetail(vm);
    }

    private void PopulateDetail(ToolItemViewModel vm)
    {
        var d = vm.Descriptor;
        DetailName.Text = d.Name;
        DetailDescription.Text = d.Description;
        DetailCategory.Text = d.Category;
        DetailBinaryPath.Text = d.BinaryPath
            + (d.IsAvailable
                ? (d.IsBuilt ? "" : "   (on PATH)")
                : "   (unavailable)");
        DetailVersion.Text = "";

        if (!string.IsNullOrWhiteSpace(d.Note))
        {
            DetailNote.Text = d.Note;
            DetailNoteBox.IsVisible = true;
        }
        else
        {
            DetailNoteBox.IsVisible = false;
        }

        UpdateStatusChip(vm.Status);

        // Reset per-tool dynamic panes.
        TestsList.ItemsSource = null;
        TestsHint.IsVisible = true;
        LogsOutput.Text = "";
        CommandsOutput.Text = "";

        // Skills for this tool.
        var links = _allLinks.Where(l => string.Equals(l.ToolName, d.Name, StringComparison.OrdinalIgnoreCase))
                             .Select(l => new SkillLinkViewModel(l)).ToList();
        SkillsList.ItemsSource = links;
        SkillsEmpty.IsVisible = links.Count == 0;
    }

    private void UpdateStatusChip(ToolStatus status)
    {
        var (label, brush) = ToolStatusVisuals.For(status);
        DetailStatusText.Text = label;
        DetailStatusChip.Background = brush;
    }

    /// <summary>
    /// Run the selected tool's checks (issue #1107, item 7).
    ///
    /// This is the whole problem in one file: RunAllAsync two methods below opens with an explicit
    /// re-entrancy guard and a label change, and this handler - which also spawns processes - had neither.
    /// Same screen, same file, two different standards.
    /// </summary>
    private async void RunButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolItemViewModel vm) return;
        if (sender is not Control button) return;

        await BusyAction.RunAsync(button, async () =>
        {
            await RunToolAsync(vm, refreshDetailIfSelected: true);
            UpdateSummary();
        }, "Running...", owner: TopLevel.GetTopLevel(this) as Window, failureTitle: "Could not run the tool");
    }

    private async void RunAllButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ToolsView] RunAllButton_Click");
        await RunAllAsync();
    }

    /// <summary>
    /// Run every tool's checks with bounded concurrency, updating the chips live. Used by the
    /// "Run All Tests" button AND auto-triggered once when the catalog first loads, so a built tool
    /// shows PASS/FAIL instead of a wall of "untested" the user must manually clear. Tools whose
    /// smoke command needs credentials declare no smoke test, so this is just their presence+version
    /// check; tools with a read-only smoke run that too. Re-entrancy is guarded by the button state.
    /// </summary>
    private async Task RunAllAsync()
    {
        if (!RunAllButton.IsEnabled) return; // a run is already in progress
        RunAllButton.IsEnabled = false;
        RunAllButton.Content = "Running...";
        try
        {
            // Bounded concurrency so we do not spawn 30 processes at once.
            using var gate = new System.Threading.SemaphoreSlim(Math.Max(1, Environment.ProcessorCount - 1));
            var selected = ToolList.SelectedItem as ToolItemViewModel;

            var tasks = _allItems.Select(async vm =>
            {
                await gate.WaitAsync();
                try { await RunToolAsync(vm, refreshDetailIfSelected: ReferenceEquals(vm, selected)); }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
        }
        finally
        {
            RunAllButton.IsEnabled = true;
            RunAllButton.Content = "Run All Tests";
            UpdateSummary();
        }
    }

    private async Task RunToolAsync(ToolItemViewModel vm, bool refreshDetailIfSelected)
    {
        var d = vm.Descriptor;
        var results = await _runner.RunAllForToolAsync(d);

        var status = !d.IsAvailable
            ? ToolStatus.NotBuilt
            : results.All(r => r.Passed) ? ToolStatus.Pass : ToolStatus.Fail;
        vm.Status = status;

        if (refreshDetailIfSelected)
        {
            Dispatcher.UIThread.Post(() =>
            {
                UpdateStatusChip(status);
                TestsHint.IsVisible = false;
                TestsList.ItemsSource = results.Select(r => new TestResultViewModel(r)).ToList();

                var version = results.FirstOrDefault(r => r.Kind == ToolTestKind.Version);
                if (version is { Passed: true })
                    DetailVersion.Text = version.Message;

                LogsOutput.Text = BuildLogText(d, results);
            });
        }
    }

    private static string BuildLogText(ToolDescriptor d, IReadOnlyList<ToolTestResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {d.Name}");
        sb.AppendLine($"# binary: {d.BinaryPath}");
        sb.AppendLine();
        foreach (var r in results)
        {
            sb.AppendLine($"=== {r.Label}  [{(r.Passed ? "PASS" : "FAIL")}]  exit={r.ExitCode?.ToString() ?? "n/a"}  {r.DurationMs}ms ===");
            if (!string.IsNullOrWhiteSpace(r.Stdout))
            {
                sb.AppendLine("--- stdout ---");
                sb.Append(r.Stdout);
            }
            if (!string.IsNullOrWhiteSpace(r.Stderr))
            {
                sb.AppendLine("--- stderr ---");
                sb.Append(r.Stderr);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private void UpdateSummary()
    {
        var available = _allItems.Count(i => i.IsAvailable);
        var pass = _allItems.Count(i => i.Status == ToolStatus.Pass);
        var fail = _allItems.Count(i => i.Status == ToolStatus.Fail);
        var unavailable = _allItems.Count - available;
        SummaryText.Text = $"{_allItems.Count} tools   {pass} PASS   {fail} FAIL   {unavailable} UNAVAILABLE";
    }

    private async void LoadCommandsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolItemViewModel vm) return;
        var d = vm.Descriptor;
        if (!d.IsAvailable)
        {
            CommandsOutput.Text = "(tool unavailable - cannot read --help)";
            return;
        }

        CommandsOutput.Text = "Loading --help...";
        try
        {
            var help = await RunHelpAsync(d.BinaryPath);
            CommandsOutput.Text = string.IsNullOrWhiteSpace(help) ? "(no output)" : help;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ToolsView] LoadCommands {d.Name} failed: {ex.Message}");
            CommandsOutput.Text = $"Failed to run --help: {ex.Message}";
        }
    }

    /// <summary>Run <c>&lt;tool&gt; --help</c> (read-only) and return the combined output.</summary>
    private static async Task<string> RunHelpAsync(string binaryPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(binaryPath) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("--help");

        // Event-based reads: reading both pipes to completion synchronously can deadlock when the
        // child fills one buffer while we block on the other.
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            return stdout + stderr.ToString() + "\n(timed out)";
        }

        var outText = stdout.ToString();
        var errText = stderr.ToString();
        return string.IsNullOrWhiteSpace(outText) ? errText : outText;
    }
}
