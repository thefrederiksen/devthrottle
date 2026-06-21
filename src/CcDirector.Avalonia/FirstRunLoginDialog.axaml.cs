using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The first-run explanation shown BEFORE the system-browser sign-in (issue #581). It tells the user
/// that an account is required to run the Director and - critically - that first-time setup needs to
/// be online, before the browser is opened to sign in. The user confirms with "Continue to sign in"
/// (this dialog closes with <see cref="Confirmed"/> true) or backs out with "Cancel" (false). It owns
/// no login logic itself - it is purely the explanatory gate the gate screen shows ahead of the
/// hand-off, so the user is never sent to the browser without first being told why.
/// </summary>
public partial class FirstRunLoginDialog : Window
{
    /// <summary>True when the user chose to continue to sign in; false when they cancelled.</summary>
    public bool Confirmed { get; private set; }

    public FirstRunLoginDialog()
    {
        InitializeComponent();
        FileLog.Write("[FirstRunLoginDialog] Shown: first-run explanation (account required, first setup must be online)");
    }

    private void BtnContinue_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[FirstRunLoginDialog] Continue clicked: proceeding to the system-browser sign-in");
            Confirmed = true;
            Close(true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunLoginDialog] Continue FAILED: {ex.Message}");
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write("[FirstRunLoginDialog] Cancel clicked: not signing in");
            Confirmed = false;
            Close(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunLoginDialog] Cancel FAILED: {ex.Message}");
        }
    }
}
