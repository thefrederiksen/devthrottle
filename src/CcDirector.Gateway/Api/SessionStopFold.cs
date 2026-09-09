using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE fold for stopping a session (mission "Stop a session", Ruling 5). Every sentence any surface will
/// ever show about a stop is composed HERE and nowhere else - the command line, the Cockpit, the Director
/// window and the phone all render these strings verbatim.
///
/// It is pure and it takes no <c>HttpContext</c>, so it is tested directly with no server standing up, the
/// way <c>VoiceDisplayFold</c> and <see cref="NetworkConnectionVerdictFold"/> are. That is not a matter of
/// convenience: the house rule is that the client is dumb and the Gateway owns all ruling, and a fold that
/// can only be exercised through a live route is a fold nobody writes enough cases for. Adding a new stop
/// state is one edit in this file, never a new branch in four clients.
///
/// WHAT IT DELIBERATELY DOES NOT KNOW. It never names a client's flags. The Gateway does not know what the
/// command line calls its options, so <see cref="ReasonMissing"/> says that a reason is what is missing and
/// stops there; the command line prints that sentence and adds the one thing only it knows - how to type it.
/// </summary>
internal static class SessionStopFold
{
    /// <summary>
    /// The sentence returned with a 400 when a stop arrives with no reason, or a blank one (Ruling 4). It
    /// names the REASON as the thing that is missing, in plain words, and names no flag - see the class
    /// note above for why that line is drawn here.
    /// </summary>
    internal const string ReasonMissing =
        "A stop needs a reason. Say why this session is being stopped - it is recorded with the stop, and it "
        + "is how anyone reading the trail later knows what happened.";

    /// <summary>
    /// What the audit trail records as the detail when a stop arrived through the door that carries no
    /// reason (<c>DELETE /sessions/{sid}</c>). The trail says honestly that none was given rather than
    /// leaving the field null or - far worse - fabricating one.
    /// </summary>
    internal const string NoReasonGivenDetail =
        "no reason was given - this stop arrived on DELETE /sessions/{sid}, which carries no reason";

    /// <summary>The actor recorded when nothing authenticated the request at all.</summary>
    internal const string UnknownActor = "unknown";

    /// <summary>
    /// The short form of a session identifier, as it appears in the headline.
    ///
    /// A GUID shortens to its first eight characters, which is how this fleet writes a session identifier
    /// everywhere else. ANYTHING ELSE IS PASSED THROUGH WHOLE. The not-on-this-fleet case is reached
    /// precisely when the caller typed something no session matched, and that is often a NAME rather than an
    /// identifier - truncating it would print "nothing in this account carries the id Stop a s", which reads
    /// as a corrupted answer rather than an honest one.
    /// </summary>
    internal static string ShortIdFor(string? sessionId)
    {
        var id = (sessionId ?? "").Trim();
        if (id.Length == 0) return "";
        // Parsed rather than measured, so a GUID written with braces or with no dashes shortens to the same
        // eight characters as the canonical form instead of printing "{9c41e7a" or a different eight.
        return Guid.TryParse(id, out var guid) ? guid.ToString("D")[..8] : id;
    }

    /// <summary>
    /// Can the Director's answer be folded into a description of what it found? False when it cannot.
    ///
    /// There are two quite different reasons it cannot, and <see cref="Fold"/> keeps them apart:
    ///
    /// 1. THE DIRECTOR SAID SO ITSELF. <see cref="SessionStopVerdict.StoppedNotDescribed"/> is now a word a
    ///    CURRENT Director sends, carrying <see cref="DirectorStopResult.NotDescribedReason"/> - it looked,
    ///    and the machine would not tell it whether a process was alive, or the session carried no process
    ///    identifier to look up. That is a known, described state; it is simply not a description of a
    ///    process. This is the correction inspection 1 forced: this doc comment used to say the opposite,
    ///    that a Director never sends this word.
    /// 2. THE ANSWER IS NOT ONE THIS GATEWAY UNDERSTANDS. The Gateway and the Directors do not deploy
    ///    together - the Gateway ships in a container and a Director updates itself on each machine - so a
    ///    Gateway carrying this mission can be handed an answer from a Director that predates it and
    ///    reports only the original hardcoded <c>killed</c> / <c>removed</c> pair. It says the verb RAN; it
    ///    says nothing about what was found.
    ///
    /// THE FIRST IMPLEMENTATION REFUSED CASE 2 WITH A 502, AND THE ARCHITECT REVERSED IT. The session was
    /// stopped, and reporting a failure for an operation that succeeded is a false report - the very
    /// complaint this mission exists to fix, rebuilt inside the fix for it. Calling it
    /// <see cref="SessionStopVerdict.Stopped"/> would be the opposite error, asserting a process nobody
    /// established. So the caller is told the stop happened AND told that this machine could not describe
    /// it: <see cref="SessionStopVerdict.StoppedNotDescribed"/>.
    ///
    /// This is only ever consulted on an answer the tunnel returned OK, which is what makes "the verb ran"
    /// safe to say. A tunnel failure is a different thing entirely and never reaches here.
    /// </summary>
    internal static bool CanDescribe(DirectorStopResult? answer)
    {
        // No body at all, or a body this Gateway could not parse. The verb ran; nothing describes it.
        if (answer is null) return false;

        // A verdict word this Gateway does not know - the shape an older Director answers with, since it
        // sets no verdict at all.
        var verdict = answer.Verdict ?? "";
        if (verdict is not (SessionStopVerdict.Stopped
            or SessionStopVerdict.AlreadyStopped
            or SessionStopVerdict.StoppedNotDescribed)) return false;

        // A KNOWN word, and still not a description: the Director is telling this Gateway, in its own
        // sentence, what it could not establish. Fold reads that sentence rather than assuming an old
        // Director, and carries through the facts the stop DID establish.
        if (verdict == SessionStopVerdict.StoppedNotDescribed) return false;

        // It claims it ended a running process but will not say which one, so the headline could not name
        // the process it is supposed to name.
        if (verdict == SessionStopVerdict.Stopped && answer.ProcessId is null) return false;

        return true;
    }

