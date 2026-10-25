using System.Runtime.InteropServices;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The shared Python tools bundle as a component the setup commands can SEE.
///
/// Before this it was the only thing the tools install actually downloads, versions and records - its
/// version sits in installed.json under "python-tools" - and it was the one thing components, status
/// and plan never mentioned, while all three reported individual tools that have no asset, no version
/// and no install action of their own (#4291).
///
/// The rule that matters most here is the NEGATIVE one: the bundle must never be actionable. It has
/// its own install step, and the generic UpdateRunner only skips .zip assets while the macOS and Linux
/// bundles are .tar.gz - so an actionable bundle would be downloaded and placed as though it were a
/// single executable.
/// </summary>
public class ToolsBundleComponentTests
{
    private const string Root = "/Users/someone/Library/Application Support/cc-director";
    private static InstallLayout Layout() => new(Root, defaultRoot: null, homeDir: "/Users/someone");

    private static ReleaseManifest Manifest(params (string asset, string version)[] assets)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{ \"version\": \"2.28.0\", \"assets\": {");
        for (var i = 0; i < assets.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"\"{assets[i].asset}\": {{ \"version\": \"{assets[i].version}\", " +
                      "\"sha256\": \"AB\", \"platform\": \"any\" }");
        }
        sb.Append("} }");
        return ReleaseManifest.Parse(sb.ToString());
    }

    [Fact]
    public void TheBundleIsAComponent_UnderTheSameIdItsVersionIsRecordedWith()
    {
        // The id has to match, or status would report a component whose version is recorded elsewhere
        // and always read as unknown.
        Assert.Equal(PythonToolsInstaller.ComponentId, ComponentRegistry.PythonTools.Id);
        Assert.Contains(ComponentRegistry.PythonTools, ComponentRegistry.Apps);
    }

    [Fact]
    public void TheBundleIsInBothRoles_BecauseAGatewayInstallIsASupersetOfAWorkstationOne()
    {
        Assert.True(ComponentRegistry.PythonTools.InRole(InstallRole.Workstation));
        Assert.True(ComponentRegistry.PythonTools.InRole(InstallRole.Gateway));
    }

    [Fact]
    public void TheBundleCarriesTheRealAssetNameForEveryPlatform()
    {
        var c = ComponentRegistry.PythonTools;

        Assert.Equal("cc-tools-pyenv-win-x64.zip", c.AssetFor(OSPlatform.Windows));
        Assert.Equal("cc-tools-pyenv-macos-arm64.tar.gz", c.AssetFor(OSPlatform.OSX));
        Assert.Equal("cc-tools-pyenv-linux-x64.tar.gz", c.AssetFor(OSPlatform.Linux));
    }

    [Fact]
    public void OnDiskTheBundleIsTheSharedVenvFolder_OnEveryPlatform()
    {
        var layout = Layout();

        foreach (var platform in new[] { OSPlatform.Windows, OSPlatform.OSX, OSPlatform.Linux })
            Assert.Equal(layout.PyenvDir, layout.PathFor(ComponentRegistry.PythonTools, platform));
    }

    // ---- the negative rule: never actionable -------------------------------

    [Fact]
    public void ThePlanAccountsForTheBundle_ButNeverAsSomethingToInstall()
    {
        var manifest = Manifest(("cc-tools-pyenv-macos-arm64.tar.gz", "2.28.0"));
        var installed = new Dictionary<string, InstalledComponent>(StringComparer.OrdinalIgnoreCase);

        var plan = UpdatePlanner.Plan(
            [ComponentRegistry.PythonTools], installed, manifest, null, OSPlatform.OSX);

        var item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemKind.InstalledByTheToolsStep, item.Kind);
        Assert.False(item.IsActionable);
        Assert.Empty(plan.Actionable);
    }

    [Fact]
    public void TheBundleIsNotActionableOnAnyPlatform_EvenWhenItsAssetIsInTheManifestAndNothingIsInstalled()
    {
        // The strongest form of the rule: absent locally, present in the release - the exact shape that
        // would otherwise plan as Install - and still not actionable, on all three platforms. On macOS
        // and Linux the asset is a .tar.gz, which the runner does NOT skip.
        var manifest = Manifest(
            ("cc-tools-pyenv-win-x64.zip", "2.28.0"),
            ("cc-tools-pyenv-macos-arm64.tar.gz", "2.28.0"),
            ("cc-tools-pyenv-linux-x64.tar.gz", "2.28.0"));
        var nothingInstalled = new Dictionary<string, InstalledComponent>(StringComparer.OrdinalIgnoreCase);

        foreach (var platform in new[] { OSPlatform.Windows, OSPlatform.OSX, OSPlatform.Linux })
        {
            var plan = UpdatePlanner.Plan(
                [ComponentRegistry.PythonTools], nothingInstalled, manifest, null, platform);

            Assert.Empty(plan.Actionable);
            Assert.Empty(plan.ToInstall);
            Assert.Empty(plan.ToUpdate);
            Assert.Equal(PlanItemKind.InstalledByTheToolsStep, Assert.Single(plan.Items).Kind);
        }
    }

    [Fact]
    public void TheBundleIsNotCountedAsAMissingAsset_WhenTheReleaseHasNoBundleAtAll()
    {
        // It must not be reported as a hole in the release either: its install step owns it.
        var manifest = Manifest(("cc-director-mac-arm64.zip", "2.28.0"));
        var installed = new Dictionary<string, InstalledComponent>(StringComparer.OrdinalIgnoreCase);

        var plan = UpdatePlanner.Plan(
            [ComponentRegistry.PythonTools], installed, manifest, null, OSPlatform.OSX);

        Assert.Empty(plan.MissingAssets);
        Assert.Equal(PlanItemKind.InstalledByTheToolsStep, Assert.Single(plan.Items).Kind);
    }

    [Fact]
    public void ThePlanReportsTheVersionTheMachineHas_SoAnAgentCanSeeWhetherItIsThere()
    {
        var manifest = Manifest(("cc-tools-pyenv-macos-arm64.tar.gz", "2.28.0"));
        var installed = new Dictionary<string, InstalledComponent>(StringComparer.OrdinalIgnoreCase)
        {
            [PythonToolsInstaller.ComponentId] = new InstalledComponent(
                PythonToolsInstaller.ComponentId, Present: true, Version: "2.28.0", Path: "/x/pyenv"),
        };

        var plan = UpdatePlanner.Plan(
            [ComponentRegistry.PythonTools], installed, manifest, null, OSPlatform.OSX);

        Assert.Equal("2.28.0", Assert.Single(plan.Items).FromVersion);
    }

    // ---- the other components keep working --------------------------------

    [Fact]
    public void AddingTheBundleLeavesTheDirectorPlannedExactlyAsBefore()
    {
        // The negative control for putting a fourth entry in Apps: the apps must still plan normally.
        var manifest = Manifest(("cc-director-mac-arm64.zip", "2.28.0"));
        var installed = new Dictionary<string, InstalledComponent>(StringComparer.OrdinalIgnoreCase);

        var plan = UpdatePlanner.Plan(
            [ComponentRegistry.Director], installed, manifest, null, OSPlatform.OSX);

        Assert.Equal(PlanItemKind.Install, Assert.Single(plan.Items).Kind);
        Assert.Single(plan.Actionable);
    }
}
