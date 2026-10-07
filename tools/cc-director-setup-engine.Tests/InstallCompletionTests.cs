using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The pure completion classification both wizards render. It moved into the engine when the
/// Prerequisites step was removed: it had lived in the Windows project alone, which is how macOS
/// came to branch for itself and report "Everything went perfectly" on a pass with failures in it.
/// </summary>
public sealed class InstallCompletionTests
{
    [Fact]
    public void Classify_AnySkipped_IsProblems()
    {
        Assert.Equal(InstallCompletionKind.Problems, InstallCompletion.Classify(skipped: 1, alreadyUpToDate: false));
        Assert.Equal(InstallCompletionKind.Problems, InstallCompletion.Classify(skipped: 1, alreadyUpToDate: true));
    }

    [Fact]
    public void Classify_NoSkipped_UpToDateOrSuccess()
    {
        Assert.Equal(InstallCompletionKind.AlreadyUpToDate, InstallCompletion.Classify(skipped: 0, alreadyUpToDate: true));
        Assert.Equal(InstallCompletionKind.Success, InstallCompletion.Classify(skipped: 0, alreadyUpToDate: false));
    }

    // "You're ready to go" is a claim about the MACHINE, not about this install. A clean pass on a
    // machine with no coding agent on it has nothing to run, so the screen may not say it.
    // Revert-proof: drop the agent term from IsReadyToGo and this goes red.
    [Fact]
    public void IsReadyToGo_NoCodingAgent_IsFalseEvenOnACleanPass()
    {
        Assert.False(InstallCompletion.IsReadyToGo(skipped: 0, anyCodingAgentPresent: false));
        Assert.True(InstallCompletion.IsReadyToGo(skipped: 0, anyCodingAgentPresent: true));
    }

    [Fact]
    public void IsReadyToGo_AnySkipped_IsFalse()
    {
        Assert.False(InstallCompletion.IsReadyToGo(skipped: 1, anyCodingAgentPresent: true));
    }

    // Issue #3503: Close on a first install used to end with nothing on screen and a machine that
    // never signed in. On a first install, closing the wizard opens the Director.
    [Fact]
    public void OpensDirectorOnClose_FirstInstallNotYetOpened_IsTrue()
        => Assert.True(InstallCompletion.OpensDirectorOnClose(isUpdate: false, directorAlreadyOpened: false, directorInstalled: true));

    // Open Director already started it - closing must not start a second one.
    [Fact]
    public void OpensDirectorOnClose_AlreadyOpened_IsFalse()
        => Assert.False(InstallCompletion.OpensDirectorOnClose(isUpdate: false, directorAlreadyOpened: true, directorInstalled: true));

    // An update: the person knows where the app is, and may have closed it on purpose.
    [Fact]
    public void OpensDirectorOnClose_Update_IsFalse()
        => Assert.False(InstallCompletion.OpensDirectorOnClose(isUpdate: true, directorAlreadyOpened: false, directorInstalled: true));

    // The Director failed to install: nothing to open.
    [Fact]
    public void OpensDirectorOnClose_DirectorNotInstalled_IsFalse()
        => Assert.False(InstallCompletion.OpensDirectorOnClose(isUpdate: false, directorAlreadyOpened: false, directorInstalled: false));

    // Issue #3503: a desktop shortcut on a first install, never on an update - an update must not
    // put back an icon the person deleted.
    [Fact]
    public void CreatesDesktopShortcut_FirstInstallOnly()
    {
        Assert.True(InstallCompletion.CreatesDesktopShortcut(isUpdate: false));
        Assert.False(InstallCompletion.CreatesDesktopShortcut(isUpdate: true));
    }

