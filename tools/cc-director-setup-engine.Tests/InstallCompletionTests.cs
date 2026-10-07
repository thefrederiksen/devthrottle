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

    // A launcher that does not install is a WARNING for the install: the Director runs, signs in and enrolls
    // without it, and the Director repairs it once connected. Five installs on one Mac placed a working
    // Director and then read "Setup finished with problems" because launchd refused the launcher (#3411).
    [Fact]
    public void FailureIsWarning_Launcher_IsTrue()
    {
        Assert.True(InstallCompletion.FailureIsWarning(ComponentRegistry.Launcher.Id));
        Assert.True(InstallCompletion.FailureIsWarning(ComponentRegistry.Launcher.Id.ToUpperInvariant()));
    }

    // The Director and the tools are not warnings: without the Director there is nothing to open.
    [Fact]
    public void FailureIsWarning_DirectorAndTools_IsFalse()
    {
        Assert.False(InstallCompletion.FailureIsWarning(ComponentRegistry.Director.Id));
        Assert.False(InstallCompletion.FailureIsWarning("cc-tools"));
        Assert.False(InstallCompletion.FailureIsWarning(""));
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

    // What the person reads under the warning: what they lose (autostart), what to do (open the Director),
    // and what happens next (the Director tries to repair it; a report was sent). In plain words - no
    // "launchd", no "launch agent", no "plist" - and no promise the repair cannot keep.
    [Fact]
    public void LauncherWarningExplanation_SaysWhatIsLostAndWhatHappensNext()
    {
        var text = InstallCompletion.LauncherWarningExplanation;
        Assert.Contains("will not start by itself when you sign in", text);
        Assert.Contains("The Director works without it", text);
        Assert.Contains("tries to repair this", text);
        Assert.Contains("report", text);
        Assert.Contains("nothing for you to type or send", text);
        Assert.DoesNotContain("launchd", text);
        Assert.DoesNotContain("launch agent", text);
        Assert.DoesNotContain("plist", text);
        Assert.DoesNotContain("will repair", text);
    }
}
