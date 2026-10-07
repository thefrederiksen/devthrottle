namespace CcDirector.Setup.Engine;

/// <summary>How the Complete step should read an install/update pass.</summary>
public enum InstallCompletionKind
{
    /// <summary>An update pass that found nothing to do (Director already current).</summary>
    AlreadyUpToDate,

    /// <summary>Every component that was meant to install/update did so.</summary>
    Success,

    /// <summary>At least one component did not install - the pass finished with problems.</summary>
    Problems,
}

/// <summary>
/// Pure completion-state classification for the setup wizard. Any skipped (failed) component reads
/// as <see cref="InstallCompletionKind.Problems"/> - the "finished with problems" state - so a
/// failure can never be rendered as "Everything went perfectly" or "Already Up to Date."
///
/// This lives in the shared engine, not in either wizard, because both wizards must reach the same
/// verdict from the same facts. It used to live in the Windows project alone, which is how macOS
/// came to branch for itself and say "Everything went perfectly" about a pass that had not gone
/// perfectly.
/// </summary>
public static class InstallCompletion
{
    /// <summary>
    /// Classify a finished pass. Any skipped component wins and reads as
    /// <see cref="InstallCompletionKind.Problems"/>; otherwise an update that found nothing to do is
    /// <see cref="InstallCompletionKind.AlreadyUpToDate"/> and everything else is
    /// <see cref="InstallCompletionKind.Success"/>.
    /// </summary>
    public static InstallCompletionKind Classify(int skipped, bool alreadyUpToDate)
    {
        if (skipped > 0)
            return InstallCompletionKind.Problems;
        return alreadyUpToDate ? InstallCompletionKind.AlreadyUpToDate : InstallCompletionKind.Success;
    }

    /// <summary>
    /// May the Complete screen tell the user they are ready to go?
    ///
    /// <see cref="Classify"/> answers "did this install do its job", which is a different question.
    /// A pass where every component landed can still leave a machine that cannot run anything,
    /// because there is no coding agent on it. "You're ready to go" is false then, and the screen
    /// used to say it anyway.
    ///
    /// The wizard no longer checks prerequisites - nothing it places needs anything already on the
    /// machine - so an agent being present is the only remaining fact this turns on.
    /// </summary>
    /// <param name="skipped">Components that did not install.</param>
    /// <param name="anyCodingAgentPresent">Any agent command line tool the Director drives is installed.</param>
    public static bool IsReadyToGo(int skipped, bool anyCodingAgentPresent)
        => skipped == 0 && anyCodingAgentPresent;

    /// <summary>
    /// The status a component row carries when it did not install and the install is NOT failed by it:
    /// the install finished, and this is the one thing to know about it.
    /// </summary>
    public const string WarningStatus = "Warning";

    /// <summary>
    /// Is a failure of this component a WARNING for the install rather than a failure of the install?
    ///
    /// The launcher only adds autostart: the Director runs, signs in and enrolls the machine without it (on
    /// macOS nothing in the Director's start-up, sign-in or Gateway connection reads the launcher). Five
    /// installs on one Mac placed a working Director and then told the person "Setup finished with
    /// problems" because launchd refused the launcher - and the person stopped there, with a Director that
    /// would have worked a click away. A launcher failure is therefore reported to DevThrottle and shown as a
    /// warning, and the install goes on to open the Director; once the Director is connected it is our
    /// channel to that machine, and it repairs the launcher itself. The Director and the tools are not
    /// warnings: without the Director there is nothing to open.
    /// </summary>
    /// <param name="componentId">The component id (<see cref="ComponentRegistry.Launcher"/> etc.).</param>
    public static bool FailureIsWarning(string componentId)
        => string.Equals(componentId, ComponentRegistry.Launcher.Id, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the Complete screen says under a launcher warning, after the reason. Said in the words of what the
    /// person loses and what happens next, never "launchd" or "launch agent". It does not promise a repair,
    /// because the repair only reaches a job launchd holds and refuses - the one state that cannot be a
    /// person's choice - and a report is sent so the cause is known on our side either way.
    /// </summary>
    public const string LauncherWarningExplanation =
        "DevThrottle will not start by itself when you sign in to this Mac until this is repaired. "
        + "The Director works without it: open it now. Once the Director is open and connected, it tries to repair this "
        + "on its own, and a report of what went wrong was sent to DevThrottle - there is nothing for you to type or send.";

    /// <summary>
    /// Does leaving the Complete screen WITHOUT clicking Open Director still open the Director?
    ///
    /// On a first install, yes. Close used to close the wizard and nothing else, so a person who
    /// clicked it instead of the green button was left with no window, no sign-in and a launcher
    /// hidden in the tray overflow - and the machine never connected (issue #3503). On a first install
    /// the Director is the only next step there is, so the wizard takes it for them.
    ///
    /// An update does not: that person already knows where the app is, and may have closed it on
    /// purpose to let the update run.
    /// </summary>
    /// <param name="isUpdate">Was the product already installed when the wizard started?</param>
    /// <param name="directorAlreadyOpened">Did Open Director already start it?</param>
    /// <param name="directorInstalled">Is the Director's executable on disk? A failed Director install has nothing to open.</param>
    public static bool OpensDirectorOnClose(bool isUpdate, bool directorAlreadyOpened, bool directorInstalled)
        => !isUpdate && !directorAlreadyOpened && directorInstalled;

    /// <summary>
    /// Does this pass put a DevThrottle shortcut on the desktop? On a first install, yes: the Start Menu
    /// alone left a person who closed the wizard with no icon to find their way back by (issue #3503).
    /// An update does not, so it can never put back an icon the person deleted.
    /// </summary>
    /// <param name="isUpdate">Was the product already installed when the wizard started?</param>
    public static bool CreatesDesktopShortcut(bool isUpdate) => !isUpdate;
}
