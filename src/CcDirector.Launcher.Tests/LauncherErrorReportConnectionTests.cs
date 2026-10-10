using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// Issue #3507: the launcher runs at the machine root, but a connected machine's connection lives in the default
/// Director's home (where the in-app wizard and <c>enroll</c> write it, #3506). Reading the machine root, every
/// connected machine's launcher looked signed out and sent its errors unowned through the before-sign-in route.
/// The launcher's error reports now go under the Director's connection.
/// </summary>
public sealed class LauncherErrorReportConnectionTests : IDisposable
{
    private readonly string _machineRoot = Path.Combine(Path.GetTempPath(), "launcher-conn-" + Guid.NewGuid().ToString("N"));

    public LauncherErrorReportConnectionTests() => Directory.CreateDirectory(_machineRoot);

    public void Dispose()
    {
        if (Directory.Exists(_machineRoot)) Directory.Delete(_machineRoot, recursive: true);
    }

    [Fact]
    public void ErrorReportConnection_AConnectedDirector_IsWhatTheLauncherReportsUnder()
    {
        var layout = new InstallLayout(_machineRoot);
        DefaultDirectorConnection.For(layout).SaveEnrolledKey("https://gateway.example.test", "per-device-key-0123456789");

        var connection = Program.ErrorReportConnection(layout);

        Assert.True(connection.HasCredential, "a connected Director's launcher reads as signed out (issue #3507)");
        Assert.Equal("https://gateway.example.test", connection.Url);
    }

    [Fact]
    public void ErrorReportConnection_AGatewayBlockOnlyInTheMachineRoot_IsNotAConnection()
    {
        // The machine root is not a Director's home; a block there (what an enroll before #3506 left) says
        // nothing about whether this machine's Director is connected.
        var config = Path.Combine(_machineRoot, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "config.json"), """{ "gateway": { "url": "https://stale.example.test", "token": "k" } }""");

        Assert.False(Program.ErrorReportConnection(new InstallLayout(_machineRoot)).HasCredential);
    }

    [Fact]
    public void LauncherProgram_StartsTheReporterOnTheDirectorsConnection()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "CcDirector.Launcher", "Program.cs"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var program = File.ReadAllText(Path.Combine(dir!.FullName, "src", "CcDirector.Launcher", "Program.cs"));

        Assert.Contains("ErrorReporter.Start(ErrorReportLimits.Launcher, () => ErrorReportConnection(InstallLayout.Default()));", program);
    }
}
