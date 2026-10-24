using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// On a Mac the app is called DevThrottle and lives in /Applications (issue #4427). A trial user found only the installer
/// in their Applications folder: the app was named Director, and installs before v2.26.0 sat in ~/Applications, where the
/// auto-updater kept them. These cover the move out of ~/Applications, the Dock offer, the words on Setup's last screen,
/// and the packaging that gives the bundle its shown name.
/// </summary>
public sealed class MacAppNameAndPlaceTests
{
    private const string UserApps = "/Users/someone/Applications";
    private const string Installed = "/Applications/Director.app";
    private const string InUserApps = UserApps + "/Director.app";

    private static string? Ours(string _) => MacBundlePurger.ProductBundleIdentifier;

    private static MacRelocationDecision Decide(string running, string installed = Installed,
        bool installedExists = false, Func<string, string?>? readId = null) =>
        MacDirectorRelocation.Decide(running, installed, UserApps, p => installedExists && p == installed, readId ?? Ours);

    [Fact]
    public void Decide_OurBundleInUserApplications_AndSystemFolderEmpty_Moves()
    {
        Assert.Equal(MacRelocationDecision.Move, Decide(InUserApps));
    }

    [Fact]
    public void Decide_TrailingSlashOnTheRunningBundle_StillMoves()
    {
        Assert.Equal(MacRelocationDecision.Move, Decide(InUserApps + "/"));
    }

    [Fact]
    public void Decide_AlreadyInTheInstalledPlace_DoesNothing()
    {
        Assert.Equal(MacRelocationDecision.AlreadyInPlace, Decide(Installed));
    }

    [Fact]
    public void Decide_UserCannotWriteSystemFolder_InstallIsInUserApplications_DoesNothing()
    {
        // A standard user's install belongs in ~/Applications (MacAppsFolder), so it is already in place.
        Assert.Equal(MacRelocationDecision.AlreadyInPlace, Decide(InUserApps, installed: InUserApps));
    }

    [Fact]
    public void Decide_ACopyIsAlreadyInTheSystemFolder_SaysSoInsteadOfMoving()
    {
        Assert.Equal(MacRelocationDecision.DestinationOccupied, Decide(InUserApps, installedExists: true));
    }

    [Theory]
    [InlineData(UserApps + "/Director Dev.app")]       // a development slot
    [InlineData(UserApps + "/CC Director 2.app")]      // a slot wrapper
    [InlineData("/Users/someone/Downloads/Director.app")] // not an install
    [InlineData("/Volumes/DevThrottle 2.31.0/Director.app")] // run from the disk image
    [InlineData("/Users/someone/dev/publish/cc-director")]    // a bare binary, no bundle
    [InlineData("")]
    public void Decide_NotAnInstalledDirectorInUserApplications_IsLeftAlone(string running)
    {
        Assert.Equal(MacRelocationDecision.NotApplicable, Decide(running));
    }

    [Theory]
    [InlineData("com.devthrottle.ccdirector.slot2")]
    [InlineData("com.example.director")]
    [InlineData(null)]
    public void Decide_SameNameButNotOurBundleIdentifier_IsNotMoved(string? id)
    {
        Assert.Equal(MacRelocationDecision.NotOurBundle, Decide(InUserApps, readId: _ => id));
    }

    [Fact]
    public void DockAddArguments_AddsTheBundleAsAFileAddress_WithItsSpacesEscaped()
    {
        var args = MacDock.AddArguments("/Users/some one/Applications/Director.app");

        Assert.StartsWith("write com.apple.dock persistent-apps -array-add \"<dict>", args);
        Assert.Contains("<string>file:///Users/some%20one/Applications/Director.app/</string>", args);
        Assert.Contains("<key>_CFURLStringType</key><integer>15</integer>", args);
        Assert.EndsWith("</dict>\"", args);
        // One quoted word: the entry holds no quote of its own that would end it early.
        Assert.Equal(2, args.Count(c => c == '"'));
    }

    [Fact]
    public void IsInDock_FindsTheBundleInTheDocksList_AndNotAnotherApp()
    {
        const string dockList = """
            (
                {
                    "tile-data" = {
                        "file-data" = {
                            "_CFURLString" = "file:///Applications/Director.app/";
                            "_CFURLStringType" = 15;
                        };
                    };
                }
            )
            """;

        Assert.True(MacDock.IsInDock(dockList, "/Applications/Director.app"));
        Assert.False(MacDock.IsInDock(dockList, "/Applications/Safari.app"));
        Assert.False(MacDock.IsInDock("(\n)", "/Applications/Director.app"));
    }

    [Fact]
    public void MacPlaceText_SystemFolder_NamesTheFolderAndTheSearch()
    {
        var text = InstallCompletion.MacPlaceText(Installed);

        Assert.Contains("DevThrottle is in the Applications folder (/Applications)", text);
        Assert.Contains("Command+Space and type DevThrottle", text);
    }

    [Fact]
    public void MacPlaceText_UserFolder_SaysItIsTheOneInTheHomeFolder()
    {
        var text = InstallCompletion.MacPlaceText(InUserApps);

        Assert.Contains("the Applications folder in your home folder (" + UserApps + ")", text);
    }

    [Fact]
    public void MacOpenButton_SaysDevThrottle()
    {
        Assert.Equal("Open DevThrottle", InstallCompletion.MacOpenButtonText);
    }

    /// <summary>
    /// The release packages the Director as "Director.app" shown as "DevThrottle". macOS shows the localized name only
    /// while the folder name matches the non-localized CFBundleDisplayName, so Info.plist must keep the folder name
    /// there and the shown name must come from en.lproj. A release step that dropped the flag would ship "Director"
    /// again with every test green, so the wiring is read here.
    /// </summary>
    [Fact]
    public void Release_PackagesTheDirectorWithTheShownNameDevThrottle()
    {
        var root = RepoRoot();
        var release = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        var script = File.ReadAllText(Path.Combine(root, "scripts", "package-mac-app.sh"));

        Assert.Contains("--dmg-name devthrottle-mac-arm64.dmg\n          --display-name DevThrottle", release.Replace("\r\n", "\n"));
        Assert.Contains("<key>CFBundleDisplayName</key>     <string>$APP_NAME</string>", script);
        Assert.Contains("<key>CFBundleName</key>            <string>$DISPLAY_NAME</string>", script);
        Assert.Contains("<key>LSHasLocalizedDisplayName</key><true/>", script);
        Assert.Contains("CFBundleDisplayName = \"$DISPLAY_NAME\";", script);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "package-mac-app.sh"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }
}
