using CcDirector.Core.Configuration;
using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// Issue #3503: the restart half of the fix works only if the flag the autostart entry carries is
/// PARSED and reaches the opening decision. These tests go from the command line the Run key holds to
/// the decision, so deleting the parser branch for --at-login turns them red.
/// </summary>
[Collection(LauncherOptionsCollection.Name)]
public sealed class AtLoginReachesTheOpenerTests : IDisposable
{
    public void Dispose() => LauncherAppOptions.Parse([]);

    [Fact]
    public void Parse_TheWindowsAutostartArguments_OpensANeverSignedInDirector()
    {
        var args = LauncherAppOptions.AutostartArgumentsFor(managed: true, windows: true)!.Split(' ');

        LauncherAppOptions.Parse(args);

        Assert.True(LauncherAppOptions.Managed);
        Assert.True(LauncherAppOptions.AtLogin);
        Assert.True(NeverSignedInOpener.ShouldOpenForThisLaunch(new GatewayConfig()));
    }

    // The installer and a self-update start the launcher with --managed alone: no opening.
    [Fact]
    public void Parse_ManagedWithoutAtLogin_DoesNotOpen()
    {
        LauncherAppOptions.Parse(["--managed"]);

        Assert.False(LauncherAppOptions.AtLogin);
        Assert.False(NeverSignedInOpener.ShouldOpenForThisLaunch(new GatewayConfig()));
    }

    [Fact]
    public void Parse_AtLoginButSignedIn_DoesNotOpen()
    {
        LauncherAppOptions.Parse(["--managed", "--at-login"]);

        Assert.False(NeverSignedInOpener.ShouldOpenForThisLaunch(
            new GatewayConfig { Url = "https://gateway.example", Token = "device-key" }));
    }

    // A parse describes its own arguments: an earlier --at-login does not survive into the next parse.
    [Fact]
    public void Parse_StartsFromDefaults()
    {
        LauncherAppOptions.Parse(["--managed", "--at-login", "--no-autostart"]);
        LauncherAppOptions.Parse([]);

        Assert.False(LauncherAppOptions.AtLogin);
        Assert.False(LauncherAppOptions.Managed);
        Assert.True(LauncherAppOptions.RegisterAutostart);
    }
}

/// <summary>
/// Serializes the test classes that change <see cref="LauncherAppOptions"/>, which is process-wide
/// static state that xUnit would otherwise let two classes change at the same time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LauncherOptionsCollection
{
    public const string Name = "launcher-options";
}
