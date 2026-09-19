using System;
using System.IO;
using System.Linq;
using CcDirector.Core.Setup;
using Xunit;

namespace CcDirector.Core.Tests.Setup;

/// <summary>
/// Covers the pure path rewriting. The registry write itself is not exercised here - a test that
/// edited the developer's real user path would be a worse bug than the one being fixed - so these
/// pin the decision that comes before the write.
/// </summary>
public class FleetToolPathRepairTests
{
    private static string P(params string[] entries) => string.Join(Path.PathSeparator, entries);

    [Fact]
    public void MoveToFront_EntryNotPresent_IsPrepended()
    {
        var result = FleetToolPathRepair.MoveToFront(P(@"C:\windows", @"C:\tools"), @"C:\mine\bin");

        Assert.Equal(P(@"C:\mine\bin", @"C:\windows", @"C:\tools"), result);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void MoveToFront_EntryBehindAStaleInstall_OvertakesIt()
    {
        // The machine this was written for: the old install's bin wins because it comes first.
        var before = P(@"C:\cc-director\instances\default\bin", @"C:\windows", @"C:\cc-director\bin");

        var result = FleetToolPathRepair.MoveToFront(before, @"C:\cc-director\bin");

        Assert.Equal(
            P(@"C:\cc-director\bin", @"C:\cc-director\instances\default\bin", @"C:\windows"),
            result);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void MoveToFront_RunTwice_DoesNotAccumulateDuplicates()
    {
        var once = FleetToolPathRepair.MoveToFront(P(@"C:\windows"), @"C:\mine\bin");
        var twice = FleetToolPathRepair.MoveToFront(once, @"C:\mine\bin");

        Assert.Equal(once, twice);
    }

    [Fact]
    public void MoveToFront_LeavesEveryOtherEntryInItsOriginalOrder()
    {
        // The repair is a reordering of one entry, not a rewrite of the user's path. Anything else
        // moving is collateral damage on shared machine state.
        var result = FleetToolPathRepair.MoveToFront(P(@"C:\a", @"C:\b", @"C:\c"), @"C:\mine");

        Assert.Equal(P(@"C:\mine", @"C:\a", @"C:\b", @"C:\c"), result);
    }

    [Fact]
    public void MoveToFront_UnexpandedVariablesAreCarriedThroughUntouched()
    {
        // %USERPROFILE% must still be %USERPROFILE% afterwards. Expanding it here would be how a
        // repair silently destroys a user's path.
        var result = FleetToolPathRepair.MoveToFront(P(@"%USERPROFILE%\bin", @"C:\windows"), @"C:\mine");

        Assert.Contains(@"%USERPROFILE%\bin", result, StringComparison.Ordinal);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void MoveToFront_IgnoresTrailingSeparatorAndCaseWhenMatching()
    {
        var before = P(@"C:\Mine\Bin\", @"C:\windows");

        var result = FleetToolPathRepair.MoveToFront(before, @"C:\mine\bin");

        Assert.Equal(P(@"C:\mine\bin", @"C:\windows"), result);
    }

    [Fact]
    public void MoveToFront_DropsEmptySegments()
    {
        var result = FleetToolPathRepair.MoveToFront(P(@"C:\a", "", @"C:\b"), @"C:\mine");

        Assert.Equal(P(@"C:\mine", @"C:\a", @"C:\b"), result);
    }

    [Fact]
    public void PutFirstOnPath_DirectoryThatDoesNotExist_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => FleetToolPathRepair.PutFirstOnPath(Path.Combine(Path.GetTempPath(), "no-such-dir-x9")));
    }

    [Fact]
    public void PutFirstOnPath_EmptyDirectory_Throws()
    {
        Assert.Throws<ArgumentException>(() => FleetToolPathRepair.PutFirstOnPath("  "));
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void PutFirstOnPath_DirectoryThatExistsButHoldsNoTool_RefusesAndSaysWhy()
    {
        // THE BUG, at the layer that could have stopped it. On 2026-08-01 this directory existed and
        // was empty - those tools had never been installed - and the old guard, which asked only
        // whether the directory EXISTED, waved it through. The path was reordered around an empty
        // directory, which changes the order and nothing about what resolves.
        //
        // It must refuse, and it must refuse BEFORE it writes anything: this test would edit the
        // developer's real user path if it did not.
        var empty = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"fleet-bin-empty-{Guid.NewGuid():N}"));
        try
        {
            var result = FleetToolPathRepair.PutFirstOnPath(empty.FullName);

            Assert.False(result.Succeeded);
            Assert.Contains("not installed", result.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(empty.FullName, result.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void HoldsFleetTool_EmptyDirectory_IsFalse_AndWithTheToolPresent_IsTrue()
    {
        // The distinction the whole fix turns on: a container is not its contents.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"fleet-bin-{Guid.NewGuid():N}"));
        try
        {
            Assert.False(FleetToolPathRepair.HoldsFleetTool(dir.FullName));

            var shim = Path.Combine(dir.FullName, OperatingSystem.IsWindows()
                ? "cc-devthrottle.cmd"
                : "cc-devthrottle");
            File.WriteAllText(shim, "");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            Assert.True(FleetToolPathRepair.HoldsFleetTool(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- The rewrite rule: the master stays, the copies it replaces come off ----
    //
    // THE WORLD THE RIGHT WAY ROUND. Every name below is deliberate, because the old rule called the
    // machine root's bin the "legacy" one and each Director's own bin "ours" - exactly backwards. The
    // MASTER is the machine root's bin: one installed copy per machine, which every Director on it
    // uses. A tools directory inside a Director's own folder of that same root is a copy the master
    // replaces, and it comes off the path.

    private static readonly string Root = Path.Combine("C:", "cc-director");
    private static readonly string MasterBin = Path.Combine(Root, "bin");
    private static readonly string DefaultDirectorBin = Path.Combine(Root, "instances", "default", "bin");
    private static readonly string NamedDirectorBin = Path.Combine(Root, "instances", "slot-5", "bin");
    private static readonly string TempRoot = Path.Combine("C:", "temp");

    /// <summary>Every directory listed exists and holds the tool; nothing else does.</summary>
    private static FleetToolPathRepair.PathRewrite RewriteWith(
        string path, params string[] existingToolDirs)
    {
        bool Exists(string dir) => existingToolDirs.Any(
            d => string.Equals(d.TrimEnd(Path.DirectorySeparatorChar), dir.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase));

        return FleetToolPathRepair.Rewrite(path, MasterBin, Exists, Exists, TempRoot);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_ADirectorsOwnToolsDirectory_ComesOffThePath()
    {
        // The whole mission in one case. A Director's folder was never an install root; every copy
        // inside one ages at its own pace and answers whenever it happens to come first.
        var result = RewriteWith(
            P(DefaultDirectorBin, @"C:\windows", MasterBin), DefaultDirectorBin, MasterBin);

        Assert.Equal(P(MasterBin, @"C:\windows"), result.Path);
        Assert.Equal(new[] { DefaultDirectorBin }, result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_ANamedDirectorsToolsDirectory_ComesOffThePathToo()
    {
        // Named Directors stay - the owner asked for them and they are good - but a named Director is
        // the same executable and has no business carrying its own copy of the tools. The rule that
        // used to protect this entry ("another live install, removing it would be sabotage") was
        // written when a Director's folder was believed to be an install. It is not one.
        var result = RewriteWith(P(NamedDirectorBin, MasterBin), NamedDirectorBin, MasterBin);

        Assert.Equal(P(MasterBin), result.Path);
        Assert.Equal(new[] { NamedDirectorBin }, result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_TheNestedCopyTheLeakProduced_ComesOffThePath()
    {
        // There is an instances\default\instances\default on the computer that prompted this work. One
        // climb out of it lands on another Director's folder, which is still not the machine, so the
        // rule has to climb until it reaches a machine root rather than assuming one level.
        var nested = Path.Combine(Root, "instances", "default", "instances", "default", "bin");

        var result = RewriteWith(P(nested, MasterBin), nested, MasterBin);

        Assert.Equal(P(MasterBin), result.Path);
        Assert.Equal(new[] { nested }, result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_ADirectorsToolsDirectoryThatIsGoneFromDisk_ComesOffThePath()
    {
        // The state this mission ENDS in, so it is the one that must not be assumed away: the folder
        // has been deleted and the entry is still there. An entry pointing at nothing is still
        // searched on every command, and it comes back to life the moment anything recreates the
        // folder. A directory that is gone cannot be asked whether it holds the tool, so it is
        // recognised by its shape instead.
        var result = RewriteWith(P(DefaultDirectorBin, MasterBin), MasterBin);

        Assert.Equal(P(MasterBin), result.Path);
        Assert.Equal(new[] { DefaultDirectorBin }, result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_ATestRigServingItsOwnRoot_KeepsItsOwnTools()
    {
        // A rig keeping its own tools is what makes a rig safe to run at all, and it is the one thing
        // this rule must never reach. The rig's Director folder belongs to the rig's machine root, not
        // to ours, so the master does not replace it.
        var rigDirectorBin = Path.Combine("D:", "rig-root", "instances", "default", "bin");

        var result = RewriteWith(P(rigDirectorBin, MasterBin), rigDirectorBin, MasterBin);

        Assert.Equal(P(MasterBin, rigDirectorBin), result.Path);
        Assert.Empty(result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_AnotherInstallsOwnMaster_IsKept()
    {
        // Somebody else's install at a root of its own is not ours to tidy away. Ours goes in front,
        // which is all that is needed.
        var otherMaster = Path.Combine("D:", "another-install", "cc-director", "bin");

        var result = RewriteWith(P(otherMaster, MasterBin), otherMaster, MasterBin);

        Assert.Equal(P(MasterBin, otherMaster), result.Path);
        Assert.Empty(result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_AToolDirectoryUnderTheTempDirectory_IsRemoved()
    {
        // There is one of these on the machine that prompted this work: a wizard test harness left
        // ...\Temp\wizard-harness-home-29ef...\cc-director\bin on the real user path.
        var leaked = Path.Combine(TempRoot, "wizard-harness-home-abc", "cc-director", "bin");

        var result = RewriteWith(P(leaked, MasterBin), leaked, MasterBin);

        Assert.Equal(P(MasterBin), result.Path);
        Assert.Equal(new[] { leaked }, result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_AnInstallBinThatIsGoneFromDisk_IsRemoved()
    {
        var vanished = Path.Combine("C:", "old", "cc-director", "bin");

        var result = RewriteWith(P(vanished, MasterBin), MasterBin);

        Assert.Equal(P(MasterBin), result.Path);
        Assert.Equal(new[] { vanished }, result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_AMissingDirectoryThatIsNothingToDoWithUs_IsKept()
    {
        // An entry whose network drive is unmapped this morning is not ours to tidy away. The removal
        // rule only ever fires on the shapes no other product writes.
        var unrelated = Path.Combine("Z:", "team", "bin");

        var result = RewriteWith(P(unrelated, MasterBin), MasterBin);

        Assert.Equal(P(MasterBin, unrelated), result.Path);
        Assert.Empty(result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_AMissingDirectoryInsideOneOfOURDirectorFoldersThatIsNotAToolsDirectory_IsKept()
    {
        // The shape test is narrow ON PURPOSE. A Director's folder holds sessions, settings and logs,
        // and somebody may legitimately have one of those on their path. A vanished entry is only
        // recognised as ours when it is a tools directory - named "bin" - not merely because it sits
        // under a Director's folder.
        var notTools = Path.Combine(Root, "instances", "default", "scripts");

        var result = RewriteWith(P(notTools, MasterBin), MasterBin);

        Assert.Equal(P(MasterBin, notTools), result.Path);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Rewrite_ADirectoryInsideOurInstallThatHoldsNoTool_IsKept()
    {
        // The install ROOT is on this machine's path as well as its bin. It is not a tool directory,
        // so it is not ours to remove - being near our files is not the same as being ours.
        //
        // The shared helper cannot express this case: it answers "exists" and "holds the tool" from
        // one list, so it can only describe directories where those two agree. Here they must NOT -
        // the root exists AND holds nothing - which is exactly the distinction being tested.
        var result = FleetToolPathRepair.Rewrite(
            P(Root, MasterBin), MasterBin,
            directoryExists: _ => true,
            holdsFleetTool: dir => !string.Equals(
                dir.TrimEnd(Path.DirectorySeparatorChar), Root, StringComparison.OrdinalIgnoreCase),
            tempRoot: TempRoot);

        Assert.Contains(Root, result.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Rewrite_DecidesOnTheEXPANDEDPathButWritesBackTheRAWOne()
    {
        // Both halves matter. Deciding on the raw text would miss
        // %LOCALAPPDATA%\cc-director\instances\default\bin - the exact entry we are here to remove.
        // Writing back the expanded text would bake today's expansion into the user's path permanently
        // and destroy every variable reference in it.
        var variable = $"CCTEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, Root);
        try
        {
            var rawDirectorBin = $"%{variable}%" + Path.DirectorySeparatorChar
                + Path.Combine("instances", "default", "bin");
            var rawKeeper = $"%{variable}%" + Path.DirectorySeparatorChar + "notes";

            var result = FleetToolPathRepair.Rewrite(
                P(rawDirectorBin, rawKeeper, MasterBin), MasterBin,
                directoryExists: _ => true,
                holdsFleetTool: dir => dir.EndsWith("bin", StringComparison.OrdinalIgnoreCase),
                tempRoot: TempRoot);

            // Decided on the expanded form: the variable entry WAS recognised as a Director's copy.
            Assert.Equal(new[] { rawDirectorBin }, result.Removed);
            // Written back raw: the survivor still carries its variable, not this morning's expansion.
            Assert.Contains(rawKeeper, result.Path, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Path.Combine(Root, "notes"), result.Path, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_EveryCopyAtOnce_LeavesExactlyOneDevThrottleEntry()
    {
        // The goal this phase carries, stated as one case rather than inferred from the ones above:
        // the path holds exactly one DevThrottle tools folder afterwards. The machine that prompted
        // this carried six of these at once.
        var vanished = Path.Combine(Root, "instances", "slot-7", "bin");
        var nested = Path.Combine(Root, "instances", "default", "instances", "default", "bin");
        var leaked = Path.Combine(TempRoot, "wizard-harness-home-abc", "cc-director", "bin");

        var result = RewriteWith(
            P(DefaultDirectorBin, @"C:\windows", NamedDirectorBin, nested, leaked, vanished, MasterBin),
            DefaultDirectorBin, NamedDirectorBin, nested, leaked, MasterBin);

        Assert.Equal(P(MasterBin, @"C:\windows"), result.Path);
        Assert.Equal(
            new[] { DefaultDirectorBin, NamedDirectorBin, nested, leaked, vanished },
            result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_RunTwice_IsStable()
    {
        var once = RewriteWith(P(DefaultDirectorBin, @"C:\windows", MasterBin), DefaultDirectorBin, MasterBin);
        var twice = RewriteWith(once.Path, DefaultDirectorBin, MasterBin);

        Assert.Equal(once.Path, twice.Path);
        Assert.Empty(twice.Removed);
    }

    [Fact]
    public void PathWithOwnToolsFirst_RemovesNothingFromTheSessionsInheritedPath()
    {
        // What a session gets is a PREFERENCE, not a cleanup: it inherits the Director's own running
        // path, which the Director already repaired at start, and only which copy of our own command
        // line wins is decided for it here.
        var result = FleetToolPathRepair.PathWithOwnToolsFirst(MasterBin, P(@"C:\windows", @"C:\tools"));

        Assert.Equal(P(MasterBin, @"C:\windows", @"C:\tools"), result);
    }

    // ---- Who is allowed to write the SAVED path ----

    [Fact]
    public void OwnsTheSavedPath_TheMachinesOwnInstall_Does()
    {
        Assert.True(FleetToolPathRepair.OwnsTheSavedPath(Root, Root));
    }

    [WindowsOnlyFact("the comparison is case-insensitive and trailing-separator insensitive because Windows paths are")]
    public void OwnsTheSavedPath_TheSameRootSpeltDifferently_StillDoes()
    {
        Assert.True(FleetToolPathRepair.OwnsTheSavedPath(Root + Path.DirectorySeparatorChar, Root.ToUpperInvariant()));
    }

    [Fact]
    public void OwnsTheSavedPath_ARigServingItsOwnRoot_DoesNot()
    {
        // The guard that keeps a proof from damaging the machine it runs on. A rig is a legitimate
        // Director and repairs its own running path; the saved path outlives the rig's directory by
        // months, and there is an entry on this machine pointing at a harness root deleted in July.
        var rig = Path.Combine(Path.GetTempPath(), "rig-root-abc");

        Assert.False(FleetToolPathRepair.OwnsTheSavedPath(rig, Root));
    }

    [Fact]
    public void OwnsTheSavedPath_NothingToCompare_IsNotAYes()
    {
        // Absence of an answer is not permission to write permanent machine state.
        Assert.False(FleetToolPathRepair.OwnsTheSavedPath(null, Root));
        Assert.False(FleetToolPathRepair.OwnsTheSavedPath(Root, null));
        Assert.False(FleetToolPathRepair.OwnsTheSavedPath("   ", "   "));
    }

    // ---- The installer's path step ----

    private static FleetToolPathRepair.PathRewrite InstallRewriteWith(
        string path, params string[] existingToolDirs)
    {
        bool Exists(string dir) => existingToolDirs.Any(
            d => string.Equals(d.TrimEnd(Path.DirectorySeparatorChar), dir.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase));

        return FleetToolPathRepair.RewriteForInstall(path, MasterBin, Exists, Exists, TempRoot);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void RewriteForInstall_AddsTheMasterAndRemovesWhatItReplaces()
    {
        // Adding alone was the whole step until now, and it read as though it were enough. It was not:
        // the install places the tools at the machine root, and a per-Director copy left in FRONT of
        // that entry goes on answering every command. The files land in the right place and nothing
        // about which copy speaks has changed.
        var result = InstallRewriteWith(
            P(DefaultDirectorBin, @"C:\windows"), DefaultDirectorBin, MasterBin);

        Assert.Equal(P(@"C:\windows", MasterBin), result.Path);
        Assert.Equal(new[] { DefaultDirectorBin }, result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void RewriteForInstall_DoesNotReorderThePathAroundTheMaster()
    {
        // An install that reached in and moved the master to the front would be rearranging machine
        // state it was not asked to rearrange. Once the copies it replaces are off, position decides
        // nothing.
        var result = InstallRewriteWith(P(@"C:\windows", MasterBin, @"C:\tools"), MasterBin);

        Assert.Equal(P(@"C:\windows", MasterBin, @"C:\tools"), result.Path);
        Assert.Empty(result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void RewriteForInstall_ASecondMentionOfTheMaster_IsDropped()
    {
        var result = InstallRewriteWith(P(MasterBin, @"C:\windows", MasterBin + @"\"), MasterBin);

        Assert.Equal(P(MasterBin, @"C:\windows"), result.Path);
        Assert.Empty(result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void RewriteForInstall_ARigsOwnTools_AreLeftAlone()
    {
        var rigDirectorBin = Path.Combine("D:", "rig-root", "instances", "default", "bin");

        var result = InstallRewriteWith(P(rigDirectorBin), rigDirectorBin, MasterBin);

        Assert.Equal(P(rigDirectorBin, MasterBin), result.Path);
        Assert.Empty(result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void RewriteForInstall_RunTwice_IsStable()
    {
        var once = InstallRewriteWith(P(DefaultDirectorBin, @"C:\windows"), DefaultDirectorBin, MasterBin);
        var twice = InstallRewriteWith(once.Path, DefaultDirectorBin, MasterBin);

        Assert.Equal(once.Path, twice.Path);
        Assert.Empty(twice.Removed);
    }

    // ---- Counting what is left, which is the sentence the goal is written in ----

    [Fact]
    public void DevThrottleToolEntries_CountsOnlyTheDirectoriesThatHoldTheTool()
    {
        // The Director reports this number at every start. "The repair ran" and "the path holds
        // exactly one" are different claims, and only the second one is the goal - so it is measured
        // on the disk rather than inferred from the fact that a rewrite happened.
        var holder = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"fleet-entries-{Guid.NewGuid():N}"));
        var empty = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"fleet-entries-empty-{Guid.NewGuid():N}"));
        try
        {
            var shim = Path.Combine(holder.FullName, OperatingSystem.IsWindows()
                ? "cc-devthrottle.cmd"
                : "cc-devthrottle");
            File.WriteAllText(shim, "");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var entries = FleetToolPathRepair.DevThrottleToolEntries(
                P(empty.FullName, holder.FullName, Path.Combine(Path.GetTempPath(), "gone-abc")));

            Assert.Equal(new[] { holder.FullName }, entries);
        }
        finally
        {
            holder.Delete(recursive: true);
            empty.Delete(recursive: true);
        }
    }
}
