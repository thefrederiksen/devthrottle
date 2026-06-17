using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CcDirector.Avalonia;

/// <summary>
/// Minimal modal yes/no confirmation: a title, a wrapped message, and a confirm/Cancel pair.
/// Returns <c>true</c> from <see cref="Window.ShowDialog{TResult}"/> when the user confirms,
/// <c>false</c> otherwise. Use for destructive actions that need an explicit "are you sure".
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <param name="title">The window title.</param>
    /// <param name="message">The body question shown to the user.</param>
    /// <param name="confirmLabel">The label on the confirm button (defaults to "Remove").</param>
    public ConfirmDialog(string title, string message, string confirmLabel = "Remove")
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
    }

    private void BtnConfirm_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
