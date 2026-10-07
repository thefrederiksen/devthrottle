using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Issue #3503: the wizard puts a desktop shortcut down on a first install only, so an update never
/// puts back an icon the person deleted. The rule is <c>InstallCompletion.CreatesDesktopShortcut</c>;
/// this pins that the wizard - the rule's only caller - hands every install runner it builds the
/// rule's answer, not a constant. Writing <c>CreateDesktopShortcut = true</c> at any construction
/// turns this red.
/// </summary>
public sealed class DesktopShortcutOnFirstInstallOnlyTests
{
    [Fact]
    public void MainWindow_EveryInstallRunner_TakesTheDesktopShortcutFromTheRule()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "cc-director-setup", "MainWindow.xaml.cs"));

        var runners = Regex.Matches(source, @"new EngineInstallRunner\s*\{(?<body>[^}]*)\}");
        Assert.NotEmpty(runners);
        foreach (Match runner in runners)
            Assert.Contains("CreateDesktopShortcut = InstallCompletion.CreatesDesktopShortcut(_isUpdate)", runner.Groups["body"].Value);
    }

    [Fact]
    public void EngineInstallRunner_WritesTheDesktopShortcutOnlyWhenAsked()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "cc-director-setup", "Services", "EngineInstallRunner.cs"));

        var calls = Regex.Matches(source, @"InstallFinalizer\.CreateDesktopShortcut\(");
        Assert.Single(calls);
        Assert.Matches(@"if \(CreateDesktopShortcut && OperatingSystem\.IsWindows\(\)\)\s*InstallFinalizer\.CreateDesktopShortcut\(", source);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tools", "cc-director-setup")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repo root (tools/cc-director-setup) walking up from " + AppContext.BaseDirectory);
    }
}
