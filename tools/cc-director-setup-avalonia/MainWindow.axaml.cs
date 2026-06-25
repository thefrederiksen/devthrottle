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
    // 5-step flow, matching the Windows wizard: Welcome -> Prerequisites -> Skills -> Install -> Complete.
    // (The old tool-group picker is gone: every cc-* tool ships as one shared-venv bundle.)
    private const int StepWelcome = 1, StepPrereq = 2, StepSkills = 3, StepInstall = 4, StepComplete = 5;

    private int _currentStep = StepWelcome;
    private InstallProfile _selectedProfile = InstallProfile.Standard;
    private List<PrerequisiteInfo> _prerequisites = [];
    private int _installedCount;
    private int _skippedCount;
    private string _installPath = "";

    private readonly bool _isUpdate;
    private readonly string? _installedVersion;
    private bool _alreadyUpToDate;
    private string? _latestVersion;

    private readonly EngineInstallRunner _runner = new();
    private EngineInstallRunner.Prep? _cachedPrep;

    private WelcomeStep? _welcomeStep;
    private PrerequisitesStep? _prerequisitesStep;
    private SkillsStep? _skillsStep;
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

        if (_isUpdate)
        {
            Title = "DevThrottle Update";
            SubtitleText.Text = "Update";
            Step4Label.Text = "Update";
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
            StepWelcome => _welcomeStep ??= new WelcomeStep(_selectedProfile, p => _selectedProfile = p, _isUpdate, _installedVersion),
            StepPrereq => _prerequisitesStep ??= new PrerequisitesStep(OnPrerequisitesChecked, _isUpdate),
            StepSkills => _skillsStep ??= new SkillsStep(_isUpdate),
            StepInstall => _installStep ??= new InstallStep(),
            StepComplete => _completeStep ??= new CompleteStep(_installedCount, _skippedCount, _installPath, _isUpdate, _alreadyUpToDate, _latestVersion),
            _ => null
        };

        if (step == StepInstall && _isUpdate)
            _installStep?.SetUpdateMode();

        if (step == StepPrereq)
            _prerequisitesStep?.RunChecks();

        if (step == StepInstall)
            _ = RunInstallAsync();
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
        else if (_currentStep == StepPrereq)
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
        if (_currentStep != StepPrereq) return;
        var allRequiredMet = _prerequisites.Count == 0 || _prerequisites.Where(p => p.IsRequired).All(p => p.IsFound);
        NextButton.IsEnabled = allRequiredMet;
    }

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
            _installStep?.SetStatus(ex.UserMessage());
            NextButton.Content = "Retry";
            NextButton.IsEnabled = true;
            return;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] RunInstallAsync: prepare FAILED: {ex.Message}");
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
        _ = RunRepairAsync();
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

    /// <summary>Run the engine apply (Director + tools bundle), install skills, then enable Next.</summary>
    private async Task ApplyAndFinishAsync(EngineInstallRunner.Prep prep)
    {
        var status = new Progress<string>(s => _installStep?.SetStatus(s));
        var (installed, skipped) = await _runner.ApplyAsync(prep, status);
        _installedCount = installed;
        _skippedCount = skipped;

        // Skills are per-user markdown downloads, handled outside the binary engine.
        var skillItems = _installStep?.GetSkillItems() ?? [];
        if (skillItems.Count > 0)
        {
            _installStep?.SetStatus("Installing skills...");
            await new ToolInstaller().InstallSkillsAsync(skillItems);
            _installStep?.UpdateSkillsStatus();
        }

        _installStep?.SetStatus($"Done - {installed} installed, {skipped} skipped");
        SetupLog.Write($"[MainWindow] ApplyAndFinishAsync: installed={installed}, skipped={skipped}");

        NextButton.Content = "Next";
        NextButton.IsEnabled = true;
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
        {
            if (_currentStep == StepWelcome)
            {
                _welcomeStep?.UpdateProfile(ref _selectedProfile);
                _prerequisitesStep = null;
                _skillsStep = null;
                _installStep = null;
                _completeStep = null;
            }
            ShowStep(_currentStep + 1);
        }
    }
}
