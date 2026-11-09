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
public sealed class LauncherTrayInstallerTests
{
    [Fact]
    public void TheRegistrationItWaitsFor_BelongsToTheInstallItIsPerforming_NotToTheAmbientRoot()
    {
        // NO ENVIRONMENT VARIABLE IS SET HERE, and the first version of this test set one (the review of
        // pull request 4808). LauncherDiscovery.DefaultPath is a static resolved ONCE per process, so
        // setting the variable now cannot move it - the only thing it could affect is other code in this
        // process that reads the variable live, which is why it had to join the collection that owns the
        // machine root. What this asserts holds in every process: the path watched is the layout's own.
        var rig = Path.Combine(Path.GetTempPath(), "cc-rig-" + Guid.NewGuid().ToString("N"));
        var layout = new InstallLayout(Path.Combine(rig, "local"));

        var installer = new LauncherTrayInstaller(layout);

        Assert.Equal(layout.LauncherRegistrationPath, installer.RegistrationPath);
        Assert.NotEqual(LauncherDiscovery.DefaultPath, installer.RegistrationPath);
    }
}
