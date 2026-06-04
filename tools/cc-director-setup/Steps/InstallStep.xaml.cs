using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        // Track overall tool progress
        foreach (var tool in _toolItems)
        {
            tool.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ToolDownloadItem.Status))
                    Dispatcher.BeginInvoke(UpdateToolsSummaryStatus);
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
            ToolsStatus.Text = $"{done} installed";
            ToolsStatus.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#22C55E"));
            ToolsSummary.Text = skipped > 0
                ? $"{done} installed, {skipped} not yet released"
                : $"{done} installed";
            ToolsOverallProgress.Visibility = Visibility.Collapsed;
        }
        else
        {
            var downloading = _toolItems.FirstOrDefault(t => t.Status == "Downloading");
            ToolsStatus.Text = downloading != null ? $"Installing {downloading.Name}..." : "Installing...";
            ToolsOverallProgress.Value = (double)processed / total * 100;
        }
    }
}
