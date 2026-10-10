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
    private int _installedCount;
    private int _skippedCount;
    private string _installPath = "";
    private string _directorExePath = "";
    // A fresh install always lays down the Director set only - no role choice, no account (issue #1807),
    // so the fresh-install role is always Workstation (Director-only). Update mode overrides this from
    // disk in the constructor via InstalledRoleDetector so an existing Gateway host stays a Gateway host.
    private InstallRole _role = InstallRole.Workstation;
    private string? _gatewayResultMessage;
    // The Gateway refresh failure reason (null when it succeeded), carried onto the Complete screen so
    // a failed Gateway update tells the user WHY, not just that a component did not install.
    private string? _gatewayFailureReason;

    private readonly bool _isUpdate;
    private readonly string? _installedVersion;
    private bool _alreadyUpToDate;
    private string? _latestVersion;
    private EngineInstallRunner.Prep? _cachedPrep;

    private WelcomeStep? _welcomeStep;
    private InstallStep? _installStep;
    private CompleteStep? _completeStep;

    private readonly record struct StepUI(Border Circle, TextBlock Label, TextBlock? Number);

    // Wizard steps: 1 Welcome, 7 Install, 8 Complete. There is one linear path for every install and
    // update - the installer always lays down the Director set with no account gate (issue #1807).
    // Ids 3 (the old Gateway-only Sign-in step) and 5 (the old mandatory gateway-join Connect step)
    // were removed with the account gate, and id 6 (the Skills screen) was removed because it showed
    // internal identifiers as tick-boxes nobody could tick and asked for no decision - the installer
    // places no skills at all now (issue 995).
    //
    // Id 2, the Prerequisites screen, is gone as well. It existed for one row with teeth: the .NET
    // runtime, which the app could not start without. The Windows executables now carry their own
    // runtime, so nothing this installer places needs anything already on the machine and there is
    // nothing left to gate on. What remained was advice this wizard could not act on, could not
    // re-check, and (on macOS) could not even install - and the Director already detects agent tools
    // in a wizard that can add what it finds to your board.
    //
    // The surviving ids keep their old numbers so this switch stays stable.
    private const int StepInstall = 7;
    private const int StepComplete = 8;

    // Step ordering lives in WizardStepFlow so it is unit-testable without constructing this WPF window.
    // These thin members bind to it - there is no parallel navigation logic in this window.
    private static List<int> VisibleSteps() => WizardStepFlow.VisibleSteps();
    private static int NextStep(int step) => WizardStepFlow.NextStep(step);
    private static int PrevStep(int step) => WizardStepFlow.PrevStep(step);

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

        // Role is a first-install choice the update wizard does not re-ask. Detect what is already
        // installed so a Gateway host stays a Gateway host on update (refresh Gateway + Cockpit and
        // re-assert the managed tray launch), instead of defaulting to a Director-only Workstation refresh.
        if (_isUpdate)
            _role = InstalledRoleDetector.Detect(InstallLayout.Default());
        SetupLog.Write($"[MainWindow] install role: {_role}");

        if (_isUpdate)
        {
            Title = "DevThrottle Update";
            SubtitleText.Text = "Update";
            Step7Label.Text = "Update";
        }

        Loaded += MainWindow_Loaded;

        // Launched by the Windows "Uninstall" button in Settings > Apps, which runs the
        // UninstallString we registered. Go straight to the uninstall flow: a person who pressed
        // Uninstall in Settings and landed on a Welcome screen offering to INSTALL would
        // reasonably think the button was broken.
        if (LaunchedToUninstall())
        {
            SetupLog.Write("[MainWindow] launched with the uninstall switch - going straight to the uninstall flow");
            OnUninstallRequested(this, EventArgs.Empty);
            return;
        }

        ShowStep(1);
    }

    /// <summary>True when this process was started with the uninstall switch.</summary>
    private static bool LaunchedToUninstall() =>
        Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(a => string.Equals(a.TrimStart('-', '/'), "uninstall", StringComparison.OrdinalIgnoreCase));

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isUpdate)
            _ = FetchLatestVersionAsync();
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
    /// role - no raw MessageBox pop-ups (issue: nicer uninstall progress). The Gateway role is a
    /// superset; we pick it only when a Gateway install is actually present so a Workstation box
    /// never tries to stop a tray app it does not have. Data under the per-user root is preserved.
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
            StepIndicators.Visibility = Visibility.Visible;
            NavBar.Visibility = Visibility.Visible;
            ShowStep(1);
        };
        step.CloseRequested += (_, _) => Close();

        // Hand the whole content area to the uninstall flow; it owns its own buttons, so hide the
        // step rail and the Back/Next nav bar while it is shown.
        StepIndicators.Visibility = Visibility.Collapsed;
        NavBar.Visibility = Visibility.Collapsed;
        StepContent.Content = step;
    }

    private async Task FetchLatestVersionAsync()
    {
        SetupLog.Write("[MainWindow] FetchLatestVersionAsync: checking the release for this setup executable");

        try
        {
            // Show the version this setup exe will actually install (its matching pre-release when
            // this is a pre-release build), so the welcome screen is not misleading (issue #1294).
            var release = await new ReleaseSource().FetchReleaseForSetupAsync(CancellationToken.None);
            _latestVersion = release.Manifest.Version;
            SetupLog.Write($"[MainWindow] FetchLatestVersionAsync: latestVersion={_latestVersion}");
            _welcomeStep?.UpdateVersionInfo(_installedVersion, _latestVersion);
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] FetchLatestVersionAsync FAILED: {ex.Message}");
            _ = WizardProgressReport.Error("release-fetch", $"Windows setup wizard could not read the latest version for the welcome screen: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    // The three sidebar rows in display order. Historical ids 2-6 are retired; the remaining circles
    // are renumbered 1..3 at runtime. This list is aligned with WizardStepFlow.VisibleSteps().
    private List<StepUI> GetStepUIs() =>
    [
        new(Step1Circle, Step1Label, null),
        new(Step7Circle, Step7Label, Step7Num),
        new(Step8Circle, Step8Label, Step8Num),
    ];

    // The two connector lines between the three rows, in order.
    private Border[] GetLines() => [Line17, Line78];

    private void ShowStep(int step)
    {
        SetupLog.Write($"[MainWindow] ShowStep: step={step}");
        _currentStep = step;

        UpdateSidebar();
        UpdateNavButtons();

        StepContent.Content = step switch
        {
            1 => _welcomeStep ??= BuildWelcomeStep(),
            StepInstall => _installStep ??= new InstallStep(),
            StepComplete => _completeStep ??= new CompleteStep(_installedCount, _skippedCount, _installPath, _directorExePath, _isUpdate, _alreadyUpToDate, _cachedPrep?.Version, _gatewayFailureReason, BuildAgentNotice(), IsReadyToGo(), SkippedComponentNames(), SkippedComponentReasons()),
            _ => null
        };

        if (step == StepInstall && _isUpdate)
            _installStep?.SetUpdateMode();

        if (step == StepInstall)
            _ = RunInstallAsync();
    }

    private void UpdateSidebar()
    {
        var stepUIs = GetStepUIs();
        var lines = GetLines();
        var visible = VisibleSteps();
        var accentBrush = (SolidColorBrush)FindResource("AccentBrush");
        var successBrush = (SolidColorBrush)FindResource("SuccessBrush");
        var inactiveBrush = (SolidColorBrush)FindResource("StepInactive");
        var dimBrush = (SolidColorBrush)FindResource("DimText");
        var doneLabelBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

        // One linear path (issue #1807): every row is always shown, so progress is just the current
        // step's position in the rail. stepUIs is aligned with VisibleSteps(), so row at position i is
        // step id visible[i]; the circle shows i+1.
        var currentPos = visible.IndexOf(_currentStep);

        for (int pos = 0; pos < stepUIs.Count; pos++)
        {
            var ui = stepUIs[pos];
            if (ui.Number != null) ui.Number.Text = (pos + 1).ToString();

            if (pos < currentPos)
            {
                ui.Circle.Background = successBrush;
                ui.Label.Foreground = doneLabelBrush;
                if (ui.Number != null) ui.Number.Foreground = Brushes.White;
            }
            else if (pos == currentPos)
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
        }

        // Line i sits between row i and row i+1; it is "done" once the current step is past row i.
        for (int i = 0; i < lines.Length; i++)
            lines[i].Background = i < currentPos ? successBrush : inactiveBrush;
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
        else
        {
            // Welcome and every other step: nothing to gate on. The Welcome screen makes no
            // choice anymore (issue #1807), so Next is always enabled here.
            NextButton.Content = "Next";
            NextButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// The one line the Complete screen shows about the MACHINE rather than about this install: no
    /// coding agent is on it, so the board has nothing to run. Null when an agent is present.
    ///
    /// This is all that remains of the old capability notice. The other lines it produced were about
    /// prerequisites the wizard no longer checks, and the Director's own tool detection is where a
    /// missing agent can actually be dealt with.
    /// </summary>
    private static string? BuildAgentNotice() =>
        AgentPresence.AnyAgent()
            ? null
            : "No coding agent is set up yet, so your board has nothing to run. "
              + "DevThrottle checks your tools when it opens and can add the ones it finds.";

    /// <summary>
    /// Whether the Complete screen is allowed to say the user is ready to go. The rule lives in
    /// <see cref="InstallCompletion.IsReadyToGo"/>; this only gathers the two facts it needs.
    /// A machine with no coding agent at all has nothing to run, so it is not ready however
    /// cleanly the install itself went.
    /// </summary>
    private bool IsReadyToGo() => InstallCompletion.IsReadyToGo(_skippedCount, AgentPresence.AnyAgent());

    /// <summary>Which components did not install, by name, so the Complete screen can say WHICH one -
    /// a count is not something the reader can act on.</summary>
    private IReadOnlyList<string> SkippedComponentNames() =>
        ComponentDisplayName.For(
            _cachedPrep?.Items
                .Where(i => i.Status is "Skipped" or "Failed")
                .Select(i => i.Name) ?? []);

    /// <summary>
    /// WHY each component failed, as the engine already worked it out. Every failure path sets
    /// StatusDetail; the install card shows it, and without this the Complete screen and the generated
    /// issue lost it again - so a report said "Launcher did not install" and nothing more, which is the
    /// information-loss this whole change exists to remove.
    /// </summary>
    private IReadOnlyList<string> SkippedComponentReasons() =>
        _cachedPrep?.Items
            .Where(i => i.Status is "Skipped" or "Failed" && !string.IsNullOrWhiteSpace(i.StatusDetail))
            .Select(i => $"{ComponentDisplayName.For(i.Name)}: {i.StatusDetail}")
            .ToList() ?? [];

    private async Task RunInstallAsync()
    {
        SetupLog.Write("[MainWindow] RunInstallAsync: starting");
        WizardProgressReport.Start(_role, _isUpdate);

        var runner = new EngineInstallRunner
        {
            OnProcessBlocking = OnProcessBlockingAsync,
            CreateDesktopShortcut = InstallCompletion.CreatesDesktopShortcut(_isUpdate),
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
        catch (GitHubRateLimitException ex)
        {
            SetupLog.Write($"[MainWindow] RunInstallAsync: prepare FAILED (rate limit): {ex.Message}");
            WizardProgressReport.Failed(_role, "release info: GitHub rate limit", ex.Message);
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
            WizardProgressReport.Failed(_role, "release info: release not ready", ex.Message);
            _installStep?.SetNotStarted();
            _installStep?.SetStatus(ex.UserMessage());
            NextButton.Content = "Retry";
            NextButton.IsEnabled = true;
            return;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] RunInstallAsync: prepare FAILED: {ex.Message}");
            WizardProgressReport.Failed(_role, "could not fetch release info", ex.ToString());
            _installStep?.SetNotStarted();
            _installStep?.SetStatus("ERROR: Could not fetch release info from GitHub.");
            NextButton.Content = "Retry";
            NextButton.IsEnabled = true;
            return;
        }

        _cachedPrep = prep;
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

            NextButton.IsEnabled = false;

            // A Gateway host re-asserts its Gateway + Cockpit even when the Director is already current:
            // the Cockpit can be version-drifted, or the managed tray launch / autostart can be broken
            // (the gateway phase re-extracts the Cockpit, relaunches the tray managed, and re-registers
            // the autostart Run key with --managed). This is what makes re-running the installer reliably
            // heal a Gateway host whose Cockpit is stuck on "Cockpit starting...". It has no card of its
            // own: this wizard installs the Director, and the Gateway is a separate do-it-yourself install.
            if (_role == InstallRole.Gateway)
                await RunGatewayTrayInstallAsync(prep);

            // Re-assert the launcher on an already-current machine too. It is idempotent (start if it is
            // not up, re-register autostart) and it is what makes the launcher card on this screen tell
            // the truth instead of sitting at "Pending" forever on the up-to-date path.
            //
            // A FAILURE here counts. This path used to discard the result and show "Already Up to Date"
            // even when the launcher's health, identity or autostart check had just failed - the same
            // false success the non-up-to-date path correctly refuses.
            if (OperatingSystem.IsWindows() && !await StartLauncherAsync())
            {
                _skippedCount++;
                _alreadyUpToDate = false;
                NextButton.Content = "Retry";
                NextButton.IsEnabled = true;
                return;
            }

            WizardProgressReport.Done(_role, _isUpdate, $"already up to date at {prep.Version}");
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
            CreateDesktopShortcut = InstallCompletion.CreatesDesktopShortcut(_isUpdate),
        };
        _installPath = runner.BinDir;
        _directorExePath = runner.AppExePath;

        var prep = _cachedPrep ?? await runner.PrepareAsync();
        _cachedPrep = prep;

        _installStep?.SetItems(prep.Items);
        _installStep?.SetStatus($"Repairing {prep.Version}...");
        _installStep?.ShowProgress();

        await RunEngineApplyAsync(runner, prep, repair: true);
    }

    /// <summary>Apply the prepared release via the engine and finalize the UI.</summary>
    private async Task RunEngineApplyAsync(EngineInstallRunner runner, EngineInstallRunner.Prep prep, bool repair)
    {
        var (installed, skipped) = await runner.ApplyAsync(prep);
        _installedCount = installed;
        _skippedCount = skipped;

        var verb = repair ? "Repair complete" : "Done";
        _installStep?.SetStatus($"{verb} - {installed} installed, {skipped} skipped");
        SetupLog.Write($"[MainWindow] RunEngineApplyAsync: repair={repair}, installed={installed}, skipped={skipped}");

        // Gateway machine: finish with the Gateway tray app + Cockpit by shelling the CLI (decision D2:
        // the CLI is the single source of truth). Per-user like everything else - no elevation, no UAC.
        // Runs on update too (role detected from disk): it refreshes the Gateway exe + Cockpit and
        // re-asserts the managed tray launch + autostart Run key, so a Gateway host never drifts into a
        // half-updated, unmanaged state where the Cockpit stops coming up.
        if (_role == InstallRole.Gateway)
            await RunGatewayTrayInstallAsync(prep);

        // Start the always-on Launcher tray app (Windows, both roles) AFTER the Gateway phase, so the
        // order matches the CLI. Hard-fail like the CLI: if it does not come up, the install is not
        // "done" - surface the error and offer Retry rather than reporting a clean success while the
        // launcher is dead.
        if (OperatingSystem.IsWindows() && !await StartLauncherAsync())
        {
            NextButton.Content = "Retry";
            NextButton.IsEnabled = true;
            return;
        }

        WizardProgressReport.Done(_role, _isUpdate, $"{prep.Version}, {installed} installed, {skipped} skipped{(repair ? " (repair)" : "")}");
        NextButton.Content = "Next";
        NextButton.IsEnabled = true;
    }

    /// <summary>
    /// Start the installed Launcher tray app in managed mode and verify it is healthy and
    /// autostart-registered (the runner placed cc-launcher.exe but does not start it). Returns false
    /// on any failure so the caller can hard-fail the install with a Retry, mirroring the CLI.
    /// </summary>
    private async Task<bool> StartLauncherAsync()
    {
        SetupLog.Write("[MainWindow] StartLauncherAsync");
        _installStep?.SetLauncherStarting();
        try
        {
            // The status line carries the wait's own commentary. A cold first start of the launcher can
            // run well past a minute while it unpacks itself, and a screen that says nothing for that
            // long reads as frozen - which is how a slow start came to look like a failed one (#1152).
            var progress = new Progress<string>(note => _installStep?.SetStatus(note));
            var result = await new LauncherTrayInstaller(InstallLayout.Default()).InstallAsync(progress: progress);
            foreach (var s in result.Steps) SetupLog.Write($"[MainWindow]   launcher: {s}");
            SetupLog.Write($"[MainWindow] launcher start success={result.Success}: {result.Message}");
            if (result.Success)
            {
                _installStep?.SetLauncherRunning();
                return true;
            }
            _installStep?.SetLauncherFailed();
            _installStep?.SetStatus($"ERROR: Launcher tray app failed to start. {result.Message}");
            WizardProgressReport.Failed(_role, $"launcher did not start: {result.Message}", string.Join(Environment.NewLine, result.Steps));
            return false;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] StartLauncherAsync FAILED: {ex.Message}");
            WizardProgressReport.Failed(_role, $"launcher did not start: {ex.GetType().Name}: {ex.Message}", ex.ToString());
            _installStep?.SetLauncherFailed();
            _installStep?.SetStatus($"ERROR: Launcher tray app failed to start. {ex.Message}");
            return false;
        }
    }

    private async Task RunGatewayTrayInstallAsync(EngineInstallRunner.Prep prep)
    {
        SetupLog.Write("[MainWindow] RunGatewayTrayInstallAsync: shelling the CLI");

        // This wizard installs the Director. The Gateway is a separate, do-it-yourself install run from
        // the repository, so it gets no card and no name on this screen - but a machine ALREADY installed
        // as a Gateway host still has its tray app and Cockpit re-asserted here, because leaving them
        // half-updated is worse than not mentioning them.
        //
        // The progress lines the CLI streams go to the log ONLY. They used to be written over the line
        // under the heading, which is how an up-to-date machine came to read
        // "Gateway: Launcher tray app installed and running on 7900." instead of its version.
        //
        // The gateway-outcome -> completion-state transition lives in GatewayRefresh (UI-free, tested):
        // a returned failure AND a thrown failure both add one to the skipped count so the Complete step
        // reports the honest failure instead of "Everything went perfectly." This is the ONLY place the
        // wizard folds the Gateway refresh into its counts.
        var launcher = new GatewayTrayLauncher(new ReleaseSource());
        var outcome = await GatewayRefresh.RunAsync(
            () => launcher.RunAsync(
                prep.Release,
                line => SetupLog.Write($"[MainWindow]   gateway: {line}")),
            _skippedCount);

        _skippedCount = outcome.Skipped;
        _gatewayResultMessage = outcome.Message;
        // outcome.Message carries the tailnet Cockpit URL on success, the failure reason on failure.
        _gatewayFailureReason = outcome.Success ? null : outcome.Message;

        // Silence on success (there is no card to update, and the heading line belongs to the Director),
        // but a FAILURE must still reach the user in words - it is counted as a skip on the Complete
        // screen, and a counted failure nobody was told about is exactly what we are removing.
        if (!outcome.Success)
        {
            _installStep?.SetStatus(outcome.Message);
            _ = WizardProgressReport.Error("gateway", $"Windows setup wizard could not refresh the Gateway tray app: {outcome.Message}", null);
        }
        SetupLog.Write($"[MainWindow] Gateway install success={outcome.Success}: {outcome.Message} (skipped now {_skippedCount})");
    }

    private Task<bool> OnProcessBlockingAsync(string processName)
    {
        var result = MessageBox.Show(
            this,
            "DevThrottle is currently running and cannot be updated.\n\n" +
            "Please close DevThrottle and click OK to retry,\n" +
            "or click Cancel to skip updating the main application.",
            "DevThrottle is Running",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        return Task.FromResult(result == MessageBoxResult.OK);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
            ShowStep(PrevStep(_currentStep));
    }

    /// <summary>
    /// Closing the wizard on the Complete screen of a first install opens the Director, by whatever
    /// route the window is closed - the Close button or the title bar. Close used to end the install
    /// with nothing on screen and the machine never signed in (issue #3503). The rule is
    /// <see cref="InstallCompletion.OpensDirectorOnClose"/>; this only gathers its facts.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_currentStep == StepComplete && _completeStep is { } complete)
        {
            var opens = InstallCompletion.OpensDirectorOnClose(
                _isUpdate, complete.DirectorOpened, File.Exists(_directorExePath));
            SetupLog.Write($"[MainWindow] OnClosing on Complete: isUpdate={_isUpdate}, " +
                           $"directorOpened={complete.DirectorOpened}, opensDirector={opens}");
            if (opens) complete.OpenDirector();
        }
        base.OnClosing(e);
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
            // Leaving Install: rebuild Complete with the final counts.
            if (_currentStep == StepInstall)
                _completeStep = null;

            ShowStep(NextStep(_currentStep));
        }
    }

}
