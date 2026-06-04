using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
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

        // Track overall tool progress
        foreach (var tool in _toolItems)
        {
            tool.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ToolDownloadItem.Status))
                    Dispatcher.UIThread.Post(UpdateToolsSummaryStatus);
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
            var downloading = _toolItems.FirstOrDefault(t => t.Status == "Downloading");
            ToolsStatus.Text = downloading != null ? $"Installing {downloading.Name}..." : "Installing...";
            ToolsOverallProgress.Value = (double)processed / total * 100;
        }
    }
}
