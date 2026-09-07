using System.Runtime.InteropServices;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Which release asset each component is installed from, one platform at a time.
///
/// Why these exist: <c>Component.AssetFor</c> took a <c>bool macOs</c>, and a bool cannot carry
/// three platforms. On Linux it was false, so the planner, the setup command line and the Avalonia
/// wizard were all told to install <c>cc-director-win-x64.exe</c> - and the wizard is the thing
/// <c>scripts/install-linux.sh</c> hands over to. The failure shape is the dangerous one: not a
/// refusal, but a confident wrong answer that finds a real asset in the release, downloads it,
/// places it and reports success.
///
/// The asset names here are the ones the Linux jobs in <c>.github/workflows/release.yml</c> publish.
/// If one changes there and not here, the installer asks for a file no release contains.
/// </summary>
public sealed class ComponentAssetSelectionTests
{
    [Fact]
    public void Director_OnLinux_IsTheLinuxExecutable()
        => Assert.Equal("cc-director-linux-x64", ComponentRegistry.Director.AssetFor(OSPlatform.Linux));

    [Fact]
    public void Launcher_OnLinux_IsTheLinuxExecutable()
        => Assert.Equal("cc-launcher-linux-x64", ComponentRegistry.Launcher.AssetFor(OSPlatform.Linux));

    [Fact]
    public void Director_OnWindows_IsUnchanged()
        => Assert.Equal("cc-director-win-x64.exe", ComponentRegistry.Director.AssetFor(OSPlatform.Windows));

    [Fact]
    public void Director_OnMacOs_IsStillTheApplicationBundleZip()
        => Assert.Equal(MacAppPlacer.DirectorAsset, ComponentRegistry.Director.AssetFor(OSPlatform.OSX));

    [Fact]
    public void Launcher_OnWindows_IsUnchanged()
        => Assert.Equal("cc-launcher-win-x64.exe", ComponentRegistry.Launcher.AssetFor(OSPlatform.Windows));

    [Fact]
    public void Launcher_OnMacOs_IsUnchanged()
        => Assert.Equal("cc-launcher-mac-arm64", ComponentRegistry.Launcher.AssetFor(OSPlatform.OSX));

    /// <summary>
    /// The Gateway has no macOS and no Linux build, and null says exactly that. Null is a different
    /// answer from the throw an unsupported PLATFORM gets: one means "we support this platform and
    /// this component has no build on it", the other means "we do not ship for this platform at all".
    /// </summary>
    [Fact]
    public void Gateway_HasNoMacOrLinuxBuild_AndSaysSoWithNull()
    {
        Assert.Null(ComponentRegistry.Gateway.AssetFor(OSPlatform.Linux));
        Assert.Null(ComponentRegistry.Gateway.AssetFor(OSPlatform.OSX));
        Assert.Equal("devthrottle-gateway-win-x64.exe", ComponentRegistry.Gateway.AssetFor(OSPlatform.Windows));
    }

    /// <summary>
    /// Tools ship per-executable on Windows only; on macOS and Linux they arrive inside the shared
    /// Python tools bundle. Null here is what makes the command line print "ships in the Python
    /// tools bundle" rather than reporting a missing download.
    /// </summary>
    [Fact]
    public void ToolComponent_ShipsPerExecutableOnWindowsOnly()
    {
        var pdf = ComponentRegistry.ToolComponent("cc-pdf");
        Assert.Equal("cc-pdf-win-x64.exe", pdf.AssetFor(OSPlatform.Windows));
        Assert.Null(pdf.AssetFor(OSPlatform.OSX));
        Assert.Null(pdf.AssetFor(OSPlatform.Linux));
    }

