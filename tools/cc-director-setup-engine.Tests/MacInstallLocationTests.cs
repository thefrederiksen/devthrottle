using System.Runtime.InteropServices;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Where a macOS install lands, and what it does about where earlier installs landed. The Director
/// bundle belongs in /Applications - the folder the Finder's Applications entry opens and the one the
/// Applications view of Spotlight lists - so that an install leaves its icon where a person looks for
/// it. Before this, the bundle went to ~/Applications only and there was no icon in the Applications
/// folder at all.
///
/// Every test fixes the chosen folder through the layout's test constructor, so both the
/// /Applications answer and the ~/Applications fallback run on any machine and on any platform - the
/// macOS branch of PathFor is a parameter, not a reading of the current operating system, for exactly
/// this reason.
/// </summary>
public class MacInstallLocationTests
{
    private const string Home = "/Users/someone";
    private const string Root = "/Users/someone/Library/Application Support/cc-director";

    private static InstallLayout Layout(string macAppsDir) =>
        new(Root, defaultRoot: null, homeDir: Home, macAppsDir: macAppsDir);

    [Fact]
    public void Director_IsPlacedInTheSystemApplicationsFolder()
    {
        var layout = Layout("/Applications");

        Assert.Equal("/Applications/Director.app", layout.PathFor(ComponentRegistry.Director, OSPlatform.OSX));
    }

    [Fact]
    public void Director_FallsBackToTheUsersOwnApplicationsFolder()
    {
        var layout = Layout(Path.Combine(Home, "Applications"));

        Assert.Equal(Path.Combine(Home, "Applications", "Director.app"),
            layout.PathFor(ComponentRegistry.Director, OSPlatform.OSX));
    }

    [Fact]
    public void MacUserAppsDir_StaysInTheUsersHome_WhateverTheInstallFolderIs()
    {
        // The per-user folder is a fixed fact about the account, not "whichever folder was chosen".
        // MacFileOwnership repairs the user's own files through it, and it must never follow the
        // install into /Applications - see OwnershipRepair_NeverNamesTheSystemApplicationsFolder.
        Assert.Equal(Path.Combine(Home, "Applications"), Layout("/Applications").MacUserAppsDir);
        Assert.Equal(Path.Combine(Home, "Applications"), Layout(Path.Combine(Home, "Applications")).MacUserAppsDir);
    }

    [Fact]
    public void DirectorExecutablePath_IsTheBinaryInsideTheChosenBundle()
    {
        // The identity slot of the running Director is keyed by the path its own process reports.
        Assert.Equal("/Applications/Director.app/Contents/MacOS/cc-director",
            Layout("/Applications").DirectorExecutablePath(OSPlatform.OSX));
    }

    [Fact]
    public void EarlierLocationsAreStillRecognisedAsAnInstall()
    {
        // A Mac that installed before the move carries ~/Applications/Director.app, and one from before
        // the #1821 rename carries "CC Director.app". Both are an install. If detection did not accept
        // them, such a Mac would read as "nothing installed" and the next install would stack a second
        // icon beside the first instead of replacing it.
        var aliases = Layout("/Applications").LegacyAliasesFor(ComponentRegistry.Director);

        // Only on macOS: the aliases are a macOS history, and the property says so by asking the running
        // operating system - there is no platform parameter to pass here.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.Empty(aliases);
            return;
        }

        Assert.Contains(Path.Combine(Home, "Applications", "Director.app"), aliases);
        Assert.Contains(Path.Combine(Home, "Applications", "CC Director.app"), aliases);

        // And every one of them is inside the home folder this layout was TOLD about. The aliases are
        // handed to the uninstall, which deletes what it is given; a hard-coded machine-wide path here
        // could not be sandboxed by any caller, which is how a unit test came to delete a developer's
        // installed application. A stray hand-placed copy in /Applications is MacBundlePurger's job,
        // because it reads a bundle's identity before deleting it.
        Assert.All(aliases, a => Assert.StartsWith(Home + "/", a, StringComparison.Ordinal));
    }

    [Fact]
    public void TheChosenLocationIsNeverAlsoListedAsAnEarlierOne()
    {
        // An alias is a place to DETECT and REMOVE an install from. The place this install writes to
        // must never appear in that list: the uninstall deletes every alias, and the purge deletes
        // every bundle that is not the one being kept.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;

        foreach (var chosen in new[] { "/Applications", Path.Combine(Home, "Applications") })
        {
            var layout = Layout(chosen);
            var target = layout.PathFor(ComponentRegistry.Director, OSPlatform.OSX);

            Assert.DoesNotContain(target, layout.LegacyAliasesFor(ComponentRegistry.Director));
        }
    }

    [Fact]
    public void OwnershipRepair_NeverNamesTheSystemApplicationsFolder()
    {
        // The ownership repair is a recursive chown to the user, run with an administrator prompt. It
        // exists for files a sudo install created in the user's own home owned by root (#3411).
        // /Applications belongs to root on every Mac and must keep belonging to root, and its PARENT is
        // "/" - so a repair that followed the install there would have handed the user /Applications and
        // named "/.zshrc" and "/.bash_profile", the dot-files at the root of the disk.
        var layout = Layout("/Applications");
        var plist = Path.Combine(Home, "Library", "LaunchAgents", "com.devthrottle.cc-launcher.plist");

        var targets = MacFileOwnership.InstallTargets(layout, plist);

        foreach (var path in targets.Trees.Concat(targets.Folders))
        {
            Assert.False(path == "/Applications" || path.StartsWith("/Applications/", StringComparison.Ordinal),
                $"the ownership repair must never touch /Applications, but it names '{path}'");
            Assert.True(path.StartsWith(Home + "/", StringComparison.Ordinal) || path == Home,
                $"the ownership repair must only name paths in the user's own home, but it names '{path}'");
        }

        // And it still covers what it is for: the user's own Applications folder, the bundles in it, and
        // the shell start-up files - named from the home folder, not from the parent of the install folder.
        Assert.Contains(Path.Combine(Home, "Applications", "Director.app"), targets.Trees);
        Assert.Contains(Path.Combine(Home, "Applications", MacFileOwnership.SetupAppName), targets.Trees);
        Assert.Contains(Path.Combine(Home, ".zshrc"), targets.Trees);
        Assert.Contains(Path.Combine(Home, ".bash_profile"), targets.Trees);
        Assert.Contains(Path.Combine(Home, "Applications"), targets.Folders);
    }
}
