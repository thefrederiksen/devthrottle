namespace CcDirector.Core.Update;

/// <summary>
/// The rule for when a staged Director update may be installed: only when one is staged AND the
/// Director holds no sessions, so restarting into the new build cannot interrupt live work.
///
/// It lives here, alone, because two processes need it and it must not be stated twice. The launcher
/// applies it to decide whether to install (issue #1033); the fold below applies it to decide whether
/// to offer the person a "install it now" action. Two copies would drift, and the drift would show up
/// as a button that offers something the launcher will refuse to do - which is exactly the shape of
/// defect this work exists to remove.
/// </summary>
public static class UpdateApplyRule
{
    /// <summary>True when a staged update may be installed right now.</summary>
    public static bool ShouldApply(bool hasStagedUpdate, int runningSessionCount)
        => hasStagedUpdate && runningSessionCount == 0;
}

/// <summary>
/// Everything the fold is allowed to look at. Gathered by whoever is rendering - the desktop reads
/// its own session count and updater state, the Control API reads the same two - and handed in whole,
/// so the fold is a pure function of its inputs and can be tested without a machine.
/// </summary>
/// <param name="CurrentVersion">The version running right now, e.g. "1.9.0".</param>
/// <param name="AutomaticUpdatesEnabled">
/// Whether this build updates itself at all. False for development and slot builds, which have no
/// updater - saying "up to date" on one of those would be a lie of a different kind.
/// </param>
/// <param name="State">The persisted record both processes write.</param>
/// <param name="Live">A check happening at this instant, or null when nothing is in flight.</param>
/// <param name="RunningSessionCount">How many sessions this Director holds. Gates the install action.</param>
/// <param name="LauncherRunning">Whether a launcher process is running on this machine right now, read
/// from the registration file the launcher writes and the liveness of the pid in it - there is no
/// launcher port to probe (remove-the-network-port mission, phase 6). Gates offering "install it now",
/// because the launcher is what carries a restart out.</param>
/// <param name="Now">The clock, passed in so the relative times in the output are testable.</param>
public sealed record UpdateStatusFacts(
    string CurrentVersion,
    bool AutomaticUpdatesEnabled,
    UpdaterState State,
    UpdateProgress? Live,
    int RunningSessionCount,
    bool LauncherRunning,
    DateTimeOffset Now);

/// <summary>
/// A finished update status: the words to show, the colors to show them in, and which actions are
/// offered. Every field is the answer, not an input to one - see <see cref="UpdateStatusFold"/>.
/// </summary>
/// <param name="State">
/// A stable token naming the situation, for tests and for API consumers that want to group machines.
/// It is NOT for a client to branch on to decide what to render; everything renderable is already
/// in the other fields.
/// </param>
/// <param name="Headline">The short line, e.g. "UP TO DATE".</param>
/// <param name="Detail">The second line, e.g. "v1.9.0 - checked 4 minutes ago".</param>
/// <param name="Tooltip">The whole story in sentences, for a hover or a details pane.</param>
/// <param name="Accent">Text and icon color, as hex.</param>
/// <param name="Background">Panel fill, as hex.</param>
/// <param name="Border">Panel border, as hex.</param>
/// <param name="Icon">Which icon to draw: "ring", "check", "cross", or "dot". Rendering, not meaning.</param>
/// <param name="Busy">True while something is actually happening, so a client can spin something.</param>
/// <param name="PercentComplete">Download percentage when one is in flight and its size is known.</param>
/// <param name="CanCheckNow">Whether to offer a check, and whether to accept one.</param>
/// <param name="CheckNowLabel">What to call that action. Null when it is not offered.</param>
/// <param name="CanInstallNow">Whether to offer installing a staged build immediately.</param>
/// <param name="InstallNowLabel">What to call that action. Null when it is not offered.</param>
public sealed record UpdateStatusView(
    string State,
    string Headline,
    string Detail,
    string Tooltip,
    string Accent,
    string Background,
    string Border,
    string Icon,
    bool Busy,
    int? PercentComplete,
    bool CanCheckNow,
    string? CheckNowLabel,
    bool CanInstallNow,
    string? InstallNowLabel);

