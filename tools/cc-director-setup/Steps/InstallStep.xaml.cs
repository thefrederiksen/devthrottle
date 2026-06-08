using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class InstallStep : UserControl
{
    private ToolDownloadItem? _directorItem;
    private List<ToolDownloadItem> _toolItems = [];
    private List<SkillItem> _skillItems = [];

    // The real cc-* tool count, reported by the installer once the bundle finishes. The bundle is a
    // single row, so the row count is not the tool count; null until known.
    private int? _toolsInstalledCount;

    public InstallStep()
    {
        InitializeComponent();
        LogFooter.Text = $"Setup log: {SetupLog.Path}";
        SetupLog.Write("[InstallStep] Created");
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] OpenLogButton_Click");
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SetupLog.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { SetupLog.Write($"[InstallStep] OpenLogButton_Click FAILED: {ex.Message}"); }
    }

    private void ReportButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] ReportButton_Click");
        try
        {
            IssueReporter.Open(IssueReporter.BuildUrl("[install] Setup stuck or failing on Windows", BuildIssueBody()));
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[InstallStep] ReportButton_Click FAILED: {ex.Message}");
            MessageBox.Show(
                $"Could not open the browser. Please file an issue at {IssueReporter.NewIssueBase} and attach the log:\n{SetupLog.Path}",
                "Report a problem", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private string BuildIssueBody()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## What happened");
        sb.AppendLine("<!-- e.g. the installer was stuck on a step, or a component failed. -->");
        sb.AppendLine();
        sb.AppendLine("## Environment");
        sb.AppendLine($"- OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"- Arch: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"- Status when reported: {StatusText.Text}");
        sb.AppendLine();
        sb.AppendLine("## Setup log");
        sb.AppendLine($"Full log (please attach it): `{SetupLog.Path}`");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(ReadLogTail(160));
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string ReadLogTail(int lines)
    {
        try
        {
            var all = System.IO.File.ReadAllLines(SetupLog.Path);
            var start = Math.Max(0, all.Length - lines);
            return string.Join("\n", all[start..]);
        }
        catch (Exception ex)
        {
            return $"(could not read log: {ex.Message})";
        }
    }

    public void SetItems(List<ToolDownloadItem> items)
    {
        _directorItem = items.FirstOrDefault(i => i.Name == "cc-director");
        _toolItems = items.Where(i => i.Name != "cc-director").ToList();

        ToolList.ItemsSource = _toolItems;
        // The bundle is one row representing many cc-* tools, so don't show the row count ("1 tools").
        // The real count arrives via SetToolsInstalledCount when the bundle finishes.
        ToolsSummary.Text = "cc-* command-line tools";

        // Set up skills list
        _skillItems = SkillInstaller.SkillNames
            .Select(name => new SkillItem { Name = name })
            .ToList();
        SkillList.ItemsSource = _skillItems;
        SkillsSummary.Text = $"{_skillItems.Count} Claude Code skills";

        // Bind director item changes
        if (_directorItem != null)
        {
            _directorItem.PropertyChanged += (_, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (e.PropertyName == nameof(ToolDownloadItem.Status))
                    {
                        DirectorStatus.Text = _directorItem.Status;
                        DirectorStatus.Foreground = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString(_directorItem.StatusColor));
                    }
                    else if (e.PropertyName == nameof(ToolDownloadItem.Progress))
                    {
                        DirectorProgress.Value = _directorItem.Progress;
                    }
                    else if (e.PropertyName == nameof(ToolDownloadItem.SizeText))
                    {
                        DirectorSize.Text = _directorItem.SizeText;
                    }
                });
            };
        }

        // Track overall tool progress. Status drives the summary text; Progress (set live by pip
        // streaming inside PythonToolsInstaller) drives the bar so the user sees motion during
        // the multi-minute python-tools step.
        foreach (var tool in _toolItems)
        {
            var capturedTool = tool;
            capturedTool.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ToolDownloadItem.Status))
                    Dispatcher.BeginInvoke(UpdateToolsSummaryStatus);
                else if (e.PropertyName == nameof(ToolDownloadItem.Progress))
                    Dispatcher.BeginInvoke(() => ToolsOverallProgress.Value = capturedTool.Progress);
            };
        }
    }

    public event Action? OnRepairRequested;

    public void SetUpdateMode()
    {
        HeadingText.Text = "Updating";
    }

    public void SetUpToDate(string version)
    {
        SetupLog.Write($"[InstallStep] SetUpToDate: version={version}");

        HeadingText.Text = "Up to Date";
        StatusText.Text = $"You are running the latest version ({version}).";
        RepairButton.Visibility = Visibility.Visible;

        var upToDateColor = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString("#22C55E"));

        DirectorStatus.Text = "Up to date";
        DirectorStatus.Foreground = upToDateColor;
        ToolsStatus.Text = "Up to date";
        ToolsStatus.Foreground = upToDateColor;
        SkillsStatus.Text = "Up to date";
        SkillsStatus.Foreground = upToDateColor;
    }

    private void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] RepairButton_Click");
        RepairButton.Visibility = Visibility.Collapsed;
        OnRepairRequested?.Invoke();
    }

    public void SetStatus(string status)
    {
        StatusText.Text = status;
    }

    /// <summary>Reveal the Gateway and Cockpit card (Gateway-role installs only).</summary>
    public void ShowGatewaySection()
    {
        GatewaySection.Visibility = Visibility.Visible;
    }

    /// <summary>The Gateway tray app + Cockpit are being placed (indeterminate - the CLI streams log lines).</summary>
    public void SetGatewayInstalling()
    {
        GatewayStatus.Text = "Installing";
        GatewayStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC"));
        GatewayProgress.Visibility = Visibility.Visible;
    }

    public void SetGatewayDone()
    {
        GatewayStatus.Text = "Done";
        GatewayStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22C55E"));
        GatewayProgress.Visibility = Visibility.Collapsed;
    }

    public void SetGatewayFailed()
    {
        GatewayStatus.Text = "Failed";
        GatewayStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CC4444"));
        GatewayProgress.Visibility = Visibility.Collapsed;
    }

    /// <summary>Record the real installed cc-* tool count (the bundle is one row) and refresh the summary.</summary>
    public void SetToolsInstalledCount(int count)
    {
        _toolsInstalledCount = count;
        UpdateToolsSummaryStatus();
    }

    public void ShowProgress()
    {
        DirectorProgress.Visibility = Visibility.Visible;
        ToolsOverallProgress.Visibility = Visibility.Visible;
    }

    public List<SkillItem> GetSkillItems() => _skillItems;

    public void UpdateSkillsStatus()
    {
        var done = _skillItems.Count(s => s.Status == "Done");
        SkillsStatus.Text = $"{done} installed";
        SkillsStatus.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString("#22C55E"));
        SkillsSummary.Text = $"{done}/{_skillItems.Count} skills installed";
    }

    private void UpdateToolsSummaryStatus()
    {
        var done = _toolItems.Count(t => t.Status == "Done");
        var failed = _toolItems.Count(t => t.Status == "Failed");
        var locked = _toolItems.Count(t => t.Status == "Locked");
        var skipped = _toolItems.Count(t => t.Status == "Skipped");
        var total = _toolItems.Count;
        var processed = done + failed + locked + skipped;

        if (processed == total)
        {
            // The bundle row counts as one; prefer the real cc-* tool count when the installer
            // has reported it (e.g. 26), falling back to the row count only if unknown.
            var shown = _toolsInstalledCount ?? done;
            ToolsStatus.Text = $"{shown} installed";
            ToolsStatus.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#22C55E"));
            ToolsSummary.Text = skipped > 0
                ? $"{shown} installed, {skipped} not yet released"
                : $"{shown} installed";
            ToolsOverallProgress.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Surface the bundle row's live Status (e.g. "Installing 5/23: scipy...") so the user sees
            // motion even when the install details Expander is collapsed. Bar value is driven from the
            // row's Progress property (set live by pip streaming) — don't overwrite it here.
            var active = _toolItems.FirstOrDefault(
                t => t.Status is not "Pending" and not "Done" and not "Failed" and not "Locked" and not "Skipped");
            ToolsStatus.Text = active?.Status ?? "Installing...";
        }
    }
}
