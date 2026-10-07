using System.Runtime.InteropServices;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// A launcher that could not register itself to start at login must not look identical to one that
/// is properly managed.
///
/// This is the original sin behind the Mac that could not install anything. The registration failed,
/// the failure was caught, one line went to a log, and the launcher carried on reporting perfect
/// health - so the installer certified it, the uninstaller could not stop it (it only asked launchd,
/// which had never heard of that process), and every later install collided with it.
/// The state was invisible, which is exactly what let it survive for hours.
///
/// The registration file the running launcher writes now carries the state, so the installer, the
/// fleet and the tray can all tell a managed launcher from an unmanaged one. These tests pin the SHAPE of that reporting; whether the
/// operating system's registration itself succeeds is not something a unit test can decide.
/// </summary>
[Collection(LauncherOptionsCollection.Name)]
public sealed class LauncherAutostartVisibilityTests
{
    // Skipping registration deliberately (--no-autostart) is not a failure and must not be reported
    // as one - a developer running the launcher by hand is not a broken machine.
    [Fact]
    public void RegisterAutostartSafe_WhenAutostartIsNotRequested_ReportsNoFailure()
    {
        LauncherAppOptions.Parse(["--no-autostart"]);
        LauncherCore.RegisterAutostartSafe();

        Assert.Null(LauncherCore.AutostartFailure);
    }

    // On a platform with no autostart mechanism there is nothing to fail, so the state stays clean.
    [Fact]
    public void AutostartFailure_IsNullUntilARegistrationIsActuallyAttemptedAndFails()
    {
        LauncherAppOptions.Parse(["--no-autostart"]);
        LauncherCore.RegisterAutostartSafe();
        Assert.Null(LauncherCore.AutostartFailure);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            LauncherAppOptions.Parse([]);
            LauncherCore.RegisterAutostartSafe();
            Assert.Null(LauncherCore.AutostartFailure);
        }
    }

    // The property exists and is readable from outside the launcher - which is what lets the
    // registration file report it. Before this, the failure lived only in a log line and nothing could read it.
    // Revert-proof: delete AutostartFailure and this file does not compile.
    [Fact]
    public void AutostartFailure_IsReadableFromOutsideTheLauncher()
    {
        var property = typeof(LauncherCore).GetProperty(nameof(LauncherCore.AutostartFailure));

        Assert.NotNull(property);
        Assert.Equal(typeof(string), property!.PropertyType);
        Assert.True(property.CanRead);
    }
}
