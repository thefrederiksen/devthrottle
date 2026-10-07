using CcDirector.Core.Configuration;
using CcDirector.Launcher;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// Issue #3503: a machine that installed but never signed in came back after a restart to nothing on
/// screen - the launcher autostarted into the tray overflow and never opened the Director. The
/// launcher now opens it at sign-in to Windows until the machine signs in, and only then.
/// </summary>
[Collection(LauncherOptionsCollection.Name)]
public sealed class NeverSignedInOpenerTests : IDisposable
{
    public void Dispose() => LauncherAppOptions.Parse([]);

    [Fact]
    public void ShouldOpen_AtLoginAndNeverSignedIn_IsTrue()
        => Assert.True(NeverSignedInOpener.ShouldOpen(startedAtLogin: true, signedIn: false));

    // Once signed in, what opens at start-up is the person's choice, not ours.
    [Fact]
    public void ShouldOpen_SignedIn_IsFalse()
        => Assert.False(NeverSignedInOpener.ShouldOpen(startedAtLogin: true, signedIn: true));

    // Started by the installer (which opens the Director itself, with a fresh PATH), a self-update
    // relaunch, or a person: never.
    [Fact]
    public void ShouldOpen_NotStartedAtLogin_IsFalse()
        => Assert.False(NeverSignedInOpener.ShouldOpen(startedAtLogin: false, signedIn: false));

    [Fact]
    public void OpenIfNeverSignedIn_SignedIn_StartsNothing()
    {
        var supervisor = new DirectorSupervisor(TempLayout());
        var config = new GatewayConfig { Url = "https://gateway.example", Token = "device-key" };
        LauncherAppOptions.Parse(["--managed", "--at-login"]);

        Assert.False(NeverSignedInOpener.OpenIfNeverSignedIn(config, supervisor));
    }

    // Never signed in but the Director is not on disk: there is nothing to open, and it must not throw
    // (the supervisor's Start would, on a missing executable).
    [Fact]
    public void OpenIfNeverSignedIn_NoDirectorInstalled_StartsNothing()
    {
        var supervisor = new DirectorSupervisor(TempLayout());
        LauncherAppOptions.Parse(["--managed", "--at-login"]);

        Assert.False(NeverSignedInOpener.OpenIfNeverSignedIn(new GatewayConfig(), supervisor));
    }

    // The Windows autostart entry is what tells the launcher it was started at sign-in. Without the
    // flag in the Run key, the opener can never fire after a restart.
    [Fact]
    public void AutostartArgumentsFor_Windows_CarriesTheAtLoginFlag()
    {
        Assert.Equal("--managed --at-login", LauncherAppOptions.AutostartArgumentsFor(managed: true, windows: true));
        Assert.Equal("--at-login", LauncherAppOptions.AutostartArgumentsFor(managed: false, windows: true));
    }

    // The macOS launch agent is also written by the installer with fixed arguments; a launcher that
    // rewrote it would reload the agent under itself. It keeps exactly what it had.
    [Fact]
    public void AutostartArgumentsFor_NotWindows_IsUnchanged()
    {
        Assert.Equal("--managed", LauncherAppOptions.AutostartArgumentsFor(managed: true, windows: false));
        Assert.Null(LauncherAppOptions.AutostartArgumentsFor(managed: false, windows: false));
    }

    private static InstallLayout TempLayout()
        => new(Path.Combine(Path.GetTempPath(), "cc-3503-" + Guid.NewGuid().ToString("N")));
}
