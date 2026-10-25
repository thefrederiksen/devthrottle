using System.Globalization;
using System.Text.RegularExpressions;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// The cause of a failed scheduled run (issue #4436): what the plain sentence is chosen by, and what a schedule's
/// failure notifications are limited by - one an hour for each cause of each schedule, so a folder that is missing and
/// a machine that is off are each told, and neither floods.
/// </summary>
public static class ScheduleFailureCauses
{
    public const string FolderMissing = "folder-missing";
    public const string MachineOffline = "machine-offline";
    public const string DirectorDidNotConnect = "director-did-not-connect";
    public const string NoMachine = "no-machine";
    public const string DirectorNotFound = "director-not-found";
    public const string DirectorDidNotAnswer = "director-did-not-answer";
    public const string ConnectionDropped = "connection-dropped";
    public const string DirectorRefused = "director-refused";
    public const string DirectorError = "director-error";
    public const string GatewayRestarted = "gateway-restarted";
    public const string GatewayWasDown = "gateway-was-down";
    public const string ReachedLate = "reached-late";
    public const string PastDeadline = "past-deadline";
    public const string FactoryAgentsOff = "factory-agents-off";
    public const string FactoriesNeedAPlan = "factories-need-a-plan";
    public const string FactoryArchived = "factory-archived";
    public const string FactorySetup = "factory-setup";
    public const string BriefNotRead = "brief-not-read";
    public const string WorkListMissing = "work-list-missing";
    public const string WorkListBusy = "work-list-busy";
    public const string StartNotKnown = "start-not-known";
    public const string DidNotReport = "did-not-report";
    public const string RanPastShift = "ran-past-its-shift";
    public const string Reported = "reported";
    public const string CheckFailed = "check-failed";
    public const string CheckNotRun = "check-not-run";
    public const string SeatLimit = "seat-limit";
    public const string PromptNotDelivered = "prompt-not-delivered";
    public const string FolderNotTrusted = "folder-not-trusted";
    public const string Other = "other";
}

/// <summary>One failed run in words: its cause, the sentence the owner reads, and the run record's own reason.</summary>
/// <param name="Cause">One of the <see cref="ScheduleFailureCauses"/>.</param>
/// <param name="Headline">One plain sentence or two: what went wrong, on which machine, and what to do. Never the
/// Director's or the runtime's own words.</param>
/// <param name="Detail">The run record's reason, unreworded, for whoever needs it; null when the run gave none.</param>
public sealed record ScheduleFailure(string Cause, string Headline, string? Detail);

/// <summary>
/// THE ONE MAPPING FROM A FAILED SCHEDULED RUN TO A SENTENCE ITS OWNER CAN ACT ON (issue #4436). A run fails along many
/// paths - the machine was off, its Director did not answer, the folder is not there, the factory is switched off, the
/// Gateway was down when it was due, its session ended without reporting - and each path writes its reason in the words
/// of the code that saw it: "director returned BadRequest: repoPath does not exist: ...", "whether it started a session
/// is not known: The Director on X did not answer within 30 seconds...". Those words are kept, as the DETAIL, and the
/// headline every surface shows - the Cockpit's runs table and schedule row, <c>schedule run</c> and
/// <c>schedule runs</c>, and the notification - is chosen here, once, from the cause.
///
/// A reason this does not recognise still gets a sentence that names the machine and says the details are below; it is
/// never shown raw as the headline. The folder sentences are <see cref="ScheduleFolderWords"/>' own (issue #4421), so a
/// save and a run that found the folder missing say the same thing.
/// </summary>
public static class ScheduleFailureWords
{
    private const string DirectorReturned = "director returned ";
    private const string NotStarted = "not started: ";
    private const string WorkListEmptyReason = "its work list did not start: " + nameof(CronWorkListOutcome.EmptyList);

    private static readonly Regex LaunchTimeout = new(@"none registered within (\d+)s", RegexOptions.CultureInvariant);
    private static readonly Regex AnswerTimeout = new(@"did not answer within (\d+) seconds", RegexOptions.CultureInvariant);
    private static readonly Regex SessionNamed = new(@"session named '([^']+)'", RegexOptions.CultureInvariant);

