using System.Threading.Tasks;
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
        Loaded += async (_, _) =>
        {
            Dispatcher.UIThread.Post(() => DisplayNameInput.Focus());
            try
            {
                var instances = await Task.Run(NamedInstanceRegistry.List);
                FillCopyFrom(instances);
            }
            catch (Exception ex)
            {
                // The list is only an offer; without it the dialog still creates a fresh instance.
                CopyFromInput.ItemsSource = new[] { new CopyFromChoice(null, FreshLabel) };
                CopyFromInput.SelectedIndex = 0;
                ShowError(ShownError.Report("create instance", "list the instances to copy from",
                    $"Could not list the instances to copy settings from: {ex.Message}", ex));
            }
        };
    }

    private const string FreshLabel = "Nothing - start fresh";

    /// <summary>One entry of the "Copy settings from" list: an instance, or none.</summary>
    private sealed record CopyFromChoice(string? Slug, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// Offer every instance to copy from, the one this window belongs to first and preselected: a test
    /// Director is almost always a copy of the one being looked at (owner, 2026-10-17).
    /// </summary>
    private void FillCopyFrom(IReadOnlyList<NamedInstance> instances)
    {
        var choices = new List<CopyFromChoice>();
        foreach (var i in instances.OrderBy(i => string.Equals(i.Name, InstanceContext.Slug, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            var label = string.IsNullOrWhiteSpace(i.DisplayName) || string.Equals(i.DisplayName, i.Name, StringComparison.Ordinal)
                ? i.Name
                : $"{i.DisplayName} ({i.Name})";
            choices.Add(new CopyFromChoice(i.Name, label));
        }
        choices.Add(new CopyFromChoice(null, FreshLabel));
        CopyFromInput.ItemsSource = choices;
        CopyFromInput.SelectedIndex = 0;
        FileLog.Write($"[CreateInstanceDialog] FillCopyFrom: {choices.Count - 1} instance(s) offered");
    }

    private void CopyFromInput_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var source = (CopyFromInput.SelectedItem as CopyFromChoice)?.Slug;
        // A clone takes the Gateway connection from its source, so the two Gateway boxes only apply to a fresh one.
        GatewayInputs.IsVisible = source is null;
        CopyFromNote.Text = source is null
            ? "A new Director with its own data folder. Nothing is copied."
            : "Copies the Gateway connection, agent settings, repositories, screenshots folder and microphone "
              + "timings. Not copied: sessions, history, the Director's identity, Outlook and browser sign-ins.";
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
            var source = (CopyFromInput.SelectedItem as CopyFromChoice)?.Slug;
            CreatedInstance = source is null
                ? NamedInstanceRegistry.Create(
                    displayName,
                    (GatewayUrlInput.Text ?? "").Trim(),
                    (GatewayTokenInput.Text ?? "").Trim())
                : NamedInstanceRegistry.Clone(source, displayName);
            FileLog.Write($"[CreateInstanceDialog] Create: {CreatedInstance.Name}, copiedFrom={source ?? "(nothing)"}");
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
