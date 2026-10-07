using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Configuration;
using CcDirector.Core.GatewayConnection;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Headless integration guards for the #1808a gateway-choice panel (rework R2). The G1-G5 revert-proofs
/// were Core-state only and did not guard the panel wiring where the real risk is - the "a dead test looks
/// like coverage" trap. These drive the REAL panel: the remote-Join transaction (the security + data-loss
/// boundary), the rendered choice cards' actionability and Mac omission, the per-consumer Skip route, and
/// the terminal outcome emission plus onboarding's advance gate. The enrollment seam is injected so the
/// transaction runs with no live Gateway, browser, or network.
///
/// The assembly runs sequentially (TestParallelization), so the process-global CC_DIRECTOR_ROOT redirect
/// and env are not raced.
/// </summary>
public class GatewayChoicePanelIntegrationTests
{
    private const string OldUrl = "https://old-gateway.example:7878";
    private const string OldToken = "old-device-token-DO-NOT-SEND";
    private const string SelectedUrl = "https://new-gateway.example:7878";

    private static void WithTempRoot(Action body)
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "cc-1808a-panel-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try { body(); }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // The remote-Join transaction is genuinely async (the real path awaits the enrollment runner and the
    // re-apply). Tests AWAIT it on the Avalonia UI thread rather than blocking, so a real async hop never
    // deadlocks against a synchronous wait.
    private static async Task WithTempRootAsync(Func<Task> body)
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "cc-1808a-panel-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try { await body(); }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Pre-write a saved connection to a PREVIOUS Gateway, so a repair/reconnect Join has an old token that
    // must NOT bypass the enrollment seam or be sent to the newly-selected URL.
    private static void SaveOldGatewayConnection()
        => CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["gateway"] = new JsonObject { ["url"] = OldUrl, ["token"] = OldToken },
        });

    // ---- R2(a): the remote-Join transaction boundary --------------------------------------------

    [AvaloniaFact]
    public async Task RemoteJoin_AlwaysCallsEnrollSeam_WithSelectedUrl_EvenWhenAnOldTokenIsSaved()
    {
        await WithTempRootAsync(async () =>
        {
            SaveOldGatewayConnection();
            Assert.Equal(OldToken, GatewayConfig.Load().Token); // an old token is present

            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            string? seenUrl = null, seenDeviceId = null;
            var calls = 0;
            panel.DirectorIdOverride = "test-director-id";
            panel.RemoteEnrollSeam = (url, deviceId, _, _) =>
            {
                calls++;
                seenUrl = url;
                seenDeviceId = deviceId;
                return Task.FromResult(OperationResult<MobileEnrollmentResponse>.Fail("not this time"));
            };

            await panel.ConnectToAsync(SelectedUrl, SelectedUrl, remote: true);

            // The runner IS called even though an old token exists (the old bug bypassed it), and it is
            // called with the SELECTED url and this device's id - never the old Gateway or its token.
            Assert.Equal(1, calls);
            Assert.Equal(SelectedUrl, seenUrl);
            Assert.Equal("test-director-id", seenDeviceId);
        });
    }

    [AvaloniaFact]
    public async Task RemoteJoin_FailedEnroll_DoesNotMutateConfig_AndDoesNotReapply()
    {
        await WithTempRootAsync(async () =>
        {
            SaveOldGatewayConnection();

            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            var reapplied = false;
            panel.DirectorIdOverride = "test-director-id";
            panel.ReapplyGatewaySeam = () => { reapplied = true; return Task.CompletedTask; };
            panel.RemoteEnrollSeam = (_, _, _, _) =>
                Task.FromResult(OperationResult<MobileEnrollmentResponse>.Fail("verification failed"));

            await panel.ConnectToAsync(SelectedUrl, SelectedUrl, remote: true);

            // Verification failed, so the previously-saved connection is untouched (no pre-write corrupts it)
            // and the client is NOT re-applied with any credential.
            var config = GatewayConfig.Load();
            Assert.Equal(OldUrl, config.Url);
            Assert.Equal(OldToken, config.Token);
            Assert.False(reapplied);
        });
    }

    [AvaloniaFact]
    public async Task RemoteJoin_SuccessfulEnroll_ReAppliesTheVerifiedCredential()
    {
        await WithTempRootAsync(async () =>
        {
            SaveOldGatewayConnection();

            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            var reapplied = false;
            panel.DirectorIdOverride = "test-director-id";
            panel.ReapplyGatewaySeam = () => { reapplied = true; return Task.CompletedTask; };
            // Simulate the runner's verified-success persistence (it owns the atomic url+key write).
            panel.RemoteEnrollSeam = (url, _, _, _) =>
            {
                CcDirectorConfigService.MergePatch(new JsonObject
                {
                    ["gateway"] = new JsonObject { ["url"] = url, ["token"] = "new-verified-key" },
                });
                return Task.FromResult(OperationResult<MobileEnrollmentResponse>.Ok(
                    new MobileEnrollmentResponse { DeviceKey = "new-verified-key" }));
            };

            await panel.ConnectToAsync(SelectedUrl, SelectedUrl, remote: true);

            // On verified success the panel re-applies so the client authenticates with the NEW credential.
            Assert.True(reapplied);
            Assert.Equal(SelectedUrl, GatewayConfig.Load().Url);
        });
    }

    // ---- R2(b): rendered card actionability + Mac omission ---------------------------------------

    [AvaloniaFact]
    public void Choice_OnWindows_RendersDisabledSelfHost_HostedJoinAndSkipActionable()
    {
        var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Onboarding);
        // Force a self-host-capable (Windows) context regardless of the test host.
        panel.SetChoiceContextForTests(new GatewayChoiceContext(GatewayChoiceConsumer.Onboarding, SelfHostSupported: true));
        panel.ShowChoiceForTests();

        // Self-host is still a disabled "coming" card; hosted is now a live, actionable card.
        Assert.False(CardFor(panel, GatewayChoiceAction.SelfHost).IsEnabled);
        Assert.True(CardFor(panel, GatewayChoiceAction.UseHosted).IsEnabled);
        Assert.True(CardFor(panel, GatewayChoiceAction.JoinExisting).IsEnabled);
        Assert.True(CardFor(panel, GatewayChoiceAction.Skip).IsEnabled);
    }

    // ---- Hosted choice: the enabled card runs the SAME proven hosted enroll the CLI uses ----------

    [AvaloniaFact]
    public async Task HostedChoice_ActivatingTheEnabledCard_RunsHostedEnroll_WithThisDeviceId()
    {
        await WithTempRootAsync(async () =>
        {
            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            panel.SetChoiceContextForTests(new GatewayChoiceContext(GatewayChoiceConsumer.Settings, SelfHostSupported: true));
            panel.ShowChoiceForTests();
            panel.DirectorIdOverride = "test-director-id";

            var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Injected hosted-enroll seam: no browser, no network. Fail so nothing re-applies or handshakes.
            panel.HostedEnrollSeam = (deviceId, _, _, _) =>
            {
                seen.TrySetResult(deviceId);
                return Task.FromResult(OperationResult<MobileEnrollmentResponse>.Fail("not this time"));
            };

            // Drive the ACTUAL card dispatch, exactly as a click would (through ActivateChoiceForTests, which
            // respects the card's enabled state) - NOT HostedEnrollAndHandshakeAsync directly. So removing the
            // UseHosted case from the dispatch (or its StartHostedEnroll wiring) reddens this test.
            Assert.True(CardFor(panel, GatewayChoiceAction.UseHosted).IsEnabled);
            panel.ActivateChoiceForTests(GatewayChoiceAction.UseHosted);

            var winner = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(ReferenceEquals(winner, seen.Task),
                "activating the enabled hosted card did not invoke the hosted enroll");
            Assert.Equal("test-director-id", await seen.Task);
        });
    }

    [AvaloniaFact]
    public async Task HostedChoice_FailedEnroll_ShowsTheFailure_DoesNotReapply_AndDoesNotMutateConfig()
    {
        await WithTempRootAsync(async () =>
        {
            SaveOldGatewayConnection();

            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            var reapplied = false;
            panel.DirectorIdOverride = "test-director-id";
            panel.ReapplyGatewaySeam = () => { reapplied = true; return Task.CompletedTask; };
            panel.HostedEnrollSeam = (_, _, _, _) =>
                Task.FromResult(OperationResult<MobileEnrollmentResponse>.Fail("hosted enroll failed"));

            await panel.HostedEnrollAndHandshakeAsync();

            // The failure is actually SHOWN, carrying the reason (removing ShowFailure reddens this) - never a
            // silent no-op and never a stuck "Connecting" spinner.
            Assert.True(panel.IsShowingFailureForTests, "a failed hosted enroll must show the failure panel");
            Assert.False(panel.IsShowingConnectingForTests, "the panel must not be stuck on Connecting");
            Assert.Contains("hosted enroll failed", panel.FailureSummaryForTests);

            // It persists nothing and never re-applies: the old connection is untouched.
            var config = GatewayConfig.Load();
            Assert.Equal(OldUrl, config.Url);
            Assert.Equal(OldToken, config.Token);
            Assert.False(reapplied);
        });
    }

    [AvaloniaFact]
    public async Task HostedChoice_SuccessfulEnroll_ReAppliesTheVerifiedCredential()
    {
        await WithTempRootAsync(async () =>
        {
            const string hostedUrl = "https://gateway.devthrottle.com";
            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            var reapplied = false;
            panel.DirectorIdOverride = "test-director-id";
            panel.ReapplyGatewaySeam = () => { reapplied = true; return Task.CompletedTask; };
            // Mirror the runner's verified-success persistence (it owns the atomic hosted-url+key write).
            panel.HostedEnrollSeam = (_, _, _, _) =>
            {
                CcDirectorConfigService.MergePatch(new JsonObject
                {
                    ["gateway"] = new JsonObject { ["url"] = hostedUrl, ["token"] = "hosted-verified-key" },
                });
                return Task.FromResult(OperationResult<MobileEnrollmentResponse>.Ok(
                    new MobileEnrollmentResponse { DeviceKey = "hosted-verified-key" }));
            };

            await panel.HostedEnrollAndHandshakeAsync();

            Assert.True(reapplied);
            Assert.Equal(hostedUrl, GatewayConfig.Load().Url);
        });
    }

    [AvaloniaFact]
    public async Task HostedChoice_ReapplyFaultsAfterEnroll_ShowsFailure_NotStuckOnConnecting()
    {
        await WithTempRootAsync(async () =>
        {
            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            panel.DirectorIdOverride = "test-director-id";
            // The enroll SUCCEEDS (the credential is persisted), but the live re-apply faults afterwards.
            panel.HostedEnrollSeam = (_, _, _, _) =>
                Task.FromResult(OperationResult<MobileEnrollmentResponse>.Ok(
                    new MobileEnrollmentResponse { DeviceKey = "hosted-verified-key" }));
            panel.ReapplyGatewaySeam = () => throw new InvalidOperationException("reapply boom");

            // The fault must be OBSERVED: this await must NOT throw out, and the panel must NOT be left
            // spinning - it lands on a named failure. Reverting the fix (awaiting reapply outside the
            // try/catch) makes this await rethrow the fault, reddening the test.
            await panel.HostedEnrollAndHandshakeAsync();

            Assert.True(panel.IsShowingFailureForTests, "a reapply fault after enroll must show the failure panel");
            Assert.False(panel.IsShowingConnectingForTests, "the panel must not be stuck on Connecting after a reapply fault");
            Assert.Contains("could not apply", panel.FailureSummaryForTests);
        });
    }

    /// <summary>
    /// Issue #3504: while the hosted sign-in waits for the browser, the connecting view shows the sign-in address
    /// with Copy and Open in browser - because Windows can open no browser at all, and then the address is the
    /// only way in. Driven through the real hosted path; the seam reports the address exactly as the real
    /// sign-in does, then holds the wait open so the waiting screen can be read.
    /// </summary>
    [AvaloniaFact]
    public async Task HostedChoice_WhileWaitingForTheBrowser_ShowsTheSignInAddress_WithCopyAndOpen()
    {
        await WithTempRootAsync(async () =>
        {
            const string address = "https://devthrottle.com/signin?redirect_uri=http%3A%2F%2F127.0.0.1%3A49174%2Fdevthrottle-login-callback%2F";
            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            panel.DirectorIdOverride = "test-director-id";
            var waiting = new TaskCompletionSource<OperationResult<MobileEnrollmentResponse>>();
            panel.HostedEnrollSeam = (_, _, display, _) =>
            {
                display.Show(address);
                return waiting.Task;
            };

            var attempt = panel.HostedEnrollAndHandshakeAsync();

            var (row, box, copy, open) = panel.SignInAddressForTests;
            Assert.True(panel.IsShowingConnectingForTests);
            Assert.True(row.IsVisible, "the sign-in address must be on the waiting screen");
            Assert.Equal(address, box.Text);
            Assert.True(box.IsReadOnly);
            Assert.Equal("Copy", copy.Content);
            Assert.Equal("Open in browser", open.Content);

            // The attempt ends: the address leaves with the waiting view, so no later view offers a dead one.
            waiting.SetResult(OperationResult<MobileEnrollmentResponse>.Fail("not this time"));
            await attempt;
            Assert.False(row.IsVisible, "an ended sign-in must not leave its address offered");
            Assert.Equal("", box.Text);
        });
    }

    // ---- Change gateway: disconnect clears the stored connection, then re-shows the choice --------

    [AvaloniaFact]
    public async Task ChangeGateway_Disconnect_ClearsTheStoredConnection_ReAppliesAndReturnsToChoice()
    {
        await WithTempRootAsync(async () =>
        {
            SaveOldGatewayConnection();
            Assert.True(GatewayConfig.Load().IsEnabled); // connected to a gateway

            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            var reapplied = false;
            panel.ReapplyGatewaySeam = () => { reapplied = true; return Task.CompletedTask; };

            await panel.DisconnectAndShowChoiceAsync();

            // The stored connection is cleared: url + token gone, so the Director is local-only again.
            var config = GatewayConfig.Load();
            Assert.False(config.IsEnabled);
            Assert.Equal("", config.Url);
            Assert.Equal("", config.Token);
            // The running client was re-applied so it drops the old connection...
            Assert.True(reapplied);
            // ...and the panel actually RETURNS TO THE STEP-0 CHOICE view (removing ShowChoice reddens this),
            // not merely leaving hidden cards around.
            Assert.True(panel.IsShowingChoiceForTests, "disconnect must return to the step-0 choice view");
            Assert.NotNull(CardFor(panel, GatewayChoiceAction.JoinExisting));
            Assert.NotNull(CardFor(panel, GatewayChoiceAction.UseHosted));
        });
    }

    [AvaloniaFact]
    public async Task ChangeGateway_Disconnect_ReapplyFault_StillClearsAndReturnsToChoice()
    {
        await WithTempRootAsync(async () =>
        {
            SaveOldGatewayConnection();

            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            // The connection is cleared before re-apply; a reapply fault must NOT undo the disconnect nor
            // leave the user stuck - it is observed and the choice is still shown.
            panel.ReapplyGatewaySeam = () => throw new InvalidOperationException("reapply boom");

            await panel.DisconnectAndShowChoiceAsync();

            Assert.False(GatewayConfig.Load().IsEnabled);
            Assert.True(panel.IsShowingChoiceForTests, "disconnect must return to the choice even if re-apply faults");
        });
    }

    [AvaloniaFact]
    public void Choice_OnMac_OmitsSelfHostCardEntirely()
    {
        var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Onboarding);
        // A Mac cannot self-host: SelfHostSupported=false.
        panel.SetChoiceContextForTests(new GatewayChoiceContext(GatewayChoiceConsumer.Onboarding, SelfHostSupported: false));
        panel.ShowChoiceForTests();

        Assert.DoesNotContain(panel.ChoiceCardsForTests,
            c => c is Border { Tag: GatewayChoiceAction.SelfHost });
        // The other three actions are still offered.
        Assert.NotNull(CardFor(panel, GatewayChoiceAction.UseHosted));
        Assert.NotNull(CardFor(panel, GatewayChoiceAction.JoinExisting));
        Assert.NotNull(CardFor(panel, GatewayChoiceAction.Skip));
    }

    // ---- R2(c): the per-consumer Skip route ------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(GatewayChoiceConsumer.Onboarding, GatewaySkipBehavior.CompleteOnboardingLocalOnly)]
    [InlineData(GatewayChoiceConsumer.Settings, GatewaySkipBehavior.ReturnToChoice)]
    [InlineData(GatewayChoiceConsumer.StatusWindow, GatewaySkipBehavior.ReturnToChoice)]
    public void Skip_ActivatingTheCard_RaisesSkipRequested_WithThePerConsumerBehavior(
        GatewayChoiceConsumer consumer, GatewaySkipBehavior expected)
    {
        var panel = GatewayConnectionPanel.CreateForCurrentState(consumer);
        panel.ShowChoiceForTests();
        GatewaySkipBehavior? raised = null;
        panel.SkipRequested += (_, behavior) => raised = behavior;

        panel.ActivateChoiceForTests(GatewayChoiceAction.Skip);

        Assert.Equal(expected, raised);
    }

    // ---- R2(d): terminal outcome emission + onboarding advance gate ------------------------------

    [AvaloniaFact]
    public void TerminalOutcome_IsEmitted_Connected_SignedIn_AndNotReadyInThisSlice()
    {
        WithTempRoot(() =>
        {
            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            GatewayConnectionOutcome? emitted = null;
            panel.ConnectionSettled += (_, outcome) => emitted = outcome;

            panel.EmitTerminalForTests();

            Assert.NotNull(emitted);
            Assert.True(emitted!.Connected);
            Assert.True(emitted.SignedIn);
            Assert.Equal(GatewayInferenceReadiness.NotReady, emitted.Inference);
        });
    }

    [AvaloniaFact]
    public void Onboarding_DoesNotAdvance_UntilTheTerminalOutcome_TransportAloneCannot()
    {
        WithTempRoot(() =>
        {
            var dialog = new OnboardingWizardDialog(new AgentOptions());
            Assert.Equal(0, dialog.CurrentStepForTests); // on the gateway step

            // No terminal outcome yet: Next must NOT advance (there is no transport-only signal that could).
            dialog.ClickNextForTests();
            Assert.Equal(0, dialog.CurrentStepForTests);

            // The panel emits its terminal settled outcome (connected AND signed in) -> now Next advances.
            dialog.GatewayPanelForTests.EmitTerminalForTests();
            dialog.ClickNextForTests();
            Assert.Equal(1, dialog.CurrentStepForTests);
        });
    }

    private static Border CardFor(GatewayConnectionPanel panel, GatewayChoiceAction action)
    {
        foreach (var child in panel.ChoiceCardsForTests)
            if (child is Border { Tag: GatewayChoiceAction tag } card && tag == action)
                return card;
        throw new Xunit.Sdk.XunitException($"No choice card was rendered for {action}.");
    }
}
