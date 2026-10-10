using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

/// <summary>
/// In-wizard uninstall flow, the port of the Windows wizard's UninstallStep (the master): three
/// themed views - confirm, live progress, and completion. Self-contained: it owns its buttons and
/// raises <see cref="Cancelled"/> / <see cref="CloseRequested"/> for the host window to act on;
/// the host hides the step rail and nav bar while this is shown.
/// </summary>
public partial class UninstallStep : UserControl
{
    private readonly InstallLayout _layout = InstallLayout.Default();
    private readonly InstallRole _role;
    private readonly Func<IProgress<string>, UninstallReport> _runner;
    private readonly ObservableCollection<string> _completed = new();
    private string? _currentPhase;

    /// <summary>When true (the "Also delete my data" opt-in, issue #261), the uninstall ALSO wipes
    /// the entire per-user data root. Default false keeps data exactly as before.</summary>
    private bool _deleteData;

    /// <summary>The "your data is kept" message shown when the opt-in is unchecked.</summary>
    private readonly string _dataKeptText = "";

    /// <summary>The amber warning shown in place of the kept message when the opt-in is checked.</summary>
    private readonly string _dataWipeText = "";

    /// <summary>Raised when the user clicks Cancel on the confirm view (no changes made).</summary>
    public event EventHandler? Cancelled;

    /// <summary>Raised when the user clicks Close on the completion view.</summary>
    public event EventHandler? CloseRequested;

    public UninstallStep()
    {
        InitializeComponent();
        _runner = _ => throw new InvalidOperationException("designer constructor");
    }

    /// <summary><paramref name="runner"/> performs the removal and reports progress; it defaults to
    /// the real engine uninstaller. Injectable so the flow's UI can be exercised without touching
    /// the machine.</summary>
    public UninstallStep(InstallLayout layout, InstallRole role,
        Func<IProgress<string>, UninstallReport>? runner = null)
    {
        InitializeComponent();
        _layout = layout;
        _role = role;
        // The default runner reads _deleteData at call time, so the checkbox state at the moment
        // the user clicks Uninstall is what flows through to the engine.
        _runner = runner ?? (p => new Uninstaller(layout).Apply(role, p, _deleteData));

        ConfirmSubtitle.Text = role == InstallRole.Gateway
            ? "This removes DevThrottle, its tools, and the Gateway from this Mac."
            : "This removes DevThrottle and its tools from this Mac.";

        RemoveList.ItemsSource = BuildRemovalList(role);
        StepList.ItemsSource = _completed;

        _dataKeptText = $"Your data is kept - config, vault, sign-ins, and recordings are preserved at "
                        + $"{_layout.LocalRoot}";
        _dataWipeText = $"Your data will be permanently removed - config, vault secrets, signed-in "
                        + $"browser sessions, and recordings under {_layout.LocalRoot} will be deleted. "
                        + $"This cannot be undone.";
        DataKeptText.Text = _dataKeptText;
        CompleteDataKept.Text = _dataKeptText;

        SetupLog.Write($"[UninstallStep] created role={role}");
    }

    private static List<string> BuildRemovalList(InstallRole role)
    {
        var items = new List<string>
        {
            "The Director and all cc-* CLI tools",
            OperatingSystem.IsMacOS()
                ? "The shell PATH entries and tool shims"
                : "The PATH entry for the tools",
            OperatingSystem.IsMacOS()
                ? "The Launcher launch agent"
                : "Scheduled tasks and the Start Menu shortcut",
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

    /// <summary>The "Also delete my data" opt-in toggled (issue #261). Track the state and swap the
    /// data card between the reassuring "kept" message and the amber full-wipe warning, so the confirm
    /// view never says data is preserved while the box that deletes it is checked.</summary>
    private void DeleteDataCheckbox_Changed(object? sender, RoutedEventArgs e)
    {
        _deleteData = DeleteDataCheckbox.IsChecked == true;
        SetupLog.Write($"[UninstallStep] deleteData opt-in={_deleteData}");

        if (_deleteData)
        {
            DataKeptCard.Background = SolidColorBrush.Parse("#3A2A1B");
            DataKeptText.Foreground = SolidColorBrush.Parse("#E5A100");
            DataKeptText.Text = _dataWipeText;
        }
        else
        {
            DataKeptCard.Background = SolidColorBrush.Parse("#1B2A3A");
            DataKeptText.Foreground = SolidColorBrush.Parse("#AACCEE");
            DataKeptText.Text = _dataKeptText;
        }
    }

    /// <summary>
    /// A failed uninstall reaches DevThrottle (issue #3645), not only this screen. Except when the person chose to
    /// delete their data and the folder is gone: the report needs the install id, and reading it would make the
    /// folder again underneath a person who asked for it to be gone. That case is logged with its reason.
    /// </summary>
    private void ReportUninstallError(string message, Exception? ex)
    {
        if (!Directory.Exists(_layout.LocalRoot))
        {
            SetupLog.Write($"[UninstallStep] NOT reported, because the data folder was deleted at the person's request and the report would recreate it: {message}");
            return;
        }
        _ = WizardErrorReport.SendAsync("wizard", "uninstall", message, ex);
    }

    private void ConfirmCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[UninstallStep] cancelled by user");
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private async void ConfirmUninstallButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write($"[UninstallStep] uninstall confirmed role={_role}");
        ConfirmView.IsVisible = false;
        ProgressView.IsVisible = true;

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
            ReportUninstallError($"Uninstall failed: {ex.GetType().Name}: {ex.Message}", ex);
            ShowComplete(success: false, errors: new[] { ex.Message });
            return;
        }

        // Mark the final phase done, then show the result.
        if (_currentPhase is not null) _completed.Add(_currentPhase);
        SetupLog.Write($"[UninstallStep] done success={report.Success}, steps={report.Steps.Count}, errors={report.Errors.Count}");
        if (report.Errors.Count > 0)
            ReportUninstallError($"Uninstall finished with {report.Errors.Count} error(s): {string.Join(" | ", report.Errors)}", null);
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
        StepScroller.ScrollToEnd();
    }

    private void ShowComplete(bool success, IReadOnlyList<string> errors)
    {
        ProgressView.IsVisible = false;
        CompleteView.IsVisible = true;

        // Reflect what actually happened to the data on the completion card.
        CompleteDataKept.Text = _deleteData
            ? $"Your data was removed - config, vault, sign-ins, and recordings under {_layout.LocalRoot} were deleted."
            : _dataKeptText;

        if (success)
        {
            CompleteDot.Fill = SolidColorBrush.Parse("#22C55E");
            CompleteHeading.Text = "Uninstall complete";
            CompleteSummary.Text = "DevThrottle has been removed from this Mac.";
            ErrorCard.IsVisible = false;
        }
        else
        {
            CompleteDot.Fill = SolidColorBrush.Parse("#CC4444");
            CompleteHeading.Text = "Uninstall finished with issues";
            CompleteSummary.Text = $"Most of DevThrottle was removed, but {errors.Count} item(s) could not be. "
                                   + "This is usually a file locked by a running app - close it and re-run.";
            ErrorList.ItemsSource = errors;
            ErrorCard.IsVisible = true;
        }
    }

    private void CompleteCloseButton_Click(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);
}
