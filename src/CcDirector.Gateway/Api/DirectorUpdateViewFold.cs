using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Turns what a launcher reported about its Director's update into the sentence a person reads (fleet
/// maintenance, devthrottle_internal#2021 and #2022). The launcher sends facts; the words live here, once.
/// </summary>
internal static class DirectorUpdateViewFold
{
    public static DirectorUpdateViewDto Fold(string machine, LauncherDirectorUpdateReport report)
    {
        var view = new DirectorUpdateViewDto
        {
            Machine = machine,
            InProgress = report.PassInProgress || report.AlreadyRunning || (!report.Finished && report.InstallingVersion is not null),
            Downloaded = report.StagedVersion is { Length: > 0 } staged
                ? $"{staged}, waiting to install"
                : "No newer build downloaded",
            LastResultAt = report.LastDecisionAt,
        };

        (view.Headline, view.Tone) =
            report.AlreadyRunning ? ("An update is already running on this machine.", FleetTone.Warn)
            : report.Finished ? Sentence(report.Decision, report.Decision == report.LastDecision ? report.LastVersion : null,
                report.Decision == report.LastDecision ? report.LastDetail : null)
            : report.InstallingVersion is { Length: > 0 } installing
                ? ($"Installing {installing}. The Director is restarting, which takes about a minute.", FleetTone.Ok)
            : report.PassInProgress ? ("An update is being installed now.", FleetTone.Ok)
            : report.LastDecision is { } last ? Prefixed("Last update: ", Sentence(last, report.LastVersion, report.LastDetail))
            : ("No update has been installed by this launcher yet.", FleetTone.Idle);

        return view;
    }

    private static (string, string) Prefixed(string prefix, (string Text, string Tone) sentence)
        => (prefix + sentence.Text, sentence.Tone);

    /// <summary>The sentence for one decision. Every name in <see cref="LauncherDirectorUpdateReport.KnownDecisions"/>
    /// has its own; a name this Gateway does not know says exactly that rather than borrowing another's words.</summary>
    internal static (string Text, string Tone) Sentence(string? decision, string? version, string? detail)
    {
        var v = string.IsNullOrWhiteSpace(version) ? "the new version" : version;
        var why = string.IsNullOrWhiteSpace(detail) ? "" : $" {detail}";
        return decision switch
        {
            "Applied" => ($"Updated to {v}.", FleetTone.Ok),
            "RolledBack" => ($"{v} did not start, so the previous version was put back.{why}", FleetTone.Bad),
            "Failed" => ($"The update to {v} failed.{why}", FleetTone.Bad),
            "NothingStaged" => ("Nothing to install: this machine has not downloaded a newer Director yet.", FleetTone.Idle),
            "HeldBecauseBusy" => ("Not installed: sessions are running. It installs by itself once the Director is empty.", FleetTone.Warn),
            "HeldBecauseUnknown" => ($"Not installed: the launcher could not tell whether the Director is empty.{why}", FleetTone.Warn),
            "HeldBecauseAnotherSwapIsRunning" => ("Not installed: another update is already running on this machine.", FleetTone.Warn),
            "HeldBecauseDirectorNotRunning" => ("Not installed: the Director is not running. It installs the update the next time it starts.", FleetTone.Idle),
            "HeldBecauseNoDisplay" => ($"Not installed: this machine has no display awake to start the Director on.{why}", FleetTone.Warn),
            "SkippedPinnedBadVersion" => ($"Not installed: {v} already failed to start on this machine, so it is skipped.", FleetTone.Bad),
            null => ("The launcher finished the update without saying what it decided.", FleetTone.Bad),
            _ => ($"The launcher answered with an update result this Gateway does not recognise ('{decision}'). "
                  + "The launcher is probably newer than this Gateway.", FleetTone.Bad),
        };
    }
}
