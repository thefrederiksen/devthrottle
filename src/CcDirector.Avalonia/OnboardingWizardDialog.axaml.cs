using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Configuration;
using CcDirector.Core.Onboarding;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// First-run onboarding wizard (issue #370, lean v1). Takes a fresh user from launch to a working
/// agent in three steps: 1) detect, enter and test the Gateway URL, 2) confirm a Claude Code agent
/// is available on PATH (with install guidance when it is not), 3) confirm the Director is ready and
/// route to the New Session dialog.
///
/// The logic lives UI-free in <see cref="OnboardingModel"/>; this dialog is the thin Avalonia shell.
/// The three steps are panels in one cell toggled by IsVisible, navigated with Back/Next. The gateway
/// step auto-scans for a gateway when it first appears and offers a Detect button to re-scan, reusing
/// the same <see cref="SettingsDetectionService"/> the Settings gateway tab uses (DetectGatewayAsync
/// for the scan, TestGatewayAsync for a typed URL). All gateway work runs async with a spinner so the
/// UI never blocks. On finish (or skip) the onboarding-complete marker is written so the wizard never
/// auto-opens again.
/// </summary>
public partial class OnboardingWizardDialog : Window
{
    private const int StepGateway = 0;
    private const int StepAgent = 1;
    private const int StepDone = 2;
    private const int TotalSteps = 3;

    private readonly AgentOptions _options;
    private readonly OnboardingModel _model = new(new ToolDetectionService());
    private readonly SettingsDetectionService _gatewayDetector = new();

    private int _currentStep = StepGateway;
    private bool _gatewayTestPassed;
    private string _persistedGatewayUrl = "";
    private bool _agentAvailable;
    private bool _gatewayAutoScanStarted;

    /// <summary>
    /// True when the user chose "Create first session" on the final step, so the caller should open
    /// the New Session dialog after this wizard closes. False when the user clicked Finish or Skip.
    /// </summary>
    public bool WantsNewSession { get; private set; }

    public OnboardingWizardDialog() : this(new AgentOptions()) { }

    public OnboardingWizardDialog(AgentOptions options)
    {
        FileLog.Write("[OnboardingWizardDialog] Constructor: initializing");
        _options = options ?? throw new ArgumentNullException(nameof(options));
        InitializeComponent();

        ShowStep(StepGateway);

        // Show the wizard instantly, then auto-scan for a gateway off the UI thread once it is
        // visible (responsive-UI rule). This brings the wizard's gateway step to parity with the
        // Settings gateway tab, which the user reached via the Detect button there.
        Loaded += (_, _) => StartGatewayAutoScan();
    }

    /// <summary>Switch the visible step panel and update the title, indicator, and navigation buttons.</summary>
    private void ShowStep(int step)
    {
        FileLog.Write($"[OnboardingWizardDialog] ShowStep: step={step}");
        _currentStep = step;

        GatewayStep.IsVisible = step == StepGateway;
        AgentStep.IsVisible = step == StepAgent;
        DoneStep.IsVisible = step == StepDone;

        StepIndicator.Text = $"Step {step + 1} of {TotalSteps}";
        StepStatusText.IsVisible = false;
        StepStatusText.Text = "";

        BackButton.IsVisible = step != StepGateway;

        switch (step)
        {
            case StepGateway:
                StepTitle.Text = "Connect to a Gateway";
                NextButton.Content = "Next";
                NextButton.IsVisible = true;
                break;

            case StepAgent:
                StepTitle.Text = "Check your agent";
                NextButton.Content = "Next";
                NextButton.IsVisible = true;
                _ = RunAgentCheckAsync();
                break;

            case StepDone:
                StepTitle.Text = "You are ready";
                NextButton.Content = "Finish";
                NextButton.IsVisible = true;
                BuildDoneSummary();
                break;
        }
    }

    /// <summary>
    /// Auto-scan for a gateway exactly once, the first time the gateway step becomes visible. The
    /// scan runs async (off the UI thread) so the wizard renders instantly. Mirrors the Settings
    /// gateway tab's Detect behavior.
    /// </summary>
    private void StartGatewayAutoScan()
    {
        if (_gatewayAutoScanStarted)
            return;
        _gatewayAutoScanStarted = true;
        FileLog.Write("[OnboardingWizardDialog] StartGatewayAutoScan");
        _ = DetectGatewayAsync();
    }

