using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.ControlApi;
using CcDirector.Gateway.Contracts;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using CcDirector.Core.GatewayConnection;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The one reusable Gateway connection panel (design spec section 5). Phase 1 implements Step 1 - Connect:
/// on show it automatically scans for Gateways in the issue-1233 discovery order (this machine, tailnet,
/// local network), teaches with a plain-English intro, and lists every reachable one as a framed one-click
/// pick with a per-kind "when to use this" line. Exactly one pick carries a Recommended badge by the rule
/// This computer &gt; On your network &gt; Over Tailscale (Tailscale only when it is the sole find). Picking a
/// Gateway IS the test (decision 5): it writes the address, re-applies the Gateway config so the Director
/// runs the two-way nonce handshake, and shows live progress until the handshake either proves the
/// connection or fails with a named leg (decision 11, no fallback).
///
/// Verification is NOT rebuilt here (spec section 9): the panel drives the existing
/// <see cref="GatewayConnectionMonitor"/> through <see cref="ControlApiHost.ReapplyGatewayAsync"/> and reads
/// its earned verdict. Green is earned only by a completed handshake (decision 4). The visual language
/// matches the dictation card (spec section 5 visual direction): a #252526 card, a blue count pill, a green
/// Recommended accent, tinted icon tiles, and muted-blue guidance.
///
/// Phase 1 wires this into a temporary menu entry for testing; the Settings tab, the status box, and the
/// onboarding wizard adopt it in later phases (decision 8).
/// </summary>
public partial class GatewayConnectionPanel : UserControl, ISignInAddressDisplay
{
    // How long to wait for a handshake verdict before calling the attempt timed out.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);

    private static readonly FontFamily MonoFont = new("Cascadia Mono,Consolas,Courier New");

    // Icon geometries (24-unit viewbox, stretched into the 38px tile). A monitor for a local machine, a
    // globe for Tailscale (spec section 5 visual direction).
    private const string MonitorGeometry =
        "M4.5,4 H19.5 A1.5,1.5 0 0 1 21,5.5 V14.5 A1.5,1.5 0 0 1 19.5,16 H4.5 A1.5,1.5 0 0 1 3,14.5 "
        + "V5.5 A1.5,1.5 0 0 1 4.5,4 Z M8,20 H16 M12,16 V20";
    private const string GlobeGeometry =
        "M3,12 A9,9 0 1 1 21,12 A9,9 0 1 1 3,12 Z M3,12 H21 M12,3 C14.6,5.5 14.6,18.5 12,21 "
        + "M12,3 C9.4,5.5 9.4,18.5 12,21";

    private readonly GatewayScanService _scan = new();

    private GatewayConnectionMonitor? _monitor;
    private bool _subscribed;

    // True only while an initiated connect is awaiting a verdict, so background monitor changes never
    // yank the panel off another view.
    private bool _connecting;

    // Distinguishes verdicts of the current attempt from a stale one; bumped on every connect.
    private int _attemptId;

    // The last address tried, so "Try again" repeats it. Remote records whether the address is off this
    // machine, so "Try again" takes the same enroll path (loopback fast path vs the remote sign-in seam).
    private (string Url, string Label, bool Remote)? _lastAttempt;

    // Cancels the Step 2 account-status polling loop when the panel is left or the flow restarts.
    private CancellationTokenSource? _pollCts;

    // Cancels an in-flight remote sign-in + enroll (the Join-existing seam, #1808a) when the panel is left.
    private CancellationTokenSource? _remoteEnrollCts;

    // The UI-free choice context this panel resolves its CHOICE step from (#1808a). Built once at
    // construction from the consumer and the host OS capability. (There is no repair dimension: repair goes
    // straight to the rediscovery scan and never shows the choice - see OnAttachedToVisualTree.) Not readonly
    // only so a test can inject a Mac (SelfHostSupported=false) context this Windows host cannot produce.
    private GatewayChoiceContext _choiceContext;

    // True when the panel opens on the gateway CHOICE step (#1808a): a first-time, not-yet-connected,
    // non-repair connect. A connected panel opens on Done and a broken one opens on the repair scan, so
    // neither shows the choice.
    private readonly bool _showChoiceFirst;

    // What the choice's Skip action does for this consumer (#1808a), captured from the resolved plan when
    // the choice renders, so the Skip click reads the Gateway/host verdict verbatim (dumb-client rule).
    private GatewaySkipBehavior _skipBehavior;

    // ---- Test seams (#1808a R2): let the remote-Join transaction run headless with no live Gateway. Each
    // defaults to null -> the real behavior. Tests inject a fake enrollment seam (so no browser/network), a
    // director id, and a re-apply capture, then drive ConnectToAsync and assert the transaction boundary:
    // the seam is always called with the SELECTED url (an old saved token cannot bypass it), a failure never
    // mutates config, and success re-applies the verified credential.
    internal Func<string, string, string, CancellationToken, Task<OperationResult<MobileEnrollmentResponse>>>? RemoteEnrollSeam;
    internal Func<Task>? ReapplyGatewaySeam;
    internal string? DirectorIdOverride;

    // ---- Hosted-enroll test seam: run the hosted account sign-in + enroll headless with no browser or
    // network. Defaults to null -> the real GatewayAccountEnrollRunner.SignInAndEnrollHostedAsync (the exact
    // path the CLI's `enroll --hosted` uses). Tests inject a fake so ActivateChoiceForTests(UseHosted) can
    // prove the enabled card runs the hosted enroll with this device's id, without a real network enroll.
    // The third argument is the screen that shows the sign-in address on the waiting view (issue #3504).
    internal Func<string, string, ISignInAddressDisplay, CancellationToken, Task<OperationResult<MobileEnrollmentResponse>>>? HostedEnrollSeam;

    // The hosted sign-in's address while it waits for the browser, behind Copy and Open in browser (issue
    // #3504). Cleared whenever the panel changes view, so no other view can offer a stale address.
    private string? _signInAddress;

    // Which step the panel opens on (spec section 6: the status-box click opens the panel on the resolver's
    // current step). Connect (the default) starts the auto-scan; SignIn/Done skip the scan and read the
    // signed-in state directly, because the handshake is already proven in those states.
    private readonly GatewayPanelStep _initialStep;

    // True when the panel opened because a prior connection failed or a connected Gateway became
    // unreachable (spec section 7, Phase 5). Step 1 then opens in repair mode: it names the failing leg,
    // renders the troubleshooter diagnostics inline, and the rediscovery scan offers the new address.
    private readonly bool _repairMode;

    public GatewayConnectionPanel()
        : this(GatewayPanelStep.Connect, repairMode: false, GatewayChoiceConsumer.StatusWindow, showChoiceFirst: false)
    {
    }

    public GatewayConnectionPanel(GatewayPanelStep initialStep)
        : this(initialStep, repairMode: false, GatewayChoiceConsumer.StatusWindow, showChoiceFirst: false)
    {
    }

    private GatewayConnectionPanel(
        GatewayPanelStep initialStep, bool repairMode, GatewayChoiceConsumer consumer, bool showChoiceFirst)
    {
        _initialStep = initialStep;
        _repairMode = repairMode;
        _showChoiceFirst = showChoiceFirst;
        // Self-host is Windows-only in source, so on a Mac the state machine makes it ABSENT (section 6 open
        // decision).
        _choiceContext = new GatewayChoiceContext(consumer, OperatingSystem.IsWindows());
        InitializeComponent();
        FileLog.Write($"[GatewayConnectionPanel] constructed (initialStep={initialStep}, repairMode={repairMode}, consumer={consumer}, showChoiceFirst={showChoiceFirst})");
    }

    // The transport-only ConnectionVerified event was REMOVED in #1808a (R3): the handshake alone is not a
    // safe advance condition - a Gateway can be reachable but not signed in or inference-ready. The one
    // terminal advance signal is ConnectionSettled below (connected AND signed in). Removing the event, not
    // just leaving it unsubscribed, stops any future consumer from re-subscribing to the unsafe seam.

    /// <summary>
    /// Raised when the panel settles the account sign-in state: signed in (Done) or signed out (the sign-in
    /// step). The sidebar status box listens for this so line 2 repaints the instant the panel learns the
    /// new state, instead of waiting for its 30-second heartbeat poll (the near-a-minute lag after signing
    /// in). Purely a nudge to refresh - the box still reads /account/status itself; no state travels on the
    /// event.
    /// </summary>
    public event EventHandler? AccountStateSettled;

    /// <summary>
    /// Raised when the choice's Skip action is chosen (#1808a). Carries what Skip MEANS for this consumer,
    /// decided by the state machine, not the panel: onboarding completes local-only (the issue #1809 seam,
    /// handled by the wizard), and Settings/status return to the choice (handled here). The panel never
    /// re-derives this - it reads the resolved plan's verdict verbatim (dumb-client rule).
    /// </summary>
    public event EventHandler<GatewaySkipBehavior>? SkipRequested;

    /// <summary>
    /// The one common TERMINAL RESULT the panel settles to (#1808a). <see cref="ConnectionVerified"/> fires
    /// on the transport handshake ALONE, so a consumer that gated on it advanced on a connection that was
    /// not yet signed in. This fires when the panel reaches the Done view - connected AND signed in - and
    /// carries the full outcome (connected + signed in + inference readiness), so a consumer advances on the
    /// whole outcome, not on transport alone. Inference readiness is a NotReady placeholder here (#1810).
    /// </summary>
    public event EventHandler<GatewayConnectionOutcome>? ConnectionSettled;

