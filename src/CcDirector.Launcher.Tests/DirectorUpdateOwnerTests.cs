using CcDirector.Core.Update;
using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// The zero-sessions gate for installing a staged Director update, and where the launcher looks for
/// the record of one (issue #1033).
///
/// The gate itself is unchanged from the rule the Director used to apply to itself - a Director holding
/// live work is never restarted out from under it - so these cases are the same cases, now asserted
/// against the process that can act on the answer safely. The location tests exist because that is the
/// part that fails INVISIBLY: the launcher runs at the storage root while the installed Director keeps
/// its home one level in, so a launcher reading only its own home would report "nothing staged" for
/// ever and look perfectly healthy doing it.
///
/// In <see cref="StorageRootCollection"/> because one of these redirects the storage root, which is a
/// process-wide variable other test classes here also redirect.
/// </summary>
[Collection(StorageRootCollection.Name)]
public class DirectorUpdateOwnerTests
{
    [Fact]
    public void ShouldApply_StagedAndNoSessions_True()
    {
        Assert.True(DirectorUpdateOwner.ShouldApply(hasStagedUpdate: true, runningSessionCount: 0));
    }

    [Fact]
    public void ShouldApply_StagedButOneSessionRunning_False()
    {
        Assert.False(DirectorUpdateOwner.ShouldApply(hasStagedUpdate: true, runningSessionCount: 1));
    }

    [Fact]
    public void ShouldApply_ManySessionsRunning_False()
    {
        Assert.False(DirectorUpdateOwner.ShouldApply(hasStagedUpdate: true, runningSessionCount: 7));
    }

    [Fact]
    public void ShouldApply_NothingStaged_False()
    {
        // No staged update: nothing to install even when the machine is completely idle.
        Assert.False(DirectorUpdateOwner.ShouldApply(hasStagedUpdate: false, runningSessionCount: 0));
    }

    [Fact]
    public void ShouldApply_NothingStagedAndSessionsRunning_False()
    {
        Assert.False(DirectorUpdateOwner.ShouldApply(hasStagedUpdate: false, runningSessionCount: 3));
    }

    [Fact]
    public void UpdaterStateFiles_IncludesEveryInstanceHome_NotJustTheLaunchersOwn()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-duo-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
            Directory.CreateDirectory(Path.Combine(root, "instances", "default", "config", "director"));
            Directory.CreateDirectory(Path.Combine(root, "instances", "work", "config", "director"));

            var files = DirectorUpdateOwner.UpdaterStateFiles().ToList();

