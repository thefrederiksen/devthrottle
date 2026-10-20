using System.Runtime.InteropServices;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class InstallLayoutTests
{
    /// <summary>
    /// An install root that is ABSOLUTE on whatever platform the test runs on.
    ///
    /// It used to be the literal "C:\root" everywhere. That string is absolute only on Windows: on
    /// macOS and Linux it is an ordinary relative file name, and InstallLayout refuses a relative root
    /// on purpose, because a relative one resolves against the working directory and the install would
    /// land beside the setup executable. So the refusal fired in this class's static initialiser and
    /// every test in the file failed with a TypeInitializationException - four permanently red tests on
    /// every Mac and Linux run, none of them about anything that was broken.
    /// </summary>
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\root" : "/root";

    private static readonly InstallLayout Layout = new(Root);

    // The Windows layout is asserted from ANY machine by naming the platform, rather than by letting
    // PathFor read the operating system this test happens to be running on. The executable suffix and
    // the per-component folders are the Windows answer; MacInstallLocationTests holds the macOS one.
    [Fact]
    public void PathFor_Director_IsInAppDir()
    {
        var path = Layout.PathFor(ComponentRegistry.Director, OSPlatform.Windows);
        Assert.Equal(Path.Combine(Root, "app", "cc-director.exe"), path);
    }

    [Fact]
    public void PathFor_Tool_IsInBinDir()
    {
        var path = Layout.PathFor(ComponentRegistry.ToolComponent("cc-pdf"), OSPlatform.Windows);
        Assert.Equal(Path.Combine(Root, "bin", "cc-pdf.exe"), path);
    }

    [Fact]
    public void PathFor_GatewayAndLauncher_AreUnderTheUserRoot()
    {
        // The Gateway is a per-user tray app: everything lives under the one user root
        // (docs/plans/gateway-tray-app.md) so install/update/uninstall never elevate.
        Assert.Equal(
            Path.Combine(Root, "gateway", "devthrottle-gateway.exe"),
            Layout.PathFor(ComponentRegistry.Gateway, OSPlatform.Windows));
        Assert.Equal(
            Path.Combine(Root, "launcher", "cc-launcher.exe"),
            Layout.PathFor(ComponentRegistry.Launcher, OSPlatform.Windows));
    }

    [Fact]
    public void StateAndLogsDirs_AreUnderTheUserRoot()
    {
        Assert.Equal(Path.Combine(Root, "state"), Layout.StateDir);
        Assert.Equal(Path.Combine(Root, "logs"), Layout.LogsDir);
    }

    [Fact]
    public void Constructor_RejectsEmptyRoot()
    {
        Assert.Throws<ArgumentException>(() => new InstallLayout(""));
        Assert.Throws<ArgumentException>(() => new InstallLayout("  "));
    }

    [Fact]
    public void Constructor_RejectsARelativeRoot()
    {
        // The refusal this file's own fixture used to trip over. It is the behaviour that keeps an
        // install from resolving against the working directory, so it is worth a test of its own rather
        // than being remembered only as the reason a root is spelled the way it is above.
        Assert.Throws<ArgumentException>(() => new InstallLayout(Path.Combine("relative", "root")));
    }
}
