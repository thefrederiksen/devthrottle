using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Core.Instances;
using CcDirector.Core.Utilities;
using CcDirector.Core.ErrorReports;

namespace CcDirector.Avalonia;

public partial class RenameDirectorDialog : Window
{
    private readonly string _slug;

    /// <summary>The new display name, once the dialog closes with true.</summary>
    public string? NewDisplayName { get; private set; }

    public RenameDirectorDialog(string slug, string currentDisplayName)
    {
        InitializeComponent();
        _slug = slug;
        NameInput.Text = currentDisplayName;
        UnchangedNote.Text =
            $"Only the label changes. The slug ({slug}), folder, id, port and gateway all stay the same.";
        Loaded += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        });
    }

    // Parameterless constructor for the XAML designer.
    public RenameDirectorDialog() : this(InstanceContext.DefaultSlug, "") { }

    private void NameInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Accept(); }
        else if (e.Key == Key.Escape) { e.Handled = true; Close(false); }
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e) => Accept();

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Accept()
    {
        var name = (NameInput.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            // shown-error-exempt (user input): the user left the display name empty
            ErrorText.Text = "Enter a display name.";
            ErrorText.IsVisible = true;
            return;
        }

        try
        {
            NamedInstanceRegistry.Rename(_slug, name);
            NewDisplayName = name;
            Close(true);
        }
        catch (Exception ex)
        {
            ErrorText.Text = ShownError.Report("rename Director", "rename this Director", $"Could not rename: {ex.Message}", ex);
            ErrorText.IsVisible = true;
        }
    }
}
