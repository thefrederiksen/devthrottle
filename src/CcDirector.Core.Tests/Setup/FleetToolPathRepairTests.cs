using System;
using System.IO;
using System.Linq;
using CcDirector.Core.Setup;
using Xunit;

namespace CcDirector.Core.Tests.Setup;

/// <summary>
/// Covers the pure PATH rewriting. The registry write itself is not exercised here - a test that
/// edited the developer's real user PATH would be a worse bug than the one being fixed - so these
/// pin the ordering rules that decide what would be written.
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
        var before = P(@"C:\cc-director\bin", @"C:\windows", @"C:\cc-director\instances\default\bin");

        var result = FleetToolPathRepair.MoveToFront(before, @"C:\cc-director\instances\default\bin");

        Assert.Equal(
            P(@"C:\cc-director\instances\default\bin", @"C:\cc-director\bin", @"C:\windows"),
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
        // The repair is a reordering of one entry, not a rewrite of the user's PATH. Anything else
        // moving is collateral damage on shared machine state.
        var result = FleetToolPathRepair.MoveToFront(P(@"C:\a", @"C:\b", @"C:\c"), @"C:\mine");

        Assert.Equal(P(@"C:\mine", @"C:\a", @"C:\b", @"C:\c"), result);
    }

    [Fact]
    public void MoveToFront_UnexpandedVariablesAreCarriedThroughUntouched()
    {
        // %USERPROFILE% must still be %USERPROFILE% afterwards. Expanding it here would be how a
        // repair silently destroys a user's PATH.
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
        // was empty - that Director's tools had never been installed - and the old guard, which asked
        // only whether the directory EXISTED, waved it through. PATH was reordered around an empty
        // directory, which changes the order and nothing about what resolves.
        //
        // It must refuse, and it must refuse BEFORE it writes anything: this test would edit the
        // developer's real user PATH if it did not.
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

    // ---- The PATH cleanup: one entry per command line, and never someone else's ----

    private static readonly string Root = Path.Combine("C:", "cc-director");
    private static readonly string OurBin = Path.Combine(Root, "instances", "default", "bin");
    private static readonly string LegacyBin = Path.Combine(Root, "bin");
    private static readonly string OtherInstanceBin = Path.Combine(Root, "instances", "slot-5", "bin");
    private static readonly string TempRoot = Path.Combine("C:", "temp");

    /// <summary>Every directory listed exists and holds the tool; nothing else does.</summary>
    private static FleetToolPathRepair.PathRewrite RewriteWith(
        string path, params string[] existingToolDirs)
    {
        bool Exists(string dir) => existingToolDirs.Any(
            d => string.Equals(d.TrimEnd(Path.DirectorySeparatorChar), dir.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase));

        return FleetToolPathRepair.Rewrite(path, OurBin, Exists, Exists, TempRoot);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_TheSupersededFlatBinOfOurOwnInstall_IsRemoved()
    {
        // Two entries for one command line serve nobody: only the first can ever win, and the loser
        // waits to win again the moment the order shifts.
        var result = RewriteWith(P(LegacyBin, @"C:\windows", OurBin), LegacyBin, OurBin);

        Assert.Equal(P(OurBin, @"C:\windows"), result.Path);
        Assert.Equal(new[] { LegacyBin }, result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_AnotherLiveInstancesBin_IsKept()
    {
        // A second Director in its own instance home is legitimate on this machine. Ours goes in
        // front, which is all that is needed; removing theirs would be sabotage dressed as hygiene.
        var result = RewriteWith(P(OtherInstanceBin, OurBin), OtherInstanceBin, OurBin);

        Assert.Equal(P(OurBin, OtherInstanceBin), result.Path);
        Assert.Empty(result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_AToolDirectoryUnderTheTempDirectory_IsRemoved()
    {
        // There is one of these on the machine that prompted this work: a wizard test harness left
        // ...\Temp\wizard-harness-home-29ef...\cc-director\bin on the real user PATH.
        var leaked = Path.Combine(TempRoot, "wizard-harness-home-abc", "cc-director", "bin");

        var result = RewriteWith(P(leaked, OurBin), leaked, OurBin);

        Assert.Equal(P(OurBin), result.Path);
        Assert.Equal(new[] { leaked }, result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_AnInstallBinThatIsGoneFromDisk_IsRemoved()
    {
        var vanished = Path.Combine("C:", "old", "cc-director", "bin");

        var result = RewriteWith(P(vanished, OurBin), OurBin);

        Assert.Equal(P(OurBin), result.Path);
        Assert.Equal(new[] { vanished }, result.Removed);
    }

    [WindowsOnlyFact("persisting a PATH change is Windows-only - the product throws PlatformNotSupportedException elsewhere because the shell profile owns PATH - and these expectations are drive-letter and semicolon shaped")]
    public void Rewrite_AMissingDirectoryThatIsNothingToDoWithUs_IsKept()
    {
        // An entry whose network drive is unmapped this morning is not ours to tidy away. The removal
        // rule only ever fires on the shape no other product writes.
        var unrelated = Path.Combine("Z:", "team", "bin");

        var result = RewriteWith(P(unrelated, OurBin), OurBin);

        Assert.Equal(P(OurBin, unrelated), result.Path);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Rewrite_ADirectoryInsideOurInstallThatHoldsNoTool_IsKept()
    {
        // The install ROOT is on this machine's PATH as well as its bin. It is not a tool directory,
        // so it is not ours to remove - being near our files is not the same as being ours.
        //
        // The shared helper cannot express this case: it answers "exists" and "holds the tool" from
        // one list, so it can only describe directories where those two agree. Here they must NOT -
        // the root exists AND holds nothing - which is exactly the distinction being tested.
        var result = FleetToolPathRepair.Rewrite(
            P(Root, OurBin), OurBin,
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
        // Both halves matter. Deciding on the raw text would miss %LOCALAPPDATA%\cc-director\bin -
        // the exact entry we are here to remove. Writing back the expanded text would bake today's
        // expansion into the user's PATH permanently and destroy every variable reference in it.
        var variable = $"CCTEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, Root);
        try
        {
            var rawLegacy = $"%{variable}%" + Path.DirectorySeparatorChar + "bin";
            var rawKeeper = $"%{variable}%" + Path.DirectorySeparatorChar + "notes";

            var result = FleetToolPathRepair.Rewrite(
                P(rawLegacy, rawKeeper, OurBin), OurBin,
                directoryExists: _ => true,
                holdsFleetTool: dir => dir.EndsWith("bin", StringComparison.OrdinalIgnoreCase),
                tempRoot: TempRoot);

            // Decided on the expanded form: the variable entry WAS recognised as the legacy bin.
            Assert.Equal(new[] { rawLegacy }, result.Removed);
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
    public void Rewrite_RunTwice_IsStable()
    {
        var once = RewriteWith(P(LegacyBin, @"C:\windows", OurBin), LegacyBin, OurBin);
        var twice = RewriteWith(once.Path, LegacyBin, OurBin);

        Assert.Equal(once.Path, twice.Path);
        Assert.Empty(twice.Removed);
    }

    [Fact]
    public void PathWithOwnToolsFirst_RemovesNothingFromTheSessionsInheritedPath()
    {
        // What a session gets is a PREFERENCE, not a cleanup: the user's own PATH reaches the session
        // intact, and only which copy of OUR command line wins is decided for them.
        var result = FleetToolPathRepair.PathWithOwnToolsFirst(OurBin, P(LegacyBin, @"C:\windows"));

        Assert.Equal(P(OurBin, LegacyBin, @"C:\windows"), result);
    }
}
