using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;
using CcDirectorSetup.Steps;

namespace CcDirectorSetup;

public partial class MainWindow : Window
{
    // 3-step flow, identical to the Windows wizard (the master, issue #1807):
    // Welcome -> Install -> Complete. A fresh install makes no decision: no profile, no role, no
    // account, no gateway - connecting a gateway is a later, optional act done from inside the app.
    // The Skills step went with the skill installer (issue 995): skills are held on the Gateway and
    // fetched, so nothing is placed on the machine and there is nothing here to choose.
    //
    // The Prerequisites step is gone too. It existed for the one row that could block - the .NET
    // runtime - and neither app needs one now: macOS always published self-contained and the Windows
    // executables now do the same. What was left was advice this wizard could not act on or re-check,
    // and the Director detects agent tools itself in a wizard that can add what it finds to your board.
    private const int StepWelcome = 1, StepInstall = 2, StepComplete = 3;

    private int _currentStep = StepWelcome;
    private int _installedCount;
    private int _skippedCount;
    private IReadOnlyList<string> _skippedNames = [];
    private IReadOnlyList<string> _skippedReasons = [];
    private string _installPath = "";

    private readonly bool _isUpdate;
    private readonly string? _installedVersion;
    private bool _alreadyUpToDate;
    private string? _latestVersion;

    private readonly EngineInstallRunner _runner = new();
    private EngineInstallRunner.Prep? _cachedPrep;

    private readonly InstallRole _role = InstallRole.Workstation;

    private WelcomeStep? _welcomeStep;
    private InstallStep? _installStep;
    private CompleteStep? _completeStep;

    private readonly record struct StepUI(Border Circle, TextBlock Label, TextBlock? Number);

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

        // Role is a first-install choice the update wizard does not re-ask. Detect what is
        // already installed (Windows parity; macOS is Workstation-only today so this always
        // answers Workstation here).
        if (_isUpdate)
            _role = InstalledRoleDetector.Detect(InstallLayout.Default());

        if (_isUpdate)
        {
            Title = "DevThrottle Update";
            SubtitleText.Text = "Update";
            Step2Label.Text = "Update";
        }

