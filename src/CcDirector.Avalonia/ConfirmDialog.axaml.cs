using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CcDirector.Avalonia;

/// <summary>
/// Minimal modal yes/no confirmation: a title, a wrapped message, and a confirm/Cancel pair.
/// Returns <c>true</c> from <see cref="Window.ShowDialog{TResult}"/> when the user confirms,
/// <c>false</c> otherwise. Used by the Settings &gt; Agents tab for the trash-icon Remove confirm
/// (issue #494) and by the Add/Edit modal for the "Discard changes?" guard.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog() : this("CC Director", "", "Remove", "Cancel") { }

    /// <param name="title">The window title.</param>
    /// <param name="message">The body question shown to the user.</param>
    /// <param name="confirmLabel">The label on the confirm button (defaults to "Remove").</param>
    /// <param name="cancelLabel">The label on the cancel button (defaults to "Cancel").</param>
    public ConfirmDialog(string title, string message, string confirmLabel = "Remove", string cancelLabel = "Cancel")
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
        CancelButton.Content = cancelLabel;
    }

    private void BtnConfirm_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
