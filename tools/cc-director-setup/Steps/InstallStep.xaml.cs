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
    private ToolDownloadItem? _launcherItem;

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
        catch (Exception ex)
        {
            SetupLog.Write($"[InstallStep] OpenLogButton_Click FAILED: {ex.Message}");
            _ = WizardProgressReport.Error("open-log", $"Windows setup wizard could not open the log folder: {ex.GetType().Name}: {ex.Message}", ex);
        }
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
            _ = WizardProgressReport.Error("report-problem", $"Windows setup wizard could not open the browser to report a problem: {ex.GetType().Name}: {ex.Message}", ex);
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
            // Not reported: the reason is written into the issue text the person is about to read and send.
            return $"(could not read log: {ex.Message})";
        }
    }

    public void SetItems(List<ToolDownloadItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _directorItem = items.FirstOrDefault(i => i.Name == "cc-director");
        _launcherItem = items.FirstOrDefault(i => i.Name == "cc-launcher");

        // The cc-* tools are no longer installed here (the app provisions them on first launch), so the
        // Tools card is a static note - there are no tool rows to bind or track. Skills are not placed
        // on the machine at all any more (issue 995): they are held on the Gateway and fetched, so this
        // step has nothing to say about them.

        Bind(_directorItem, DirectorStatus, DirectorProgress, DirectorSize, DirectorDetail);

        // The launcher card was driven ONLY by the start call, so a launcher whose download or swap
        // failed could still be painted green "Running" by starting the binary already on disk - while
        // the Complete screen counted it as a skipped component. The item is the truth about the
        // install; the start call only reports what happened afterwards.
        Bind(_launcherItem, LauncherStatus, LauncherProgress, null, LauncherDetail);
    }

    /// <summary>
    /// Mirror one component's live state onto its card, including the failure REASON.
    ///
    /// The reason was always computed - every failure path in EngineInstallRunner sets StatusDetail -
    /// and then discarded, because nothing bound it. A user saw the word "Failed" and had to open a
    /// log to learn that, for example, the launcher was healthy but had not registered its autostart.
    /// </summary>
    private void Bind(ToolDownloadItem? item, TextBlock status, ProgressBar progress, TextBlock? size, TextBlock detail)
    {
        if (item is null) return;

        item.PropertyChanged += (_, e) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (e.PropertyName == nameof(ToolDownloadItem.Status))
                {
                    status.Text = item.Status;
                    status.Foreground = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(item.StatusColor));
                }
                else if (e.PropertyName == nameof(ToolDownloadItem.Progress))
                {
                    progress.Value = item.Progress;
                }
                else if (e.PropertyName == nameof(ToolDownloadItem.SizeText))
                {
                    if (size is not null) size.Text = item.SizeText;
                }
                else if (e.PropertyName == nameof(ToolDownloadItem.StatusDetail))
                {
                    detail.Text = item.StatusDetail;
                    detail.Visibility = string.IsNullOrWhiteSpace(item.StatusDetail)
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                }
            });
        };
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
    }

    private void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[InstallStep] RepairButton_Click");
        RepairButton.Visibility = Visibility.Collapsed;
        OnRepairRequested?.Invoke();
    }

    /// <summary>
    /// Nothing was attempted - say so. A release fetch that fails (a rate limit, no network) never
    /// reaches SetItems, so every card sat at "Pending" for ever, which reads as work still in
    /// progress on a run that has already stopped.
    /// </summary>
    public void SetNotStarted()
    {
        var dim = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888"));
        DirectorStatus.Text = "Not started";
        DirectorStatus.Foreground = dim;
        LauncherStatus.Text = "Not started";
        LauncherStatus.Foreground = dim;
    }

    public void SetStatus(string status)
    {
        StatusText.Text = status;
    }

    /// <summary>The launcher tray app is being started (indeterminate - the installer waits on a health probe).</summary>
    public void SetLauncherStarting()
    {
        LauncherStatus.Text = "Starting";
        LauncherStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC"));
        LauncherProgress.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The launcher came up - but only say so if its INSTALL did not fail. Starting the binary that
    /// was already on disk is not evidence that the new one landed, and painting green here over a
    /// failed item is how the install screen came to claim success for a component the Complete
    /// screen counted as skipped.
    /// </summary>
    public void SetLauncherRunning()
    {
        LauncherProgress.Visibility = Visibility.Collapsed;
        if (_launcherItem is not null && _launcherItem.Status is "Failed" or "Skipped") return;

        LauncherStatus.Text = "Running";
        LauncherStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22C55E"));
    }

    public void SetLauncherFailed()
    {
        LauncherStatus.Text = "Failed";
        LauncherStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CC4444"));
        LauncherProgress.Visibility = Visibility.Collapsed;
    }

    public void ShowProgress()
    {
        DirectorProgress.Visibility = Visibility.Visible;
    }
}
