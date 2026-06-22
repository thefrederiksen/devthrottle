using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The sign-in screen (issue #580), redesigned as a single branded two-panel screen. Shown after the
/// splash, in place of the main window, when no DevThrottle credential has ever been stored on this
/// install. It states briefly that an account is required and that the Director then runs locally,
/// and offers the action that leads to login - merging the old separate "before you sign in"
/// explanation into this one screen so the user is not walked through two text screens.
///
/// The Sign in action drives the first-run login hand-off (issue #581): it opens the system browser
/// at the DevThrottle sign-in address (where the user chooses Google, GitHub, or email), captures the
/// credential the sign-in completion hands back on a loopback listener, stores it through the
/// credential service (issue #583), then clears the gate and shows the main window with no restart.
///
/// Because there is no usable main window without a credential, this screen IS the application's
/// window while blocked: the Quit action shuts the Director down.
/// </summary>
public partial class AccountGateScreen : Window
{
    /// <summary>
    /// How long a browser sign-in is waited for before it is abandoned on its own. Without this, a
    /// sign-in the user walked away from (closed the browser, never finished) would wait forever.
    /// </summary>
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Cancels the in-progress sign-in (manual Cancel or the timeout). Null when none is running.</summary>
    private CancellationTokenSource? _signInCts;

    /// <summary>True when the user pressed Cancel, so the cancelled result is reported as cancelled (not timed out).</summary>
    private bool _cancelRequested;

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

            var app = (App)global::Avalonia.Application.Current!;
            var account = app.AccountService
                ?? throw new InvalidOperationException("The DevThrottle credential service was not initialized.");

            // A fresh cancellation source per attempt, armed with the auto-timeout. Cancel (manual) or
            // the timeout both trip this token, which unblocks the loopback wait inside the coordinator.
            _cancelRequested = false;
            _signInCts?.Dispose();
            _signInCts = new CancellationTokenSource(SignInTimeout);

            // Immediate feedback (CodingStyle: responsive UI): show the busy state with a Cancel option,
            // while leaving Quit enabled so the user is never trapped waiting.
            SetBusy("Waiting for sign-in to complete in your web browser...");

            var coordinator = new FirstRunLoginCoordinator(account);
            var result = await coordinator.RunAsync(_signInCts.Token);

            if (result.Succeeded)
            {
                FileLog.Write("[AccountGateScreen] First-run login succeeded: clearing the gate and showing the main window");
                app.ProceedToMainWindowAfterLogin(this);
                return;
            }

            // Not signed in: distinguish a user cancel, the timeout, and a genuine failure so the
            // message is honest, and restore the screen so the user can try again.
            ClearBusy();
            if (_cancelRequested)
            {
                FileLog.Write("[AccountGateScreen] Sign-in cancelled by the user");
                ShowStatus("Sign-in cancelled. Click Sign in to try again.");
            }
            else if (_signInCts.IsCancellationRequested)
            {
                FileLog.Write("[AccountGateScreen] Sign-in timed out waiting for the browser hand-back");
                ShowStatus("Sign-in timed out. Click Sign in to try again.");
            }
            else
            {
                FileLog.Write($"[AccountGateScreen] First-run login did not complete: {result.FailureReason}");
                ShowStatus(result.FailureReason ?? "Sign-in did not complete. Please try again.");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountGateScreen] Sign in FAILED: {ex.Message}");
            ClearBusy();
            ShowStatus("Something went wrong while signing in. Please try again.");
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[AccountGateScreen] Cancel clicked: cancelling the in-progress sign-in");
            _cancelRequested = true;
            _signInCts?.Cancel();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountGateScreen] Cancel FAILED: {ex.Message}");
        }
    }

    private void SetBusy(string message)
    {
        SignInButton.IsEnabled = false;
        CancelButton.IsVisible = true;
        StatusText.Text = message;
        StatusText.IsVisible = true;
    }

    private void ClearBusy()
    {
        SignInButton.IsEnabled = true;
        CancelButton.IsVisible = false;
    }

    private void ShowStatus(string message)
    {
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
