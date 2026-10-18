using System.Runtime.InteropServices;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Where an install's cc-* tool links go (mission #4070).
///
/// A Director started on a non-default root - CC_DIRECTOR_ROOT pointing at a scratch folder, as every
/// slot-5 test Director is - runs the start-up tools self-heal against that root. It used to write the
/// links into the user's real ~/.local/bin, re-pointing every cc-* command at the scratch root's virtual
/// environment, so deleting the scratch root broke cc-devthrottle for every session on the machine.
///
/// Every test here fixes the machine's default root and the home folder itself, inside a temporary
/// folder: no test reads or writes the real home folder.
/// </summary>
public sealed class InstallLayoutToolShimDirTests : IDisposable
{
    private readonly string _dir;
    private readonly string _home;
    private readonly string _defaultRoot;
    private readonly string _scratchRoot;

    public InstallLayoutToolShimDirTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-shimdir-" + Guid.NewGuid().ToString("N"));
        _home = Path.Combine(_dir, "home");
        _defaultRoot = Path.Combine(_home, ".local", "share", "cc-director");
        _scratchRoot = Path.Combine(_dir, "scratch-root");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string GlobalBin => Path.Combine(_home, ".local", "bin");

    private InstallLayout ScratchLayout() => new(_scratchRoot, _defaultRoot, _home);

    private InstallLayout DefaultLayout() => new(_defaultRoot, _defaultRoot, _home);

    public static TheoryData<string> UnixPlatforms => new() { "linux", "osx" };

    private static OSPlatform Platform(string name) => name == "linux" ? OSPlatform.Linux : OSPlatform.OSX;

    // --- Where: every platform, asserted from any host --------------------------------------------

    [Theory]
    [MemberData(nameof(UnixPlatforms))]
    public void ToolShimDirFor_NonDefaultRootOnUnix_IsTheRootsOwnBin(string platform)
    {
        var layout = ScratchLayout();

        Assert.Equal(layout.BinDir, layout.ToolShimDirFor(Platform(platform)));
    }

    [Theory]
    [MemberData(nameof(UnixPlatforms))]
    public void ToolShimDirFor_DefaultRootOnUnix_IsTheUsersLocalBin(string platform)
    {
        Assert.Equal(GlobalBin, DefaultLayout().ToolShimDirFor(Platform(platform)));
    }

    [Fact]
    public void ToolShimDirFor_Windows_IsTheRootsOwnBin_ForTheDefaultAndAnyOtherRoot()
    {
        Assert.Equal(DefaultLayout().BinDir, DefaultLayout().ToolShimDirFor(OSPlatform.Windows));
        Assert.Equal(ScratchLayout().BinDir, ScratchLayout().ToolShimDirFor(OSPlatform.Windows));
    }

    [Fact]
    public void ToolShimDirFor_UnknownPlatform_Throws()
    {
        Assert.Throws<PlatformNotSupportedException>(
            () => ScratchLayout().ToolShimDirFor(OSPlatform.Create("PLAN9")));
    }

    // --- What is written: the real shim writer on a macOS or Linux host ---------------------------

    [Fact]
    public void WriteShims_NonDefaultRootOnUnix_LinksInsideTheRoot_AndLeavesTheGlobalBinUntouched()
    {
        if (OperatingSystem.IsWindows()) return;

        // The machine's real install already has its link in the global bin.
        var machineScript = Path.Combine(_defaultRoot, "pyenv", "bin", "cc-pdf");
        Directory.CreateDirectory(GlobalBin);
        var globalLink = Path.Combine(GlobalBin, "cc-pdf");
        File.CreateSymbolicLink(globalLink, machineScript);

        var layout = ScratchLayout();
        Directory.CreateDirectory(layout.PyenvBinDir);
        File.WriteAllText(PythonToolsInstaller.ConsoleScriptPath(layout, "cc-pdf"), "scratch console script");

        new PythonToolsInstaller(layout).WriteShims(new[] { "cc-pdf" });

        var ownLink = Path.Combine(layout.BinDir, "cc-pdf");
        Assert.Equal(PythonToolsInstaller.ConsoleScriptPath(layout, "cc-pdf"), new FileInfo(ownLink).LinkTarget);
        Assert.Equal(machineScript, new FileInfo(globalLink).LinkTarget);
        Assert.Equal(new[] { globalLink }, Directory.GetFileSystemEntries(GlobalBin));
    }

    [Fact]
    public void WriteShims_DefaultRootOnUnix_RelinksTheGlobalBin_AsBefore()
    {
        if (OperatingSystem.IsWindows()) return;

        // An older link in the global bin is replaced by one pointing at this install's environment.
        Directory.CreateDirectory(GlobalBin);
        var globalLink = Path.Combine(GlobalBin, "cc-pdf");
        File.CreateSymbolicLink(globalLink, Path.Combine(_dir, "old", "cc-pdf"));

        var layout = DefaultLayout();
        Directory.CreateDirectory(layout.PyenvBinDir);
        File.WriteAllText(PythonToolsInstaller.ConsoleScriptPath(layout, "cc-pdf"), "installed console script");

        new PythonToolsInstaller(layout).WriteShims(new[] { "cc-pdf" });

        Assert.Equal(PythonToolsInstaller.ConsoleScriptPath(layout, "cc-pdf"), new FileInfo(globalLink).LinkTarget);
        Assert.False(File.Exists(Path.Combine(layout.BinDir, "cc-pdf")), "the default root wrote a link into its own bin as well");
    }
}
