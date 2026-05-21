using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace CcDirector.Avalonia;

public partial class RenameSessionDialog : Window
{
    public string? SessionName { get; private set; }

    public RenameSessionDialog(string currentName)
    {
        InitializeComponent();
        NameInput.Text = currentName;
        Loaded += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                NameInput.Focus();
                NameInput.SelectAll();
            });
        };
    }

    // Parameterless constructor for XAML designer
    public RenameSessionDialog() : this("") { }

    private void NameInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Accept();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(false);
        }
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e) => Accept();

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Accept()
    {
        SessionName = NameInput.Text;
        Close(true);
    }
}