            // The installed Director boots as the "default" instance, so THIS is the path that matters
            // in production - and the one a launcher reading only its own home would never look at.
            Assert.Contains(Path.Combine(root, "instances", "default", "config", "director", "updater-state.json"), files);
            Assert.Contains(Path.Combine(root, "instances", "work", "config", "director", "updater-state.json"), files);
            Assert.Contains(files, f => f.StartsWith(Path.Combine(root, "config"), StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", previousRoot);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ResolveStagedBuild_OnAMacBundle_ResolvesTheBundleNotTheBinaryInsideIt()
    {
        // The Director records the binary inside the downloaded bundle, because that is what it used to
        // run in its own swap mode. The thing that becomes the install is the bundle.
        var recorded = "/Users/soren/Library/staged/1.9.0/extracted/Director.app/Contents/MacOS/cc-director";
        Assert.Equal("/Users/soren/Library/staged/1.9.0/extracted/Director.app",
            DirectorUpdateOwner.ResolveStagedBuild(recorded));
    }

    [Fact]
    public void ResolveStagedBuild_OnAWindowsExecutable_IsTheExecutableItself()
    {
        var recorded = @"C:\Users\soren\AppData\Local\cc-director\config\director\updates\1.9.0\cc-director-win-x64.exe";
        Assert.Equal(recorded, DirectorUpdateOwner.ResolveStagedBuild(recorded));
    }

    [Fact]
    public void StagedRecord_SurvivesARoundTripThroughAnExplicitPath()
    {
        // The launcher reads and writes the DIRECTOR's state file, not its own, so the explicit-path
        // round trip is the thing that has to work. If this quietly wrote somewhere else, the launcher
        // would reinstall the same update every cycle.
        var dir = Path.Combine(Path.GetTempPath(), "cc-duo-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "updater-state.json");
            new UpdaterState { StagedVersion = "1.9.0", StagedExecutable = "x", InstallTarget = "y" }.SaveTo(path);

            var reloaded = UpdaterState.LoadFrom(path);
            Assert.Equal("1.9.0", reloaded.StagedVersion);

            reloaded.StagedVersion = null;
            reloaded.PinnedBadVersion = "1.9.0";
            reloaded.SaveTo(path);

            var afterPin = UpdaterState.LoadFrom(path);
            Assert.Null(afterPin.StagedVersion);
            Assert.Equal("1.9.0", afterPin.PinnedBadVersion);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    // ---- What the pass decided, written where something other than the log can read it -----------

    [Fact]
    public void Record_WritesTheDecisionIntoTheDirectorsOwnStateFile()
    {
        // Issue #1030. Before this, every one of these decisions existed only as a line in the
        // launcher's log on that machine. HeldBecauseBusy - "waiting for your sessions to finish" -
        // looked exactly like a stall from outside, and a rollback could not be learned at all.
        var dir = Path.Combine(Path.GetTempPath(), "cc-duo-record-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "updater-state.json");
            new UpdaterState { StagedVersion = "1.9.1", StagedExecutable = "x", InstallTarget = "y" }.SaveTo(path);
            var staged = new StagedDirectorUpdate("1.9.1", "x", "y", path);

            var returned = DirectorUpdateOwner.Record(staged, DirectorUpdateDecision.HeldBecauseBusy,
                "2 session(s) were running.");

            // The decision is passed straight back, so recording can never change what the launcher did.
            Assert.Equal(DirectorUpdateDecision.HeldBecauseBusy, returned);

            var reloaded = UpdaterState.LoadFrom(path);
            Assert.Equal("HeldBecauseBusy", reloaded.LastApplyDecision);
            Assert.Equal("1.9.1", reloaded.LastApplyVersion);
            Assert.Equal("2 session(s) were running.", reloaded.LastApplyDetail);
            Assert.NotNull(reloaded.LastApplyDecisionAt);

            // Everything else in the file is left exactly as it was - the launcher owns four fields
            // here and the Director owns the rest.
            Assert.Equal("1.9.1", reloaded.StagedVersion);
            Assert.Equal("x", reloaded.StagedExecutable);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    // ---- Whether a build that did not start may be blamed for it -----------

    [Fact]
    public void ShouldPinFailedBuild_TheRestoredBuildCameUp_True()
    {
        // The machine has just demonstrated it can run a Director. So the one that would not run is the
        // thing at fault, and pinning it is a true judgement about a build.
        Assert.True(DirectorUpdateOwner.ShouldPinFailedBuild(restoredBuildAnswered: true));
    }

    [Fact]
    public void ShouldPinFailedBuild_TheRestoredBuildDidNotComeUpEither_False()
    {
        // The owner's Mac, 2026-09-03 at 1:57 in the morning. The display was asleep, so macOS gave a
        // starting Director no render loop and NOTHING could have come up. The launcher wrote down
        // "restored build answering=not yet" and pinned version 2.0.5 anyway. The machine then sat five
        // days on 2.0.4 for a fault that was never in the build.
        Assert.False(DirectorUpdateOwner.ShouldPinFailedBuild(restoredBuildAnswered: false));
    }

    [Fact]
    public void ShouldPinFailedBuild_NothingWasLearned_False()
    {
        // No roll back happened, or there was no backup to restore. Either way nothing was established
        // about the build, and an absence of evidence must not become a permanent verdict.
        Assert.False(DirectorUpdateOwner.ShouldPinFailedBuild(restoredBuildAnswered: null));
    }

    [Fact]
    public void Record_OnAnUnwritableFile_StillReturnsTheDecision()
    {
        // Failing to write down what happened must never change what happened.
        var staged = new StagedDirectorUpdate("1.9.1", "x", "y",
            Path.Combine(Path.GetTempPath(), "cc-duo-missing-" + Guid.NewGuid().ToString("N"), "nested", "..", "?", "s.json"));

        Assert.Equal(DirectorUpdateDecision.RolledBack,
            DirectorUpdateOwner.Record(staged, DirectorUpdateDecision.RolledBack, "it never came up"));
    }
}
