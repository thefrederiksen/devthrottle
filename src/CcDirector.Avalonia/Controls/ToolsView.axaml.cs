using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

    public ToolsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await LoadCatalogAsync();
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

    private async void RunButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolItemViewModel vm) return;
        await RunToolAsync(vm, refreshDetailIfSelected: true);
        UpdateSummary();
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
