using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;
using CcDirectorSetup.Steps;

namespace CcDirectorSetup;

public partial class MainWindow : Window
{
    private int _currentStep = 1;
    private List<PrerequisiteInfo> _prerequisites = [];
    private int _installedCount;
    private int _skippedCount;
    private string _installPath = "";
    private string _directorExePath = "";
    private InstallRole _role = InstallRole.Workstation;
    private string? _gatewayResultMessage;

    private readonly bool _isUpdate;
    private readonly string? _installedVersion;
    private bool _alreadyUpToDate;
    private string? _latestVersion;
    private EngineInstallRunner.Prep? _cachedPrep;

    private WelcomeStep? _welcomeStep;
    private PrerequisitesStep? _prerequisitesStep;
    private SkillsStep? _skillsStep;
    private InstallStep? _installStep;
    private CompleteStep? _completeStep;

    private readonly record struct StepUI(Border Circle, TextBlock Label, TextBlock? Number);

    // Wizard steps: 1 Welcome, 2 Prerequisites, 3 Skills, 4 Install, 5 Complete.
    private const int StepInstall = 4;
    private const int StepComplete = 5;

    public MainWindow()
    {
        InitializeComponent();

        // Version stamped by Directory.Build.props - read at runtime, never hardcoded in XAML.
        var info = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        VersionText.Text = $"v{info.Split('+')[0]}";

        _isUpdate = InstallDetector.IsInstalled();
        _installedVersion = _isUpdate ? InstallDetector.GetInstalledVersion() : null;

        SetupLog.Write($"[MainWindow] Started: isUpdate={_isUpdate}, installedVersion={_installedVersion}");

        if (_isUpdate)
        {
            Title = "CC Director Update";
            SubtitleText.Text = "Update";
            Step4Label.Text = "Update";
        }

        Loaded += MainWindow_Loaded;
        ShowStep(1);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isUpdate)
            _ = FetchLatestVersionAsync();
    }

    /// <summary>Build the Welcome step and wire its Uninstall request (issue #257). The step
    /// only shows the button in update mode, so the handler is harmless on a fresh install.</summary>
    private WelcomeStep BuildWelcomeStep()
    {
        var step = new WelcomeStep(_isUpdate, _installedVersion);
        step.UninstallRequested += OnUninstallRequested;
        return step;
    }

    /// <summary>
    /// Run the engine uninstaller for the detected role (issue #257). The Gateway role is a
    /// superset; we pick it only when a Gateway install is actually present so a Workstation box
    /// never tries to stop a tray app it does not have. Data under the per-user root is preserved.
    /// Entry-point boundary: owns the try-catch and shows a friendly result.
    /// </summary>
    private async void OnUninstallRequested(object? sender, EventArgs e)
    {
        var layout = InstallLayout.Default();
        var role = Directory.Exists(layout.GatewayDir) ? InstallRole.Gateway : InstallRole.Workstation;
        SetupLog.Write($"[MainWindow] OnUninstallRequested: role={role}");

        var detail = role == InstallRole.Gateway
            ? "the app, the CLI tools, the PATH entry, the CC Director skills, scheduled tasks, the Gateway autostart, and the Tailscale mapping"
            : "the app, the CLI tools, the PATH entry, the CC Director skills, and scheduled tasks";
        var confirm = MessageBox.Show(this,
            $"Uninstall CC Director?\n\nThis removes {detail}.\n\n" +
            $"Your data (config, vault, sign-ins, recordings) is preserved under:\n{layout.LocalRoot}\n\nContinue?",
            "Uninstall CC Director",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            SetupLog.Write("[MainWindow] OnUninstallRequested: cancelled by user");
            return;
        }

        BackButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        try
        {
            var report = await Task.Run(() => new Uninstaller(layout).Apply(role));
            SetupLog.Write($"[MainWindow] Uninstall done: success={report.Success}, steps={report.Steps.Count}, errors={report.Errors.Count}");

            var summary = report.Success
                ? $"Uninstall complete.\n\nYour data was preserved at:\n{layout.LocalRoot}"
                : $"Uninstall finished with {report.Errors.Count} issue(s):\n\n - "
                    + string.Join("\n - ", report.Errors)
                    + $"\n\nYour data was preserved at:\n{layout.LocalRoot}";
            MessageBox.Show(this, summary, "Uninstall CC Director",
                MessageBoxButton.OK, report.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            Close();
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] OnUninstallRequested FAILED: {ex}");
            MessageBox.Show(this, $"Uninstall failed:\n\n{ex.Message}", "Uninstall CC Director",
                MessageBoxButton.OK, MessageBoxImage.Error);
            NextButton.IsEnabled = true;
        }
    }

    private async Task FetchLatestVersionAsync()
    {
        SetupLog.Write("[MainWindow] FetchLatestVersionAsync: checking for latest release");

        try
        {
            var release = await new ReleaseSource().FetchLatestAsync(CancellationToken.None);
            _latestVersion = release.Manifest.Version;
            SetupLog.Write($"[MainWindow] FetchLatestVersionAsync: latestVersion={_latestVersion}");
            _welcomeStep?.UpdateVersionInfo(_installedVersion, _latestVersion);
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] FetchLatestVersionAsync FAILED: {ex.Message}");
        }
    }

    private List<StepUI> GetStepUIs() =>
    [
        new(Step1Circle, Step1Label, null),
        new(Step2Circle, Step2Label, Step2Num),
        new(Step3Circle, Step3Label, Step3Num),
        new(Step4Circle, Step4Label, Step4Num),
        new(Step5Circle, Step5Label, Step5Num),
    ];

    private Border[] GetLines() => [Line12, Line23, Line34, Line45];

    private void ShowStep(int step)
    {
        SetupLog.Write($"[MainWindow] ShowStep: step={step}");
        _currentStep = step;

        UpdateSidebar();
        UpdateNavButtons();

        StepContent.Content = step switch
        {
            1 => _welcomeStep ??= BuildWelcomeStep(),
            2 => _prerequisitesStep ??= new PrerequisitesStep(OnPrerequisitesChecked, _isUpdate),
            3 => _skillsStep ??= new SkillsStep(_isUpdate),
            4 => _installStep ??= new InstallStep(),
            5 => _completeStep ??= new CompleteStep(_installedCount, _skippedCount, _installPath, _directorExePath, _isUpdate, _alreadyUpToDate, _cachedPrep?.Version),
            _ => null
        };

        if (step == StepInstall && _isUpdate)
            _installStep?.SetUpdateMode();

        if (step == 2)
            _prerequisitesStep?.RunChecks();

        if (step == StepInstall)
            _ = RunInstallAsync();
    }

    private void UpdateSidebar()
    {
        var stepUIs = GetStepUIs();
        var lines = GetLines();
        var accentBrush = (SolidColorBrush)FindResource("AccentBrush");
        var successBrush = (SolidColorBrush)FindResource("SuccessBrush");
        var inactiveBrush = (SolidColorBrush)FindResource("StepInactive");
        var dimBrush = (SolidColorBrush)FindResource("DimText");

        for (int i = 0; i < stepUIs.Count; i++)
        {
            var stepNum = i + 1;
            var ui = stepUIs[i];

            if (stepNum < _currentStep)
            {
                ui.Circle.Background = successBrush;
                ui.Label.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
                if (ui.Number != null) ui.Number.Foreground = Brushes.White;
            }
            else if (stepNum == _currentStep)
            {
                ui.Circle.Background = accentBrush;
                ui.Label.Foreground = Brushes.White;
                if (ui.Number != null) ui.Number.Foreground = Brushes.White;
            }
            else
            {
                ui.Circle.Background = inactiveBrush;
                ui.Label.Foreground = dimBrush;
                if (ui.Number != null) ui.Number.Foreground = dimBrush;
            }

            if (i < lines.Length)
            {
                lines[i].Background = stepNum < _currentStep ? successBrush : inactiveBrush;
            }
        }
    }

    private void UpdateNavButtons()
    {
        BackButton.Visibility = _currentStep > 1 && _currentStep < StepComplete
            ? Visibility.Visible : Visibility.Collapsed;

        if (_currentStep == StepComplete)
        {
            NextButton.Content = "Close";
        }
        else if (_currentStep == StepInstall)
        {
            NextButton.Content = _isUpdate ? "Updating..." : "Installing...";
            NextButton.IsEnabled = false;
        }
        else if (_currentStep == 2)
        {
            NextButton.Content = "Next";
            UpdateNextButtonForPrereqs();
        }
        else
        {
            NextButton.Content = "Next";
            NextButton.IsEnabled = true;
        }
    }

    private void OnPrerequisitesChecked(List<PrerequisiteInfo> prerequisites)
    {
        _prerequisites = prerequisites;
        UpdateNextButtonForPrereqs();
    }

    private void UpdateNextButtonForPrereqs()
    {
        if (_currentStep != 2) return;

        var allRequiredMet = _prerequisites.Count == 0 ||
            _prerequisites.Where(p => p.IsRequired).All(p => p.IsFound);
        NextButton.IsEnabled = allRequiredMet;
    }

    private async Task RunInstallAsync()
    {
        SetupLog.Write("[MainWindow] RunInstallAsync: starting");

        var runner = new EngineInstallRunner
        {
            OnProcessBlocking = OnProcessBlockingAsync,
            OnToolsInstalled = c => Dispatcher.BeginInvoke(() => _installStep?.SetToolsInstalledCount(c)),
        };
        _installPath = runner.BinDir;
        _directorExePath = runner.AppExePath;

        _installStep?.SetStatus("Fetching release info...");
        _installStep?.ShowProgress();

        EngineInstallRunner.Prep prep;
        try
        {
            prep = await runner.PrepareAsync();
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] RunInstallAsync: prepare FAILED: {ex.Message}");
            _installStep?.SetStatus("ERROR: Could not fetch release info from GitHub.");
            NextButton.Content = "Retry";
            NextButton.IsEnabled = true;
            return;
        }

        _cachedPrep = prep;
        VersionText.Text = prep.Version;
        _installStep?.SetItems(prep.Items);

        // A Gateway install adds the tray app + Cockpit (done by the gateway phase below); show that
        // card up front so the user can see it's part of THIS install, not just a Workstation set.
        if (_role == InstallRole.Gateway && !_isUpdate)
            _installStep?.ShowGatewaySection();

        if (_isUpdate && prep.IsUpToDate)
        {
            SetupLog.Write($"[MainWindow] Already up to date: {prep.Version}");
            _alreadyUpToDate = true;
            _installStep?.SetUpToDate(prep.Version);
            if (_installStep != null)
                _installStep.OnRepairRequested += OnRepairRequested;
            _installedCount = 0;
            _skippedCount = 0;
            NextButton.Content = "Next";
            NextButton.IsEnabled = true;
            return;
        }

        _installStep?.SetStatus(_isUpdate && _installedVersion != null
            ? $"Updating from v{_installedVersion.Split('+')[0]} to {prep.Version}..."
            : $"Installing {prep.Version}...");

        await RunEngineApplyAsync(runner, prep, repair: false);
    }

    private void OnRepairRequested()
    {
        SetupLog.Write("[MainWindow] OnRepairRequested: user requested repair reinstall");
        _alreadyUpToDate = false;
        _ = RunRepairAsync();
    }

    private async Task RunRepairAsync()
    {
        SetupLog.Write("[MainWindow] RunRepairAsync: starting forced reinstall");

        NextButton.Content = _isUpdate ? "Updating..." : "Installing...";
        NextButton.IsEnabled = false;

        var runner = new EngineInstallRunner
        {
            OnProcessBlocking = OnProcessBlockingAsync,
            OnToolsInstalled = c => Dispatcher.BeginInvoke(() => _installStep?.SetToolsInstalledCount(c)),
        };
        _installPath = runner.BinDir;
        _directorExePath = runner.AppExePath;

        var prep = _cachedPrep ?? await runner.PrepareAsync();
        _cachedPrep = prep;

        _installStep?.SetItems(prep.Items);
        if (_role == InstallRole.Gateway && !_isUpdate)
            _installStep?.ShowGatewaySection();
        _installStep?.SetStatus($"Repairing {prep.Version}...");
        _installStep?.ShowProgress();

        await RunEngineApplyAsync(runner, prep, repair: true);
    }

    /// <summary>Apply the prepared release via the engine, install skills, and finalize the UI.</summary>
    private async Task RunEngineApplyAsync(EngineInstallRunner runner, EngineInstallRunner.Prep prep, bool repair)
    {
        var (installed, skipped) = await runner.ApplyAsync(prep);
        _installedCount = installed;
        _skippedCount = skipped;

        _installStep?.SetStatus("Installing skills...");
        var skillItems = _installStep?.GetSkillItems() ?? [];
        if (skillItems.Count > 0)
        {
            await new SkillInstaller().InstallSkillsAsync(skillItems);
            _installStep?.UpdateSkillsStatus();
        }

        var verb = repair ? "Repair complete" : "Done";
        _installStep?.SetStatus($"{verb} - {installed} installed, {skipped} skipped");
        SetupLog.Write($"[MainWindow] RunEngineApplyAsync: repair={repair}, installed={installed}, skipped={skipped}");

        // Gateway role: finish with the Gateway tray app + Cockpit by shelling the CLI (decision D2:
        // the CLI is the single source of truth). Per-user like everything else - no elevation, no
        // UAC. Updates refresh the per-user layer only; the resident tray app self-updates itself.
        if (_role == InstallRole.Gateway && !_isUpdate)
            await RunGatewayTrayInstallAsync(prep);

        NextButton.Content = "Next";
        NextButton.IsEnabled = true;
    }

    private async Task RunGatewayTrayInstallAsync(EngineInstallRunner.Prep prep)
    {
        SetupLog.Write("[MainWindow] RunGatewayTrayInstallAsync: shelling the CLI");
        _installStep?.SetStatus("Installing the Gateway tray app...");
        _installStep?.SetGatewayInstalling();

        try
        {
            var launcher = new GatewayTrayLauncher(new ReleaseSource());
            var result = await launcher.RunAsync(
                prep.Release,
                line => Dispatcher.BeginInvoke(() => _installStep?.SetStatus($"Gateway: {line}")));

            _gatewayResultMessage = result.Message;
            // result.Message already carries the tailnet Cockpit URL (never localhost).
            _installStep?.SetStatus(result.Message);
            if (result.Success) _installStep?.SetGatewayDone();
            else _installStep?.SetGatewayFailed();
            SetupLog.Write($"[MainWindow] Gateway install success={result.Success}: {result.Message}");
        }
        catch (Exception ex)
        {
            _gatewayResultMessage = $"Gateway install error: {ex.Message}";
            _installStep?.SetStatus(_gatewayResultMessage);
            _installStep?.SetGatewayFailed();
            SetupLog.Write($"[MainWindow] RunGatewayTrayInstallAsync FAILED: {ex.Message}");
        }
    }

    private Task<bool> OnProcessBlockingAsync(string processName)
    {
        var result = MessageBox.Show(
            this,
            "CC Director is currently running and cannot be updated.\n\n" +
            "Please close CC Director and click OK to retry,\n" +
            "or click Cancel to skip updating the main application.",
            "CC Director is Running",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        return Task.FromResult(result == MessageBoxResult.OK);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
            ShowStep(_currentStep - 1);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep == StepComplete)
        {
            Close();
            return;
        }

        if (_currentStep == StepInstall && NextButton.Content?.ToString() == "Retry")
        {
            _installStep = null;
            ShowStep(StepInstall);
            return;
        }

        if (_currentStep < StepComplete)
        {
            // Leaving Welcome: capture the chosen role and rebuild all forward steps fresh.
            if (_currentStep == 1)
            {
                _role = _welcomeStep?.SelectedRole ?? InstallRole.Workstation;
                SetupLog.Write($"[MainWindow] role selected: {_role}");
                _prerequisitesStep = null;
                _skillsStep = null;
                _installStep = null;
                _completeStep = null;
            }

            // Leaving Install: rebuild Complete with the final counts.
            if (_currentStep == StepInstall)
                _completeStep = null;

            ShowStep(_currentStep + 1);
        }
    }
}