    private async void BtnDetectGateway_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[OnboardingWizardDialog] BtnDetectGateway_Click");
        await DetectGatewayAsync();
    }

    /// <summary>
    /// Scan the tailnet and this machine for a gateway via the shared detector (the same
    /// <see cref="SettingsDetectionService.DetectGatewayAsync"/> the Settings gateway tab uses).
    /// On success, pre-fills the URL box and marks the test passed so the user can just click Next.
    /// On no result, leaves the field for manual entry. Runs async with the spinner so the UI never
    /// blocks; never throws (this is a UI helper called from event handlers and lifecycle).
    /// </summary>
    private async Task DetectGatewayAsync()
    {
        FileLog.Write("[OnboardingWizardDialog] DetectGatewayAsync");
        DetectGatewayButton.IsEnabled = false;
        TestGatewayButton.IsEnabled = false;
        GatewayTestSpinner.IsVisible = true;
        ShowGatewayStatus("Scanning the tailnet and this machine for a gateway ...", error: false);
        try
        {
            var result = await _gatewayDetector.DetectGatewayAsync();
            if (result.Url is not null)
            {
                GatewayUrlBox.Text = result.Url;
                _gatewayTestPassed = true;
                ShowGatewayStatus($"Found gateway at {result.Url}. Click Next to connect, or edit the address above.", error: false);
                FileLog.Write($"[OnboardingWizardDialog] DetectGatewayAsync: found {result.Url}");
            }
            else
            {
                _gatewayTestPassed = false;
                ShowGatewayStatus($"No gateway found on this network ({result.Scanned.Count} address(es) scanned). Enter the gateway URL above and click Test.", error: true);
                FileLog.Write($"[OnboardingWizardDialog] DetectGatewayAsync: none found, scanned={result.Scanned.Count}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OnboardingWizardDialog] DetectGatewayAsync FAILED: {ex.Message}");
            ShowGatewayStatus($"Detection failed: {ex.Message}", error: true);
            _gatewayTestPassed = false;
        }
        finally
        {
            GatewayTestSpinner.IsVisible = false;
            DetectGatewayButton.IsEnabled = true;
            TestGatewayButton.IsEnabled = true;
        }
    }

    private async void BtnTestGateway_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[OnboardingWizardDialog] BtnTestGateway_Click");
        var validation = OnboardingModel.ValidateGatewayUrl(GatewayUrlBox.Text);
        if (!validation.IsValid)
        {
            ShowGatewayStatus(validation.Message, error: true);
            _gatewayTestPassed = false;
            return;
        }

        TestGatewayButton.IsEnabled = false;
        GatewayTestSpinner.IsVisible = true;
        ShowGatewayStatus($"Testing {validation.NormalizedUrl} ...", error: false);
        try
        {
            var result = await _gatewayDetector.TestGatewayAsync(validation.NormalizedUrl);
            ShowGatewayStatus(result.Message, error: !result.Ok);
            _gatewayTestPassed = result.Ok;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OnboardingWizardDialog] BtnTestGateway_Click FAILED: {ex.Message}");
            ShowGatewayStatus($"Test failed: {ex.Message}", error: true);
            _gatewayTestPassed = false;
        }
        finally
        {
            GatewayTestSpinner.IsVisible = false;
            TestGatewayButton.IsEnabled = true;
        }
    }

    private async void BtnNext_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write($"[OnboardingWizardDialog] BtnNext_Click: step={_currentStep}");
        try
        {
            switch (_currentStep)
            {
                case StepGateway:
                    await AdvanceFromGatewayAsync();
                    break;

                case StepAgent:
                    ShowStep(StepDone);
                    break;

                case StepDone:
                    await FinishAsync(wantsNewSession: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OnboardingWizardDialog] BtnNext_Click FAILED: {ex.Message}");
            ShowStepStatus($"Something went wrong: {ex.Message}", error: true);
        }
    }

    /// <summary>
    /// Gateway step Next: version 1 expects a gateway, so a blank URL is not a normal mode - we nudge
    /// the user to detect or enter one (they can still leave via "Skip for now" without bricking
    /// first-run). A non-blank URL must be valid; if it has not been tested green yet we test it now
    /// and only advance on success, then persist it to gateway.url.
    /// </summary>
    private async Task AdvanceFromGatewayAsync()
    {
        var raw = GatewayUrlBox.Text?.Trim() ?? "";
        if (raw.Length == 0)
        {
            FileLog.Write("[OnboardingWizardDialog] AdvanceFromGateway: blank URL, nudging for a gateway");
            ShowGatewayStatus("Enter or detect a gateway URL to continue, or use \"Skip for now\" to set it later in Settings.", error: true);
            return;
        }

        var validation = OnboardingModel.ValidateGatewayUrl(raw);
        if (!validation.IsValid)
        {
            ShowGatewayStatus(validation.Message, error: true);
            return;
        }

        if (!_gatewayTestPassed)
        {
            TestGatewayButton.IsEnabled = false;
            GatewayTestSpinner.IsVisible = true;
            ShowGatewayStatus($"Testing {validation.NormalizedUrl} ...", error: false);
            try
            {
                var result = await _gatewayDetector.TestGatewayAsync(validation.NormalizedUrl);
                _gatewayTestPassed = result.Ok;
                ShowGatewayStatus(result.Message, error: !result.Ok);
            }
            finally
            {
                GatewayTestSpinner.IsVisible = false;
                TestGatewayButton.IsEnabled = true;
            }

            if (!_gatewayTestPassed)
                return;
        }

        await Task.Run(() => OnboardingModel.PersistGatewayUrl(validation.NormalizedUrl));
        _persistedGatewayUrl = validation.NormalizedUrl;
        FileLog.Write($"[OnboardingWizardDialog] AdvanceFromGateway: persisted {validation.NormalizedUrl}");
        ShowStep(StepAgent);
    }

    private void BtnBack_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write($"[OnboardingWizardDialog] BtnBack_Click: step={_currentStep}");
        if (_currentStep == StepAgent)
            ShowStep(StepGateway);
        else if (_currentStep == StepDone)
            ShowStep(StepAgent);
    }

    private async void BtnSkip_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[OnboardingWizardDialog] BtnSkip_Click");
        try
        {
            await FinishAsync(wantsNewSession: false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OnboardingWizardDialog] BtnSkip_Click FAILED: {ex.Message}");
            Close(false);
        }
    }

    /// <summary>Run the Claude Code availability check off the UI thread, then render the verdict.</summary>
    private async Task RunAgentCheckAsync()
    {
        FileLog.Write("[OnboardingWizardDialog] RunAgentCheckAsync");
        AgentCheckSpinner.IsVisible = true;
        SetAgentBadge("CHECKING", "#3A2A1B", "#F59E0B");
        AgentStatusMessage.Text = "Checking for Claude Code...";
        AgentInstallGuidance.IsVisible = false;
        try
        {
            var availability = await Task.Run(() => _model.CheckClaudeAvailable(_options));
            _agentAvailable = availability.IsAvailable;
            AgentStatusMessage.Text = availability.Message;

            if (availability.IsAvailable)
            {
                SetAgentBadge("AVAILABLE", "#1B3A2A", "#22C55E");
                AgentInstallGuidance.IsVisible = false;
            }
            else
            {
                SetAgentBadge("NOT FOUND", "#3A2A1B", "#F59E0B");
                AgentInstallGuidance.IsVisible = true;
            }

            FileLog.Write($"[OnboardingWizardDialog] RunAgentCheckAsync: available={availability.IsAvailable}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OnboardingWizardDialog] RunAgentCheckAsync FAILED: {ex.Message}");
            SetAgentBadge("ERROR", "#3A2A1B", "#F59E0B");
            AgentStatusMessage.Text = $"Could not check for Claude Code: {ex.Message}";
            AgentInstallGuidance.IsVisible = true;
            _agentAvailable = false;
        }
        finally
        {
            AgentCheckSpinner.IsVisible = false;
        }
    }

    private async void BtnCreateSession_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[OnboardingWizardDialog] BtnCreateSession_Click");
        try
        {
            await FinishAsync(wantsNewSession: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OnboardingWizardDialog] BtnCreateSession_Click FAILED: {ex.Message}");
            ShowStepStatus($"Something went wrong: {ex.Message}", error: true);
        }
    }

    private void BtnRecheckAgent_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[OnboardingWizardDialog] BtnRecheckAgent_Click");
        _ = RunAgentCheckAsync();
    }

    private void BtnInstallClaude_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[OnboardingWizardDialog] BtnInstallClaude_Click");
        try
        {
            Process.Start(new ProcessStartInfo(OnboardingModel.ClaudeInstallUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OnboardingWizardDialog] BtnInstallClaude_Click FAILED: {ex.Message}");
            ShowStepStatus($"Could not open the browser. Visit {OnboardingModel.ClaudeInstallUrl} manually.", error: true);
        }
    }

    private void BuildDoneSummary()
    {
        var gatewayPart = string.IsNullOrEmpty(_persistedGatewayUrl)
            ? "No gateway is configured yet - add one from Settings so this Director shows up there."
            : $"Connected to gateway {_persistedGatewayUrl}.";
        var agentPart = _agentAvailable
            ? "Claude Code is available."
            : "No agent was detected yet - install Claude Code, then add it from Settings.";
        DoneSummary.Text = $"{gatewayPart} {agentPart}";
    }

    /// <summary>Mark onboarding complete and close. Sets WantsNewSession so the caller can route to New Session.</summary>
    private async Task FinishAsync(bool wantsNewSession)
    {
        FileLog.Write($"[OnboardingWizardDialog] FinishAsync: wantsNewSession={wantsNewSession}");
        await Task.Run(OnboardingModel.MarkComplete);
        WantsNewSession = wantsNewSession;
        Close(true);
    }

    private void SetAgentBadge(string text, string background, string foreground)
    {
        AgentStatusBadgeText.Text = text;
        AgentStatusBadge.Background = new SolidColorBrush(Color.Parse(background));
        AgentStatusBadgeText.Foreground = new SolidColorBrush(Color.Parse(foreground));
    }

    private void ShowGatewayStatus(string text, bool error)
    {
        GatewayTestStatus.Text = text;
        GatewayTestStatus.IsVisible = true;
        GatewayTestStatus.Foreground = error ? Brushes.IndianRed : Brushes.MediumSeaGreen;
    }

    private void ShowStepStatus(string text, bool error)
    {
        StepStatusText.Text = text;
        StepStatusText.IsVisible = true;
        StepStatusText.Foreground = error ? Brushes.IndianRed : Brushes.MediumSeaGreen;
    }
}
