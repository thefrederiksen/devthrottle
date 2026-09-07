using System.Runtime.InteropServices;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The Python tools bundle asset names, one platform at a time.
///
/// Why these exist: the resolver used to be a two-way branch - Windows, or else macOS - so a Linux
/// Director asked the release for cc-python-macos-arm64.tar.gz. That tarball does contain
/// bin/python3, so the installer's "is the interpreter there" check passed; the next check, can the
/// staged interpreter import its own standard library, failed because it is an arm64 Mach-O binary
/// on a 64-bit Intel Linux machine. The Director then reported a corrupt-download reason for a
/// wrong-platform cause.
///
/// Nothing went red when that was true, because the only thing under test was a property that reads
/// the environment - and the test run was always on Windows. So the platform is a parameter here.
///
/// The literal names are shared with two other places and must not drift from either:
/// scripts/build-python-bundle.sh (and .ps1), which produces them, and the create-release job in
/// .github/workflows/release.yml, which publishes them under exactly these names.
/// </summary>
public sealed class PythonToolsBundleAssetTests
{
    [Fact]
    public void PythonAssetFor_Windows_IsTheWindowsZip()
        => Assert.Equal("cc-python-win-x64.zip", PythonToolsInstaller.PythonAssetFor(OSPlatform.Windows));

    [Fact]
    public void ToolsAssetFor_Windows_IsTheWindowsZip()
        => Assert.Equal("cc-tools-pyenv-win-x64.zip", PythonToolsInstaller.ToolsAssetFor(OSPlatform.Windows));

    [Fact]
    public void PythonAssetFor_MacOs_IsTheAppleSiliconTarball()
        => Assert.Equal("cc-python-macos-arm64.tar.gz", PythonToolsInstaller.PythonAssetFor(OSPlatform.OSX));

    [Fact]
    public void ToolsAssetFor_MacOs_IsTheAppleSiliconTarball()
        => Assert.Equal("cc-tools-pyenv-macos-arm64.tar.gz", PythonToolsInstaller.ToolsAssetFor(OSPlatform.OSX));

    [Fact]
    public void PythonAssetFor_Linux_IsTheLinuxTarball()
        => Assert.Equal("cc-python-linux-x64.tar.gz", PythonToolsInstaller.PythonAssetFor(OSPlatform.Linux));

    [Fact]
    public void ToolsAssetFor_Linux_IsTheLinuxTarball()
        => Assert.Equal("cc-tools-pyenv-linux-x64.tar.gz", PythonToolsInstaller.ToolsAssetFor(OSPlatform.Linux));

    /// <summary>
    /// Every supported platform gets its OWN pair. This is the assertion the old two-way branch
    /// failed: it handed Linux the macOS names, and no equality test on a single platform could see
    /// it. Six distinct names across three platforms is the property that was actually broken.
    /// </summary>
    [Fact]
    public void EverySupportedPlatform_GetsItsOwnPairOfAssetNames()
    {
        var platforms = new[] { OSPlatform.Windows, OSPlatform.OSX, OSPlatform.Linux };
        var names = platforms
            .SelectMany(p => new[] { PythonToolsInstaller.PythonAssetFor(p), PythonToolsInstaller.ToolsAssetFor(p) })
            .ToList();

        Assert.Equal(6, names.Count);
        Assert.Equal(6, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// An unsupported platform throws rather than quietly returning another platform's bundle.
    /// Returning somebody else's asset is precisely the defect these tests exist for, and a default
    /// branch would reintroduce it the next time a platform is added.
    /// </summary>
    [Fact]
    public void PythonAssetFor_UnsupportedPlatform_Throws()
        => Assert.Throws<PlatformNotSupportedException>(() => PythonToolsInstaller.PythonAssetFor(OSPlatform.FreeBSD));

    [Fact]
    public void ToolsAssetFor_UnsupportedPlatform_Throws()
        => Assert.Throws<PlatformNotSupportedException>(() => PythonToolsInstaller.ToolsAssetFor(OSPlatform.FreeBSD));

    /// <summary>
    /// The environment-reading properties agree with the parameterised resolver for whatever this
    /// test run is actually on. That keeps the convenience properties honest without pretending to
    /// cover the platforms this run is not.
    /// </summary>
    [Fact]
    public void CurrentOsProperties_AgreeWithTheResolverForThisRunsPlatform()
    {
        var here =
            OperatingSystem.IsWindows() ? OSPlatform.Windows
            : OperatingSystem.IsMacOS() ? OSPlatform.OSX
            : OperatingSystem.IsLinux() ? OSPlatform.Linux
            : throw new PlatformNotSupportedException("This test run is on an operating system the Director does not support.");

        Assert.Equal(PythonToolsInstaller.PythonAssetFor(here), PythonToolsInstaller.PythonAsset);
        Assert.Equal(PythonToolsInstaller.ToolsAssetFor(here), PythonToolsInstaller.ToolsAsset);
    }
}
