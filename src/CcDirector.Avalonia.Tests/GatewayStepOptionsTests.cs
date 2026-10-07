using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using CcDirector.Core.Configuration;
using CcDirector.Core.Onboarding;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The gateway step's three options - Hosted, Self-hosted, Not now - must stay REACHABLE. All of them,
/// by mouse and by keyboard, before a sign-in attempt and after one that failed or was cancelled.
///
/// That is not a styling preference. The step gives "Not now" equal prominence deliberately, because it
/// is the no-account, local-only choice and the product installs and runs without an account; the source
/// comment says a first screen reading as sign-up-to-continue "would change what the product is". Two
/// separate mechanisms were quietly taking that promise back:
///
///   * #1070 - a cancelled sign-in swapped the whole choice view for a failure view, so all three cards
///     vanished, and re-entering the step never reset the sub-view, so Back-then-forward brought the
///     user back to the cards-less screen. No path declined the gateway and continued.
///   * #1071 - the cards were Borders with handlers hung on them. A Border gets no automation peer, so
///     they exposed no name, no pattern, and never entered the tab order: Tab cycled Back and Sign in
///     and connect for ever, and a keyboard user could reach exactly one option (the pre-selected one).
///
/// THESE TESTS ASSERT THE INVARIANT, NOT THE INSTANCE. "Every option is still reachable" rather than
/// "this particular card is present", because the defect is about the set, and a test naming one card
/// would pass while another went missing.
/// </summary>
public class GatewayStepOptionsTests
{
    /// <summary>
    /// Open the gateway step against a CLEAN machine, with config redirected to a throwaway root.
    ///
    /// This isolation is not boilerplate - without it these tests read the DEVELOPER'S OWN gateway. The
    /// step calls AdoptExistingGateway on entry, which loads the real config.json, and on a machine that
    /// already has a gateway the step opens on the connected view with the primary button reading
    /// "Continue" instead of the choice cards. So the same code gave one answer on a clean CI runner and
    /// another on this machine. A test whose verdict depends on whose laptop it runs on is not a check.
    /// The assembly runs sequentially (TestParallelization), so the process-global variable is not raced.
    /// </summary>
    private static void WithCleanMachine(Action<FirstRunWizardDialog> body)
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "cc-director-gateway-step-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            // Guard the isolation itself rather than assuming it: if a gateway is somehow configured in
            // this temp root, every assertion below is about the wrong screen.
            Assert.False(GatewayConfig.Load().IsEnabled, "the throwaway root is not clean, so the step will open on the connected view");

            var dialog = new FirstRunWizardDialog(new AgentOptions());
            dialog.ShowStepForTests(WizardStep.Gateway);
            body(dialog);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// THE POSITIVE CONTROL FOR EVERY ASSERTION BELOW.
    ///
    /// A card that cannot be driven and a card that is not there look identical from a test, so before
    /// trusting anything this file says about the cards, prove the instrument can SEE a control it should
    /// see. The step's own primary button is known-good: it is a real Button, it is focusable, and it is
    /// visible. If this fails, every other result in this file is meaningless rather than reassuring -
    /// and in particular a "the cards are fine" green would be unearned.
    /// </summary>
    [AvaloniaFact]
    public void Control_TheStepsOwnButtonsAreVisibleAndFocusable_SoAbsenceMeansSomething()
    {
        WithCleanMachine(dialog =>
        {

            Assert.True(dialog.GatewayPanel.IsVisible, "the gateway step itself is not showing - nothing below can be trusted");
            Assert.True(dialog.PrimaryButton.IsVisible);
            Assert.True(dialog.PrimaryButton.Focusable);
            Assert.Equal(3, dialog.GatewayOptionCardsForTests.Count);
        });
    }

