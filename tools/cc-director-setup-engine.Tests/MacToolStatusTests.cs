using System.Runtime.InteropServices;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// What the setup engine says about a cc-* tool on a platform where tools are not standalone downloads.
///
/// Two defects, one surface. #4290: the engine looked for a tool at &lt;root&gt;/bin/&lt;id&gt;, which on
/// macOS and Linux is not where it lands, so a healthy install reported every tool as not installed and
/// exited 0 while saying it. #4291: a tool with no asset for the platform was reported as a MissingAsset
/// - a hole in the release that somebody should fix - when in truth the release is exactly as intended
/// and the tool arrives inside the Python tools bundle.
///
/// Every test states the platform rather than reading the one it runs on, which is the discipline
/// PathFor and AssetFor are written for.
/// </summary>
public class MacToolStatusTests
{
    private const string Root = "/Users/someone/Library/Application Support/cc-director";
    private static InstallLayout Layout() => new(Root, defaultRoot: null, homeDir: "/Users/someone");

    private static readonly Component Pdf = ComponentRegistry.ToolComponent("cc-pdf");

    // ---- #4290: where a tool actually is ----------------------------------

    [Fact]
    public void OnMac_AToolIsTheConsoleScriptInTheSharedVenv_NotAFileInRootBin()
    {
        var layout = Layout();

        var path = layout.PathFor(Pdf, OSPlatform.OSX);

        Assert.Equal(Path.Combine(Root, "pyenv", "bin", "cc-pdf"), path);
    }

    [Fact]
    public void OnLinux_AToolIsAlsoTheConsoleScriptInTheSharedVenv()
    {
        // Linux has the identical shape - the tools arrive in the same bundle - and had the identical
        // defect, so fixing only the platform the issue was filed from would have left it half done.
        var layout = Layout();

        var path = layout.PathFor(Pdf, OSPlatform.Linux);

        Assert.Equal(Path.Combine(Root, "pyenv", "bin", "cc-pdf"), path);
    }

    [Fact]
    public void OnWindows_AToolIsStillTheExeInRootBin_WhichIsWhereItsShimGoes()
    {
        // Unchanged, and deliberately so: on Windows the tool shims DO live in <root>\bin.
        var layout = Layout();

        var path = layout.PathFor(Pdf, OSPlatform.Windows);

        Assert.Equal(Path.Combine(Root, "bin", "cc-pdf.exe"), path);
    }

    [Fact]
    public void TheToolPathIsNeverRootBin_OnAnyPlatformWhereToolsShipInTheBundle()
    {
        var layout = Layout();

        foreach (var platform in new[] { OSPlatform.OSX, OSPlatform.Linux })
        {
            var path = layout.PathFor(Pdf, platform);
            Assert.DoesNotContain(Path.Combine(Root, "bin"), path);
            Assert.Contains("pyenv", path);
        }
    }

    [Fact]
    public void TheVenvExecutablesFolderIsScriptsOnWindowsAndBinElsewhere()
    {
        var layout = Layout();

        Assert.Equal(Path.Combine(Root, "pyenv", "Scripts"), layout.PyenvBinDirFor(OSPlatform.Windows));
        Assert.Equal(Path.Combine(Root, "pyenv", "bin"), layout.PyenvBinDirFor(OSPlatform.OSX));
        Assert.Equal(Path.Combine(Root, "pyenv", "bin"), layout.PyenvBinDirFor(OSPlatform.Linux));
    }

    // ---- #4291: what the plan SAYS about a tool it cannot download --------