        Loaded += MainWindow_Loaded;
        ShowStep(StepWelcome);
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        if (_isUpdate) await FetchLatestVersionAsync();
    }

    private async Task FetchLatestVersionAsync()
    {
        try
        {
            var prep = await _runner.PrepareAsync();
            _cachedPrep = prep;
            _latestVersion = prep.Version;
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
    ];

    private Border[] GetLines() => [Line12, Line23];

    private void ShowStep(int step)
    {
        SetupLog.Write($"[MainWindow] ShowStep: step={step}");
        _currentStep = step;

        UpdateSidebar();
        UpdateNavButtons();

        StepContent.Content = step switch
        {
            StepWelcome => _welcomeStep ??= BuildWelcomeStep(),
            StepInstall => _installStep ??= new InstallStep(),
            StepComplete => _completeStep ??= new CompleteStep(_installedCount, _skippedCount, _installPath, _isUpdate, _alreadyUpToDate, _latestVersion, BuildAgentNotice(), _skippedNames, IsReadyToGo(), _skippedReasons),
            _ => null
        };

        if (step == StepInstall && _isUpdate)
            _installStep?.SetUpdateMode();

        if (step == StepInstall)
            _ = RunGuardedAsync(RunInstallAsync, "install");
    }

    /// <summary>
    /// Run an install or repair so that an exception can never leave the window on a disabled
    /// "Installing..." button with no message. That happened when a download threw out of the Director
    /// placement: the fire-and-forget task swallowed it, nothing was logged, nothing was reported (#3311).
    /// </summary>
    private async Task RunGuardedAsync(Func<Task> run, string step)
    {
        try
        {
            await run();
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] {step} FAILED with an exception: {ex}");
            var sent = await WizardErrorReport.SendAsync("wizard", step, $"The {step} stopped: {ex.GetType().Name}: {ex.Message}", ex);
            ShowUnexpectedError(ex, sent);
        }
    }

    /// <summary>Say on screen that the wizard hit an error, where its log is, and offer Retry.</summary>
    public void ShowUnexpectedError(Exception ex, bool? reportSent = null)
    {
        var reported = reportSent switch
        {
            true => " A report of this error was sent to DevThrottle.",
            false => " A report could not be sent.",
            null => "",
        };
        _installStep?.SetStatus($"ERROR: {ex.Message}.{reported} The log is at {SetupLog.Path}");
        NextButton.Content = "Retry";
        NextButton.IsEnabled = true;
    }

    private void UpdateSidebar()
    {
        var stepUIs = GetStepUIs();
        var lines = GetLines();
        var accentBrush = SolidColorBrush.Parse("#007ACC");
        var successBrush = SolidColorBrush.Parse("#22C55E");
        var inactiveBrush = SolidColorBrush.Parse("#3C3C3C");
        var dimBrush = SolidColorBrush.Parse("#888888");
        var whiteBrush = SolidColorBrush.Parse("#CCCCCC");

        for (int i = 0; i < stepUIs.Count; i++)
        {
            var stepNum = i + 1;
            var ui = stepUIs[i];

            if (stepNum < _currentStep)
            {
                ui.Circle.Background = successBrush;
                ui.Label.Foreground = whiteBrush;
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
                lines[i].Background = stepNum < _currentStep ? successBrush : inactiveBrush;
        }
    }

    private void UpdateNavButtons()
    {
        BackButton.IsVisible = _currentStep > StepWelcome && _currentStep < StepComplete;

        if (_currentStep == StepComplete)
        {
            NextButton.Content = "Close";
            NextButton.IsEnabled = true;
        }
        else if (_currentStep == StepInstall)
        {
            NextButton.Content = _isUpdate ? "Updating..." : "Installing...";
            NextButton.IsEnabled = false;
        }
        else
        {
            NextButton.Content = "Next";
            NextButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// The one line the Complete screen shows about the MACHINE rather than about this install: no
    /// coding agent is on it, so the board has nothing to run. Null when an agent is present. Word
    /// for word what the Windows wizard says.
    /// </summary>
    private static string? BuildAgentNotice() =>
        AgentPresence.AnyAgent()
            ? null
            : "No coding agent is set up yet, so your board has nothing to run. "
              + "DevThrottle checks your tools when it opens and can add the ones it finds.";

    /// <summary>
    /// Whether the Complete screen may say the user is ready to go. The rule lives in the shared
    /// <see cref="InstallCompletion.IsReadyToGo"/> so both wizards reach the same verdict - this
    /// screen used to decide for itself, which is how it came to say "Everything went perfectly"
    /// on a machine with nothing to run.
    /// </summary>
    private bool IsReadyToGo() => InstallCompletion.IsReadyToGo(_skippedCount, AgentPresence.AnyAgent());

    private async Task RunInstallAsync()
    {
        SetupLog.Write("[MainWindow] RunInstallAsync: starting");
        _installPath = _runner.DirectorPath;

        _installStep?.SetStatus("Fetching release info...");
        _installStep?.ShowProgress();

        EngineInstallRunner.Prep prep;
        try
        {
            prep = _cachedPrep ?? await _runner.PrepareAsync();
            _cachedPrep = prep;
        }
        catch (GitHubRateLimitException ex)
        {
            SetupLog.Write($"[MainWindow] RunInstallAsync: prepare FAILED (rate limit): {ex.Message}");
            _ = WizardErrorReport.SendAsync("wizard", "release-fetch", $"GitHub rate limit while fetching the release: {ex.Message}", ex);
            _installStep?.SetNotStarted();
            _installStep?.SetStatus(ex.UserMessage());
            NextButton.Content = "Retry";
            NextButton.IsEnabled = true;
            return;
        }
        // Installing during the minutes between a release being published and its files finishing
        // upload is not an error, and must not read as one: the correct advice is "wait a moment and
        // press Retry", not "could not fetch release info" (issue #1079).
        catch (ReleaseNotReadyException ex)
        {
            SetupLog.Write($"[MainWindow] RunInstallAsync: prepare deferred (release not ready): {ex.Message}");
            _installStep?.SetNotStarted();
            _installStep?.SetStatus(ex.UserMessage());
            NextButton.Content = "Retry";
            NextButton.IsEnabled = true;
            return;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] RunInstallAsync: prepare FAILED: {ex}");
            // Shown on screen, and the most likely first failure on a brand-new machine - it was never reported.
            _ = WizardErrorReport.SendAsync("wizard", "release-fetch", $"Could not fetch release info: {ex.GetType().Name}: {ex.Message}", ex);
            _installStep?.SetNotStarted();
            _installStep?.SetStatus("ERROR: Could not fetch release info from GitHub.");
            NextButton.Content = "Retry";
            NextButton.IsEnabled = true;
            return;
        }

        VersionText.Text = prep.Version;
        _installStep?.SetItems(prep.Items);

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

        await ApplyAndFinishAsync(prep);
    }

    private void OnRepairRequested()
    {
        SetupLog.Write("[MainWindow] OnRepairRequested: user requested repair reinstall");
        _alreadyUpToDate = false;
        _ = RunGuardedAsync(RunRepairAsync, "repair");
    }

    private async Task RunRepairAsync()
    {
        SetupLog.Write("[MainWindow] RunRepairAsync: starting forced reinstall");
        if (_cachedPrep is null) { SetupLog.Write("[MainWindow] RunRepairAsync: no cached prep"); return; }

        NextButton.Content = _isUpdate ? "Updating..." : "Installing...";
        NextButton.IsEnabled = false;

        _installStep?.SetItems(_cachedPrep.Items);
        _installStep?.SetStatus($"Repairing {_cachedPrep.Version}...");
        _installStep?.ShowProgress();

        await ApplyAndFinishAsync(_cachedPrep);
    }

    /// <summary>Run the engine apply (Director + tools bundle), then enable Next.</summary>
    private async Task ApplyAndFinishAsync(EngineInstallRunner.Prep prep)
    {
        var status = new Progress<string>(s => _installStep?.SetStatus(s));
        var (installed, skipped) = await _runner.ApplyAsync(prep, status);
        _installedCount = installed;
        _skippedCount = skipped;
        _skippedNames = ComponentDisplayName.For(
            prep.Items.Where(i => i.Status is "Skipped" or "Failed").Select(i => i.Name));
        // WHY, as the engine already worked it out. Without this the Complete screen named the component
        // and dropped the reason, sending the user to a log for a sentence we already had.
        _skippedReasons = prep.Items
            .Where(i => i.Status is "Skipped" or "Failed" && !string.IsNullOrWhiteSpace(i.StatusDetail))
            .Select(i => $"{ComponentDisplayName.For(i.Name)}: {i.StatusDetail}")
            .ToList();

        _installStep?.SetStatus($"Done - {installed} installed, {skipped} skipped");
        SetupLog.Write($"[MainWindow] ApplyAndFinishAsync: installed={installed}, skipped={skipped}");

        NextButton.Content = "Next";
        NextButton.IsEnabled = true;
    }

    /// <summary>Build the Welcome step and wire its Uninstall request (issue #257). The step
    /// only shows the button in update mode, so the handler is harmless on a fresh install.</summary>
    private WelcomeStep BuildWelcomeStep()
    {
        var step = new WelcomeStep(_isUpdate, _installedVersion, _role);
        step.UninstallRequested += OnUninstallRequested;
        return step;
    }

    /// <summary>
    /// Show the in-wizard uninstall flow (confirm -> live progress -> completion) for the detected
    /// role, mirroring the Windows wizard. macOS is Workstation-only, but the same
    /// Gateway-presence probe keeps the two windows identical. Data under the per-user root is
    /// preserved unless the user opts in to the wipe.
    /// </summary>
    private void OnUninstallRequested(object? sender, EventArgs e)
    {
        var layout = InstallLayout.Default();
        var role = Directory.Exists(layout.GatewayDir) ? InstallRole.Gateway : InstallRole.Workstation;
        SetupLog.Write($"[MainWindow] OnUninstallRequested: showing uninstall step, role={role}");

        var step = new UninstallStep(layout, role);
        step.Cancelled += (_, _) =>
        {
            // Back to the Welcome screen with the normal wizard chrome restored.
            StepIndicators.IsVisible = true;
            NavBar.IsVisible = true;
            ShowStep(StepWelcome);
        };
        step.CloseRequested += (_, _) => Close();

        // Hand the whole content area to the uninstall flow; it owns its own buttons, so hide the
        // step rail and the Back/Next nav bar while it is shown.
        StepIndicators.IsVisible = false;
        NavBar.IsVisible = false;
        StepContent.Content = step;
    }

    private void BackButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentStep > StepWelcome)
            ShowStep(_currentStep - 1);
    }

    private void NextButton_Click(object? sender, RoutedEventArgs e)
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
            ShowStep(_currentStep + 1);
    }
}
