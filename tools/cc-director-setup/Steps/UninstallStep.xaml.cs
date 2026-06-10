using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

/// <summary>
/// In-wizard uninstall flow (issue: nicer uninstall progress). Replaces the old raw MessageBox
/// confirm + frozen window + MessageBox result with three themed views - confirm, live progress,
/// and completion - so removing CC Director looks like the rest of the wizard. Self-contained:
/// it owns its buttons and raises <see cref="Cancelled"/> / <see cref="CloseRequested"/> for the
/// host window to act on; the host hides the step rail and nav bar while this is shown.
/// </summary>
public partial class UninstallStep : UserControl
{
    private readonly InstallLayout _layout;
    private readonly InstallRole _role;
    private readonly Func<IProgress<string>, UninstallReport> _runner;
    private readonly ObservableCollection<string> _completed = new();
    private string? _currentPhase;

    /// <summary>Raised when the user clicks Cancel on the confirm view (no changes made).</summary>
    public event EventHandler? Cancelled;

    /// <summary>Raised when the user clicks Close on the completion view.</summary>
    public event EventHandler? CloseRequested;

    /// <summary><paramref name="runner"/> performs the removal and reports progress; it defaults to
    /// the real engine uninstaller. Injectable so the flow's UI can be exercised without touching
    /// the machine.</summary>
    public UninstallStep(InstallLayout layout, InstallRole role,
        Func<IProgress<string>, UninstallReport>? runner = null)
    {
        InitializeComponent();
        _layout = layout;
        _role = role;
        _runner = runner ?? (p => new Uninstaller(layout).Apply(role, p));

        ConfirmSubtitle.Text = role == InstallRole.Gateway
            ? "This removes CC Director, its tools, and the Gateway from this PC."
            : "This removes CC Director and its tools from this PC.";

        RemoveList.ItemsSource = BuildRemovalList(role);
        StepList.ItemsSource = _completed;

        var dataKept = $"Your data is kept - config, vault, sign-ins, and recordings are preserved at "
                       + $"{_layout.LocalRoot}";
        DataKeptText.Text = dataKept;
        CompleteDataKept.Text = dataKept;

        SetupLog.Write($"[UninstallStep] created role={role}");
    }

    private static List<string> BuildRemovalList(InstallRole role)
    {
        var items = new List<string>
        {
            "The CC Director app and all cc-* CLI tools",
            "The PATH entry for the tools",
            "The CC Director skills (only the ones it installed)",
            "Scheduled tasks and the Start Menu shortcut",
        };
        if (OperatingSystem.IsWindows())
            items.Add("The Apps & features (Add/Remove Programs) entry");
        if (role == InstallRole.Gateway)
        {
            items.Insert(1, "The Gateway tray app and the Cockpit");
            items.Add("The Gateway autostart entry and the Tailscale mapping");
        }
        return items;
    }

    private void ConfirmCancelButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[UninstallStep] cancelled by user");
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private async void ConfirmUninstallButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write($"[UninstallStep] uninstall confirmed role={_role}");
        ConfirmView.Visibility = Visibility.Collapsed;
        ProgressView.Visibility = Visibility.Visible;

        // Progress<T> created on the UI thread marshals Report(...) back here, so the handler
        // updates the UI safely while Apply runs on a background thread.
        var progress = new Progress<string>(OnPhase);
        UninstallReport report;
        try
        {
            report = await Task.Run(() => _runner(progress));
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[UninstallStep] uninstall FAILED: {ex}");
            ShowComplete(success: false, errors: new[] { ex.Message });
            return;
        }

        // Mark the final phase done, then show the result.
        if (_currentPhase is not null) _completed.Add(_currentPhase);
        SetupLog.Write($"[UninstallStep] done success={report.Success}, steps={report.Steps.Count}, errors={report.Errors.Count}");
        ShowComplete(report.Success, report.Errors);
    }

    /// <summary>Each engine phase reports as it BEGINS: bank the previous phase as completed,
    /// then surface the new one as the current action.</summary>
    private void OnPhase(string phase)
    {
        if (_currentPhase is not null)
            _completed.Add(_currentPhase);
        _currentPhase = phase;
        ProgressStatus.Text = phase + "...";
        StepScroller.ScrollToBottom();
    }

    private void ShowComplete(bool success, IReadOnlyList<string> errors)
    {
        ProgressView.Visibility = Visibility.Collapsed;
        CompleteView.Visibility = Visibility.Visible;

        if (success)
        {
            CompleteDot.Fill = (System.Windows.Media.Brush)FindResource("SuccessBrush");
            CompleteHeading.Text = "Uninstall complete";
            CompleteSummary.Text = "CC Director has been removed from this PC.";
            ErrorCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            CompleteDot.Fill = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            CompleteHeading.Text = "Uninstall finished with issues";
            CompleteSummary.Text = $"Most of CC Director was removed, but {errors.Count} item(s) could not be. "
                                   + "This is usually a file locked by a running app - close it and re-run.";
            ErrorList.ItemsSource = errors;
            ErrorCard.Visibility = Visibility.Visible;
        }
    }

    private void CompleteCloseButton_Click(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);
}
