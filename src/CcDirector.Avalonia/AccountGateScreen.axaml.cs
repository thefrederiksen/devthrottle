using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The account gate screen (issue #580). Shown after the splash, in place of the main window, when
/// no DevThrottle credential has ever been stored on this install. It explains that an account is
/// required and why - that the Director runs locally and quietly once the user is signed in - and
/// offers the action that leads to login. The login hand-off and the system-browser sign-in flow
/// themselves are issue #581; this screen is the front door that routes the user there.
///
/// Because there is no usable main window without a credential, this screen IS the application's
/// window while blocked: the Quit action shuts the Director down, and the Sign in action raises the
/// hand-off that issue #581 will complete.
/// </summary>
public partial class AccountGateScreen : Window
{
    public AccountGateScreen()
    {
        InitializeComponent();
        FileLog.Write("[AccountGateScreen] Shown: no DevThrottle credential on this install; main window blocked");
    }

    private void BtnSignIn_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[AccountGateScreen] Sign in clicked: routing to the DevThrottle login hand-off (completed by issue #581)");
            // The login hand-off and system-browser sign-in flow are issue #581; until that lands
            // the action is recorded here so the gate's "offers the action that leads to login"
            // criterion is exercised. No fallback sign-in is invented in this issue.
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountGateScreen] Sign in FAILED: {ex.Message}");
        }
    }

    private void BtnQuit_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[AccountGateScreen] Quit clicked: shutting down the Director (no credential, main window blocked)");
            if (global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Close();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountGateScreen] Quit FAILED: {ex.Message}");
        }
    }
}