    /// <summary>
    /// Build a panel opened on the resolver's current step (spec section 6), for the three hosts that
    /// embed it (Settings Gateway tab, onboarding Gateway step, and the status-box window). A proven
    /// handshake opens on the signed-in view; a prior failure opens Step 1 in repair mode; and a fresh,
    /// not-yet-connected Director opens on the gateway CHOICE step (#1808a - self-host / hosted / join /
    /// skip). The <paramref name="consumer"/> filters the choice's state machine (one fork, not three UIs).
    /// </summary>
    public static GatewayConnectionPanel CreateForCurrentState(GatewayChoiceConsumer consumer)
    {
        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        var status = host?.GatewayMonitor?.Status;

        // A proven handshake opens on the signed-in view (Step 2 vs Done settles from account status).
        if (status == GatewayConnectionStatus.Connected)
            return new GatewayConnectionPanel(GatewayPanelStep.Done, repairMode: false, consumer, showChoiceFirst: false);

        // A prior failure (or a lost tailnet identity) opens Step 1 in REPAIR mode (Phase 5): the failing
        // leg is named, diagnostics render inline, and the rediscovery scan offers the new address. Repair
        // reconnects a known Gateway, so it skips the choice.
        if (status is GatewayConnectionStatus.Failed or GatewayConnectionStatus.NoTailnetIdentity
            or GatewayConnectionStatus.SubscriptionRequired)
            return new GatewayConnectionPanel(GatewayPanelStep.Connect, repairMode: true, consumer, showChoiceFirst: false);

        // A refused key cannot be repaired by reconnecting - the Gateway refuses it on every dial - so the Director is
        // set up again from the gateway CHOICE step, which signs in and issues a new key (devthrottle_internal#2311,
        // live proof F3).
        if (status == GatewayConnectionStatus.KeyRefused)
            return new GatewayConnectionPanel(GatewayPanelStep.Connect, repairMode: false, consumer, showChoiceFirst: true);

        // Otherwise a fresh connect: open on the gateway CHOICE step (#1808a).
        return new GatewayConnectionPanel(GatewayPanelStep.Connect, repairMode: false, consumer, showChoiceFirst: true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Open on the step the resolver pointed at (spec section 6). When the box already resolved to
        // signed-in or done, the handshake is proven - go straight to the signed-in view rather than
        // re-scanning from Step 1. Otherwise start the automatic scan (there is no Detect button, decision 5).
        if (_initialStep is GatewayPanelStep.SignIn or GatewayPanelStep.Done)
        {
            _ = RefreshSignedInViewAsync();
        }
        else if (_repairMode)
        {
            // Repair mode (Phase 5): name the failing leg, kick the inline diagnostics, THEN scan - the
            // rediscovery scan offers the Gateway's current address as a one-click fix.
            EnterRepairMode();
            StartScan();
        }
        else if (_showChoiceFirst)
        {
            // A fresh connect opens on the gateway CHOICE (#1808a): self-host / hosted / join / skip. The
            // scan starts only when the user picks Join existing.
            ShowChoice();
        }
        else
        {
            StartScan();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopPolling();
        _remoteEnrollCts?.Cancel();
        _diagCts?.Cancel();
        if (_subscribed && _monitor is not null)
        {
            _monitor.Changed -= OnMonitorChanged;
            _subscribed = false;
        }
    }

    // ---- Step 0: the gateway CHOICE (#1808a) ----------------------------------------------------

    // Show the gateway CHOICE step: self-host / use hosted / join existing / skip. The state machine
    // (GatewayChoiceStateMachine) decides which are offered and whether each is enabled - the panel only
    // renders that verdict (dumb-client rule). Self-host and Hosted have no orchestrator/provisioning yet
    // (#1808b/#1810, #1808c/#1811), so they render as clearly DISABLED "coming" actions, never live buttons.
    private void ShowChoice()
    {
        _connecting = false;
        StopPolling();
        var plan = GatewayChoiceStateMachine.Resolve(_choiceContext);
        RenderChoice(plan);
        ShowOnly(ChoicePanel);
        FileLog.Write($"[GatewayConnectionPanel] showing gateway choice (consumer={_choiceContext.Consumer}, selfHostSupported={_choiceContext.SelfHostSupported}, skip={plan.SkipBehavior})");
    }

    // Render the resolved plan's options into the choice host. Absent options (for example self-host on a
    // Mac) are omitted; disabled options render greyed with a "coming soon" reason and no click; enabled
    // options are clickable cards. The Skip behavior travels with the plan.
    private void RenderChoice(GatewayChoicePlan plan)
    {
        _skipBehavior = plan.SkipBehavior;
        ChoiceHost.Children.Clear();
        foreach (var option in plan.Options)
        {
            if (option.Availability == GatewayChoiceAvailability.Absent)
                continue;
            ChoiceHost.Children.Add(BuildChoiceCard(option));
        }
    }

    private Control BuildChoiceCard(GatewayChoiceOption option)
    {
        var (title, description) = ChoiceCopyFor(option.Action);
        var enabled = option.Availability == GatewayChoiceAvailability.Enabled;

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 3),
        };
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brush(enabled ? "#E6E6E6" : "#7A7A7A"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        // A disabled action is shown, clearly, as a not-yet-available "coming" action (dumb-client rule).
        if (!enabled && option.DisabledReason is { } reason)
            titleRow.Children.Add(BuildComingSoonBadge(reason));

        var info = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(titleRow);
        info.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(enabled ? "#888888" : "#5F5F5F"),
        });
        Grid.SetColumn(info, 0);

        var chevron = new TextBlock
        {
            Text = ">",
            FontSize = 18,
            Foreground = Brush("#666666"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            IsVisible = enabled,
        };
        Grid.SetColumn(chevron, 1);

        var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        inner.Children.Add(info);
        inner.Children.Add(chevron);

        var card = new Border
        {
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(enabled ? "#3C3C3C" : "#2A2A2A"),
            Background = Brush(enabled ? "#252526" : "#1E1E1F"),
            Padding = new Thickness(15, 14),
            Child = inner,
            Tag = option.Action,
            Opacity = enabled ? 1.0 : 0.65,
            // Render-level non-actionability: a disabled "coming" card is genuinely inert - Avalonia raises
            // no pointer input on an IsEnabled=false control - so it can never fire, even if a handler existed.
            IsEnabled = enabled,
        };
        // Only enabled actions get a cursor, hover, and click handler - a disabled "coming" card is inert,
        // never a live-looking button that no-ops.
        if (enabled)
        {
            card.Cursor = new Cursor(StandardCursorType.Hand);
            card.PointerPressed += Choice_PointerPressed;
            WireHover(card, recommended: false);
        }
        return card;
    }

    // The per-action title and description for the choice cards. Self-host and Hosted are described so the
    // user understands the coming option, but they carry a "coming soon" badge and no click in this slice.
    private static (string Title, string Description) ChoiceCopyFor(GatewayChoiceAction action) => action switch
    {
        // The tradeoffs are stated on the CARD, where the choice is actually made - not after the
        // user has committed. Both are capability facts, never privacy claims: self-hosting means
        // this machine is the Gateway, so it has to be awake, and reaching it from a phone or
        // another computer needs Tailscale. A user who learns that after provisioning has been
        // sold something (#1810).
        GatewayChoiceAction.SelfHost => (
            "Self-host a Gateway",
            "Run your own Gateway on this computer. Free. This machine has to stay on and awake "
            + "for your agents to keep running, and reaching it from your phone or another "
            + "computer needs Tailscale."),
        GatewayChoiceAction.UseHosted => (
            "Use a hosted Gateway",
            "Let DevThrottle run the Gateway for you - always on, and reachable from anywhere."),
        GatewayChoiceAction.JoinExisting => (
            "Join an existing Gateway",
            "Connect to a Gateway that is already running - on your network, over Tailscale, or on your account."),
        GatewayChoiceAction.Skip => (
            "Skip for now",
            "Use this Director on its own. You can connect a Gateway later from Settings."),
        _ => (string.Empty, string.Empty),
    };