    [AvaloniaFact]
    public void EveryOption_IsARealControlWithAnAccessibleName_NotDecoratedText()
    {
        WithCleanMachine(dialog =>
        {

            foreach (var card in dialog.GatewayOptionCardsForTests)
            {
                // A real control, so it gets an automation peer at all. This is the whole of #1071: the
                // markup already declared Focusable, KeyDown, GotFocus and a name on a Border, and none of
                // it surfaced, because a Border is not a control no matter what you hang on it.
                var option = Assert.IsAssignableFrom<RadioButton>(card);

                // In the tab order, and named for a screen reader.
                Assert.True(option.Focusable, $"{card.Name} is not focusable, so Tab cannot reach it");
                Assert.True(option.IsTabStop, $"{card.Name} is not a tab stop");
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(option)),
                    $"{card.Name} exposes no accessible name");
            }
        });
    }

    [AvaloniaFact]
    public void EveryOption_IsOneOfASingleChoiceGroup_SoSelectionIsExposedAndExclusive()
    {
        WithCleanMachine(dialog =>
        {
            var cards = dialog.GatewayOptionCardsForTests;

            // One group: that is what gives arrow-key movement between the options and a SelectionItem
            // pattern a screen reader can read the selected state from. A Border carried no selected state
            // at all, which is why it had to be smuggled into the accessible help text.
            var groups = cards.Select(c => Assert.IsAssignableFrom<RadioButton>(c).GroupName).Distinct().ToList();
            Assert.Single(groups);
            Assert.False(string.IsNullOrWhiteSpace(groups[0]));

            // Exclusive, and exactly one selected to begin with - a group where nothing is checked gives a
            // keyboard user no entry point.
            Assert.Single(cards, c => c is RadioButton { IsChecked: true });
        });
    }

    [AvaloniaFact]
    public void ChoosingAnyOption_MovesTheWizardsChoice_SoTheControlIsWiredNotJustPresent()
    {
        WithCleanMachine(dialog =>
        {
            // Reachable and inert is still broken. Drive each card the way the control itself is driven and
            // assert the wizard's own choice followed - including "Not now", the option that was unreachable.
            var cards = dialog.GatewayOptionCardsForTests;

            var seen = new List<string>();
            foreach (var card in cards)
            {
                Assert.IsAssignableFrom<RadioButton>(card).IsChecked = true;
                seen.Add(dialog.GatewayChoiceForTests);
            }

            // Three cards, three DIFFERENT choices: proves each card is bound to its own option rather than
            // all three landing on the same one.
            Assert.Equal(3, seen.Distinct().Count());
            Assert.Contains("NotNow", seen);
        });
    }

    [AvaloniaFact]
    public void ACancelledSignIn_LeavesEveryOptionReachable()
    {
        WithCleanMachine(dialog =>
        {
            // THE #1070 INVARIANT. Not "the cards are visible" for one named card - every option, still
            // reachable, after the failure path has run.

            dialog.ReportGatewayFailureForTests("Sign-in was cancelled.");

            Assert.True(dialog.GatewayChoiceView.IsVisible, "the choice view was replaced, so the options are gone");
            foreach (var card in dialog.GatewayOptionCardsForTests)
            {
                Assert.True(card.IsVisible, $"{card.Name} disappeared when the sign-in was cancelled");
                Assert.True(card.Focusable, $"{card.Name} is no longer reachable by keyboard");
            }

            // And the reason is actually shown - otherwise this could pass by the failure never rendering.
            Assert.True(dialog.GatewayFailBanner.IsVisible);
            Assert.Contains("cancelled", dialog.GatewayFailText.Text ?? "", StringComparison.OrdinalIgnoreCase);
        });
    }

    [AvaloniaFact]
    public void ACancelledSignIn_DoesNotStickAcrossLeavingAndReenteringTheStep()
    {
        WithCleanMachine(dialog =>
        {
            // The second half of #1070, and the half that made it a trap rather than a glitch: Back went to
            // Welcome, and coming forward again returned to the cancelled state with the cards still gone,
            // because the step's own entry never set a sub-view.
            dialog.ReportGatewayFailureForTests("Sign-in was cancelled.");

            dialog.ShowStepForTests(WizardStep.Welcome);
            dialog.ShowStepForTests(WizardStep.Gateway);

            Assert.True(dialog.GatewayChoiceView.IsVisible);
            Assert.All(dialog.GatewayOptionCardsForTests, card => Assert.True(card.IsVisible));
            // The stale reason is gone too - a cancellation the user has navigated away from must not be
            // re-presented as if it had just happened.
            Assert.False(dialog.GatewayFailBanner.IsVisible);
        });
    }

    [AvaloniaFact]
    public void ACancelledSignIn_StillLetsTheUserDeclineTheGatewayAndContinue()
    {
        WithCleanMachine(dialog =>
        {
            // The consequence the issue is actually about, asserted end to end: after cancelling, the user
            // can still choose "Not now" and the step offers to move on WITHOUT a gateway. Before the fix
            // there was no path that declined the gateway and continued through the remaining six steps.
            dialog.ReportGatewayFailureForTests("Sign-in was cancelled.");

            var notNow = dialog.GatewayOptionCardsForTests.Single(c => c.Name == "GatewayNotNowCard");
            Assert.IsAssignableFrom<RadioButton>(notNow).IsChecked = true;

            Assert.Equal("NotNow", dialog.GatewayChoiceForTests);
            Assert.True(dialog.PrimaryButton.IsEnabled);
            Assert.Equal("Continue without a gateway", dialog.PrimaryButton.Content);
        });
    }

    /// <summary>
    /// Issue #3504: "Finish signing in in your browser" must offer a way in that does not depend on Windows
    /// opening a browser. With a newly installed second browser, Windows answers the sign-in link with an app
    /// chooser that vanishes when focus moves, and the screen then waited with nothing to sign in to. The
    /// address the sign-in reports is shown, selectable, beside Copy and Open in browser - and Copy really puts
    /// that address on the clipboard.
    /// </summary>
    [AvaloniaFact]
    public void TheWaitingScreen_ShowsTheSignInAddress_AndCopyPutsItOnTheClipboard()
    {
        WithCleanMachine(dialog =>
        {
            const string address = "https://devthrottle.com/signin?redirect_uri=http%3A%2F%2F127.0.0.1%3A49174%2Fdevthrottle-login-callback%2F";
            dialog.Show();

            dialog.ShowSignInAddressForTests(address);

            var (row, box, copy, open) = dialog.SignInAddressForTests;
            Assert.True(dialog.GatewayConnectingViewForTests.IsVisible);
            Assert.True(row.IsVisible, "the sign-in address must be on the waiting screen");
            Assert.Equal(address, box.Text);
            Assert.True(box.IsReadOnly);
            Assert.Equal("Copy", copy.Content);
            Assert.Equal("Open in browser", open.Content);

            copy.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var onClipboard = global::Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(dialog.Clipboard!).GetAwaiter().GetResult();
            Assert.Equal(address, onClipboard);
            dialog.Close();
        });
    }

    [AvaloniaFact]
    public void TheFailureMessage_NamesNoControlThatIsNotOnTheScreen()
    {
        WithCleanMachine(dialog =>
        {
            // The cheap half of #1070. The cancelled state read: Sign-in was cancelled. Click "Sign in to
            // DevThrottle" to try again. There is no control with that label anywhere in the product - it is
            // the heading of the browser sign-in page, which at that moment is the thing the user just
            // closed. Asserted over the WHOLE banner, message and guidance together, because either could
            // reintroduce it.
            dialog.ReportGatewayFailureForTests("Sign-in was cancelled.");

            var bannerText = string.Join(" ", dialog.GatewayFailBanner
                .GetLogicalDescendants()
                .OfType<TextBlock>()
                .Select(t => t.Text ?? ""));

            Assert.DoesNotContain("Sign in to DevThrottle", bannerText, StringComparison.OrdinalIgnoreCase);
            // Positive half, so this cannot pass by the banner being empty: it points at what IS there.
            Assert.Contains("Not now", bannerText, StringComparison.OrdinalIgnoreCase);
        });
    }
}
