namespace CcDirector.Setup.Engine;

/// <summary>
/// A screen that shows the browser sign-in's address while the sign-in waits for the browser (issue #3504).
///
/// Opening a link goes through the Windows shell. On a machine with a newly installed second browser the shell
/// answers with an app chooser that disappears when focus moves, and on a machine with no browser registered it
/// fails outright. Either way no browser opens, so the address on screen - with Copy and Open in browser - is the
/// person's way in. The sign-in calls these in order: <see cref="Show"/> before the browser is asked to open,
/// <see cref="BrowserDidNotOpen"/> if asking failed, and <see cref="Withdraw"/> when the wait ends for any reason,
/// because from then on nothing is listening at that address.
/// </summary>
public interface ISignInAddressDisplay
{
    /// <summary>Show the sign-in address. Called before the browser is asked to open it.</summary>
    void Show(string address);

    /// <summary>Windows could not open a browser at all; the sign-in keeps waiting for the address shown.</summary>
    void BrowserDidNotOpen(string reason);

    /// <summary>The sign-in stopped waiting (signed in, cancelled, timed out or failed): stop offering the address.</summary>
    void Withdraw();
}
