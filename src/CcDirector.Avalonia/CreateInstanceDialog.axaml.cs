using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Core.Instances;
using CcDirector.Core.Utilities;
using CcDirector.Core.ErrorReports;

namespace CcDirector.Avalonia;

public partial class CreateInstanceDialog : Window
{
    /// <summary>The instance that was created, once the dialog closes with true.</summary>
    public NamedInstance? CreatedInstance { get; private set; }

    /// <summary>Whether the user asked to launch the new instance immediately.</summary>
    public bool LaunchAfter { get; private set; }

    public CreateInstanceDialog()
    {
        InitializeComponent();
        DisplayNameInput.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) UpdateSlugPreview();
        };
        Loaded += (_, _) => Dispatcher.UIThread.Post(() => DisplayNameInput.Focus());
    }

    private void UpdateSlugPreview()
    {
        var name = DisplayNameInput.Text ?? "";
        SlugPreview.Text = string.IsNullOrWhiteSpace(name)
            ? "slug: (from the name)"
            : $"slug: {NamedInstanceRegistry.PreviewSlug(name)}";
    }

    private void Input_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Create(); }
        else if (e.Key == Key.Escape) { e.Handled = true; Close(false); }
    }

    private void BtnCreate_Click(object? sender, RoutedEventArgs e) => Create();

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Create()
    {
        var displayName = (DisplayNameInput.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            // shown-error-exempt (user input): the user left the display name empty
            ShowError("Enter a display name.");
            return;
        }

        try
        {
            CreatedInstance = NamedInstanceRegistry.Create(
                displayName,
                (GatewayUrlInput.Text ?? "").Trim(),
                (GatewayTokenInput.Text ?? "").Trim());
            LaunchAfter = LaunchAfterCheck.IsChecked == true;
            Close(true);
        }
        catch (Exception ex)
        {
            ShowError(ShownError.Report("create instance", "create the instance", $"Could not create instance: {ex.Message}", ex));
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
