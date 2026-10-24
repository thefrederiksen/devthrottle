using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Every way into a Settings tab lands on that tab. They used to select by position, and inserting the Team tab shifted
/// every position after it: Manage Browser Profiles opened the Agents tab and the Home tools Fix opened Advanced. The
/// GitHub tab (issue #4426) is selected the same way, by the tab itself.
/// </summary>
public sealed class SettingsDialogTabSelectionTests
{
    private static string? SelectedHeader(SettingsDialog dialog) =>
        (dialog.FindControl<TabControl>("SettingsTabs")!.SelectedItem as TabItem)?.Header as string;

    [AvaloniaFact]
    public void SelectGitHubTab_ShowsTheGitHubTab()
    {
        var dialog = new SettingsDialog();
        dialog.SelectGitHubTab();
        Assert.Equal("GitHub", SelectedHeader(dialog));
    }

    [AvaloniaFact]
    public void SelectBrowsersTab_ShowsBrowserProfiles_NotAgents()
    {
        var dialog = new SettingsDialog();
        dialog.SelectBrowsersTab();
        Assert.Equal("Browser profiles", SelectedHeader(dialog));
    }

    [AvaloniaFact]
    public void SelectToolsTab_ShowsTools_NotAdvanced()
    {
        var dialog = new SettingsDialog();
        dialog.SelectToolsTab();
        Assert.Equal("Tools", SelectedHeader(dialog));
    }

    [AvaloniaFact]
    public void SelectGatewayTab_ShowsGateway()
    {
        var dialog = new SettingsDialog();
        dialog.SelectGatewayTab();
        Assert.Equal("Gateway", SelectedHeader(dialog));
    }
}