    /// <summary>
    /// The failed run in words, or null when <paramref name="run"/> did not fail (<see cref="CronRunHistoryStore.IsFailed"/>).
    /// <paramref name="os"/> is the system the run's machine last reported (<see cref="DirectorOs"/>), or null when it is
    /// not known; only the folder sentences use it.
    /// </summary>
    public static ScheduleFailure? Of(CronRunRecord run, string? os)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!CronRunHistoryStore.IsFailed(run)) return null;
        // A drain that found its list empty started nothing ON PURPOSE (nothing was due), though the engine records it as
        // a run that did not run: it is not a failure to tell anyone about.
        if (run.Problem == CronRunProblems.DidNotRun && (run.ResultReason ?? "").Contains(WorkListEmptyReason, StringComparison.Ordinal))
            return null;
        var machine = string.IsNullOrWhiteSpace(run.Machine) ? "its machine" : run.Machine.Trim();
        var reason = (run.ResultReason ?? "").Trim();
        var detail = reason.Length == 0 ? null : reason;
        var (cause, headline) = run.Result == CronRunResults.StartUnknown
            ? StartUnknown(reason, machine)
            : run.Problem switch
            {
                CronRunProblems.DidNotRun => DidNotRun(reason, machine, os),
                CronRunProblems.DidNotReport => (ScheduleFailureCauses.DidNotReport,
                    $"The run's session on {machine} ended without reporting how the run went. Open the session to see what it did."),
                CronRunProblems.RanPastShift => (ScheduleFailureCauses.RanPastShift,
                    $"The run's session on {machine} had not reported and was still open when its shift ended. Open it to see whether it is stuck, and close it if it is."),
                // What the session or the check wrote is its own words, so it is the detail, never the headline (the
                // issue #4436 review, finding 4): a report may hold anything, a Director's error text included.
                CronRunProblems.Reported => (ScheduleFailureCauses.Reported,
                    $"The run's session on {machine} reported a problem. Its own words are in the details."),
                CronRunProblems.CheckFailed => (ScheduleFailureCauses.CheckFailed,
                    $"The schedule's check failed on {machine}. What the check said is in the details."),
                CronRunProblems.CheckNotRun => CheckNotRun(reason, machine),
                // The agent's own words about its limit are the detail (issue #3907).
                CronRunProblems.SeatLimit => (ScheduleFailureCauses.SeatLimit,
                    $"The run's session on {machine} started, but its agent's seat is at its usage or spend limit, so no work was done. Raise the limit or wait for it to reset; the agent's own words are in the details."),
                // Why the Director could not type it is the detail (issue #3392).
                CronRunProblems.PromptNotDelivered => (ScheduleFailureCauses.PromptNotDelivered,
                    $"The run's session on {machine} started, but its prompt never reached the agent, so no work was done. Open the session to see where it stopped; the details say why the prompt was not typed."),
                _ => (ScheduleFailureCauses.Other, $"This run on {machine} went wrong. The details below say what happened."),
            };
        return new ScheduleFailure(cause, headline, detail);
    }

    /// <summary>
    /// The newest run's state on the schedule's row (issue #4436): "Last run failed: ..." for a run that failed, "Last
    /// run may not have started: ..." for one whose start is not known, and null for every other run.
    /// </summary>
    public static (string Text, string Tone)? LastRunWords(CronRunRecord? newest, string? os)
    {
        if (newest is null || Of(newest, os) is not { } failure) return null;
        return newest.Result == CronRunResults.StartUnknown
            ? ($"Last run may not have started: {failure.Headline}", "warn")
            : ($"Last run failed: {failure.Headline}", "err");
    }

    /// <summary>
    /// The note on a schedule whose failure notifications the change of default switched on (issue #4436), while it
    /// still notifies on failure; null otherwise.
    /// </summary>
    public static string? NotifyNote(string? notifyOn, DateTime? defaultedUtc)
    {
        if (defaultedUtc is not { } at || CronNotify.Normalize(notifyOn) != CronNotify.Failure) return null;
        return $"Failure notifications were switched on for this schedule on {at.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}, "
               + "when they became the default. To stop them, set Notify to Off.";
    }

    private static (string, string) DidNotRun(string reason, string machine, string? os)
    {
        // The folder sentences are the save's own (issue #4421).
        if (ScheduleFolderWords.MissingFolderIn(reason) is { } path)
            return (ScheduleFailureCauses.FolderMissing, $"{ScheduleFolderWords.Missing(path, machine, os)} Then edit the schedule.");

        if (reason.Contains("the launcher could not start one", StringComparison.Ordinal)
            || reason.Contains("has no registered director", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("NoSuchDirector", StringComparison.Ordinal))
            return (ScheduleFailureCauses.MachineOffline,
                $"{machine} was not reachable: DevThrottle was not running there and could not be started. Switch {machine} on and open DevThrottle on it; the next run starts as usual.");
        if (LaunchTimeout.Match(reason) is { Success: true } launch)
            return (ScheduleFailureCauses.DirectorDidNotConnect,
                $"DevThrottle was started on {machine} but did not connect within {launch.Groups[1].Value} seconds, so the run did not start. Open DevThrottle on {machine} and check that it is signed in.");
        if (reason.Contains("no target machine", StringComparison.Ordinal))
            return (ScheduleFailureCauses.NoMachine, "The schedule names no machine to run on. Edit the schedule and choose one.");
        if (reason.Contains("no Director '", StringComparison.Ordinal) || reason.Contains("names ", StringComparison.Ordinal) && reason.Contains("Directors (", StringComparison.Ordinal))
            return (ScheduleFailureCauses.DirectorNotFound,
                $"The DevThrottle this run had to start on is not running on {machine}, so the run did not start. Open DevThrottle on {machine}; the next run starts as usual.");
        if (AnswerTimeout.Match(reason) is { Success: true } timeout)
            return (ScheduleFailureCauses.DirectorDidNotAnswer,
                $"DevThrottle on {machine} did not answer within {timeout.Groups[1].Value} seconds, so the run did not start. Check that {machine} is awake and DevThrottle is open on it.");
        if (reason.Contains("dropped while", StringComparison.Ordinal))
            return (ScheduleFailureCauses.ConnectionDropped,
                $"The connection to {machine} dropped while the run was starting. Check that {machine} is online; the next run starts as usual.");

        if (reason.Contains("The Gateway stopped while", StringComparison.Ordinal))
            return (ScheduleFailureCauses.GatewayRestarted,
                "The DevThrottle service restarted while this run was starting, so it did not start. Nothing needs doing: the next run starts as usual.");
        if (reason.Contains("the Gateway was not running when it was due", StringComparison.Ordinal))
            return (ScheduleFailureCauses.GatewayWasDown,
                "This run was missed: the DevThrottle service was not running when it was due. Nothing needs doing: the next run starts as usual.");
        if (reason.Contains("the schedule was reached late", StringComparison.Ordinal))
            return (ScheduleFailureCauses.ReachedLate,
                "This run was skipped: the schedule was reached late, so it ran once for an earlier time instead. Nothing needs doing.");
        if (reason.Contains("only after its deadline", StringComparison.Ordinal))
            return (ScheduleFailureCauses.PastDeadline,
                "This run was skipped: it was reached only after its deadline, so it was not started late. Nothing needs doing: the next run starts as usual.");

        if (reason.Contains("factories come with the Pro and Teams plans", StringComparison.Ordinal))
            return (ScheduleFailureCauses.FactoriesNeedAPlan,
                "This factory run did not start: factories come with the Pro and Teams plans, and this account's plan does not include them. Change the plan to run it.");
        if (reason.Contains("factory agents are switched off", StringComparison.Ordinal))
            return (ScheduleFailureCauses.FactoryAgentsOff,
                "This factory run did not start: factory agents are switched off for this account. Switch them on to run it.");
        if (reason.Contains(DirectorCronSessionStarter.BriefNotRead, StringComparison.Ordinal))
            return (ScheduleFailureCauses.BriefNotRead,
                "This run did not start: its seat's brief could not be read. It is tried again at the next time; if it keeps happening, publish the seat's brief again.");

        // Every way the work-list runner says no (CronEngine.DidNotStartReason over CronWorkListOutcome).
        if (reason.Contains("its work list did not start", StringComparison.Ordinal))
        {
            if (reason.Contains("NoSuchList", StringComparison.Ordinal))
                return (ScheduleFailureCauses.WorkListMissing, "The work list this schedule drains does not exist. Edit the schedule and choose one that does.");
            if (reason.Contains("MachineBusy", StringComparison.Ordinal) || reason.Contains("AlreadyClaimed", StringComparison.Ordinal))
                return (ScheduleFailureCauses.WorkListBusy,
                    $"The work list was already being worked on {machine}, so this run did not start another. Nothing needs doing.");
        }

        // A refusal the Gateway wrote as a sentence for a person ("not started: ..."): an archived factory, a seat that is
        // not registered, a definition to publish again. Its own words ARE the plain sentence.
        if (reason.StartsWith(NotStarted, StringComparison.Ordinal) || reason.Contains(": " + NotStarted, StringComparison.Ordinal))
        {
            var refusal = reason[(reason.IndexOf(NotStarted, StringComparison.Ordinal) + NotStarted.Length)..].Trim();
            // A folder Claude Code was never told to trust is refused before the session starts (issue #3469).
            var cause = refusal.Contains(" is archived", StringComparison.Ordinal) ? ScheduleFailureCauses.FactoryArchived
                : refusal.Contains("never been told to trust it", StringComparison.Ordinal) ? ScheduleFailureCauses.FolderNotTrusted
                : ScheduleFailureCauses.FactorySetup;
            return (cause, $"This run did not start: {Sentence(refusal)}");
        }

        // The Director answered, and said no: what it said stays in the details.
        if (reason.Contains(DirectorReturned + "BadRequest", StringComparison.Ordinal)
            || reason.Contains(DirectorReturned + "Conflict", StringComparison.Ordinal)
            || reason.Contains(DirectorReturned + "NotFound", StringComparison.Ordinal))
            return (ScheduleFailureCauses.DirectorRefused,
                $"DevThrottle on {machine} refused to start the session. The details below give its reason; edit the schedule to fix it.");
        if (reason.Contains(DirectorReturned, StringComparison.Ordinal))
            return (ScheduleFailureCauses.DirectorError,
                $"DevThrottle on {machine} hit an error while starting the session. If it happens again, restart DevThrottle on {machine}.");

        return (ScheduleFailureCauses.Other,
            $"This run did not start on {machine}. The details below say why.");
    }

    private static (string, string) StartUnknown(string reason, string machine)
    {
        // A start the Gateway gave up looking for, once its window closed (CronRunResultService.SettleUnknownStarts).
        if (reason.Contains("when its window closed", StringComparison.Ordinal))
        {
            var named = SessionNamed.Match(reason) is { Success: true } m ? $" named '{m.Groups[1].Value}'" : "";
            return (ScheduleFailureCauses.StartNotKnown,
                $"It is not known whether this run started: no session{named} appeared on {machine} before its window closed. Look on {machine}; if nothing ran, use Run now.");
        }
        if (AnswerTimeout.IsMatch(reason))
            return (ScheduleFailureCauses.DirectorDidNotAnswer,
                $"DevThrottle on {machine} did not answer in time, so it is not known whether this run started. Look for the session on {machine}; if it is not there, use Run now.");
        if (reason.Contains("dropped while", StringComparison.Ordinal))
            return (ScheduleFailureCauses.ConnectionDropped,
                $"The connection to {machine} dropped while the run was starting, so it is not known whether it started. Look for the session on {machine}; if it is not there, use Run now.");
        if (reason.Contains("The Gateway stopped while", StringComparison.Ordinal))
            return (ScheduleFailureCauses.GatewayRestarted,
                $"The DevThrottle service restarted while this run was starting, so it is not known whether it started. Look for the session on {machine}; if it is not there, use Run now.");
        return (ScheduleFailureCauses.StartNotKnown,
            $"It is not known whether this run started on {machine}. Look for the session on {machine}; if it is not there, use Run now.");
    }

    private static (string, string) CheckNotRun(string reason, string machine)
    {
        if (reason.Contains("factories come with the Pro and Teams plans", StringComparison.Ordinal))
            return (ScheduleFailureCauses.FactoriesNeedAPlan,
                "The schedule's check did not run: factories come with the Pro and Teams plans, and this account's plan does not include them.");
        if (reason.Contains("factory agents are switched off", StringComparison.Ordinal))
            return (ScheduleFailureCauses.FactoryAgentsOff,
                "The schedule's check did not run: factory agents are switched off for this account. Switch them on to run it.");
        return (ScheduleFailureCauses.CheckNotRun,
            $"No DevThrottle on {machine} ran the schedule's check before its next time. Switch {machine} on and open DevThrottle on it.");
    }

    // A reason as a sentence: its first letter capitalised, ending in a full stop.
    private static string Sentence(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return t;
        t = char.ToUpperInvariant(t[0]) + t[1..];
        return t[^1] is '.' or '!' or '?' ? t : t + ".";
    }
}
