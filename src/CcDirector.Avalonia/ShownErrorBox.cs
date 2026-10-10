using System.Runtime.CompilerServices;
using Avalonia.Controls;
using CcDirector.Core.ErrorReports;

namespace CcDirector.Avalonia;

/// <summary>
/// An error in a message box, reported in the same call (issue #3675, step 4a). The message-box half of
/// <see cref="ShownError"/>: every box that tells the user something failed goes through here, so the box and
/// the report cannot come apart. <c>ShownErrorSourceScanTests</c> fails the build on a message box anywhere else
/// that is not marked as a box that shows no error.
///
/// The report is written BEFORE the box opens, because the box waits for the user and the user may never press
/// OK - a report written after it would be lost with a closed window or a killed process.
/// </summary>
public static class ShownErrorBox
{
    /// <summary>Report the error, then show it in the plain code-built box (<see cref="MessageBox.ShowAsync"/>).</summary>
    /// <param name="owner">The window the box belongs to.</param>
    /// <param name="surface">Which screen, in plain words (see <see cref="ShownError.Report"/>).</param>
    /// <param name="action">What the user was trying to do, as it reads after "could not".</param>
    /// <param name="title">The box's title.</param>
    /// <param name="message">The text in the box.</param>
    /// <param name="exception">The exception behind it, when there is one.</param>
    /// <param name="reported">What to report instead of <paramref name="message"/> when the message carries words
    /// that must not leave the machine.</param>
    public static Task ShowAsync(
        Window owner,
        string surface,
        string action,
        string title,
        string message,
        Exception? exception = null,
        string? reported = null,
        [CallerFilePath] string callerFile = "",
        [CallerMemberName] string callerMember = "")
    {
        ArgumentNullException.ThrowIfNull(owner);
        ShownError.Report(surface, action, message, exception, reported, fatal: false, callerFile, callerMember);
        return MessageBox.ShowAsync(owner, title, message);
    }

    /// <summary>Report the error, then show it in the XAML <see cref="MessageDialog"/> - for the sites that used that
    /// look, so moving them onto the helper does not change what the user sees.</summary>
    public static Task ShowDialogAsync(
        Window owner,
        string surface,
        string action,
        string title,
        string message,
        Exception? exception = null,
        string? reported = null,
        [CallerFilePath] string callerFile = "",
        [CallerMemberName] string callerMember = "")
    {
        ArgumentNullException.ThrowIfNull(owner);
        ShownError.Report(surface, action, message, exception, reported, fatal: false, callerFile, callerMember);
        return new MessageDialog(title, message).ShowDialog<bool?>(owner);
    }
}