    private static Border BuildComingSoonBadge(string reason) => new()
    {
        Background = Brush("#2A2A2A"),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = reason.ToUpperInvariant(),
            Foreground = Brush("#9A9A9A"),
            FontSize = 9.5,
            FontWeight = FontWeight.Bold,
        },
    };

    private void Choice_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Border)?.Tag is GatewayChoiceAction action)
            InvokeChoiceAction(action);
    }

    // The single dispatch for an activated choice card. Only Join and Skip do anything; Self-host and Hosted
    // are disabled cards that never reach here (no handler, IsEnabled=false), and if one somehow did it
    // would no-op. Kept separate so the render-level test can activate a card exactly as a click would.
    private void InvokeChoiceAction(GatewayChoiceAction action)
    {
        switch (action)
        {
            case GatewayChoiceAction.JoinExisting:
                // Join drives the existing scan / manual-URL / enroll flow.
                FileLog.Write("[GatewayConnectionPanel] choice: Join existing -> scan");
                StartScan();
                break;
            case GatewayChoiceAction.Skip:
                HandleSkip();
                break;
            case GatewayChoiceAction.UseHosted:
                // Hosted is live: a browser account sign-in whose token is enrolled at the one shared
                // hosted Gateway, which mints this machine's tenant-scoped device key. Same proven runner
                // the CLI's `enroll --hosted` uses - there is no address to type and nothing to provision.
                StartHostedEnroll();
                break;
            case GatewayChoiceAction.SelfHost:
                // Routed, but UNREACHABLE in production today: the card is still rendered disabled
                // and wires no handler, so a user cannot fire this. The route exists so the
                // transaction behind it is wired and pinned by a test rather than bolted on later
                // in the same change that first exposes it to a stranger's machine.
                StartSelfHost();
                break;
        }
    }

    /// <summary>
    /// Entry point for the self-host provisioning transaction.
    ///
    /// The panel does NOT own the sequencing, the ownership rules, or the rollback - those live in
    /// <see cref="CcDirector.Setup.Engine.SelfHostOrchestrator"/> so they can be proven without a
    /// machine. This method's only jobs are to start it and to render what it reports.
    ///
    /// It is deliberately not reachable from the user interface yet. Provisioning installs and
    /// starts a real Gateway, which cannot be verified on a developer machine that is already
    /// running one, so the card stays disabled until a real install proves the path on an isolated
    /// machine. A disabled card is honest; a button that half-provisions a Gateway is not.
    /// </summary>
    private void StartSelfHost()
    {
        FileLog.Write("[GatewayConnectionPanel] choice: Self-host -> provisioning transaction");
        SelfHostRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised when the self-host choice is activated. The consumer owns running the transaction, so
    /// the panel stays a renderer and the flow can be driven headlessly in a test.
    /// </summary>
    public event EventHandler? SelfHostRequested;

    // Raise the Skip verdict for the consumer to act on. Onboarding completes local-only (the #1809 seam,
    // done by the wizard, which then closes); Settings/status return to the choice (done here). The panel
    // does not decide which - it reads the plan's SkipBehavior verbatim.
    private void HandleSkip()
    {
        FileLog.Write($"[GatewayConnectionPanel] choice: Skip ({_skipBehavior})");
        SkipRequested?.Invoke(this, _skipBehavior);
        if (_skipBehavior == GatewaySkipBehavior.ReturnToChoice)
            ShowChoice();
    }

    // ---- #1808a R2 test hooks: drive the RENDERED choice + terminal emission headless -----------

    /// <summary>Inject a choice context this Windows test host cannot otherwise produce - specifically a Mac
    /// context (SelfHostSupported=false) - so the render test can prove Self-host is OMITTED there.</summary>
    internal void SetChoiceContextForTests(GatewayChoiceContext context) => _choiceContext = context;

    /// <summary>Render the choice for this panel's context (as OnAttached would), so a headless test can
    /// inspect the rendered cards and activate them exactly as a click would.</summary>
    internal void ShowChoiceForTests() => ShowChoice();

    /// <summary>The rendered choice cards (each a Border tagged with its action, IsEnabled per availability).
    /// Absent actions are not present. Lets a test assert Mac omission and disabled-card non-actionability at
    /// the render level, not just the Core value.</summary>
    internal IReadOnlyList<Control> ChoiceCardsForTests => ChoiceHost.Children;

    /// <summary>Activate a rendered choice card exactly as a real click would: a disabled/absent card does
    /// nothing (matching IsEnabled=false swallowing pointer input), an enabled card runs its action.</summary>
    /// <summary>
    /// Drive the dispatch DIRECTLY, bypassing the card's enabled state, so a test can pin that the
    /// self-host route is wired correctly while the card that would fire it is still disabled.
    /// <see cref="ActivateChoiceForTests"/> is the other half of that pair: it goes through the
    /// card and therefore proves the disabled card cannot fire at all.
    /// </summary>
    internal void InvokeChoiceForTests(GatewayChoiceAction action) => InvokeChoiceAction(action);

    internal void ActivateChoiceForTests(GatewayChoiceAction action)
    {
        foreach (var child in ChoiceHost.Children)
            if (child is Border { Tag: GatewayChoiceAction cardAction } card && cardAction == action)
            {
                if (card.IsEnabled)
                    InvokeChoiceAction(action);
                return;
            }
    }

    /// <summary>Drive the panel to its Done view so it emits the terminal ConnectionSettled result, letting a
    /// test assert the emitted outcome (NotReady in this slice) without a live Gateway.</summary>
    internal void EmitTerminalForTests()
        => ShowDone(GatewayConfig.Load(), GatewayAccountStatus.NotConfigured());

    // ---- Test hooks: which step sub-panel is currently shown, and the surfaced failure text. These let a
    // test pin the VISIBLE outcome (a failure is actually shown, disconnect actually returns to the choice)
    // rather than just inspecting hidden state - so removing ShowFailure / ShowChoice reddens.
    internal bool IsShowingChoiceForTests => ChoicePanel.IsVisible;
    internal bool IsShowingConnectingForTests => ConnectingPanel.IsVisible;

    /// <summary>The connecting view's sign-in address row, its address box, and its two buttons.</summary>
    internal (Control Row, TextBox Address, Button Copy, Button Open) SignInAddressForTests =>
        (SignInAddressRow, SignInAddressBox, CopySignInAddressButton, OpenSignInAddressButton);

    /// <summary>The line under the address that says it was copied, or that Windows could not open a browser.</summary>
    internal TextBlock SignInAddressStatusForTests => SignInAddressStatus;
    internal bool IsShowingFailureForTests => FailedPanel.IsVisible;
    internal string FailureSummaryForTests => FailureSummaryText.Text ?? string.Empty;

    // ---- Step 1a: scan --------------------------------------------------------------------------

    /// <summary>
    /// True while a scan is running (issue #1107, item 4).
    ///
    /// The feedback on this one was already right - "SCANNING..." appears before the await - but there was
    /// no guard, so every Rescan click started another concurrent ScanAsync and every one of them called
    /// RenderFound on the way out. The list you ended up looking at was whichever scan happened to finish
    /// LAST, not the one you most recently asked for. That is on the gateway connect screen, which is
    /// exactly where a new user is when they are anxious and clicking.
    ///
    /// The guard lives here rather than on the Rescan button because StartScan has a second caller
    /// (OnAttached kicks a rediscovery scan): guarding only the click would still let an automatic scan and
    /// a clicked one race, which is the same defect with a less obvious trigger.
    /// </summary>
    private bool _scanInFlight;

    private async void StartScan()
    {
        if (_scanInFlight)
        {
            FileLog.Write("[GatewayConnectionPanel] StartScan: ignored, a scan is already running");
            return;
        }
        _scanInFlight = true;

        _connecting = false;
        StopPolling();
        CountPillText.Text = "SCANNING...";
        ScanningProgress.IsVisible = true;
        ResultsArea.IsVisible = false;
        ShowOnly(ConnectPanel);
        try
        {
            var found = await _scan.ScanAsync();
            RenderFound(found);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] scan FAILED: {ex.Message}");
            RenderFound(Array.Empty<FoundGateway>());
        }
        finally
        {
            _scanInFlight = false;
        }
    }

    private void RenderFound(IReadOnlyList<FoundGateway> found)
    {
        ScanningProgress.IsVisible = false;
        CountPillText.Text = $"{found.Count} FOUND";

        OptionsHost.Children.Clear();
        var recommended = RecommendedIndex(found);
        for (var i = 0; i < found.Count; i++)
            OptionsHost.Children.Add(BuildOptionRow(found[i], recommended == i));

        var any = found.Count > 0;
        NoneFoundText.IsVisible = !any;
        // When nothing was found, open the manual-entry section so the fallback is one step away.
        AdvancedToggle.IsChecked = !any;
        ResultsArea.IsVisible = true;
        ShowOnly(ConnectPanel);
        FileLog.Write($"[GatewayConnectionPanel] scan rendered: {found.Count} pick(s), recommended index {recommended}");
    }

    private void Rescan_Click(object? sender, RoutedEventArgs e) => StartScan();

    // ---- Repair mode (Phase 5): named failing leg + inline diagnostics --------------------------

    private CancellationTokenSource? _diagCts;

    // Show the repair banner and name the failing leg from the live monitor. The rediscovery scan
    // (StartScan, kicked right after in OnAttached) provides the one-click new-address fix; the
    // diagnostics ladder runs on demand when the user expands it.
    private void EnterRepairMode()
    {
        var monitor = (global::Avalonia.Application.Current as App)?.ControlApiHost?.GatewayMonitor;

        // The teaching intro is for a first-time connect; in repair we lead with the problem instead.
        IntroText.IsVisible = false;
        ConnectHeaderTitle.Text = "Reconnect to your Gateway";
        RepairBanner.IsVisible = true;

        var (title, summary) = RepairCopyFor(monitor);
        RepairTitle.Text = title;
        RepairSummary.Text = summary;
        FileLog.Write($"[GatewayConnectionPanel] repair mode: {title}");
    }

    // Name the failing leg (decision 11) from the monitor's already-earned verdict.
    private static (string Title, string Summary) RepairCopyFor(GatewayConnectionMonitor? monitor)
    {
        if (monitor is null)
            return ("Reconnect to your Gateway",
                "The connection stopped working. Pick your Gateway below to reconnect - we re-scanned for its current address.");

        if (monitor.Status == GatewayConnectionStatus.NoTailnetIdentity)
            return ("This machine has no Tailscale identity",
                (monitor.FailureSummary ?? "Start Tailscale on this machine, or set the Director public URL under Advanced.")
                + " Once Tailscale is up this heals automatically; you can also pick your Gateway below to reconnect.");

        // The callback-leg branch is gone with the handshake (tunnel-only): there is no Gateway->Director
        // dial any more, so "the callback leg failed" is not a thing that can happen. The monitor's own
        // summary says why the tunnel is not up.
        var wasWorking = monitor.LastVerifiedAt is not null;
        var title = wasWorking ? "Your Gateway became unreachable" : "Could not connect to your Gateway";
        var summary = (monitor.FailureSummary ?? "The connection could not be completed.")
              + " It may have moved - pick its current address below to reconnect. Show diagnostics for the full check.";
        return (title, summary);
    }

    private void DiagnosticsToggle_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var open = DiagnosticsToggle.IsChecked == true;
        // Guard: fires during control construction, before the sibling fields are assigned.
        if (DiagnosticsPanel is not null) DiagnosticsPanel.IsVisible = open;
        if (DiagnosticsCaret is not null) DiagnosticsCaret.Text = open ? "^" : "v";
        // Run (or re-run) the ladder each time it is opened, so it is always fresh.
        if (open) _ = RunDiagnosticsAsync();
    }

    // Reuse the troubleshooter's diagnostic engine INLINE (spec section 8: the dialog-as-destination is
    // gone, the logic is reused): report the live connection state, then walk GatewayConnectivitySelfTest's
    // ladder and render each rung. No new verification is built here.
    //
    // Gateway Cleanup mission (tunnel-only): opening this used to RUN the two-way handshake first
    // (host.VerifyGatewayNowAsync). That handshake's Gateway route was deleted at the cut, so the call could
    // only ever 404 - and on the 404 it wrote "the Gateway does not support the verify handshake - update the
    // Gateway" into the monitor as a FAILURE. The result: opening diagnostics on a perfectly healthy,
    // tunnel-connected Director flipped its light from green to red and told the owner to go update a Gateway
    // that was already correct. Diagnostics now REPORT the connection state; they do not manufacture one.
    private async Task RunDiagnosticsAsync()
    {
        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        if (host is null)
        {
            DiagnosticsVerdict.Text = "The Control API is not running yet, so diagnostics cannot run.";
            return;
        }

        _diagCts?.Cancel();
        _diagCts = new CancellationTokenSource();
        var ct = _diagCts.Token;

        DiagnosticsHost.Children.Clear();
        DiagnosticsRunning.IsVisible = true;
        DiagnosticsVerdict.Text = "Running the diagnostic ladder...";
        try
        {
            DiagnosticsVerdict.Text = DiagnosticsVerdictText(host.GatewayMonitor);

            var gatewayUrl = await Task.Run(() =>
            {
                var cfg = GatewayConfig.Load();
                return cfg.IsEnabled ? cfg.Url : null;
            }, ct);

            // The ladder is outbound-only now: configured -> Gateway answers -> tunnel connected.
            // There is no advertised endpoint and no local port to check; the Director listens on
            // nothing (Remove-the-network-port mission).
            var selfTest = new GatewayConnectivitySelfTest(
                host.DirectorId, gatewayUrl, () => host.GatewayMonitor.Status);

            DiagnosticsHost.Children.Clear();
            var index = 0;
            await foreach (var rung in selfTest.RunAsync(ct))
            {
                index++;
                DiagnosticsHost.Children.Add(BuildRungRow(index, rung));
            }
        }
        catch (OperationCanceledException) { /* panel left or re-run superseded */ }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] diagnostics FAILED: {ex.Message}");
            DiagnosticsVerdict.Text = $"Diagnostics failed to run: {ex.Message}";
        }
        finally
        {
            DiagnosticsRunning.IsVisible = false;
        }
    }

    private static string DiagnosticsVerdictText(GatewayConnectionMonitor m) => m.Status switch
    {
        GatewayConnectionStatus.Connected => "CONNECTED - this Director's tunnel to the Gateway is up, which is what lets the two reach each other.",
        GatewayConnectionStatus.Failed => $"NOT CONNECTED - {m.FailureSummary}",
        GatewayConnectionStatus.NoTailnetIdentity => $"No tailnet identity - {m.FailureSummary}",
        GatewayConnectionStatus.SubscriptionRequired => $"SUBSCRIPTION REQUIRED - {m.FailureSummary}",
        GatewayConnectionStatus.KeyRefused => $"KEY REFUSED - {m.FailureSummary}",
        GatewayConnectionStatus.Connecting => "Connecting - the tunnel is dialing. Re-open diagnostics in a few seconds.",
        _ => "No Gateway is configured.",
    };

    // Render one ladder rung: a mark, the title + what was found, and the fix (with Copy command, and
    // "Fix it now" for the auto-fixable serve-mapping rung - the same reused capability).
    private Control BuildRungRow(int index, LadderRung rung)
    {
        var (mark, markColor) = rung.Status switch
        {
            RungStatus.Pass => ("OK", "#22C55E"),
            RungStatus.Fail => ("X", "#EF4444"),
            RungStatus.Info => ("i", "#3B82F6"),
            _ => ("-", "#666666"),
        };
        var dim = rung.Status == RungStatus.Skipped;

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = $"{index}. {rung.Title}",
            Foreground = Brush(dim ? "#666666" : "#CCCCCC"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
        });
        body.Children.Add(new TextBlock
        {
            Text = rung.Found,
            Foreground = Brush(dim ? "#666666" : "#999999"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        if (rung.Fix is { } fix)
        {
            body.Children.Add(new Border
            {
                Background = Brush("#1E1E1E"),
                BorderBrush = Brush("#3C3C3C"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 5),
                Margin = new Thickness(0, 6, 0, 0),
                Child = new TextBlock
                {
                    Text = fix,
                    Foreground = Brush("#CCCCCC"),
                    FontSize = 11,
                    FontFamily = MonoFont,
                    TextWrapping = TextWrapping.Wrap,
                },
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0),
            };
            var copyBtn = new Button
            {
                Content = "Copy command",
                Padding = new Thickness(12, 4),
                Background = Brush("#3C3C3C"),
                Foreground = Brush("#CCCCCC"),
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            copyBtn.Click += async (_, _) =>
            {
                try
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard is not null) await clipboard.SetTextAsync(fix);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayConnectionPanel] copy command FAILED: {ex.Message}");
                }
            };
            buttons.Children.Add(copyBtn);
            body.Children.Add(buttons);
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*"), Margin = new Thickness(4, 0, 4, 0) };
        var markBlock = new TextBlock
        {
            Text = mark,
            Foreground = Brush(markColor),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        Grid.SetColumn(markBlock, 0);
        Grid.SetColumn(body, 1);
        row.Children.Add(markBlock);
        row.Children.Add(body);

        return new Border
        {
            BorderBrush = Brush("#2E2E2E"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 8),
            Child = row,
        };
    }

    // The Recommended rule (spec section 5): the most stable LOCAL name reachable, in priority order This
    // computer > On your network > Over Tailscale. Tailscale wins only when it is the sole find, which the
    // priority ranking already yields (a local kind, if present, always outranks it).
    private static int RecommendedIndex(IReadOnlyList<FoundGateway> found)
    {
        var best = -1;
        var bestRank = int.MaxValue;
        for (var i = 0; i < found.Count; i++)
        {
            var rank = KindRank(found[i].Kind);
            if (rank < bestRank)
            {
                bestRank = rank;
                best = i;
            }
        }
        return best;
    }

    private static int KindRank(GatewayLocationKind kind) => kind switch
    {
        GatewayLocationKind.ThisMachine => 0,
        GatewayLocationKind.LocalNetwork => 1,
        GatewayLocationKind.Tailnet => 2,
        _ => 3,
    };

    // ---- Option row construction (built in code to match the mockup's mixed-emphasis copy) ------

    private Control BuildOptionRow(FoundGateway gateway, bool recommended)
    {
        var copy = CopyFor(gateway);

        var icon = new global::Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(copy.IconGeometry),
            Stroke = Brush(copy.StrokeColor),
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var iconTile = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(8),
            Background = Brush(copy.TileColor),
            Child = icon,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        Grid.SetColumn(iconTile, 0);

        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameRow.Children.Add(new TextBlock
        {
            Text = copy.Name,
            Foreground = Brush("#E6E6E6"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        nameRow.Children.Add(new TextBlock
        {
            Text = copy.Address,
            FontFamily = MonoFont,
            Foreground = Brush("#888888"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (recommended)
            nameRow.Children.Add(BuildRecommendedBadge());

        var when = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#888888") };
        when.Inlines!.Add(new Run(copy.WhenPrefix));
        when.Inlines.Add(new Run(copy.WhenBold) { Foreground = Brush("#AAAAAA"), FontWeight = FontWeight.SemiBold });
        when.Inlines.Add(new Run(copy.WhenSuffix));

        var info = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(nameRow);
        info.Children.Add(when);
        Grid.SetColumn(info, 1);

        var chevron = new TextBlock
        {
            Text = ">",
            FontSize = 18,
            Foreground = Brush(recommended ? "#34D06E" : "#666666"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(chevron, 2);

        var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        inner.Children.Add(iconTile);
        inner.Children.Add(info);
        inner.Children.Add(chevron);

        var card = new Border
        {
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(recommended ? "#2E7D50" : "#3C3C3C"),
            Background = recommended ? RecommendedBackground() : Brush("#252526"),
            Padding = new Thickness(15, 14),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = inner,
            Tag = gateway,
        };
        card.PointerPressed += Pick_PointerPressed;
        WireHover(card, recommended);

        // A green left-accent bar for the recommended row, overlaid on the card's left edge.
        var outer = new Grid();
        outer.Children.Add(card);
        if (recommended)
        {
            outer.Children.Add(new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(3),
                Background = Brush("#34D06E"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 8, 0, 8),
                IsHitTestVisible = false,
            });
        }
        return outer;
    }

    private static Border BuildRecommendedBadge() => new()
    {
        Background = Brush("#1B3A2A"),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = "RECOMMENDED",
            Foreground = Brush("#34D06E"),
            FontSize = 9.5,
            FontWeight = FontWeight.Bold,
        },
    };

    // Non-recommended cards get a subtle hover; the recommended card keeps its green wash.
    private static void WireHover(Border card, bool recommended)
    {
        if (recommended) return;
        card.PointerEntered += (_, _) =>
        {
            card.Background = Brush("#2E2E30");
            card.BorderBrush = Brush("#4A4A4A");
        };
        card.PointerExited += (_, _) =>
        {
            card.Background = Brush("#252526");
            card.BorderBrush = Brush("#3C3C3C");
        };
    }

    private static IBrush RecommendedBackground() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#242E7D50"), 0),
            new GradientStop(Color.Parse("#E6252526"), 0.34),
            new GradientStop(Color.Parse("#FF252526"), 1),
        },
    };

    // The per-kind name, address, and "when to use this" copy (spec section 5 - exact strings).
    private static OptionCopy CopyFor(FoundGateway gateway) => gateway.Kind switch
    {
        GatewayLocationKind.ThisMachine => new OptionCopy(
            Name: "This computer",
            Address: Environment.MachineName,
            WhenPrefix: "The Gateway runs on this machine. ",
            WhenBold: "Fastest and always available",
            WhenSuffix: " - the computer name does not change, so this keeps working.",
            TileColor: "#1F2A22",
            StrokeColor: "#34D06E",
            IconGeometry: MonitorGeometry),

        GatewayLocationKind.LocalNetwork => new OptionCopy(
            Name: "On your network",
            Address: SafeHost(gateway.Url),
            WhenPrefix: "The Gateway is on another computer on your network. The computer name rarely changes, so this keeps working - ",
            WhenBold: "best when you are on the same network",
            WhenSuffix: ".",
            TileColor: "#172433",
            StrokeColor: "#5AA9F0",
            IconGeometry: MonitorGeometry),

        _ => new OptionCopy(
            Name: "Over Tailscale",
            Address: SafeHost(gateway.Url),
            WhenPrefix: "Reaches the Gateway ",
            WhenBold: "from any network",
            WhenSuffix: ". Use this on a laptop you travel with, or from a remote office - anywhere you are not on the same network as the Gateway.",
            TileColor: "#172433",
            StrokeColor: "#5AA9F0",
            IconGeometry: GlobeGeometry),
    };

    private static string SafeHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    private readonly record struct OptionCopy(
        string Name, string Address, string WhenPrefix, string WhenBold, string WhenSuffix,
        string TileColor, string StrokeColor, string IconGeometry);

    // ---- Step 1b: pick / manual entry ----------------------------------------------------------

    private void Pick_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Border)?.Tag is FoundGateway pick)
            // A Gateway anywhere but this machine is remote - it takes the sign-in enroll seam, not the
            // loopback fast path (#1808a drops the same-machine-only limit).
            _ = ConnectToAsync(pick.Url, pick.Label, remote: pick.Kind != GatewayLocationKind.ThisMachine);
    }

    private void AdvancedToggle_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var open = AdvancedToggle.IsChecked == true;
        // Guard: this can fire during control construction, before the sibling fields are assigned.
        if (ManualEntryPanel is not null) ManualEntryPanel.IsVisible = open;
        if (AdvancedCaret is not null) AdvancedCaret.Text = open ? "^" : "v";
    }

    private void ManualConnect_Click(object? sender, RoutedEventArgs e)
    {
        ManualErrorText.IsVisible = false;
        var raw = ManualUrlBox.Text ?? string.Empty;
        if (!GatewayAddress.TryNormalize(raw, out var url, out var error))
        {
            ManualErrorText.Text = error ?? "That is not a valid address.";
            ManualErrorText.IsVisible = true;
            return;
        }
        // A manually-entered address is treated as remote: it takes the sign-in enroll seam, which works for
        // any reachable Gateway (LAN, tailnet, or account) and is not limited to this machine (#1808a).
        _ = ConnectToAsync(url, url, remote: true);
    }

    // ---- Step 1c/d/e: connect (the click IS the test) ------------------------------------------

    // Internal so the #1808a R2 integration tests can drive the connect transaction headless and await it.
    internal async Task ConnectToAsync(string url, string label, bool remote)
    {
        var attempt = ++_attemptId;
        _lastAttempt = (url, label, remote);
        _connecting = true;

        // A REMOTE Join (a Gateway off this machine, or a manually-entered address) ALWAYS verifies and
        // enrolls against the SELECTED url through the runner - REGARDLESS of any token saved for a PREVIOUS
        // Gateway (#1808a R1). It never sends the old token to the candidate, and it touches no active
        // Gateway config until the runner's verified-success persistence commits url+key atomically, so a
        // cancel/failure leaves the previously-saved connection untouched. This is the whole security +
        // data-loss boundary; the same-machine loopback fast path below keeps its distinct route.
        if (remote)
        {
            await RemoteEnrollAndHandshakeAsync(url, label, attempt);
            return;
        }

        ConnectingTitle.Text = $"Connecting to {label}...";
        LegReachMarker.Text = "[.]";
        LegCallbackMarker.Text = "[.]";
        ShowOnly(ConnectingPanel);

        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        if (host is null)
        {
            ShowFailure("The Control API is not running yet, so this Director cannot connect.",
                "Wait for the Director to finish starting, then try again.");
            return;
        }

        try
        {
            // Same-machine (co-located) fast path only. Write the chosen local address, then re-apply so the
            // Director runs a fresh handshake against it.
            await Task.Run(() => CcDirectorConfigService.MergePatch(new JsonObject
            {
                ["gateway"] = new JsonObject { ["url"] = url },
            }));

            // Epic #1069 (fresh-device unblock): a brand-new Director holds NO Gateway key, and the register
            // handshake needs one, so it would 401. Earn the key FIRST via the co-located loopback
            // enrollment (which is public and self-guards), then the handshake succeeds. On the common
            // same-machine case where the Gateway is already signed in this reaches green with ZERO extra
            // clicks. A signed-out Gateway (409) routes to the browser sign-in.
            if (!HasDeviceToken(GatewayConfig.Load()))
            {
                switch (await TryEnrollFirstAsync(url, host))
                {
                    case EnrollFirst.SignInNeeded:
                        ShowSignIn();
                        return;
                    case EnrollFirst.RemoteNotSupported:
                        // A co-located pick the Gateway reports as non-loopback: route to the remote seam,
                        // which verifies against the selected url before any config change.
                        await RemoteEnrollAndHandshakeAsync(url, label, attempt);
                        return;
                    // Enrolled (key earned) or FellThrough (no local Gateway to enroll with) both continue to
                    // the handshake below - a real failure there surfaces through the normal verdict path.
                }
            }

            EnsureSubscribed(host.GatewayMonitor);
            await host.ReapplyGatewayAsync();
            _ = TimeoutAsync(attempt);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] ConnectTo FAILED to start: {ex.Message}");
            ShowFailure($"Could not start connecting: {ex.Message}", null);
        }
    }

    // Join a REMOTE Gateway (#1808a): sign in with DevThrottle and enroll this Director against the explicit
    // gateway URL via the remote-capable GatewayAccountEnrollRunner seam, then re-apply and run the normal
    // handshake so the verdict path drives to Sign-in/Done. This is the seam that removes the panel's old
    // same-machine-only enrollment limit - it works for a Gateway on the LAN, over Tailscale, or on the
    // account, not just one on this machine. The runner reuses the PUBLIC /signin surface (Google + GitHub +
    // email magic-link); on success it has already persisted the gateway URL + this device's local key.
    private async Task RemoteEnrollAndHandshakeAsync(string url, string label, int attempt)
    {
        _connecting = true;
        ConnectingTitle.Text = $"Signing in to DevThrottle to join {label}...";
        LegReachMarker.Text = "[.]";
        LegCallbackMarker.Text = "[.]";
        ShowOnly(ConnectingPanel);

        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        var directorId = DirectorIdOverride ?? host?.DirectorId;
        if (directorId is null)
        {
            ShowFailure("The Control API is not running yet, so this Director cannot connect.",
                "Wait for the Director to finish starting, then try again.");
            return;
        }

        _remoteEnrollCts?.Cancel();
        _remoteEnrollCts = new CancellationTokenSource();
        var ct = _remoteEnrollCts.Token;

        OperationResult<MobileEnrollmentResponse> result;
        try
        {
            // The runner signs in on the PUBLIC /signin surface, registers the workstation, enrolls against
            // the SELECTED url, and persists url+key on verified success ONLY. Nothing is written before this
            // returns success, so a cancel/failure never mutates the previously-saved connection.
            var enroll = RemoteEnrollSeam ?? DefaultRemoteEnroll;
            result = await enroll(url, directorId, Environment.MachineName, ct);
        }
        catch (OperationCanceledException)
        {
            // The panel was left, or a newer attempt superseded this one; leave the view (and config) alone.
            return;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] remote enroll ERROR: {ex.Message}");
            ShowFailure($"Could not sign in and join the Gateway: {ex.Message}", null);
            return;
        }

        if (attempt != _attemptId) return; // superseded by a newer connect

        if (!result.Success)
        {
            // Verification failed: the runner persisted nothing, so the previously-saved connection is
            // untouched. Surface the reason only.
            ShowFailure(result.ErrorMessage ?? "Could not join the Gateway.", null);
            return;
        }

        // The runner committed the verified url + this device's local key atomically. Re-apply so the running
        // client authenticates with the NEW credential (never the old token), then the handshake settles.
        FileLog.Write("[GatewayConnectionPanel] remote enroll succeeded; re-applying and handshaking");
        if (host is not null)
            EnsureSubscribed(host.GatewayMonitor);
        var reapply = ReapplyGatewaySeam ?? (host is not null ? host.ReapplyGatewayAsync : null);
        if (reapply is null)
        {
            ShowFailure("The Control API is not running yet, so this Director cannot finish connecting.", null);
            return;
        }
        // OBSERVE the re-apply. The verified credential is ALREADY persisted, so if reapply faults the awaited
        // Task would fault unobserved in this fire-and-forget flow, leaving the panel spinning on "Connecting".
        // Catch it and land on a named failure that says the enroll succeeded but could not be applied live,
        // with a retry affordance - never a stuck spinner.
        try
        {
            await reapply();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] remote enroll: re-apply after enroll FAILED: {ex.Message}");
            ShowFailure(
                "Signed in and enrolled with the Gateway, but this Director could not apply the new connection right now.",
                "Try again, or restart the Director to finish connecting.");
            return;
        }
        _ = TimeoutAsync(attempt);
    }

    // The real remote-enroll seam: verify + enroll against the selected url and persist on success only.
    private static Task<OperationResult<MobileEnrollmentResponse>> DefaultRemoteEnroll(
        string url, string deviceId, string machineName, CancellationToken ct)
        => new GatewayAccountEnrollRunner().VerifyAndSaveAsync(url, deviceId, machineName, ct);

    // ---- Step 0->Done: Use hosted (#1808c/#1811) ------------------------------------------------

    // Fire the hosted-enroll transaction from the choice card. Kept separate (fire-and-forget) so the render
    // test can activate the enabled card exactly as a click would, while the awaitable core runs the work.
    private void StartHostedEnroll()
    {
        FileLog.Write("[GatewayConnectionPanel] choice: Use hosted -> hosted enroll");
        _ = HostedEnrollAndHandshakeAsync();
    }

    // Join DevThrottle's HOSTED Gateway (#1808c): the SAME proven flow the CLI's `enroll --hosted` runs. A
    // browser account sign-in whose account token goes straight to the hosted Gateway
    // (GatewayAccountEnrollRunner.SignInAndEnrollHostedAsync), which mints this machine's tenant-scoped device
    // key in one step and persists the hosted url + key on verified success ONLY. There is no address to type
    // and nothing to provision - enrolling IS the join. On success we re-apply and run the normal handshake so
    // the verdict path drives to the signed-in Done view (which then reads "Connected to Gateway on" the
    // hosted host from HostedGateway.ResolveUrl). Nothing is written on cancel or failure, so a
    // previously-saved connection is untouched. Internal so a headless test can drive and await it with the
    // enroll seam injected.
    internal async Task HostedEnrollAndHandshakeAsync()
    {
        var attempt = ++_attemptId;
        _connecting = true;
        // Hosted has no address; clear any stale Join attempt so a post-failure "Try again" does not retry a
        // previous Join URL - it falls back to the scan instead.
        _lastAttempt = null;
        ConnectingTitle.Text = "Signing in to DevThrottle to join the hosted Gateway...";
        LegReachMarker.Text = "[.]";
        LegCallbackMarker.Text = "[.]";
        ShowOnly(ConnectingPanel);

        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        var directorId = DirectorIdOverride ?? host?.DirectorId;
        if (directorId is null)
        {
            ShowFailure("The Control API is not running yet, so this Director cannot connect.",
                "Wait for the Director to finish starting, then try again.");
            return;
        }

        _remoteEnrollCts?.Cancel();
        _remoteEnrollCts = new CancellationTokenSource();
        var ct = _remoteEnrollCts.Token;

        OperationResult<MobileEnrollmentResponse> result;
        try
        {
            // The runner signs in on the PUBLIC /signin surface and enrolls the account token at the hosted
            // Gateway, which mints and returns this machine's tenant-scoped device key. It persists hosted
            // url + key on verified success ONLY, so a cancel/failure never mutates the saved connection.
            var enroll = HostedEnrollSeam ?? DefaultHostedEnroll;
            result = await enroll(directorId, Environment.MachineName, this, ct);
        }
        catch (OperationCanceledException)
        {
            // The panel was left, or a newer attempt superseded this one; leave the view (and config) alone.
            return;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] hosted enroll ERROR: {ex.Message}");
            ShowFailure($"Could not sign in and join the hosted Gateway: {ex.Message}", null);
            return;
        }

        if (attempt != _attemptId) return; // superseded by a newer connect

        if (!result.Success)
        {
            // Enrollment failed: the runner persisted nothing, so any previously-saved connection is
            // untouched. Surface the reason inline - never a silent no-op.
            ShowFailure(result.ErrorMessage ?? "Could not join the hosted Gateway.", null);
            return;
        }

        // The runner committed the verified hosted url + this device's local key atomically. Re-apply so the
        // running client authenticates with the NEW credential, then the handshake settles to Sign-in/Done.
        FileLog.Write("[GatewayConnectionPanel] hosted enroll succeeded; re-applying and handshaking");
        if (host is not null)
            EnsureSubscribed(host.GatewayMonitor);
        var reapply = ReapplyGatewaySeam ?? (host is not null ? host.ReapplyGatewayAsync : null);
        if (reapply is null)
        {
            ShowFailure("The Control API is not running yet, so this Director cannot finish connecting.", null);
            return;
        }
        // OBSERVE the re-apply. The hosted credential is ALREADY persisted at this point, so if reapply faults
        // the awaited Task would fault unobserved in this fire-and-forget flow, leaving the panel spinning on
        // "Connecting" with partial live state. Catch it and land on a named failure that says the enroll
        // succeeded but could not be applied live, with a retry affordance - never a stuck spinner.
        try
        {
            await reapply();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] hosted enroll: re-apply after enroll FAILED: {ex.Message}");
            ShowFailure(
                "Signed in and enrolled with the hosted Gateway, but this Director could not apply the new connection right now.",
                "Try again, or restart the Director to finish connecting.");
            return;
        }
        _ = TimeoutAsync(attempt);
    }

    // The real hosted-enroll seam: sign in with DevThrottle, ask which team this Director is for when there is
    // one to choose (screen D1, devthrottle_internal#2311), and enroll at the hosted Gateway for that team's device
    // key, persisting the hosted url + key + team on success only.
    private Task<OperationResult<MobileEnrollmentResponse>> DefaultHostedEnroll(
        string deviceId, string machineName, ISignInAddressDisplay addressDisplay, CancellationToken ct)
    {
        var owner = TopLevel.GetTopLevel(this) as Window
            ?? throw new InvalidOperationException("The Gateway connection panel is not in a window, so it cannot ask which team.");
        return HostedTeamSetup.SignInChooseTeamAndEnrollAsync(owner, deviceId, machineName, addressDisplay, ct);
    }

    /// <summary>
    /// Put the sign-in address on the connecting view with Copy and Open in browser (issue #3504). The sign-in
    /// calls this before it asks Windows to open the browser, so the address is there even when nothing opens.
    /// </summary>
    private void ShowSignInAddress(string address)
    {
        FileLog.Write("[GatewayConnectionPanel] ShowSignInAddress");
        _signInAddress = address;
        SignInAddressBox.Text = address;
        SignInAddressStatus.IsVisible = false;
        SignInAddressRow.IsVisible = true;
    }

    void ISignInAddressDisplay.Show(string address) => ShowSignInAddress(address);

    void ISignInAddressDisplay.BrowserDidNotOpen(string reason)
    {
        FileLog.Write($"[GatewayConnectionPanel] BrowserDidNotOpen: {reason}");
        ShowSignInAddressStatus(SignInAddressActions.BrowserDidNotOpenMessage(reason));
    }

    // The sign-in stopped waiting, so nothing answers at the address any more (issue #3504 review).
    void ISignInAddressDisplay.Withdraw() => ClearSignInAddress();

    private void ClearSignInAddress()
    {
        _signInAddress = null;
        SignInAddressBox.Text = "";
        SignInAddressStatus.IsVisible = false;
        SignInAddressRow.IsVisible = false;
    }

    private async void CopySignInAddress_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[GatewayConnectionPanel] CopySignInAddress_Click");
        if (_signInAddress is null) return;
        try
        {
            await SignInAddressActions.CopyAsync(this, _signInAddress);
            ShowSignInAddressStatus(SignInAddressActions.CopiedMessage);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] CopySignInAddress_Click FAILED: {ex.Message}");
            ShowSignInAddressStatus($"Could not copy the address: {ex.Message}. Select it above and copy it by hand.");
        }
    }

    private void OpenSignInAddress_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[GatewayConnectionPanel] OpenSignInAddress_Click");
        if (_signInAddress is null) return;
        try
        {
            SignInAddressActions.Open(_signInAddress);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] OpenSignInAddress_Click FAILED: {ex.Message}");
            ShowSignInAddressStatus($"Could not open a browser: {ex.Message}. Copy the address instead.");
        }
    }

    private void ShowSignInAddressStatus(string message)
    {
        SignInAddressStatus.Text = message;
        SignInAddressStatus.IsVisible = true;
    }

    private enum EnrollFirst { Enrolled, SignInNeeded, RemoteNotSupported, FellThrough }

    // Try to earn this device's first Gateway key via the co-located loopback enrollment (epic #1069 A3).
    // Dials the LOOPBACK address explicitly (127.0.0.1 + the local Gateway port from the pick) so the
    // Gateway's guardrail-1 IsLoopback check passes even though "This computer" displays the machine name.
    private async Task<EnrollFirst> TryEnrollFirstAsync(string pickedUrl, ControlApiHost host)
    {
        var loopbackUrl = BuildLoopbackEnrollUrl(pickedUrl);
        var result = await GatewayEnrollmentClient.EnrollSignedInAsync(
            loopbackUrl, token: null, host.DirectorId, Environment.MachineName, "windows");

        switch (result.Outcome)
        {
            case EnrollOutcome.Enrolled:
                // Store the key under the address the running client will actually register with (the pick),
                // then the re-apply below registers WITH the key -> handshake -> green.
                await Task.Run(() => GatewayCredentialStore.SaveEnrolledKey(pickedUrl, result.Value!.DeviceKey));
                FileLog.Write("[GatewayConnectionPanel] enroll-first: device key issued (zero-click, Gateway already signed in)");
                return EnrollFirst.Enrolled;
            case EnrollOutcome.GatewayNotSignedIn:
                FileLog.Write("[GatewayConnectionPanel] enroll-first: Gateway not signed in -> browser sign-in");
                return EnrollFirst.SignInNeeded;
            case EnrollOutcome.NotLoopback:
                FileLog.Write("[GatewayConnectionPanel] enroll-first: not a loopback caller -> remote device (epic #1069 case B)");
                return EnrollFirst.RemoteNotSupported;
            default:
                FileLog.Write($"[GatewayConnectionPanel] enroll-first: no local Gateway to enroll with ({result.Message}); continuing to the handshake");
                return EnrollFirst.FellThrough;
        }
    }

    // The same-machine enroll MUST be dialed at loopback (guardrail 1 checks the caller's remote IP with
    // IPAddress.IsLoopback). Take the port from the pick - which for "This computer" is the local Gateway's
    // own port (issue-1233) - and force the host to the literal 127.0.0.1 (deterministic over "localhost",
    // which could resolve to the IPv6 ::1). If this is wrong the guard 403s, so keep this construction (and
    // its test) intact.
    internal static string BuildLoopbackEnrollUrl(string pickedUrl)
    {
        var port = EndpointProbe.DefaultGatewayPort;
        if (Uri.TryCreate(pickedUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
            port = uri.Port;
        return $"http://127.0.0.1:{port}";
    }

    private void EnsureSubscribed(GatewayConnectionMonitor monitor)
    {
        _monitor = monitor;
        if (_subscribed) return;
        monitor.Changed += OnMonitorChanged;
        _subscribed = true;
    }

    private void OnMonitorChanged()
    {
        var monitor = _monitor;
        if (monitor is null) return;
        // Changed may fire on any thread; settle the UI on the UI thread.
        Dispatcher.UIThread.Post(() => ApplyVerdict(monitor));
    }

    private void ApplyVerdict(GatewayConnectionMonitor monitor)
    {
        // Only an initiated attempt drives the view; ignore background heartbeat churn otherwise.
        if (!_connecting) return;

        switch (monitor.Status)
        {
            case GatewayConnectionStatus.Connected:
                OnHandshakeVerified();
                break;
            case GatewayConnectionStatus.Failed:
            case GatewayConnectionStatus.NoTailnetIdentity:
                // Epic #1069 A3 fallback: a 401/Unauthorized handshake means this device is not authorized
                // yet (e.g. a stale key). Route to Sign in - which re-enrolls and earns a fresh key - rather
                // than a dead-end failure. (A brand-new no-key device is already handled by enroll-first in
                // ConnectTo, so this covers the has-a-bad-key case.)
                if (IsUnauthorizedFailure(monitor.FailureSummary))
                {
                    FileLog.Write("[GatewayConnectionPanel] handshake unauthorized (401) -> routing to Sign in");
                    ShowSignIn();
                }
                else
                {
                    ShowFailure(monitor.FailureSummary ?? "The connection could not be completed.", DeriveFix(monitor));
                }
                break;
            case GatewayConnectionStatus.KeyRefused:
                // The key this attempt dialled with is refused for good: Sign in, which sets the Director up again
                // with a new key (devthrottle_internal#2311, live proof F3).
                FileLog.Write($"[GatewayConnectionPanel] key refused ({monitor.KeyRefusal?.Kind}) -> routing to Sign in");
                ShowSignIn();
                break;
            case GatewayConnectionStatus.Connecting:
            case GatewayConnectionStatus.NotConfigured:
                // Stay on the connecting view until a final verdict arrives (or the timeout fires).
                break;
        }
    }

    private async Task TimeoutAsync(int attempt)
    {
        try { await Task.Delay(ConnectTimeout); }
        catch { /* ignored */ }

        if (attempt != _attemptId || !_connecting) return; // superseded or already settled

        var monitor = _monitor;
        if (monitor?.Status == GatewayConnectionStatus.Connected)
        {
            OnHandshakeVerified();
            return;
        }

        FileLog.Write("[GatewayConnectionPanel] connect timed out awaiting a handshake verdict");
        ShowFailure(
            "The Gateway did not finish the two-way connection in time. It may be unreachable, or it could "
            + "not reach this Director back (the callback leg).",
            "Check that the Gateway is running and reachable, or set the Director public URL under Advanced.");
    }

    // ---- Step 2: sign in with DevThrottle -------------------------------------------------------

    // The handshake proved Connected. Route to Step 2 (sign in) or the Done view based on the current
    // signed-in state, resolved via the same resolver the status box uses (spec section 4).
    private async void OnHandshakeVerified()
    {
        _connecting = false;
        FileLog.Write("[GatewayConnectionPanel] connected (handshake verified); resolving sign-in state");
        // No transport-only event fires here (#1808a R3): consumers advance only on the terminal
        // ConnectionSettled result, raised from ShowDone once connected AND signed in.
        await RefreshSignedInViewAsync();
    }

    // Read the account status + device-token presence, resolve, and show Step 2 or the Done view.
    private async Task RefreshSignedInViewAsync()
    {
        var config = GatewayConfig.Load();
        var account = await SafeStatusAsync(config, CancellationToken.None);

        var inputs = new GatewayConnectionInputs(
            GatewayConfigured: config.IsEnabled,
            Connection: GatewayConnectionVerification.Connected,
            FailedLeg: GatewayConnectionFailedLeg.None,
            WasEverConnected: true,
            DeviceKeyPresent: HasDeviceToken(config),
            Account: MapAccount(account));

        if (GatewayConnectionStateResolver.ResolveState(inputs) == GatewayConnectionState.AllGreen)
            ShowDone(config, account);
        else
            ShowSignIn();
    }

    private void ShowSignIn()
    {
        // Reaching Step 2 ends any in-flight connect attempt, so background verdict churn stops driving the
        // view (ApplyVerdict ignores changes while not connecting).
        _connecting = false;
        StopPolling();
        SignInWaitRow.IsVisible = false;
        SignInErrorText.IsVisible = false;
        SignInButton.IsEnabled = true;
        ShowOnly(SignInPanel);
        FileLog.Write("[GatewayConnectionPanel] showing Step 2 (sign in)");
        AccountStateSettled?.Invoke(this, EventArgs.Empty);
    }

    // Epic #1069 A3 fallback: is a handshake failure an authorization failure (401)? The monitor's failure
    // summary carries the HTTP reason from the register call (GatewayClient.ReportRegistrationFailure).
    private static bool IsUnauthorizedFailure(string? summary) =>
        summary is not null
        && (summary.Contains("401")
            || summary.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase));

    private void SignIn_Click(object? sender, RoutedEventArgs e) => _ = StartSignInAsync();

    private async Task StartSignInAsync()
    {
        SignInErrorText.IsVisible = false;
        SignInButton.IsEnabled = false;
        SignInWaitRow.IsVisible = true;
        SignInWaitText.Text = "Opening the DevThrottle sign-in in your browser...";

        try
        {
            await OpenDevThrottleSignInAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] sign-in launch FAILED: {ex.Message}");
            ShowSignInError("Could not open the sign-in page. " + ex.Message);
            return;
        }

        // Watch for the Gateway reporting signed in, then settle to Done. Enroll runs at most once and
        // NEVER from the poll loop itself (guardrail against the #1136 key leak).
        SignInWaitText.Text = "Waiting for you to sign in...";
        StartPolling();
    }

    // Open the DevThrottle sign-in front door in the system browser. The Gateway itself decides the
    // loopback-versus-remote redirect (AccountSignInStartEndpoint).
    private async Task OpenDevThrottleSignInAsync()
    {
        var config = GatewayConfig.Load();
        var baseUrl = global::CcDirector.Avalonia.CockpitUrlResolver.ResolveCockpitBase(config);
        var url = baseUrl.TrimEnd('/') + "/account/sign-in-start";
        await Task.Run(() => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }));
        FileLog.Write($"[GatewayConnectionPanel] opened DevThrottle sign-in: {url}");
    }

    private void ShowSignInError(string message)
    {
        StopPolling();
        SignInWaitRow.IsVisible = false;
        SignInButton.IsEnabled = true;
        SignInErrorText.Text = message;
        SignInErrorText.IsVisible = true;
    }

    private void StartPolling()
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        _ = PollAccountAsync(_pollCts.Token);
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    // After the user starts the browser sign-in, watch for it to complete and settle to the Done view.
    //
    // A brand-new device has NO key, so it cannot read the credential-gated /account/status yet (epic #1069
    // A2 keeps account data gated). It therefore polls by RETRYING the loopback enrollment: that returns 409
    // while the Gateway is still signed out and 200 the instant it is signed in, and the server mint is
    // idempotent per device (the #1136 leak guard, so retrying is safe). Once the device earns its key, the
    // authenticated status read confirms signed-in and the view settles to Done.
    private async Task PollAccountAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var config = GatewayConfig.Load();

            // Keyless: retry the enroll until the Gateway is signed in (then it hands over the key).
            if (!HasDeviceToken(config))
            {
                await EnrollThroughGatewayOnceAsync();
                if (ct.IsCancellationRequested) return;
                config = GatewayConfig.Load();
            }

            // With a key, read the (now authenticated) status; signed-in means both checks are green.
            if (HasDeviceToken(config))
            {
                var account = await SafeStatusAsync(config, ct);
                if (ct.IsCancellationRequested) return;
                if (account.SignedIn)
                {
                    var finalConfig = config;
                    var finalAccount = account;
                    await Dispatcher.UIThread.InvokeAsync(() => ShowDone(finalConfig, finalAccount));
                    return;
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // Enroll THIS co-located Director through its Gateway now that the account is signed in: the Gateway
    // mints (or returns) this Director's own per-device token, which is stored locally. Called at most once
    // per sign-in flow and never from the poll loop's read path (guardrail against the #1136 key leak).
    private async Task EnrollThroughGatewayOnceAsync()
    {
        try
        {
            var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
            if (host is null)
            {
                FileLog.Write("[GatewayConnectionPanel] enroll-through-Gateway: Control API not running");
                return;
            }

            var config = GatewayConfig.Load();
            // Dial the enroll at LOOPBACK (guardrail 1), same as the pre-sign-in enroll-first path - the
            // configured URL may be the machine name, which would 403.
            var result = await GatewayEnrollmentClient.EnrollSignedInAsync(
                BuildLoopbackEnrollUrl(config.Url), token: null, host.DirectorId, Environment.MachineName, "windows");

            if (result.Outcome != EnrollOutcome.Enrolled || result.Value is null)
            {
                FileLog.Write($"[GatewayConnectionPanel] enroll-through-Gateway not enrolled ({result.Outcome}): {result.Message}");
                return;
            }

            // Store this device's own token under the configured URL, then re-apply so the running client
            // authenticates with it.
            await Task.Run(() => GatewayCredentialStore.SaveEnrolledKey(config.Url, result.Value.DeviceKey));
            await host.ReapplyGatewayAsync();
            FileLog.Write("[GatewayConnectionPanel] enroll-through-Gateway: this Director's token stored");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] enroll-through-Gateway ERROR: {ex.Message}");
        }
    }

    private static async Task<GatewayAccountStatus> SafeStatusAsync(GatewayConfig config, CancellationToken ct)
    {
        try { return await new GatewayAccountStatusClient().GetStatusAsync(config, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] account status read FAILED: {ex.Message}");
            return GatewayAccountStatus.NotConfigured();
        }
    }

    private void ShowDone(GatewayConfig config, GatewayAccountStatus account)
    {
        StopPolling();
        var host = SafeHost(config.Url);
        DoneConnectedText.Text = string.IsNullOrWhiteSpace(host)
            ? "Connected to the Gateway"
            : $"Connected to Gateway on {host}";
        DoneSignedInText.Text = string.IsNullOrWhiteSpace(account.Email)
            ? "Signed in"
            : $"Signed in as {account.Email}";
        DoneMaskedToken.Text = MaskToken(config.Token);
        DoneGatewayUrl.Text = config.Url;
        ShowOnly(DonePanel);
        FileLog.Write("[GatewayConnectionPanel] showing Done (both checks green)");
        AccountStateSettled?.Invoke(this, EventArgs.Empty);

        // The one common TERMINAL RESULT (#1808a): connected AND signed in. Consumers gate on this, not on
        // the transport handshake alone. Inference readiness is a NotReady placeholder until #1810.
        ConnectionSettled?.Invoke(this,
            GatewayConnectionOutcome.ConnectedAndSignedIn(GatewayInferenceReadiness.NotReady));
    }

    private void DoneAdvancedToggle_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var open = DoneAdvancedToggle.IsChecked == true;
        if (DoneAdvancedPanel is not null) DoneAdvancedPanel.IsVisible = open;
        if (DoneAdvancedCaret is not null) DoneAdvancedCaret.Text = open ? "^" : "v";
    }

    // ---- Done: Change gateway (disconnect + reconnect) ------------------------------------------

    // Change which Gateway this Director is connected to: confirm, DISCONNECT (clear the stored connection),
    // then return to the step-0 CHOICE so the user can connect to a different Gateway - local OR hosted.
    private async void ChangeGateway_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var host = SafeHost(GatewayConfig.Load().Url);
            var target = string.IsNullOrWhiteSpace(host) ? "the Gateway" : host;

            // Confirm before disconnecting (a connection is not cheap to re-establish).
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner is not null)
            {
                var confirm = new ConfirmDialog(
                    "Change gateway",
                    $"This will disconnect this Director from {target}. Continue?",
                    confirmLabel: "Disconnect",
                    cancelLabel: "Cancel");
                if (!await confirm.ShowDialog<bool>(owner))
                {
                    FileLog.Write("[GatewayConnectionPanel] change gateway: cancelled at the confirm prompt");
                    return;
                }
            }

            await DisconnectAndShowChoiceAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] ChangeGateway FAILED: {ex.Message}");
            ShowFailure($"Could not disconnect from the Gateway: {ex.Message}", null);
        }
    }

    // Clear the stored connection (config.json gateway url + urls + token, and the per-device credential
    // file), re-apply so the running client drops the old connection immediately, then show the CHOICE so a
    // different Gateway can be connected. Internal so a headless test can drive the disconnect transaction
    // without the modal confirm.
    internal async Task DisconnectAndShowChoiceAsync()
    {
        FileLog.Write("[GatewayConnectionPanel] disconnecting from the current gateway");
        await Task.Run(GatewayCredentialStore.ClearConnection);

        // Re-apply so the running Gateway client picks up the now-empty config and stops presenting the old
        // per-device key. Uses the same re-apply seam the connect paths use (injectable for tests). OBSERVE a
        // reapply fault here: the connection is ALREADY cleared (config emptied, credential deleted), so a
        // reapply failure does NOT undo the disconnect - log it and still return to the choice rather than
        // reporting a false "could not disconnect" or leaving the user stuck. (A ClearConnection failure DOES
        // propagate, so the caller surfaces it: nothing was cleared in that case.)
        var reapply = ReapplyGatewaySeam
            ?? ((global::Avalonia.Application.Current as App)?.ControlApiHost is { } host ? host.ReapplyGatewayAsync : null);
        if (reapply is not null)
        {
            try
            {
                await reapply();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayConnectionPanel] disconnect: re-apply after clear FAILED (connection already cleared): {ex.Message}");
            }
        }

        FileLog.Write("[GatewayConnectionPanel] disconnected; showing the gateway choice");
        ShowChoice();
    }

    // The token is shown masked, never in the clear (decision 7) - same masking as the old pairing dialog.
    private static string MaskToken(string? token)
        => !string.IsNullOrEmpty(token) && token.Length > 8
            ? token[..4] + "..." + token[^4..]
            : "********";

    // True when this device already holds its per-device token. GatewayConfig.Load resolves it from the
    // local credential file for a same-machine Gateway, so the config token is the complete check.
    private static bool HasDeviceToken(GatewayConfig config) => !string.IsNullOrWhiteSpace(config.Token);

    private static GatewayAccountSignInState MapAccount(GatewayAccountStatus status)
    {
        if (!status.GatewayConfigured) return GatewayAccountSignInState.Unknown;
        if (!status.Reachable) return GatewayAccountSignInState.Unavailable;
        return status.SignedIn ? GatewayAccountSignInState.SignedIn : GatewayAccountSignInState.SignedOut;
    }

    private void ShowFailure(string summary, string? fix)
    {
        _connecting = false;
        FailureSummaryText.Text = summary;
        if (string.IsNullOrWhiteSpace(fix))
        {
            FailureFixText.IsVisible = false;
        }
        else
        {
            FailureFixText.Text = "Fix: " + fix;
            FailureFixText.IsVisible = true;
        }
        ShowOnly(FailedPanel);
        FileLog.Write($"[GatewayConnectionPanel] connect failed: {summary}");
    }

    private void TryAgain_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastAttempt is { } attempt)
            _ = ConnectToAsync(attempt.Url, attempt.Label, attempt.Remote);
        else
            StartScan();
    }

    // Name the fix for the failing leg (decision 11) from the monitor's already-named failure summary.
    private static string? DeriveFix(GatewayConnectionMonitor monitor)
    {
        if (monitor.Status == GatewayConnectionStatus.NoTailnetIdentity)
            return "Start Tailscale on this machine, or set the Director public URL under Advanced.";

        var summary = monitor.FailureSummary ?? string.Empty;
        if (summary.Contains("callback", StringComparison.OrdinalIgnoreCase))
            return "Make sure this Director's port is reachable from the Gateway host, or set the Director "
                 + "public URL under Advanced.";
        if (summary.Contains("Cannot reach the Gateway", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("reach the Gateway", StringComparison.OrdinalIgnoreCase))
            return "Make sure the Gateway is running and reachable at that address.";
        return null;
    }

    // Show exactly one of the step sub-panels.
    private void ShowOnly(Control panel)
    {
        ClearSignInAddress();
        ChoicePanel.IsVisible = ReferenceEquals(panel, ChoicePanel);
        ConnectPanel.IsVisible = ReferenceEquals(panel, ConnectPanel);
        ConnectingPanel.IsVisible = ReferenceEquals(panel, ConnectingPanel);
        SignInPanel.IsVisible = ReferenceEquals(panel, SignInPanel);
        DonePanel.IsVisible = ReferenceEquals(panel, DonePanel);
        FailedPanel.IsVisible = ReferenceEquals(panel, FailedPanel);
    }
}
