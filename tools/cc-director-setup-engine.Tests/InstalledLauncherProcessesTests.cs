using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Which launcher processes belong to an install - and, the part that matters here, what happens when
/// one of them cannot be read at all.
/// </summary>
public class InstalledLauncherProcessesTests
{
    [Fact]
    public void EverythingReadable_IsTheAnswer()
    {
        var readable = new[] { new LauncherProcess(1, "/opt/cc-director/launcher/cc-launcher --managed") };

        Assert.Same(readable, InstalledLauncherProcesses.Resolve(readable, []));
    }

    [Fact]
    public void AProcessThatCouldNotBeRead_MakesTheMachineUNDECIDABLE_NotEmpty()
    {
        // THE KNOWN-BAD INPUT. A cc-launcher process is running and its command line could not be read,
        // so whether it belongs to this install is unknown. Returning the short list would answer "not
        // ours", which both callers turn into "nothing to stop" - so the uninstaller would certify a
        // clean machine over a live launcher, and the Director's swap would replace a binary out from
        // under a running process. The only honest answer is that the question could not be answered.
        var readable = new[] { new LauncherProcess(1, "/opt/cc-director/launcher/cc-launcher --managed") };

        var refused = Assert.Throws<InvalidOperationException>(
            () => InstalledLauncherProcesses.Resolve(readable, ["4242 (Access is denied)"]));

        Assert.Contains("could not be read", refused.Message);
        Assert.Contains("4242", refused.Message);
    }

    [Theory]
    // A live process in OUR session whose path could not be read might be ours: undecidable.
    [InlineData(null, false, 1, 1, InstalledLauncherProcesses.UnreadableVerdict.CouldBeOurs)]
    // Its session could not even be read, so it cannot be ruled out either.
    [InlineData(null, false, null, 1, InstalledLauncherProcesses.UnreadableVerdict.CouldBeOurs)]
    // Another logon session cannot be running this per-user install's binary.
    [InlineData(null, false, 2, 1, InstalledLauncherProcesses.UnreadableVerdict.CannotBeOurs)]
    // It has exited; it is not running from anywhere.
    [InlineData(null, true, 1, 1, InstalledLauncherProcesses.UnreadableVerdict.CannotBeOurs)]
    // It was readable after all - the caller keeps it, this path is not about it.
    [InlineData("/opt/cc-director/launcher/cc-launcher", false, 1, 1, InstalledLauncherProcesses.UnreadableVerdict.CannotBeOurs)]
    internal void AProcessWhosePathCannotBeRead_IsUndecidableOnlyWhenItCouldBeOurs(
        string? commandLine, bool hasExited, int? sessionId, int ourSession,
        InstalledLauncherProcesses.UnreadableVerdict expected)
    {
        // THE SHAPE THAT RAISED NO EXCEPTION. A null main module used to be coalesced to an empty
        // command line, which Ours then drops for having no matching prefix - the exact fail-open the
        // throw exists to close, surviving in the one case that does not throw.
        //
        // And the opposite over-correction is guarded here too: treating EVERY unreadable process as
        // possibly ours is far too broad, because the enumeration is machine-wide while the install is
        // per-user. One other signed-in user's launcher would otherwise make this user's update
        // undecidable on every pass, for ever, with nothing wrong on this user's machine.
        Assert.Equal(expected,
            InstalledLauncherProcesses.Classify(commandLine, hasExited, sessionId, ourSession));
    }

    [Fact]
    public void AnUnreadableProcessIsRefusedEvenWhenNothingElseIsRunning()
    {
        // The dangerous shape specifically: nothing readable, one unreadable. Dropped, this is an empty
        // machine - the most reassuring answer there is, and the wrong one.
        Assert.Throws<InvalidOperationException>(
            () => InstalledLauncherProcesses.Resolve([], ["4242 (Access is denied)"]));
    }
}
