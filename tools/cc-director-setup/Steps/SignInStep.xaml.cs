using System.Windows;
using System.Windows.Controls;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

/// <summary>
/// The forced first-run sign-in step (issue #657), shown after the prerequisite Checks. It presents a
/// single "Sign in to DevThrottle" button; clicking it opens the system browser at the sign-in page
/// (with a loopback redirect_uri) and waits for the browser to hand the credential back over the same
/// loopback contract the Director uses. While waiting it shows a "waiting for sign-in..." state with a
/// Cancel action and a timeout, so an abandoned sign-in is never a dead end. The step raises
/// <see cref="SignInCompleted"/> exactly once, when a credential is captured, so the wizard can enable
/// Next - there is no skip. The access token is never surfaced here and never logged.
/// </summary>
public partial class SignInStep : UserControl
{
    private readonly SignInRunner _runner;
    private CancellationTokenSource? _cts;

    /// <summary>True once a sign-in has completed successfully. The wizard gates Next on this; once
    /// true it stays true so returning to the step via Back keeps the completed state.</summary>
    public bool IsSignedIn { get; private set; }

    /// <summary>Raised once when the browser hand-back is captured, so the wizard can enable Next.</summary>
    public event EventHandler? SignInCompleted;

    public SignInStep() : this(new SignInRunner())
    {
    }

    /// <summary>Constructor seam so a test or a non-default timeout can inject a runner.</summary>
    public SignInStep(SignInRunner runner)
    {
        InitializeComponent();
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        SetupLog.Write("[SignInStep] Created");
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[SignInStep] SignInButton_Click");
        try
        {
            EnterWaitingState();

            _cts = new CancellationTokenSource();
            var result = await _runner.RunAsync(_cts.Token);

            ApplyResult(result);
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[SignInStep] SignInButton_Click FAILED: {ex}");
            ShowRetryable("Sign-in failed unexpectedly. Please try again.");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[SignInStep] CancelButton_Click: cancelling the sign-in wait");
        // Cancelling the wait makes RunAsync return a Cancelled result, which ApplyResult turns into
        // a retryable state. The button itself stays disabled until the wait unwinds.
        CancelButton.IsEnabled = false;
        _cts?.Cancel();
    }

    /// <summary>Shows the "waiting for sign-in..." state: hide the button and any prior message,
    /// show the waiting row with an enabled Cancel.</summary>
    private void EnterWaitingState()
    {
        SignInButton.IsEnabled = false;
        StatusText.Visibility = Visibility.Collapsed;
        SuccessPanel.Visibility = Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        WaitingPanel.Visibility = Visibility.Visible;
    }

    private void ApplyResult(SignInResult result)
    {
        SetupLog.Write($"[SignInStep] ApplyResult: outcome={result.Outcome}");
        WaitingPanel.Visibility = Visibility.Collapsed;

        if (result.Succeeded)
        {
            IsSignedIn = true;
            SignInButton.Visibility = Visibility.Collapsed;
            SuccessPanel.Visibility = Visibility.Visible;
            SignInCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        ShowRetryable(result.Message);
    }

    /// <summary>Returns the step to a retryable state with a message - the Sign-in button is
    /// re-enabled so the user can try again (cancel, timeout, or failure paths).</summary>
    private void ShowRetryable(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        SignInButton.IsEnabled = true;
    }
}