    // A warning does not count as skipped, so the classification and the ready-to-go verdict are unchanged
    // by it: the screen says the Director is installed, and the warning is said beside that, not instead.
    [Fact]
    public void WarningStatus_IsNotASkippedOrFailedStatus()
    {
        Assert.NotEqual("Skipped", InstallCompletion.WarningStatus);
        Assert.NotEqual("Failed", InstallCompletion.WarningStatus);
        Assert.Equal(InstallCompletionKind.Success, InstallCompletion.Classify(skipped: 0, alreadyUpToDate: false));
    }

    // What the person reads under the warning depends on what is actually true: whether there is a Director
    // to open, whether the launcher file is there for the Director to repair, and whether DevThrottle has the
    // report. Every combination is rendered here as the screen renders it, in plain words - no "launchd", no
    // "launch agent", no "plist" - and no promise the facts do not support (#3411, review round four).
    [Theory]
    [InlineData(true, "start", true)]
    [InlineData(true, "start", false)]
    [InlineData(true, "place", true)]
    [InlineData(true, "place", false)]
    [InlineData(false, "start", true)]
    [InlineData(false, "start", false)]
    [InlineData(false, "place", true)]
    [InlineData(false, "place", false)]
    public void WarningPanelText_SaysOnlyWhatTheFactsAllow(bool directorInstalled, string step, bool reportAccepted)
    {
        var warning = new InstallWarning(ComponentRegistry.Launcher.Id, "macOS could not start the launcher at all: launchd reports 'spawn failed' (exit code 78: EX_CONFIG).", step, reportAccepted);

        var text = InstallCompletion.WarningPanelText([warning], directorInstalled);
        var explanation = InstallCompletion.LauncherWarningExplanation(directorInstalled, step == "start", reportAccepted);

        // The reason line is the runner's own words, verbatim, and the explanation follows it.
        Assert.StartsWith("Launcher: macOS could not start the launcher at all", text);
        Assert.EndsWith("\n\n" + explanation, text);
        Assert.Contains("will not start by itself when you sign in", text);
        // The Director is the next step only when there is one.
        Assert.Equal(directorInstalled, text.Contains("The Director works without it: open it now."));
        Assert.Equal(!directorInstalled, text.Contains("The Director did not install either"));
        // A later repair is promised only when the Director is there to do it AND the file is there to repair.
        Assert.Equal(directorInstalled && step == "start", text.Contains("repairs it when it can"));
        Assert.Equal(directorInstalled && step == "place", text.Contains("Running the installer again puts the missing part in place"));
        // The report is with DevThrottle only when the Gateway accepted it.
        Assert.Equal(reportAccepted, text.Contains("nothing for you to type or send"));
        Assert.Equal(!reportAccepted, text.Contains("could not reach DevThrottle to report it"));
        // The explanation itself is in plain words: the reason line above it may name launchd, this never does.
        Assert.DoesNotContain("was sent", explanation);
        Assert.DoesNotContain("reports to DevThrottle what it finds", explanation);
        Assert.DoesNotContain("launchd", explanation);
        Assert.DoesNotContain("launch agent", explanation);
        Assert.DoesNotContain("plist", explanation);
        Assert.DoesNotContain("will repair", explanation);
    }

    [Fact]
    public void WarningPanelText_WithoutALauncherWarning_IsOnlyTheLines()
    {
        var text = InstallCompletion.WarningPanelText([new InstallWarning("cc-tools", "did not install", InstallWarning.PlaceStep, false)], directorInstalled: true);

        Assert.Equal("Tools: did not install", text);
    }

    [Fact]
    public void InstallWarning_PlacedMeansTheStepWasNotPlace()
    {
        Assert.True(new InstallWarning(ComponentRegistry.Launcher.Id, "r", InstallWarning.StartStep, true).Placed);
        Assert.False(new InstallWarning(ComponentRegistry.Launcher.Id, "r", InstallWarning.PlaceStep, true).Placed);
        Assert.True(new InstallWarning("CC-LAUNCHER", "r", InstallWarning.StartStep, true).IsLauncher);
        Assert.False(new InstallWarning("cc-director", "r", InstallWarning.StartStep, true).IsLauncher);
    }
}
