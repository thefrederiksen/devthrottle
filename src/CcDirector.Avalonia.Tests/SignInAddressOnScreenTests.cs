using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Configuration;
using CcDirector.Core.GatewayConnection;
using CcDirector.Core.Onboarding;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Issue #3504, through the PRODUCTION path: the runner both desktop surfaces sign in with
/// (<see cref="HostedTeamSetup.CreateRunner"/>), the real browser sign-in and loopback listener, and the real
/// screen as the display. Only the Windows shell call is replaced.
///
/// What each test reads is the screen AT THE MOMENT the browser is asked to open. That is the moment that
/// matters: with a newly installed second browser, Windows answers that call with an app chooser that vanishes
/// when focus moves, and from then on the address on screen is the only way in. An address that is merely
/// queued to the screen - not yet drawn when the shell call runs - fails here.
/// </summary>
public class SignInAddressOnScreenTests
{
    private const string GatewayUrl = "http://gateway.test:7878";

    [AvaloniaFact]
    public async Task Wizard_TheAddressIsOnScreenWhenTheBrowserIsAskedToOpen_AndGoneWhenTheWaitEnds()
    {
        await WithCleanRootAsync(async () =>
        {
            var dialog = new FirstRunWizardDialog(new AgentOptions());
            dialog.ShowStepForTests(WizardStep.Gateway);
            dialog.Show();
            try
            {
                await AssertOnScreenAtOpenThenWithdrawnAsync(dialog, () => dialog.SignInAddressForTests.Row,
                    () => dialog.SignInAddressForTests.Address);
            }
            finally { dialog.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task Panel_TheAddressIsOnScreenWhenTheBrowserIsAskedToOpen_AndGoneWhenTheWaitEnds()
    {
        await WithCleanRootAsync(async () =>
        {
            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            await AssertOnScreenAtOpenThenWithdrawnAsync(panel, () => panel.SignInAddressForTests.Row,
                () => panel.SignInAddressForTests.Address);
        });
    }

    /// <summary>
    /// Windows cannot open a browser at all. The wizard keeps the address up, says the browser did not open, and
    /// keeps waiting - the sign-in is not ended by the one failure the address exists to get around.
    /// </summary>
    [AvaloniaFact]
    public async Task Wizard_WhenWindowsCannotOpenABrowser_SaysSo_AndKeepsTheAddressUp()
    {
        await WithCleanRootAsync(async () =>
        {
            var dialog = new FirstRunWizardDialog(new AgentOptions());
            dialog.ShowStepForTests(WizardStep.Gateway);
            dialog.Show();
            try
            {
                await AssertBrowserDidNotOpenIsShownAsync(dialog, () => dialog.SignInAddressForTests.Row,
                    () => dialog.SignInAddressStatusForTests);
            }
            finally { dialog.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task Panel_WhenWindowsCannotOpenABrowser_SaysSo_AndKeepsTheAddressUp()
    {
        await WithCleanRootAsync(async () =>
        {
            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            await AssertBrowserDidNotOpenIsShownAsync(panel, () => panel.SignInAddressForTests.Row,
                () => panel.SignInAddressStatusForTests);
        });
    }

    private static async Task AssertOnScreenAtOpenThenWithdrawnAsync(
        ISignInAddressDisplay screen, Func<Control> row, Func<TextBox> address)
    {
        string? opened = null;
        var rowVisibleAtOpen = false;
        string? addressAtOpen = null;
        using var stop = new CancellationTokenSource();
        var runner = HostedTeamSetup.CreateRunner(_ => { }, screen, openBrowser: url =>
        {
            opened = url;
            rowVisibleAtOpen = row().IsVisible;
            addressAtOpen = address().Text;
            // The chooser was dismissed and nothing opened; the person gives up and cancels.
            stop.Cancel();
        });

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, "test-director-id", "WORKSTATION-1", stop.Token);

        Assert.False(result.Success);
        Assert.NotNull(opened);
        Assert.True(rowVisibleAtOpen, "the address was not on screen when the browser was asked to open");
        Assert.Equal(opened, addressAtOpen);
        Assert.False(row().IsVisible, "the address of an ended sign-in must not stay offered");
    }

    private static async Task AssertBrowserDidNotOpenIsShownAsync(
        ISignInAddressDisplay screen, Func<Control> row, Func<TextBlock> status)
    {
        using var stop = new CancellationTokenSource();
        var runner = HostedTeamSetup.CreateRunner(_ => { }, screen,
            openBrowser: _ => throw new System.ComponentModel.Win32Exception("No application is associated with the specified file"));

        var signingIn = runner.VerifyAndSaveAsync(GatewayUrl, "test-director-id", "WORKSTATION-1", stop.Token);

        Assert.False(signingIn.IsCompleted, "a browser that cannot be opened must not end a sign-in whose address is on screen");
        Assert.True(row().IsVisible);
        Assert.True(status().IsVisible);
        Assert.Contains("Windows could not open a browser", status().Text);
        Assert.Contains("No application is associated", status().Text);

        stop.Cancel();
        var result = await signingIn;
        Assert.False(result.Success);
        Assert.False(row().IsVisible);
    }

    /// <summary>Config redirected to a throwaway root, so the screens never read the developer's own gateway.</summary>
    private static async Task WithCleanRootAsync(Func<Task> body)
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "cc-director-signin-address-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            Assert.False(GatewayConfig.Load().IsEnabled, "the throwaway root is not clean");
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
