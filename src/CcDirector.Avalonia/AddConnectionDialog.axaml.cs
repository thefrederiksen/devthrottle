using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// Dialog for adding a new browser connection.
/// Validates name as lowercase alphanumeric with hyphens.
/// </summary>
public partial class AddConnectionDialog : Window
{
    private static readonly Regex ValidName = new("^[a-z0-9][a-z0-9-]*$");

    public string ConnectionName { get; private set; } = "";
    public string ConnectionDescription { get; private set; } = "";
    public string? ConnectionUrl { get; private set; }
    public string? ConnectionTool { get; private set; }

    public AddConnectionDialog()
    {
        InitializeComponent();
        FileLog.Write("[AddConnectionDialog] Opened");
    }

    private void TxtName_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var name = TxtName.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(name))
        {
            NameError.IsVisible = false;
            BtnOk.IsEnabled = false;
            return;
        }

        if (!ValidName.IsMatch(name))
        {
            // shown-error-exempt (user input): the name the user typed has characters a connection name cannot hold
            NameError.Text = "Use lowercase letters, numbers, and hyphens only";
            NameError.IsVisible = true;
            BtnOk.IsEnabled = false;
            return;
        }

        if (name.Length > 50)
        {
            // shown-error-exempt (user input): the name the user typed is longer than a connection name may be
            NameError.Text = "Name must be 50 characters or less";
            NameError.IsVisible = true;
            BtnOk.IsEnabled = false;
            return;
        }

        NameError.IsVisible = false;
        BtnOk.IsEnabled = true;
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        ConnectionName = TxtName.Text?.Trim() ?? "";
        ConnectionDescription = TxtDescription.Text?.Trim() ?? "";
        ConnectionUrl = string.IsNullOrWhiteSpace(TxtUrl.Text) ? null : TxtUrl.Text.Trim();

        var selectedTool = (CmbTool.SelectedItem as ComboBoxItem)?.Content?.ToString();
        ConnectionTool = selectedTool == "(none)" ? null : selectedTool;

        FileLog.Write($"[AddConnectionDialog] OK: name={ConnectionName}, url={ConnectionUrl}, tool={ConnectionTool}");

        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[AddConnectionDialog] Cancelled");
        Close(false);
    }
}