/// <summary>
/// THE one place that decides what an update state means (issue #1030).
///
/// Auto-update worked the whole time. It carried the owner's machine from 1.8.0 to 1.8.6 and never
/// said a word about it, and on 2026-07-29 he concluded it was broken. He was right to: up to date,
/// has-not-checked-yet, downloading, downloaded-and-waiting-for-a-restart, and a check that failed
/// because a release's downloads had not attached yet all rendered as one thing - a version number
/// that did not change. Five situations, one appearance. A feature nobody can observe is a feature
/// nobody can trust, and no amount of it working fixes that.
///
/// So this is a SURFACING job over a model that already exists and is already written down. It
/// invents no states. It reads what the Director's check concluded and what the launcher's install
/// pass decided, and turns the pair into finished words, finished colors, and the finished list of
/// actions that are actually available.
///
/// WHY IT IS SHAPED AS A FOLD (critical rule 7). The client is dumb. A client that branches on a
/// state to work out what that state means will, the first time it meets a combination nobody
/// anticipated, render something PLAUSIBLE instead of something TRUE - which is how a red "voice
/// unavailable" badge once came to sit next to a "generate narration now" button that could never
/// work. So the offer of an action is computed HERE, next to the reason it is or is not possible:
/// "install it now" is offered only when a build is staged, no session would be interrupted, and
/// there is a launcher able to carry out the restart. A caller cannot get that wrong, because it is
/// never asked.
///
/// Adding a state is one edit in this file, and every surface gets it.
/// </summary>
public static class UpdateStatusFold
{
    // The palette. It matches the Gateway and tools indicators beside it, and it lives here rather
    // than in each client for the same reason the words do.
    private const string QuietAccent = "#8A8A8A", QuietBackground = "#242424", QuietBorder = "#3C3C3C";
    private const string BusyAccent = "#3B82F6", BusyBackground = "#1B2A3A", BusyBorder = "#3B82F6";
    private const string ReadyAccent = "#22C55E", ReadyBackground = "#1B3A2A", ReadyBorder = "#22C55E";
    private const string WarnAccent = "#F59E0B", WarnBackground = "#3A2A1B", WarnBorder = "#F59E0B";
    private const string ProblemAccent = "#EF4444", ProblemBackground = "#3A1B1B", ProblemBorder = "#EF4444";

    private const string CheckNow = "Check for updates now";
    private const string InstallNow = "Install it now and restart";

