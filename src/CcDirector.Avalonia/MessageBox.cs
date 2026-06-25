using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// Minimal modal "OK" message dialog, built in code so callers can surface a plain
/// error/notice without authoring a one-off XAML window each time. Matches the app's
/// dark theme (see InputDialog.axaml for the shared palette).
/// </summary>
public static class MessageBox
{
    private static readonly IBrush WindowBackground = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush TextForeground = new SolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush ButtonBackground = new SolidColorBrush(Color.Parse("#007ACC"));

    /// <summary>Show a modal message dialog with a single OK button and wait for it to close.</summary>
    public static Task ShowAsync(Window owner, string title, string message)
    {
        FileLog.Write($"[MessageBox] ShowAsync: title={title}");

        var messageText = new TextBlock
        {
            Text = message,
            Foreground = TextForeground,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var okButton = new Button
        {
            Content = "OK",
            Width = 80,
            Height = 28,
            Background = ButtonBackground,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };

        var dialog = new Window
        {
            Title = title,
            Background = WindowBackground,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    messageText,
                    okButton
                }
            }
        };

        okButton.Click += (_, _) => dialog.Close();

        return dialog.ShowDialog(owner);
    }

    /// <summary>
    /// Show a modal confirmation dialog with Yes / No buttons and return the user's choice
    /// (true = Yes). Used for destructive or overwrite confirmations such as replacing an
    /// existing named session.
    /// </summary>
    public static async Task<bool> ShowConfirmAsync(Window owner, string title, string message,
        string confirmText = "Yes", string cancelText = "No")
    {
        FileLog.Write($"[MessageBox] ShowConfirmAsync: title={title}");

        var messageText = new TextBlock
        {
            Text = message,
            Foreground = TextForeground,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var confirmButton = new Button
        {
            Content = confirmText,
            Width = 80,
            Height = 28,
            Background = ButtonBackground,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            IsDefault = true
        };

        var cancelButton = new Button
        {
            Content = cancelText,
            Width = 80,
            Height = 28,
            Background = new SolidColorBrush(Color.Parse("#3C3C3C")),
            Foreground = TextForeground,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true
        };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { confirmButton, cancelButton }
        };

        var dialog = new Window
        {
            Title = title,
            Background = WindowBackground,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    messageText,
                    buttonRow
                }
            }
        };

        var confirmed = false;
        confirmButton.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancelButton.Click += (_, _) => { confirmed = false; dialog.Close(); };

        await dialog.ShowDialog(owner);
        return confirmed;
    }
}
