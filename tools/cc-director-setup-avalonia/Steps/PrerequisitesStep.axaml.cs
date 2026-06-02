using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class PrerequisitesStep : UserControl
{
    private readonly Action<List<PrerequisiteInfo>>? _onChecksComplete;
    private List<PrerequisiteInfo> _items;

    public PrerequisitesStep()
    {
        InitializeComponent();
        _items = [];
    }

    public PrerequisitesStep(Action<List<PrerequisiteInfo>> onChecksComplete, bool isUpdate)
    {
        InitializeComponent();
        _onChecksComplete = onChecksComplete;
        _items = PrerequisiteChecker.CreateChecklist();
        PrereqList.ItemsSource = _items;

        if (isUpdate)
            SubtitleText.Text = "Verifying your environment...";

        SetupLog.Write($"[PrerequisitesStep] Created: isUpdate={isUpdate}");
    }

    public async void RunChecks()
    {
        SetupLog.Write("[PrerequisitesStep] RunChecks: starting");
        RefreshButton.IsEnabled = false;

        _items = PrerequisiteChecker.CreateChecklist();
        PrereqList.ItemsSource = _items;

        await PrerequisiteChecker.CheckAllAsync(_items);

        RefreshButton.IsEnabled = true;

        var allRequiredMet = _items.Where(p => p.IsRequired).All(p => p.IsFound);
        if (allRequiredMet)
        {
            SubtitleText.Text = "All required prerequisites found. You can install now.";
            SuccessBanner.IsVisible = true;
        }
        else
        {
            SubtitleText.Text = "Some required prerequisites are missing. Install them and re-check.";
            SuccessBanner.IsVisible = false;
        }

        _onChecksComplete?.Invoke(_items);
        SetupLog.Write($"[PrerequisitesStep] RunChecks: complete, allRequiredMet={allRequiredMet}");
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        RunChecks();
    }

    private void InstallLink_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TextBlock textBlock && textBlock.Tag is string url && !string.IsNullOrEmpty(url))
        {
            SetupLog.Write($"[PrerequisitesStep] InstallLink_PointerPressed: url={url}");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }
}