    [Fact]
    public void ATooWithNoAssetHere_IsReportedAsShippingInTheToolsBundle_NotAsAMissingAsset()
    {
        var manifest = ManifestWith(("cc-director-mac-arm64.zip", "2.26.0"));
        var installed = new Dictionary<string, InstalledComponent>(StringComparer.OrdinalIgnoreCase);

        var plan = UpdatePlanner.Plan([Pdf], installed, manifest, null, OSPlatform.OSX);

        var item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemKind.ProvidedByToolsBundle, item.Kind);
        Assert.NotEqual(PlanItemKind.MissingAsset, item.Kind);
    }

    [Fact]
    public void SuchAToolDoesNotCountAsAMissingAsset_SoNobodyIsSentToLookAtTheRelease()
    {
        var manifest = ManifestWith(("cc-director-mac-arm64.zip", "2.26.0"));
        var installed = new Dictionary<string, InstalledComponent>(StringComparer.OrdinalIgnoreCase);

        var plan = UpdatePlanner.Plan([Pdf], installed, manifest, null, OSPlatform.OSX);

        Assert.Empty(plan.MissingAssets);
    }

    [Fact]
    public void ANonToolWithNoAsset_IsStillAMissingAsset()
    {
        // The negative control for the change above: the MissingAsset meaning must survive for the case
        // it was built for - a component that genuinely should have had a download and does not. Without
        // this, "report nothing as missing" would pass the two tests above and break the release check.
        var manifest = ManifestWith(("something-else", "2.26.0"));
        var installed = new Dictionary<string, InstalledComponent>(StringComparer.OrdinalIgnoreCase);

        var plan = UpdatePlanner.Plan(
            [ComponentRegistry.Director], installed, manifest, null, OSPlatform.OSX);

        var item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemKind.MissingAsset, item.Kind);
    }

    [Fact]
    public void OnWindows_AToolWhoseExeIsInTheManifest_IsStillPlannedAsAnInstall()
    {
        // And the Windows path is untouched: where a per-tool executable IS published, it is planned.
        var manifest = ManifestWith(("cc-pdf-win-x64.exe", "2.26.0"));
        var installed = new Dictionary<string, InstalledComponent>(StringComparer.OrdinalIgnoreCase);

        var plan = UpdatePlanner.Plan([Pdf], installed, manifest, null, OSPlatform.Windows);

        var item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemKind.Install, item.Kind);
    }

    // ---- the finding behind the open decision on #4291 ---------------------

    [Fact]
    public void TheLiveReleaseShapePublishesNoPerToolExecutable_SoNoToolIdIsEverDiscovered()
    {
        // The real asset list of the published release, as of 21 Oct 2026. Every *-win-x64.exe in it is
        // on the exclusion list, so DiscoverToolIds answers EMPTY - on Windows too, not only on a Mac.
        // That is why 'plan' lists two components and no tool at all, and it is the part of #4291 that
        // is an open decision rather than a defect. This test pins the shape so the day the release
        // starts shipping per-tool executables again, somebody is told.
        var manifest = ManifestWith(
            ("cc-director-win-x64.exe", "2.28.0"),
            ("cc-launcher-win-x64.exe", "2.28.0"),
            ("devthrottle-gateway-win-x64.exe", "2.28.0"),
            ("devthrottle-setup-cli-win-x64.exe", "2.28.0"),
            ("devthrottle-setup-win-x64.exe", "2.28.0"),
            ("cc-director-mac-arm64.zip", "2.28.0"),
            ("cc-launcher-mac-arm64", "2.28.0"),
            ("cc-python-macos-arm64.tar.gz", "2.28.0"),
            ("cc-tools-pyenv-macos-arm64.tar.gz", "2.28.0"));

        Assert.Empty(ComponentRegistry.DiscoverToolIds(manifest));
    }

    /// <summary>Same shape as UpdatePlannerTests.Manifest - the manifest is built by parsing JSON.</summary>
    private static ReleaseManifest ManifestWith(params (string Name, string Version)[] assets)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{ \"version\": \"2.28.0\", \"assets\": {");
        for (var i = 0; i < assets.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"\"{assets[i].Name}\": {{ \"version\": \"{assets[i].Version}\", " +
                      "\"sha256\": \"AB\", \"platform\": \"any\" }");
        }
        sb.Append("} }");
        return ReleaseManifest.Parse(sb.ToString());
    }
}
