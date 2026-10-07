using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Owner ruling of 7 October 2026 (#3411): a failed launcher must NOT fail the Mac install. The installer
/// continues past it, installs and opens the Director, and the launcher failure is a warning that is reported
/// to DevThrottle. No test project references the macOS wizard (tools/cc-director-setup-avalonia), so its
/// wiring is pinned here from source, the way <see cref="DesktopShortcutOnFirstInstallOnlyTests"/> pins the
/// Windows wizard's. Marking the launcher row "Failed" again, or dropping the warnings from the Complete
/// screen, turns this red.
/// </summary>
public sealed class MacWizardLauncherWarningTests
{
    private static string MacWizard(params string[] parts)
        => File.ReadAllText(Path.Combine([FindRepoRoot(), "tools", "cc-director-setup-avalonia", .. parts]));

    [Fact]
    public void InstallLauncherAsync_NeverMarksTheLauncherRowFailed()
    {
        var source = MacWizard("Services", "EngineInstallRunner.cs");
        var start = source.IndexOf("private async Task<bool> InstallLauncherAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "InstallLauncherAsync not found");
        var end = source.IndexOf("private async Task ReportOutcomeAsync(", start, StringComparison.Ordinal);
        var body = source[start..(end > start ? end : source.Length)];

        Assert.DoesNotContain("\"Failed\"", body);
        // Both ways the launcher can fail - placement and start - carry the warning status and are reported.
        Assert.Equal(2, Regex.Matches(body, @"item\.Status = InstallCompletion\.WarningStatus").Count
                        + Regex.Matches(body, @"startResult\.Success \? ""Done"" : InstallCompletion\.WarningStatus").Count);
        Assert.Contains("ReportFailureAsync(item, \"launcher\", \"place\", WarningMessage(", body);
        Assert.Contains("ReportFailureAsync(item, \"launcher\", \"start\", WarningMessage(", body);
    }

    [Fact]
    public void ApplyAsync_DoesNotCountAWarningAsSkipped()
    {
        var source = MacWizard("Services", "EngineInstallRunner.cs");
        Assert.Contains("var skipped = prep.Items.Count(i => i.Status is \"Skipped\" or \"Failed\");", source);
    }

    [Fact]
    public void WarningMessage_SaysTheInstallContinued()
    {
        var source = MacWizard("Services", "EngineInstallRunner.cs");
        Assert.Contains("\"WARNING (the install continued without autostart): \" + failure", source);
    }

    [Fact]
    public void MainWindow_HandsTheWarningsToTheCompleteScreen()
    {
        var source = MacWizard("MainWindow.axaml.cs");
        Assert.Contains(".Where(i => i.Status == InstallCompletion.WarningStatus)", source);
        Assert.Matches(@"new CompleteStep\([^;]*_warnings\)", source);
    }

    // The Windows wizard's rule (#3503), now on the Mac too: closing the wizard on a first install opens the
    // Director, and the rule is read from InstallCompletion, not re-decided here.
    [Fact]
    public void MainWindow_OpensTheDirectorOnCloseByTheSharedRule()
    {
        var source = MacWizard("MainWindow.axaml.cs");
        Assert.Contains("protected override void OnClosing(WindowClosingEventArgs e)", source);
        Assert.Contains("InstallCompletion.OpensDirectorOnClose(_isUpdate, complete.DirectorOpened,", source);
        Assert.Contains("if (opens) complete.OpenDirector();", source);
    }

    [Fact]
    public void CompleteStep_ShowsTheWarningPanelWithTheSharedExplanation()
    {
        var markup = MacWizard("Steps", "CompleteStep.axaml");
        var code = MacWizard("Steps", "CompleteStep.axaml.cs");
        Assert.Contains("x:Name=\"WarningPanel\"", markup);
        Assert.Contains("x:Name=\"WarningText\"", markup);
        Assert.Contains("InstallCompletion.LauncherWarningExplanation", code);
        Assert.Contains("WarningPanel.IsVisible = true;", code);
        Assert.Contains("public bool DirectorOpened { get; private set; }", code);
        Assert.Contains("public bool OpenDirector()", code);
    }

    [Fact]
    public void InstallRows_RenderTheWarningStatusAmberNotRed()
    {
        var source = MacWizard("Models", "ToolDownloadItem.cs");
        Assert.Matches(@"""Warning"" => ""#E0A030""", source);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tools", "cc-director-setup-avalonia")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repo root (tools/cc-director-setup-avalonia) walking up from " + AppContext.BaseDirectory);
    }
}
