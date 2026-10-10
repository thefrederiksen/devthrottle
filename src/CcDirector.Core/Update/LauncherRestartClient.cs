using CcDirector.Core.Lifecycle;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Update;

/// <summary>What happened when the Director asked its launcher to restart it.</summary>
/// <param name="Ok">True when the launcher accepted the request.</param>
/// <param name="Message">One plain sentence, suitable for showing to a person either way.</param>
public sealed record LauncherRestartResult(bool Ok, string Message);

/// <summary>
/// Asks the local launcher to stop this Director, install whatever is staged, and start it again -
/// the "install it now" action behind the update status (issue #1030).
///
/// It goes THROUGH the launcher rather than restarting from inside the Director, for the reason the
/// whole update moved to the launcher in the first place (issue #1033): whatever replaces the binary
/// has to outlive the process being replaced, so a Director that restarted itself would leave nothing
/// behind able to confirm the new build came up, or to put the old one back when it did not. The
/// launcher is still there afterwards. This action is therefore not a shortcut around the launcher's
/// ownership; it is a request to do now what it was going to do on its next pass anyway.
///
/// The action is only ever OFFERED when the fold says it can be (a build is staged, no session would
/// be interrupted, and a launcher is reachable), so a failure here means the machine changed under the
/// person between the offer and the click. That is reported as itself, never swallowed.
/// </summary>
public static class LauncherRestartClient
{
    /// <summary>
    /// Ask the launcher to restart the Director. Never throws: this is behind a button, so every
    /// failure comes back as a sentence the caller can show.
    ///
    /// A NAMED SIGNAL, NOT A POST. This used to read the launcher's port and bearer token off disk and
    /// post to its loopback interface, which made an update depend on three things that have nothing to
    /// do with updating: a discovery file being current, a token file being readable, and a socket
    /// accepting connections. The signal needs none of them and cannot reach a launcher other than the
    /// one serving this machine's storage root.
    ///
    /// Returning true means the request was DELIVERED, not that the install succeeded. That is the same
    /// promise the post made - the launcher answered as soon as it accepted, and the swap happened
    /// afterwards - so nothing downstream is weaker. The install's own outcome is recorded by the
    /// launcher in the updater state, which is where the status display reads it.
    /// </summary>
    public static Task<LauncherRestartResult> RequestRestartAsync(CancellationToken ct = default)
    {
        FileLog.Write("[LauncherRestartClient] RequestRestartAsync");
        try
        {
            var signal = LifecycleSignalNames.LauncherRestartDirector();
            if (!LifecycleSignal.Raise(signal))
            {
                FileLog.Write($"[LauncherRestartClient] nothing is listening for {signal}");
                return Task.FromResult(new LauncherRestartResult(false,
                    "Could not ask the launcher to restart the Director: no launcher is running on this machine. "
                    + "Closing and reopening the Director installs the update instead."));
            }

            FileLog.Write("[LauncherRestartClient] the launcher was asked to restart the Director");
            return Task.FromResult(new LauncherRestartResult(true,
                "Installing the update now - the Director will restart."));
        }
        catch (Exception ex)
        {
            // Not a FAILED line: the window shows this result and reports it (issue #3675), and one failure is one
            // report. The exception stays in the log here.
            FileLog.Write($"[LauncherRestartClient] RequestRestartAsync: the request threw, and the window reports it: {ex}");
            return Task.FromResult(new LauncherRestartResult(false,
                $"Could not ask the launcher to restart the Director: {ex.Message}. Closing and reopening the "
                + "Director installs the update instead."));
        }
    }
}
