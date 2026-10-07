using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Launcher;

/// <summary>
/// Opens the Director when Windows signs in on a machine that has never signed in to DevThrottle.
///
/// The launcher autostarts at every sign-in to Windows, but it lives in the tray overflow where nobody
/// sees it, and it never opened the Director. So a person who closed the installer without clicking
/// Open Director came back after a restart to a machine with nothing on screen - no window, no
/// notice - and the account looked as if it had never installed (issue #3503). Until the first sign-in
/// the Director's sign-in screen IS the next step, so the launcher puts it on screen.
///
/// Once the machine has signed in this does nothing: what opens at start-up is then the person's choice.
/// </summary>
public static class NeverSignedInOpener
{
    /// <summary>
    /// The rule. Opens only when the launcher was started by the sign-in to Windows (not by the
    /// installer, a self-update or a person) and this machine has no Gateway credential yet.
    /// </summary>
    public static bool ShouldOpen(bool startedAtLogin, bool signedIn) => startedAtLogin && !signedIn;

    /// <summary>
    /// Apply the rule to this machine. Returns true when a Director was started. Does file reads and
    /// starts a process, so callers run it off the user-interface thread.
    /// </summary>
    public static bool OpenIfNeverSignedIn(bool startedAtLogin, GatewayConfig config, DirectorSupervisor supervisor)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(supervisor);

        var signedIn = config.HasCredential;
        if (!ShouldOpen(startedAtLogin, signedIn))
        {
            FileLog.Write($"[NeverSignedInOpener] not opening the Director: startedAtLogin={startedAtLogin}, signedIn={signedIn}");
            return false;
        }

        if (!supervisor.DirectorExeExists)
        {
            FileLog.Write($"[NeverSignedInOpener] this machine has never signed in, but there is no Director to open at {supervisor.DirectorExePath}");
            return false;
        }

        FileLog.Write("[NeverSignedInOpener] this machine has never signed in; opening the Director so its sign-in is on screen");
        var started = supervisor.Start(out var pid);
        FileLog.Write($"[NeverSignedInOpener] Director started={started}, pid={pid}");
        return started;
    }
}