    /// <summary>
    /// Fold the Director's answer into the finished words.
    ///
    /// An answer that does not pass <see cref="CanDescribe"/> is NOT refused and NOT guessed at: it folds to
    /// <see cref="SessionStopVerdict.StoppedNotDescribed"/>, which says the stop happened and says that this
    /// machine could not describe it. Reporting a failure for a stop that succeeded, which is what refusing
    /// did, is the same false report this mission exists to remove; inventing a plausible line for a state
    /// nothing established is the other half of it.
    /// </summary>
    /// <param name="sessionId">The identifier the stop was asked for, exactly as the caller gave it.</param>
    /// <param name="answer">What the owning Director found and did.</param>
    /// <param name="reason">The reason the caller gave, or null on the door that carries none.</param>
    /// <param name="stoppedBy">Who asked, as it is recorded in the audit trail.</param>
    internal static SessionStopResponse Fold(
        string sessionId, DirectorStopResult answer, string? reason, string? stoppedBy)
    {
        if (answer is null) throw new ArgumentNullException(nameof(answer));

        var shortId = ShortIdFor(sessionId);

        // THE STOP HAPPENED; THIS ANSWER CANNOT SAY WHAT IT FOUND. Two causes, told apart in words here and
        // nowhere else - see CanDescribe. The Director's own sentence is the signal that it is the second
        // kind: a current Director that looked and could not read, or a session with no process identifier
        // to look up at all.
        var directorSaid = answer.Verdict == SessionStopVerdict.StoppedNotDescribed
            ? (answer.NotDescribedReason ?? "").Trim()
            : "";

        if (!CanDescribe(answer))
        {
            // WHAT WAS ESTABLISHED IS STILL REPORTED, and only under the Director's own sentence: it read
            // the process identifier off the row, it knows whether it removed the row, and it probed the
            // worktree - Ruling 2 makes that sentence mandatory whenever the session held a tree, and
            // blanking it would drop the one line telling the operator about uncommitted work. Under the
            // older-Director cause nothing was established at all, so every field stays empty - not because
            // it was established to be false. ProcessEnded is false in both: nothing established it.
            var known = directorSaid.Length > 0;
            var undescribed = new SessionStopResponse
            {
                Verdict = SessionStopVerdict.StoppedNotDescribed,
                Headline = known
                    ? $"stopped {shortId} - {directorSaid}"
                    : $"stopped {shortId} - the Director on that machine is an older version and could "
                        + "not say what it found, so this answer cannot name the process it ended or the "
                        + "worktree it left behind",
                SessionId = sessionId,
                ShortId = shortId,
                ProcessId = known ? answer.ProcessId : null,
                ProcessEnded = false,
                RowRemoved = known && answer.RowRemoved,
                WorktreePath = known ? answer.WorktreePath : null,
                WorktreeHadUncommittedChanges = known ? answer.WorktreeHadUncommittedChanges : null,
                Reason = reason,
                StoppedBy = stoppedBy,
                Killed = answer.Killed,
                Removed = answer.Removed,
            };
            // Under the older-Director cause there is no worktree to name, and Ruling 2's sentence must never
            // be invented. The reason line always belongs - it is the CALLER's words, which this Gateway knows.
            AddDetails(undescribed, undescribed.WorktreePath, undescribed.WorktreeHadUncommittedChanges, reason);
            return undescribed;
        }

        var headline = answer.Verdict == SessionStopVerdict.Stopped
            ? (answer.RowRemoved
                ? $"stopped {shortId} - process {answer.ProcessId} ended, row removed"
                : $"stopped {shortId} - process {answer.ProcessId} ended, no row was left to remove")
            : (answer.RowRemoved
                ? $"already stopped {shortId} - no process was running; the row it left behind has been cleared"
                : $"already stopped {shortId} - no process was running, and no row was left to remove");

        var response = new SessionStopResponse
        {
            Verdict = answer.Verdict,
            Headline = headline,
            SessionId = sessionId,
            ShortId = shortId,
            ProcessId = answer.ProcessId,
            ProcessEnded = answer.ProcessEnded,
            RowRemoved = answer.RowRemoved,
            WorktreePath = answer.WorktreePath,
            WorktreeHadUncommittedChanges = answer.WorktreeHadUncommittedChanges,
            Reason = reason,
            StoppedBy = stoppedBy,
            Killed = answer.Killed,
            Removed = answer.Removed,
        };

        AddDetails(response, answer.WorktreePath, answer.WorktreeHadUncommittedChanges, reason);
        return response;
    }

