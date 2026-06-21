using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The account gate screen (issue #580). Shown after the splash, in place of the main window, when
/// no DevThrottle credential has ever been stored on this install. It explains that an account is
/// required and why - that the Director runs locally and quietly once the user is signed in - and
/// offers the action that leads to login.
///
/// The Sign in action drives the first-run login hand-off (issue #581): it shows the first-run
/// explanation (an account is required and first setup must be online), opens the system browser at
/// the DevThrottle sign-in address, captures the credential the sign-in completion hands back on a
/// loopback listener, stores it through the credential service (issue #583), then clears the gate and
/// shows the main window with no restart.
///
/// Because there is no usable main window without a credential, this screen IS the application's
/// window while blocked: the Quit action shuts the Director down.
/// </summary>
public partial class AccountGateScreen : Window
{
    public AccountGateScreen()
    {
        InitializeComponent();
        FileLog.Write("[AccountGateScreen] Shown: no DevThrottle credential on this install; main window blocked");
    }

    private async void BtnSignIn_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[AccountGateScreen] Sign in clicked: starting the first-run login hand-off (issue #581)");

            // First-run explanation BEFORE login: an account is required and first setup must be
            // online. The user must confirm before the browser is opened.
            var explanation = new FirstRunLoginDialog();
            var confirmed = await explanation.ShowDialog<bool>(this);
            if (!confirmed)
            {
                FileLog.Write("[AccountGateScreen] First-run explanation cancelled: staying on the gate");
                return;
            }

            var app = (App)global::Avalonia.Application.Current!;
            var account = app.AccountService
                ?? throw new InvalidOperationException("The DevThrottle credential service was not initialized.");

            // Immediate feedback (CodingStyle: responsive UI): show the busy state and disable the
            // action while we wait for the browser hand-back.
            SetBusy("Waiting for sign-in to complete in your web browser...");

            var coordinator = new FirstRunLoginCoordinator(account);
            var result = await coordinator.RunAsync();

            if (result.Succeeded)
            {
                FileLog.Write("[AccountGateScreen] First-run login succeeded: clearing the gate and showing the main window");
                app.ProceedToMainWindowAfterLogin(this);
                return;
            }

            FileLog.Write($"[AccountGateScreen] First-run login did not complete: {result.FailureReason}");
            ShowError(result.FailureReason ?? "Sign-in did not complete. Please try again.");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountGateScreen] Sign in FAILED: {ex.Message}");
            ShowError("Something went wrong while signing in. Please try again.");
        }
    }

    private void SetBusy(string message)
    {
        SignInButton.IsEnabled = false;
        QuitButton.IsEnabled = false;
        StatusText.Text = message;
        StatusText.IsVisible = true;
    }

    private void ShowError(string message)
    {
        SignInButton.IsEnabled = true;
        QuitButton.IsEnabled = true;
        StatusText.Text = message;
        StatusText.IsVisible = true;
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
