using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CcDirector.Avalonia;

/// <summary>
/// Minimal modal message box: a window title, a wrapped message, and an OK button.
/// Use for blocking "you need to do X first" / "this action cannot proceed" messages
/// that must not be missed (the bottom notification bar is too subtle for those).
/// </summary>
public partial class MessageDialog : Window
{
    public MessageDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e) => Close(true);
}