    /// <summary>
    /// Fold the facts into the finished status. Total: every combination of inputs produces a status,
    /// and there is no path that returns nothing - "nothing to say" was the original defect, so it is
    /// not a possible answer.
    /// </summary>
    public static UpdateStatusView Fold(UpdateStatusFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var state = facts.State;

        // 1. Something is happening RIGHT NOW. That always wins: it is the most specific thing true
        //    about this machine, and it is the one thing a person watching wants to see move.
        if (facts.Live is { } live)
        {
            switch (live.Phase)
            {
                case UpdatePhase.Checking:
                    return Busy("Checking", "CHECKING FOR UPDATES", "asking GitHub what the latest release is",
                        $"Running v{facts.CurrentVersion}. Checking whether a newer release exists.", null);

                case UpdatePhase.Downloading:
                    var percent = live.Fraction is { } fraction ? (int)Math.Round(fraction * 100) : (int?)null;
                    return Busy("Downloading", "DOWNLOADING UPDATE",
                        percent is null ? $"v{live.Version}" : $"v{live.Version} - {percent}%",
                        $"Downloading v{live.Version}. Nothing restarts while this runs.", percent);

                case UpdatePhase.Verifying:
                    return Busy("Verifying", "VERIFYING UPDATE", $"v{live.Version} - checking it downloaded intact",
                        $"Checking the download of v{live.Version} against the release manifest before trusting it.", 100);
            }
        }

        // 2. This build has no updater at all. Development and slot builds are not "up to date"; they
        //    are outside the system, and a check would do nothing, so none is offered.
        if (!facts.AutomaticUpdatesEnabled)
        {
            return new UpdateStatusView(
                State: "AutomaticUpdatesOff",
                Headline: "AUTOMATIC UPDATES OFF",
                Detail: $"v{facts.CurrentVersion} - this build does not update itself",
                Tooltip: $"Running v{facts.CurrentVersion}. This is a development or slot build, which has no updater, "
                         + "so it will not find or install new releases. An installed Director updates itself.",
                Accent: QuietAccent, Background: QuietBackground, Border: QuietBorder,
                Icon: "dot", Busy: false, PercentComplete: null,
                CanCheckNow: false, CheckNowLabel: null,
                CanInstallNow: false, InstallNowLabel: null);
        }

        var checkedPhrase = Describe(state.LastCheckedAt, facts.Now);

        // 3. A build is downloaded and waiting. What happens next is the LAUNCHER's decision, so this
        //    reports the launcher's decision rather than working one out - except for whether to offer
        //    the person the immediate route, which is gated on the shared rule plus a launcher able to
        //    carry the restart out.
        if (!string.IsNullOrEmpty(state.StagedVersion))
        {
            var staged = state.StagedVersion;
            var decision = DecisionAbout(state, staged);
            var launcherAvailable = facts.LauncherRunning;
            var canInstall = UpdateApplyRule.ShouldApply(hasStagedUpdate: true, facts.RunningSessionCount)
                             && launcherAvailable;

            // Held for sessions is decided on the count RIGHT NOW, not on the launcher's last decision.
            // A "held because busy" recorded an hour ago is a true record that has since become a false
            // statement about the machine - reading it as current would announce that the update is
            // waiting for zero sessions to finish.
            if (facts.RunningSessionCount > 0)
            {
                var sessions = facts.RunningSessionCount == 1 ? "1 session" : $"{facts.RunningSessionCount} sessions";
                return new UpdateStatusView(
                    State: "StagedWaitingForSessions",
                    Headline: "UPDATE WAITING",
                    Detail: $"v{staged} installs when your {sessions} finish",
                    Tooltip: $"v{staged} is downloaded and verified. It installs automatically once this Director has no "
                             + $"sessions running - you have {sessions} open. No session is ever interrupted to update, "
                             + "so there is nothing to do but finish or close them.",
                    Accent: ReadyAccent, Background: ReadyBackground, Border: ReadyBorder,
                    Icon: "check", Busy: false, PercentComplete: null,
                    // Deliberately NOT offered: taking it would end the very sessions the hold exists to protect.
                    CanCheckNow: false, CheckNowLabel: null,
                    CanInstallNow: false, InstallNowLabel: null);
            }

            // The machine cannot draw, so it cannot start a Director. Said plainly, because the version
            // number simply not moving is what made this whole area untrustworthy, and because the person
            // reading it can fix this one in a second by touching the keyboard.
            if (decision == "HeldBecauseNoDisplay")
                return new UpdateStatusView(
                    State: "StagedWaitingForDisplay",
                    Headline: "UPDATE WAITING",
                    Detail: $"v{staged} downloaded - waiting for a display to be awake",
                    Tooltip: $"v{staged} is downloaded and verified. It has not been installed because this machine "
                             + "had no display awake when the launcher last looked, and a Director cannot start "
                             + "without one - it would die before its first window and be blamed for it. Nothing is "
                             + "wrong with the update and it has not been pinned. Waking the screen is enough; the "
                             + "next pass installs it.",
                    Accent: WarnAccent, Background: WarnBackground, Border: WarnBorder,
                    Icon: "check", Busy: false, PercentComplete: null,
                    CanCheckNow: false, CheckNowLabel: null,
                    // Deliberately NOT offered: it would fail in exactly the way that is being waited out.
                    CanInstallNow: false, InstallNowLabel: null);

            if (decision == "HeldBecauseUnknown")
            {
                return new UpdateStatusView(
                    State: "StagedLauncherCannotSee",
                    Headline: "UPDATE WAITING",
                    Detail: $"v{staged} downloaded - the launcher could not reach this Director",
                    Tooltip: $"v{staged} is downloaded and verified, but when the launcher last looked it could not ask "
                             + "this Director whether it was busy, so it held the update rather than guessing. It tries "
                             + "again on its next pass.",
                    Accent: WarnAccent, Background: WarnBackground, Border: WarnBorder,
                    Icon: "check", Busy: false, PercentComplete: null,
                    CanCheckNow: false, CheckNowLabel: null,
                    CanInstallNow: canInstall, InstallNowLabel: canInstall ? InstallNow : null);
            }

            var waitLine = launcherAvailable
                ? "installs automatically - no sessions are running"
                : "restart the Director to install it";
            return new UpdateStatusView(
                State: "StagedReady",
                Headline: "UPDATE READY",
                Detail: $"v{staged} downloaded - {waitLine}",
                Tooltip: launcherAvailable
                    ? $"v{staged} is downloaded and verified, and nothing is running that could be interrupted, so the "
                      + "launcher installs it on its next pass. Take the action to have it done now instead of waiting."
                    : $"v{staged} is downloaded and verified. There is no launcher running to install it, so it will be "
                      + "applied the next time this Director starts.",
                Accent: ReadyAccent, Background: ReadyBackground, Border: ReadyBorder,
                Icon: "check", Busy: false, PercentComplete: null,
                CanCheckNow: false, CheckNowLabel: null,
                CanInstallNow: canInstall, InstallNowLabel: canInstall ? InstallNow : null);
        }

        // 4. An install was attempted and did not stick. Nothing else on the machine can tell anybody
        //    this happened, which is why it is checked before the ordinary check outcomes.
        var lastDecision = state.LastApplyDecision;
        var decidedPhrase = Describe(state.LastApplyDecisionAt, facts.Now);

        if (lastDecision == "RolledBack")
            return new UpdateStatusView(
                State: "RolledBack",
                Headline: "UPDATE ROLLED BACK",
                Detail: $"v{state.LastApplyVersion} did not start - v{facts.CurrentVersion} was put back {decidedPhrase}",
                Tooltip: $"v{state.LastApplyVersion} was installed {decidedPhrase} and never came up, so the launcher "
                         + $"restored v{facts.CurrentVersion} and will not offer that build again. "
                         + (state.LastApplyDetail ?? "See the launcher log for what happened at each step."),
                Accent: ProblemAccent, Background: ProblemBackground, Border: ProblemBorder,
                Icon: "cross", Busy: false, PercentComplete: null,
                CanCheckNow: true, CheckNowLabel: CheckNow,
                CanInstallNow: false, InstallNowLabel: null);

        if (lastDecision == "Failed")
            return new UpdateStatusView(
                State: "InstallFailed",
                Headline: "UPDATE INSTALL FAILED",
                Detail: $"v{state.LastApplyVersion} could not be installed {decidedPhrase}",
                Tooltip: $"The launcher tried to install v{state.LastApplyVersion} {decidedPhrase} and the attempt failed. "
                         + (state.LastApplyDetail ?? "See the launcher log for what was left where."),
                Accent: ProblemAccent, Background: ProblemBackground, Border: ProblemBorder,
                Icon: "cross", Busy: false, PercentComplete: null,
                CanCheckNow: true, CheckNowLabel: CheckNow,
                CanInstallNow: false, InstallNowLabel: null);

        // 5. What the last completed check concluded.
        switch (state.LastCheckOutcome)
        {
            case "ReleaseNotReady":
                return new UpdateStatusView(
                    State: "ReleaseNotReady",
                    Headline: "UPDATE NOT READY YET",
                    Detail: $"v{state.LastCheckLatestVersion} is published - its download is still being built",
                    Tooltip: $"v{state.LastCheckLatestVersion} was published, but the files to download had not been "
                             + "attached to it yet when this machine looked. That takes a few minutes after a release "
                             + "goes out. Nothing is wrong; the check runs again shortly.",
                    Accent: BusyAccent, Background: BusyBackground, Border: BusyBorder,
                    Icon: "ring", Busy: false, PercentComplete: null,
                    CanCheckNow: true, CheckNowLabel: CheckNow,
                    CanInstallNow: false, InstallNowLabel: null);

            case "NoBuildForThisPlatform":
                return new UpdateStatusView(
                    State: "NoBuildForThisPlatform",
                    Headline: "NO UPDATE FOR THIS COMPUTER",
                    Detail: $"v{state.LastCheckLatestVersion} carries no build for this machine",
                    Tooltip: $"v{state.LastCheckLatestVersion} was published complete, and it contains no build for this "
                             + $"computer's operating system and processor ({state.LastCheckError ?? "no matching asset"}). "
                             + "This is not a wait - until a release carries this platform, this Director cannot update "
                             + "itself, so it is worth reporting rather than retrying.",
                    Accent: WarnAccent, Background: WarnBackground, Border: WarnBorder,
                    Icon: "cross", Busy: false, PercentComplete: null,
                    CanCheckNow: true, CheckNowLabel: CheckNow,
                    CanInstallNow: false, InstallNowLabel: null);

            case "Failed":
                return new UpdateStatusView(
                    State: "CheckFailed",
                    Headline: "UPDATE CHECK FAILED",
                    Detail: $"v{facts.CurrentVersion} - last tried {checkedPhrase}",
                    Tooltip: $"Running v{facts.CurrentVersion}. The last check for a newer release failed "
                             + $"{checkedPhrase}: {state.LastCheckError ?? "no reason was recorded"}. "
                             + "This machine does not know whether it is up to date.",
                    Accent: WarnAccent, Background: WarnBackground, Border: WarnBorder,
                    Icon: "cross", Busy: false, PercentComplete: null,
                    CanCheckNow: true, CheckNowLabel: CheckNow,
                    CanInstallNow: false, InstallNowLabel: null);

            case "UpToDate":
                // The one case that used to render as nothing at all, which is precisely why it now
                // renders as itself, with the time attached. "Up to date" without a time is a claim
                // about the past that looks like a claim about the present.
                // Worth saying while it is news - the update the person may have been waiting for landed.
                // Bounded to a day so a machine does not still be announcing an update it took last month.
                var updatedRecently = state.LastApplyDecisionAt is { } appliedAt
                                      && facts.Now - appliedAt < TimeSpan.FromHours(24);
                var updatedNote = lastDecision == "Applied"
                                  && updatedRecently
                                  && VersionMatches(state.LastApplyVersion, facts.CurrentVersion)
                    ? $" Updated to v{facts.CurrentVersion} {decidedPhrase}."
                    : "";
                return new UpdateStatusView(
                    State: "UpToDate",
                    Headline: "UP TO DATE",
                    Detail: $"v{facts.CurrentVersion} - checked {checkedPhrase}",
                    Tooltip: $"Running v{facts.CurrentVersion}, which was the latest release when this machine checked "
                             + $"{checkedPhrase}.{updatedNote}",
                    Accent: QuietAccent, Background: QuietBackground, Border: QuietBorder,
                    Icon: "check", Busy: false, PercentComplete: null,
                    CanCheckNow: true, CheckNowLabel: CheckNow,
                    CanInstallNow: false, InstallNowLabel: null);
        }

        // 6. Nothing has concluded yet - a Director that has only just started, or one whose very first
        //    check has not come back. Saying "up to date" here would be asserting something nobody has
        //    established.
        return new UpdateStatusView(
            State: "NotCheckedYet",
            Headline: "NOT CHECKED YET",
            Detail: $"v{facts.CurrentVersion} - no check has finished on this machine",
            Tooltip: $"Running v{facts.CurrentVersion}. No check for a newer release has completed yet, so whether this "
                     + "is the latest is not known. The first check runs shortly after start.",
            Accent: QuietAccent, Background: QuietBackground, Border: QuietBorder,
            Icon: "dot", Busy: false, PercentComplete: null,
            CanCheckNow: true, CheckNowLabel: CheckNow,
            CanInstallNow: false, InstallNowLabel: null);
    }

