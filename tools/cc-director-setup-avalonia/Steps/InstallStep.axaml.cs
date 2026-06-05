using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class InstallStep : UserControl
{
    private ToolDownloadItem? _directorItem;
    private List<ToolDownloadItem> _toolItems = [];
    private List<SkillItem> _skillItems = [];

    public InstallStep()
    {
        InitializeComponent();
        LogFooter.Text = $"Setup log: {SetupLog.Path}";
        SetupLog.Write("[InstallStep] Created");
    }

    private void OpenLogButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] OpenLogButton_Click");
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SetupLog.Path}\"") { UseShellExecute = true });
            else
            {
                var psi = new ProcessStartInfo(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open");
                psi.ArgumentList.Add(SetupLog.Dir);
                Process.Start(psi);
            }
        }
        catch (Exception ex) { SetupLog.Write($"[InstallStep] OpenLogButton_Click FAILED: {ex.Message}"); }
    }

    private void ReportButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] ReportButton_Click");
        try
        {
            var os = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" : "Windows";
            IssueReporter.Open(IssueReporter.BuildUrl($"[install] Setup stuck or failing on {os}", BuildIssueBody(os)));
        }
        catch (Exception ex) { SetupLog.Write($"[InstallStep] ReportButton_Click FAILED: {ex.Message}"); }
    }

    private string BuildIssueBody(string os)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## What happened");
        sb.AppendLine("<!-- e.g. the installer was stuck on a step, or a component failed. -->");
        sb.AppendLine();
        sb.AppendLine("## Environment");
        sb.AppendLine($"- OS: {os} ({RuntimeInformation.OSDescription})");
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
            var all = File.ReadAllLines(SetupLog.Path);
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
        ToolsSummary.Text = $"{_toolItems.Count} tools";

        // Set up skills list
        _skillItems = ToolInstaller.SkillNames
            .Select(name => new SkillItem { Name = name })
            .ToList();
        SkillList.ItemsSource = _skillItems;
        SkillsSummary.Text = $"{_skillItems.Count} Claude Code skills";

        // Bind director item changes
        if (_directorItem != null)
        {
            _directorItem.PropertyChanged += (_, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (e.PropertyName == nameof(ToolDownloadItem.Status))
                    {
                        DirectorStatus.Text = _directorItem.Status;
                        DirectorStatus.Foreground = SolidColorBrush.Parse(_directorItem.StatusColor);
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

        // Track overall tool progress. Status drives the summary text; Progress (set live by the
        // download byte counter and pip streaming inside PythonToolsInstaller) drives the bar so
        // the user sees motion during the multi-minute python-tools step.
        foreach (var tool in _toolItems)
        {
            var capturedTool = tool;
            capturedTool.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ToolDownloadItem.Status))
                    Dispatcher.UIThread.Post(UpdateToolsSummaryStatus);
                else if (e.PropertyName == nameof(ToolDownloadItem.Progress))
                    Dispatcher.UIThread.Post(() => ToolsOverallProgress.Value = capturedTool.Progress);
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
        RepairButton.IsVisible = true;

        var upToDateBrush = SolidColorBrush.Parse("#22C55E");

        DirectorStatus.Text = "Up to date";
        DirectorStatus.Foreground = upToDateBrush;
        ToolsStatus.Text = "Up to date";
        ToolsStatus.Foreground = upToDateBrush;
        SkillsStatus.Text = "Up to date";
        SkillsStatus.Foreground = upToDateBrush;
    }

    private void RepairButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] RepairButton_Click");
        RepairButton.IsVisible = false;
        OnRepairRequested?.Invoke();
    }

    public void SetStatus(string status)
    {
        StatusText.Text = status;
    }

    public void ShowProgress()
    {
        DirectorProgress.IsVisible = true;
        ToolsOverallProgress.IsVisible = true;
    }

    public List<SkillItem> GetSkillItems() => _skillItems;

    public void UpdateSkillsStatus()
    {
        var done = _skillItems.Count(s => s.Status == "Done");
        SkillsStatus.Text = $"{done} installed";
        SkillsStatus.Foreground = SolidColorBrush.Parse("#22C55E");
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
            ToolsStatus.Text = $"{done} installed";
            ToolsStatus.Foreground = SolidColorBrush.Parse("#22C55E");
            ToolsSummary.Text = skipped > 0
                ? $"{done} installed, {skipped} not yet released"
                : $"{done} installed";
            ToolsOverallProgress.IsVisible = false;
        }
        else
        {
            // Surface the bundle row's live Status (e.g. "Downloading 118.2 MB / 334.5 MB") so the
            // user sees motion even when the details Expander is collapsed. Bar value is driven from
            // the row's Progress property (download bytes + pip streaming) — don't overwrite it here.
            var active = _toolItems.FirstOrDefault(
                t => t.Status is not "Pending" and not "Done" and not "Failed" and not "Locked" and not "Skipped");
            ToolsStatus.Text = active?.Status ?? "Installing...";
        }
    }
}
