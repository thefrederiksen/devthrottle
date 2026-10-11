using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Configuration;
using CcDirector.Core.GatewayConnection;
using CcDirector.Core.Onboarding;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;
using CcDirector.Core.ErrorReports;

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

    private int _currentStep = StepGateway;
    // The gateway step advances only on the panel's TERMINAL result - connected AND signed in - not on the
    // transport handshake alone (#1808a). Skip remains the escape hatch when the user does not connect.
    private bool _gatewaySettled;
    private bool _agentAvailable;

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

        // The Gateway step IS the one reusable connection panel now (Phase 4, design spec section 8). In
        // #1808a it opens on the gateway CHOICE (self-host / hosted / join / skip). Next advances only once
        // the panel settles to connected AND signed in (ConnectionSettled), not on transport alone; Skip
        // completes onboarding local-only via the panel's SkipRequested verdict (the #1809 seam).
        var panel = Controls.GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Onboarding);
        // Advance ONLY on the terminal result - connected AND signed in (#1808a). There is no transport-only
        // event to subscribe to any more (it was removed), so bare transport can never advance the wizard.
        panel.ConnectionSettled += (_, _) => OnGatewaySettled();
        panel.SkipRequested += (_, behavior) =>
        {
            // Only the onboarding-completion verdict closes the wizard; the panel handles ReturnToChoice.
            if (behavior == GatewaySkipBehavior.CompleteOnboardingLocalOnly)
                _ = FinishAsync(wantsNewSession: false);
        };
        GatewayStep.Child = panel;

        ShowStep(StepGateway);
    }

    /// <summary>The embedded panel reached its TERMINAL result - connected AND signed in (#1808a). Record it
    /// so Next advances, and tell the user they are ready to continue.</summary>
    private void OnGatewaySettled()
    {
        _gatewaySettled = true;
        FileLog.Write("[OnboardingWizardDialog] gateway settled (connected and signed in)");
        ShowStepStatus("Connected and signed in. Click Next to continue.", error: false);
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

    private async void BtnNext_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write($"[OnboardingWizardDialog] BtnNext_Click: step={_currentStep}");
        try
        {
            switch (_currentStep)
            {
                case StepGateway:
                    AdvanceFromGateway();
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
            ShowStepStatus(ShownError.Report("onboarding", "go to the next step", $"Something went wrong: {ex.Message}", ex), error: true);
        }
    }

    /// <summary>
    /// Gateway step Next (#1808a): advance only once the embedded panel has SETTLED - connected AND signed
    /// in (the terminal result), not on the transport handshake alone. If it has not settled, nudge the user
    /// to connect and sign in in the panel (they can still leave via "Skip for now" without bricking
    /// first-run).
    /// </summary>
    private void AdvanceFromGateway()
    {
        if (!_gatewaySettled)
        {
            FileLog.Write("[OnboardingWizardDialog] AdvanceFromGateway: not settled yet (connected+signed-in), nudging");
            // shown-error-exempt (user input): the user pressed Next before connecting and signing in; this says what to do first
            ShowStepStatus("Connect and sign in to your Gateway above to continue, or use \"Skip for now\" to set it up later in Settings.", error: true);
            return;
        }

        FileLog.Write("[OnboardingWizardDialog] AdvanceFromGateway: settled; advancing to the agent step");
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
            // Error report decision (issue #3675): NOT reported. "Claude Code was not found on your PATH" is a setup
            // state on a new machine - nothing has failed, the next step is to install it - the same ruling as the
            // "No agent configured" box (exempt as user input). The amber badge says NOT FOUND, never an error.
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
            SetAgentBadge("ERROR", "#3A2A1B", "#F59E0B");
            AgentStatusMessage.Text = ShownError.Report("onboarding", "check for Claude Code", $"Could not check for Claude Code: {ex.Message}", ex);
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
            ShowStepStatus(ShownError.Report("onboarding", "create the first session", $"Something went wrong: {ex.Message}", ex), error: true);
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
            ShowStepStatus(ShownError.Report("onboarding", "open the Claude Code install page",
                $"Could not open the browser. Visit {OnboardingModel.ClaudeInstallUrl} manually.", ex), error: true);
        }
    }

    private void BuildDoneSummary()
    {
        // The panel wrote gateway.url on connect (Phase 4); read it back for the summary line.
        var gatewayUrl = GatewayConfig.Load().Url;
        var gatewayPart = string.IsNullOrWhiteSpace(gatewayUrl)
            ? "No gateway is connected yet - connect one from Settings so this Director shows up there."
            : $"Connected to gateway {gatewayUrl}.";
        var agentPart = _agentAvailable
            ? "Claude Code is available."
            : "No agent was detected yet - install Claude Code, then add it from Settings.";
        DoneSummary.Text = $"{gatewayPart} {agentPart}";
    }

    /// <summary>Mark onboarding complete and close. Sets WantsNewSession so the caller can route to New Session.
    /// Internal so the local-only regression proof (issue #1809) can drive the real Skip/Finish seam and
    /// assert it persists the completion marker without writing a gateway.</summary>
    internal async Task FinishAsync(bool wantsNewSession)
    {
        FileLog.Write($"[OnboardingWizardDialog] FinishAsync: wantsNewSession={wantsNewSession}");
        await Task.Run(OnboardingModel.MarkComplete);
        WantsNewSession = wantsNewSession;
        Close(true);
    }

    // ---- #1808a R2 test hooks: prove transport alone cannot advance; only the terminal result can --------

    /// <summary>The wizard's current step index, so a test can assert Next did or did not advance.</summary>
    internal int CurrentStepForTests => _currentStep;

    /// <summary>The embedded gateway panel, so a test can drive its terminal emission (the only advance signal).</summary>
    internal Controls.GatewayConnectionPanel GatewayPanelForTests => (Controls.GatewayConnectionPanel)GatewayStep.Child!;

    /// <summary>Invoke the Next button's real handler, exactly as the button click does.</summary>
    internal void ClickNextForTests() => BtnNext_Click(this, new RoutedEventArgs());

    private void SetAgentBadge(string text, string background, string foreground)
    {
        AgentStatusBadgeText.Text = text;
        AgentStatusBadge.Background = new SolidColorBrush(Color.Parse(background));
        AgentStatusBadgeText.Foreground = new SolidColorBrush(Color.Parse(foreground));
    }

    private void ShowStepStatus(string text, bool error)
    {
        StepStatusText.Text = text;
        StepStatusText.IsVisible = true;
        StepStatusText.Foreground = error ? Brushes.IndianRed : Brushes.MediumSeaGreen;
    }
}
