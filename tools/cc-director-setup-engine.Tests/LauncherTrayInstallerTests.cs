#pragma warning disable CA1416 // Only the constructor is exercised here; the install path is Windows-only in production.
using CcDirector.Core.Configuration;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// WHICH registration file the Windows launcher step waits for (issue #4519). The Windows installer
/// carried the same default as the macOS one, so it carried the same fault: an agent installing from
/// inside a Director session sets CC_DIRECTOR_ROOT, and the ambient path it resolves to is a file no
/// launcher has ever written.
/// </summary>
[Collection(MachineRootCollection.Name)]
public sealed class LauncherTrayInstallerTests : IDisposable
{
    private const string RootVariable = "CC_DIRECTOR_ROOT";
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(RootVariable);

    public void Dispose() => Environment.SetEnvironmentVariable(RootVariable, _previousRoot);

    [Fact]
    public void TheRegistrationItWaitsFor_BelongsToTheInstallItIsPerforming_NotToTheAmbientRoot()
    {
        var rig = Path.Combine(Path.GetTempPath(), "cc-rig-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(RootVariable, Path.Combine(rig, "instances", "default"));
        var layout = new InstallLayout(Path.Combine(rig, "local"));

        var installer = new LauncherTrayInstaller(layout);

        Assert.Equal(layout.LauncherRegistrationPath, installer.RegistrationPath);
        Assert.NotEqual(LauncherDiscovery.DefaultPath, installer.RegistrationPath);
    }
}
