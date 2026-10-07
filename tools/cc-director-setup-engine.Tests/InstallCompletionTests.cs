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
}