    /// <summary>
    /// The answer when no session in the account carries that identifier. A SUCCESS, not a failure
    /// (Ruling 3), and the headline says in so many words that NOTHING WAS ASKED - because claiming "it is
    /// gone" when no machine looked is the worse of the two mistakes this verdict exists to keep apart from
    /// <see cref="SessionStopVerdict.AlreadyStopped"/>, where a machine WAS asked and did look.
    ///
    /// One line, no line break: the command line prints the headline as one line.
    /// </summary>
    internal static SessionStopResponse NotOnFleet(string sessionId, string? reason, string? stoppedBy)
    {
        var shortId = ShortIdFor(sessionId);
        var response = new SessionStopResponse
        {
            Verdict = SessionStopVerdict.NotOnFleet,
            Headline = $"not on this fleet - nothing in this account carries the id {shortId}, so no machine "
                + "was asked and no machine's processes were searched",
            SessionId = sessionId,
            ShortId = shortId,
            // StoppedBy is who ASKED. Nothing was stopped here and no audit row is written for this verdict
            // (see the route), so this is the one case where the field is not also a row in the trail.
            StoppedBy = stoppedBy,
            Reason = reason,
        };

        // No worktree line: there is no session, so there is no worktree, and inventing "no worktree" would
        // be a claim about a machine nobody asked.
        AddDetails(response, worktreePath: null, worktreeHadUncommittedChanges: null, reason: reason);
        return response;
    }

    /// <summary>
    /// The further lines, in the order they are shown: the worktree line first (Ruling 2 REQUIRES it
    /// whenever the session held one), then the reason.
    ///
    /// THE UNKNOWN CASE IS NOT THE CLEAN CASE. A null <paramref name="worktreeHadUncommittedChanges"/> means
    /// the probe could not answer, and it gets its own sentence - never the "had no uncommitted changes" one.
    /// Reporting a failed probe as clean is the single thing most likely to be got wrong here, because both
    /// values are falsy in every language this answer passes through.
    /// </summary>
    private static void AddDetails(
        SessionStopResponse response, string? worktreePath, bool? worktreeHadUncommittedChanges, string? reason)
    {
        if (!string.IsNullOrWhiteSpace(worktreePath))
        {
            var tail = worktreeHadUncommittedChanges switch
            {
                true => "it has uncommitted changes in it",
                false => "it had no uncommitted changes",
                null => "whether it has uncommitted changes could not be determined",
            };
            response.Details.Add($"the worktree {worktreePath} was left untouched - {tail}");
        }

        if (!string.IsNullOrWhiteSpace(reason))
            response.Details.Add($"reason: {reason}");
    }

    /// <summary>
    /// Who asked for the stop, as one honest short string, for the audit trail's actor and for
    /// <see cref="SessionStopResponse.StoppedBy"/>. Three credential shapes reach this route - a session's
    /// own key, a per-device key, and the shared machine token - and the fourth possibility is that the
    /// host-wide gate is off and NOTHING was authenticated, which is <see cref="UnknownActor"/> and never a
    /// guess at who it might have been.
    ///
    /// Pure, and given the already-resolved facts rather than the request, for the same reason the rest of
    /// this file is: the route reads them once off the items the authentication gate stamped, and this
    /// composes the words. Re-deriving an identity from raw headers in a second place is how two parsers of
    /// one request come to disagree.
    /// </summary>
    internal static string ActorFor(
        string? callingSessionId, string? deviceType, string? deviceId, bool credentialAuthenticated)
    {
        if (!string.IsNullOrWhiteSpace(callingSessionId))
            return $"session {callingSessionId.Trim()}";

        if (!string.IsNullOrWhiteSpace(deviceType))
            return string.IsNullOrWhiteSpace(deviceId)
                ? $"device {deviceType.Trim()}"
                : $"device {deviceType.Trim()} {deviceId.Trim()}";

        return credentialAuthenticated ? "machine token" : UnknownActor;
    }
}