    /// <summary>
    /// THE property the two-way branch violated: no platform is ever handed another platform's
    /// asset. Each component's non-null answers must be pairwise distinct. An equality test on a
    /// single platform cannot catch a shared name; this can.
    /// </summary>
    [Theory]
    [InlineData("director")]
    [InlineData("gateway")]
    [InlineData("cc-launcher")]
    public void NoComponentHandsTwoPlatformsTheSameAsset(string componentId)
    {
        var component = ComponentRegistry.Apps.Single(c => c.Id == componentId);

        var answers = new[] { OSPlatform.Windows, OSPlatform.OSX, OSPlatform.Linux }
            .Select(component.AssetFor)
            .Where(a => a is not null)
            .ToList();

        Assert.Equal(answers.Count, answers.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Every app that ships on Linux names an asset the Linux release jobs actually publish. This is
    /// the pair to the test above: distinctness alone would be satisfied by three made-up names.
    /// </summary>
    [Fact]
    public void EveryLinuxAssetIsOneTheReleaseWorkflowPublishes()
    {
        // The six assets the create-release job copies for Linux. The two Python bundle tarballs are
        // asserted in PythonToolsBundleAssetTests; the setup wizard and the setup command line are
        // not Components (the installer never installs itself).
        var published = new HashSet<string>(StringComparer.Ordinal)
        {
            "cc-director-linux-x64",
            "cc-launcher-linux-x64",
        };

        foreach (var component in ComponentRegistry.Apps)
        {
            var asset = component.AssetFor(OSPlatform.Linux);
            if (asset is null) continue;   // no Linux build for this component; nothing to publish
            Assert.Contains(asset, published);
        }
    }

    [Fact]
    public void AssetFor_UnsupportedPlatform_ThrowsRatherThanFallingThroughToWindows()
        => Assert.Throws<PlatformNotSupportedException>(
            () => ComponentRegistry.Director.AssetFor(OSPlatform.FreeBSD));

    [Fact]
    public void HostPlatform_Current_MatchesThisTestRunsOperatingSystem()
    {
        var expected =
            OperatingSystem.IsWindows() ? OSPlatform.Windows
            : OperatingSystem.IsMacOS() ? OSPlatform.OSX
            : OperatingSystem.IsLinux() ? OSPlatform.Linux
            : throw new PlatformNotSupportedException("This test run is on an unsupported operating system.");

        Assert.Equal(expected, HostPlatform.Current);
        Assert.True(HostPlatform.IsSupported(HostPlatform.Current));
        Assert.False(HostPlatform.IsSupported(OSPlatform.FreeBSD));
    }

    /// <summary>
    /// The planner is where the asset choice turns into a download, so assert it there too rather
    /// than only on the record. A plan built for Linux must name the Linux Director and must not
    /// name the Windows one.
    /// </summary>
    [Fact]
    public void Plan_ForLinux_SelectsTheLinuxAssets()
    {
        var manifest = ManifestWith(
            ("cc-director-win-x64.exe", "2.0.0"),
            ("cc-director-mac-arm64.zip", "2.0.0"),
            ("cc-director-linux-x64", "2.0.0"),
            ("cc-launcher-linux-x64", "2.0.0"));

        var plan = UpdatePlanner.Plan(
            [ComponentRegistry.Director, ComponentRegistry.Launcher],
            new Dictionary<string, InstalledComponent>(),
            manifest,
            platform: OSPlatform.Linux);

        var assets = plan.Items.Select(i => i.AssetName).ToList();
        Assert.Contains("cc-director-linux-x64", assets);
        Assert.Contains("cc-launcher-linux-x64", assets);
        Assert.DoesNotContain("cc-director-win-x64.exe", assets);
        Assert.DoesNotContain("cc-director-mac-arm64.zip", assets);
    }

    /// <summary>
    /// The same plan built for Windows and for Linux must not agree about the Director. This is the
    /// planner-level statement of the same property: two platforms, two answers.
    /// </summary>
    [Fact]
    public void Plan_ForLinuxAndForWindows_DisagreeAboutTheDirector()
    {
        var manifest = ManifestWith(
            ("cc-director-win-x64.exe", "2.0.0"),
            ("cc-director-linux-x64", "2.0.0"));

        string DirectorAssetIn(OSPlatform platform) => UpdatePlanner.Plan(
                [ComponentRegistry.Director],
                new Dictionary<string, InstalledComponent>(),
                manifest,
                platform: platform)
            .Items.Single(i => i.ComponentId == ComponentRegistry.Director.Id).AssetName;

        Assert.NotEqual(DirectorAssetIn(OSPlatform.Windows), DirectorAssetIn(OSPlatform.Linux));
    }

    /// <summary>
    /// Where a Linux install PUTS the Director. The asset choice and the install location are the
    /// same defect in two costumes: this method also read "not Windows, therefore macOS", so even
    /// with the right download the file would have landed at ~/Applications/Director.app - a macOS
    /// application-bundle path holding a bare Linux executable.
    /// </summary>
    [Fact]
    public void Director_OnLinux_IsPlacedAsASingleExecutable_NotAsAMacApplicationBundle()
    {
        var layout = new InstallLayout(Path.Combine(Path.GetTempPath(), "cc-layout-test"));

        var linux = layout.PathFor(ComponentRegistry.Director, OSPlatform.Linux);

        Assert.Equal(Path.Combine(layout.AppDir, "cc-director"), linux);
        Assert.DoesNotContain("Director.app", linux, StringComparison.Ordinal);
        Assert.DoesNotContain("Applications", linux, StringComparison.Ordinal);
        Assert.NotEqual(layout.PathFor(ComponentRegistry.Director, OSPlatform.OSX), linux);
        Assert.NotEqual(layout.PathFor(ComponentRegistry.Director, OSPlatform.Windows), linux);
    }

    /// <summary>Linux binaries carry no .exe suffix; Windows ones do. Every kind, not just the Director.</summary>
    [Fact]
    public void LinuxPaths_CarryNoExecutableSuffix()
    {
        var layout = new InstallLayout(Path.Combine(Path.GetTempPath(), "cc-layout-test"));

        foreach (var component in ComponentRegistry.Apps.Append(ComponentRegistry.ToolComponent("cc-pdf")))
        {
            var path = layout.PathFor(component, OSPlatform.Linux);
            Assert.False(path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                $"{component.Id} is placed at {path} on Linux");
        }
    }

    [Fact]
    public void PathFor_UnsupportedPlatform_ThrowsRatherThanFallingThroughToWindowsPaths()
    {
        var layout = new InstallLayout(Path.Combine(Path.GetTempPath(), "cc-layout-test"));

        Assert.Throws<PlatformNotSupportedException>(
            () => layout.PathFor(ComponentRegistry.Director, OSPlatform.FreeBSD));
    }

    private static ReleaseManifest ManifestWith(params (string Name, string Version)[] assets)
    {
        var json = string.Join(",", assets.Select(a =>
            $"\"{a.Name}\": {{\"version\": \"{a.Version}\", \"size\": 1, \"sha256\": \"AB\", \"platform\": \"any\"}}"));
        return ReleaseManifest.Parse($"{{\"version\": \"2.0.0\", \"assets\": {{{json}}}}}");
    }
}