    private static UpdateStatusView Busy(string state, string headline, string detail, string tooltip, int? percent)
        => new(state, headline, detail, tooltip,
            BusyAccent, BusyBackground, BusyBorder,
            Icon: "ring", Busy: true, PercentComplete: percent,
            // A check is already running; offering to start a second one is offering a no-op.
            CanCheckNow: false, CheckNowLabel: null,
            CanInstallNow: false, InstallNowLabel: null);

    /// <summary>
    /// The launcher's decision, but only when it is about the version currently staged. A decision left
    /// over from a previous download would otherwise be read as being about this one - "held because
    /// busy" from an hour ago describing a build that has since been replaced.
    /// </summary>
    private static string? DecisionAbout(UpdaterState state, string stagedVersion)
        => VersionMatches(state.LastApplyVersion, stagedVersion) ? state.LastApplyDecision : null;

    private static bool VersionMatches(string? a, string? b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
           && string.Equals(a.TrimStart('v', 'V'), b.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A moment in plain words - "4 minutes ago", "yesterday". Public and pure so the tests can pin the
    /// wording; a timestamp nobody can read at a glance is barely better than no timestamp.
    /// </summary>
    public static string Describe(DateTimeOffset? moment, DateTimeOffset now)
    {
        if (moment is null) return "never";

        var elapsed = now - moment.Value;
        if (elapsed < TimeSpan.Zero) return "just now";          // clock moved; do not claim the future
        if (elapsed < TimeSpan.FromSeconds(90)) return "just now";
        if (elapsed < TimeSpan.FromMinutes(60)) return $"{(int)elapsed.TotalMinutes} minutes ago";
        if (elapsed < TimeSpan.FromMinutes(120)) return "an hour ago";
        if (elapsed < TimeSpan.FromHours(24)) return $"{(int)elapsed.TotalHours} hours ago";
        if (elapsed < TimeSpan.FromHours(48)) return "yesterday";
        return $"{(int)elapsed.TotalDays} days ago";
    }
}
