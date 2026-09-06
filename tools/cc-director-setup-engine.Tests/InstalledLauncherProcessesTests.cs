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

    [Fact]
    public void AnUnreadableProcessIsRefusedEvenWhenNothingElseIsRunning()
    {
        // The dangerous shape specifically: nothing readable, one unreadable. Dropped, this is an empty
        // machine - the most reassuring answer there is, and the wrong one.
        Assert.Throws<InvalidOperationException>(
            () => InstalledLauncherProcesses.Resolve([], ["4242 (Access is denied)"]));
    }
}
