using System.Diagnostics;
using Avalonia.Controls;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The Copy and Open in browser buttons beside a sign-in address that is waiting for the browser (issue #3504).
/// Both desktop surfaces that run the hosted sign-in - the first-run wizard and the Gateway connection panel -
/// show the address, so the two buttons do exactly the same thing in both places.
/// </summary>
internal static class SignInAddressActions
{
    /// <summary>The line shown once the address is on the clipboard.</summary>
    public const string CopiedMessage = "Copied. Paste it into your browser's address bar to sign in.";

    /// <summary>The line shown when Windows could not open a browser at all; the address above still works.</summary>
    public static string BrowserDidNotOpenMessage(string reason) =>
        $"Windows could not open a browser ({reason}). Copy the address into the browser you use - this window is still waiting.";

    /// <summary>Put the sign-in address on the clipboard of the window that shows it.</summary>
    public static async Task CopyAsync(Control owner, string address)
    {
        FileLog.Write("[SignInAddressActions] CopyAsync");
        var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard
            ?? throw new InvalidOperationException("This window has no clipboard to copy the sign-in address to.");
        await clipboard.SetTextAsync(address);
    }

    /// <summary>Ask Windows to open the sign-in address in the default browser again.</summary>
    public static void Open(string address)
    {
        FileLog.Write("[SignInAddressActions] Open");
        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }
}
