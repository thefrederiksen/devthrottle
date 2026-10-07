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
    /// The text of the Complete screen's warning panel: one line per warning (the display name and the reason,
    /// exactly as the install runner recorded it), and under a launcher warning the explanation, built from
    /// the facts rather than from one sentence that is sometimes false. Pure; the screen renders it verbatim.
    /// </summary>
    /// <param name="warnings">The components that did not install without failing the install.</param>
    /// <param name="directorInstalled">This install placed the Director. When it did not, there is nothing to open.</param>
    public static string WarningPanelText(IReadOnlyList<InstallWarning> warnings, bool directorInstalled)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        var text = string.Join("\n", warnings.Select(w => w.Line));
        var launcher = warnings.FirstOrDefault(w => w.IsLauncher);
        return launcher is null
            ? text
            : text + "\n\n" + LauncherWarningExplanation(directorInstalled, launcher.Placed, launcher.ReportAccepted);
    }

    /// <summary>
    /// What the Complete screen says under a launcher warning, after the reason line, in the words of what the
    /// person loses and what happens next - never "launchd" or "launch agent" - and only what the facts allow.
    ///
    /// The launcher only adds autostart: on macOS nothing in the Director's start-up, sign-in or Gateway
    /// connection reads it, so a Director that is installed is the next step whatever the launcher did. The
    /// Director checks the launcher each time it starts and rebuilds a job launchd holds and refuses - so a
    /// later repair is said only when the launcher file is there for it to repair, and never when the Director
    /// itself did not install. A report is said to be with DevThrottle only when the Gateway accepted it; the
    /// Director does not send a report for a launcher it decides to leave alone, so none is promised.
    /// </summary>
    /// <param name="directorInstalled">This install placed the Director.</param>
    /// <param name="launcherPlaced">The launcher file is on disk and only its start failed; one that was never placed is put there by another install, not by the Director.</param>
    /// <param name="reportAccepted">DevThrottle accepted this install's report of the failure.</param>
    public static string LauncherWarningExplanation(bool directorInstalled, bool launcherPlaced, bool reportAccepted)
    {
        var parts = new List<string> { "DevThrottle will not start by itself when you sign in to this Mac until this is repaired." };
        if (!directorInstalled)
            parts.Add("The Director did not install either, and that comes first: nothing can be opened or repaired until it does.");
        else if (launcherPlaced)
            parts.Add("The Director works without it: open it now. Each time the Director starts it checks this again and repairs it when it can.");
        else
            parts.Add("The Director works without it: open it now. Running the installer again puts the missing part in place.");
        parts.Add(reportAccepted
            ? "DevThrottle already has the details of this failure: there is nothing for you to type or send."
            : "This install could not reach DevThrottle to report it, so the details are only in the setup log on this Mac.");
        return string.Join(" ", parts);
    }

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
