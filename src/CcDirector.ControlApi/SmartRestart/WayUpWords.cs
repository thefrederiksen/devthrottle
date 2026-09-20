using System.Globalization;
using CcDirector.ControlApi.Drain;
using CcDirector.Core.AgentPlugins;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.SmartRestart;

/// <summary>
/// THE WORDS OF THE WAY UP, IN ONE PLACE - and the one rule that decides what reopening a seat would
/// really do.
///
/// A screen shows these as they are and never words a state itself, so a new state is one edit here and no
/// new branch in any window (critical rule 7 in CLAUDE.md). It is the same arrangement phase 1 made for the
/// way down in <see cref="SmartShutdownWords"/>.
/// </summary>
public static class WayUpWords
{
    /// <summary>The headline of an offer, shown at start-up. The owner's own words for it.</summary>
    public const string Headline = "A restart is available";

    /// <summary>What is said when the Gateway answered and there is nothing waiting.</summary>
    public const string NothingWaiting =
        "There is nothing waiting to come back. No smart shutdown of this Director in the last seven days is " +
        "still on offer: each one has been used already, has been cleared, or has no session left to bring " +
        "back and no saved conversation left to reopen. They are all still in File, Restart history.";

    /// <summary>What is said when the history is empty, which is not the same as unreadable.</summary>
    public const string NoHistory =
        "This Director has no restart history yet. A record is written every time it shuts its own sessions " +
        "down.";

    /// <summary>What the history is, when it has entries.</summary>
    /// <param name="count">How many records were read.</param>
    /// <param name="olderNotRead">
    /// How many older records this Director has that were NOT read, because the way up reads only the newest
    /// <see cref="DirectorWayUp.MostRecentRecordsRead"/>. Zero when the whole history was read.
    /// </param>
    /// <remarks>
    /// THE SENTENCE NEVER STATES A CAPPED READ AS A TOTAL. A Director restarted most mornings passes
    /// twenty-five records inside a month, and "This Director has 25 restart records" would then be an
    /// absence presented as a complete answer - on the one surface whose whole promise is that nothing is
    /// deleted. So when older records exist the sentence says how many are being shown AND that the rest are
    /// still on the Gateway. When none are cut off the words are exactly what they always were.
    /// </remarks>
    public static string HistoryRead(int count, int olderNotRead = 0)
    {
        if (olderNotRead > 0)
            return $"The newest {count} of this Director's {count + olderNotRead} restart records, newest " +
                   $"first. The other {olderNotRead} are older and are not read here; they are kept on the " +
                   "Gateway and nothing has been deleted.";
        return count == 1
            ? "This Director has one restart record."
            : $"This Director has {count} restart records, newest first.";
    }

    /// <summary>
    /// Why nothing could be read from the Gateway. NEVER an empty list: an empty list reads as "you have no
    /// records", and that is a lie about a Gateway that simply did not answer.
    /// </summary>
    /// <param name="reason">What went wrong, from the Gateway or the client.</param>
    public static string GatewayRefusal(string reason) =>
        $"The records of what this Director shut down could not be read ({reason}). They are kept on the " +
        "Gateway, not on this machine, so nothing can be offered until the Gateway answers. Nothing has " +
        "been lost: try again when it does, or open the restart history later.";

    /// <summary>When the shutdown was, in plain words.</summary>
    /// <param name="local">The moment, in the local time of the machine the person is at.</param>
    public static string When(DateTime local) =>
        local.ToString("d MMMM yyyy 'at' HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The line naming when the shutdown was.</summary>
    /// <param name="local">The moment, in local time.</param>
    public static string WhenLabel(DateTime local) => $"Shut down on {When(local)}.";

    /// <summary>
    /// The reason the shutdown was run, in plain words, or NULL when there was none - and a window shows
    /// nothing at all for a null.
    ///
    /// IT USED TO SAY "No reason was given." That is a line of text that tells the reader nothing he did not
    /// already know, on the one window the owner called confusing: "It's really confusing with all of those on
    /// the screen" (20 September 2026). Saying nothing when there is nothing to say is the fix, and it is the
    /// engine's to decide rather than each window's - a window that hid a sentence it did not like would be
    /// deciding what a state means (critical rule 7 in CLAUDE.md).
    /// </summary>
    /// <param name="reason">The record's own reason, or null.</param>
    public static string? ReasonLabel(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? null : $"Reason: {reason.Trim()}";

    /// <summary>How many seats are waiting to come back.</summary>
    /// <param name="owed">The count.</param>
    public static string SeatsOwedLabel(int owed) =>
        owed == 1
            ? "One session is waiting to be brought back."
            : $"{owed} sessions are waiting to be brought back.";

    /// <summary>
    /// WHAT THE MAIN LIST HOLDS, AND ONLY THAT (the owner's reading, 20 September 2026).
    ///
    /// It used to name BOTH counts in one line, above a list that then held both kinds of row. What the owner
    /// saw was "One session is waiting to be brought back" over seven rows, six of them greyed, and he could
    /// not read it: "I don't understand because when the restart comes back, why are there multiple sessions
    /// ... It's really confusing with all of those on the screen."
    ///
    /// So the two kinds are now two places: this line counts exactly the rows in the main list, and the
    /// sessions that ended without a handover are counted by <see cref="EndedSectionLabel"/> on their own
    /// section. A person reading this line and counting the rows under it gets the same number.
    /// </summary>
    /// <param name="owed">How many seats handed over and are waiting to be brought back.</param>
    public static string SeatsLabel(int owed) =>
        owed == 0
            ? "No session handed over, so there is nothing to bring back."
            : SeatsOwedLabel(owed);

    /// <summary>
    /// THE ONE LINE THE SESSIONS THAT ENDED WITHOUT A HANDOVER SIT BEHIND, or null when there are none.
    ///
    /// Ruling 10.3 still holds in full: those sessions are still listed, still unticked, and still each carry
    /// their one reopen button. They are MOVED, not dropped - behind a line that says how many there are and
    /// opens them when asked. The owner's complaint was never that they were there; it was that they drowned
    /// the one row he could act on and the date he needed.
    /// </summary>
    /// <param name="endedWithoutHandover">How many seats ended without a handover.</param>
    public static string? EndedSectionLabel(int endedWithoutHandover) => endedWithoutHandover switch
    {
        <= 0 => null,
        1 => "One session ended without a handover.",
        _ => $"{endedWithoutHandover} sessions ended without a handover.",
    };

    /// <summary>What those sessions are, said once for the section rather than repeated on every row. Null when
    /// there are none. It is true whether the section is open or shut, so opening it changes no words.</summary>
    /// <param name="endedWithoutHandover">How many seats ended without a handover.</param>
    public static string? EndedSectionDetail(int endedWithoutHandover) =>
        endedWithoutHandover <= 0
            ? null
            : "None of them comes back unless you ask; each one can have its saved conversation reopened.";

    /// <summary>
    /// WHY A RECORD HAS STOPPED APPEARING AT START-UP although it still holds something to act on: it is older
    /// than the <paramref name="days"/> the Director offers a record for. Shown in the restart history, which
    /// is where nothing is ever hidden.
    ///
    /// The owner asked for both halves of this: "there should be a way to remove old [ones]. Once you restart
    /// a session, it shouldn't be there anymore, I think, or they should timeout." A record that simply stopped
    /// appearing with no sentence anywhere would be the same confusion in the other direction.
    /// </summary>
    /// <param name="days">How many days a record is offered at start-up for.</param>
    public static string TooOldToOfferLabel(int days) =>
        $"More than {days} days old, so the Director no longer offers it when it starts. Nothing has been " +
        "deleted: what it holds can still be brought back from here.";

    /// <summary>
    /// WHAT THE CLEARING ACTION DOES, said on the offer window itself beside the button.
    ///
    /// The owner's ruling asks for two things of these words: that the difference from "Not now" is obvious
    /// from the window rather than from a manual, and that it reads as "stop asking me" and never as "delete".
    /// So the sentence names the other answer and says in the same breath what is NOT done - which is
    /// everything except asking.
    /// </summary>
    public const string ClearDetail =
        "Not now asks you again the next time the Director starts. This stops it asking at all: nothing is " +
        "brought back and nothing is deleted, and the record stays in File, Restart history.";

    /// <summary>
    /// What is said once the clearing has been recorded. It says what was NOT done, because "cleared" is the
    /// word most likely to be read as "deleted", and the owner's own rule is that nothing is ever deleted.
    /// </summary>
    public const string ClearedMessage =
        "You will not be asked about this again when the Director starts. Nothing was brought back and " +
        "nothing was deleted: the record is still in File, Restart history, with every session it holds and " +
        "every button it had.";

    /// <summary>
    /// WHAT IS SAID WHEN THE RECORD COULD NOT BE MARKED, and it is the loud, safe answer: nothing was
    /// recorded, and it says so rather than pretending. Telling the owner he will not be asked again when the
    /// record carries nothing of the kind is the one outcome this must never have - he would meet the same
    /// window at the next start with no idea why.
    /// </summary>
    /// <param name="reason">The Gateway's own words for the refusal.</param>
    public static string ClearRefusedMessage(string? reason) =>
        "Nothing was changed: this record could not be marked as cleared " +
        $"({(string.IsNullOrWhiteSpace(reason) ? "no reason was given" : reason)}). Nothing has been deleted " +
        "and nothing has been brought back, and you WILL be asked about this again the next time the Director " +
        "starts. A Gateway older than this Director does not know the mark; it accepts it once it is up to date.";

    /// <summary>
    /// WHY A RECORD THAT HAS BEEN USED HAS STOPPED APPEARING AT START-UP, although it still holds something
    /// to act on. Shown in the restart history, which is where nothing is ever hidden.
    ///
    /// The owner's ruling of 20 September 2026: "as soon as we have used a restart it should no longer be
    /// offered on startup ... automatically we should only see this restart message once if we use it." A
    /// record one of whose seats has come back or been reopened has been used, whatever else it still holds -
    /// and what it still holds is reached from here, which is the second half of the same sentence.
    /// </summary>
    public const string AlreadyUsedLabel =
        "This restart has been used - a session has been brought back or reopened from it - so the Director " +
        "no longer offers it when it starts. Nothing has been deleted: whatever it still holds can be brought " +
        "back from here.";

    /// <summary>
    /// WHY A RECORD THE OWNER CLEARED HAS STOPPED APPEARING AT START-UP. His own case: "it could be that they
    /// shut down but they don't want to use it and they don't want to see it on every upstart."
    /// </summary>
    /// <param name="clearedAtLocal">When he cleared it, in local time.</param>
    public static string ClearedLabel(DateTime clearedAtLocal) =>
        $"You asked on {When(clearedAtLocal)} not to be offered this when the Director starts, so it is not " +
        "offered any more. Nothing was deleted: everything it holds can still be brought back from here.";

    /// <summary>The name of a bring back row: the mission head, and its mission when it has one.</summary>
    /// <param name="seat">The mission head.</param>
    public static string RowTitle(WorkspaceSeat seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        var mission = seat.Mission?.Name;
        return string.IsNullOrWhiteSpace(mission) ? seat.Name : $"{mission}: {seat.Name}";
    }

    /// <summary>What ticking a bring back row will do.</summary>
    /// <param name="seatsInRow">How many seats the row brings back, the head included.</param>
    /// <param name="headIsOwed">Whether the mission head itself is one of them.</param>
    public static string BringBackRowDetail(int seatsInRow, bool headIsOwed)
    {
        var what = seatsInRow == 1
            ? "Brings back one session, reading its own handover."
            : $"Brings back {seatsInRow} sessions, leads first, each reading its own handover.";
        return headIsOwed
            ? what
            : what + " The lead of this mission is not coming back, so these sessions come back under " +
                     "whoever the record says owns them.";
    }

    /// <summary>What will happen to one seat inside a bring back row.</summary>
    /// <param name="seat">The seat.</param>
    public static string SeatDetail(WorkspaceSeat seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        var role = string.IsNullOrWhiteSpace(seat.Role) ? "" : $" ({seat.Role})";
        return string.IsNullOrWhiteSpace(seat.HandoverPath)
            ? $"{seat.Name}{role} comes back with no handover of its own."
            : $"{seat.Name}{role} comes back reading its handover.";
    }

    /// <summary>The name of a row for a seat that ended without a handover. The words are the ruling's own.</summary>
    /// <param name="seat">The seat.</param>
    public static string EndedRowTitle(WorkspaceSeat seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        return $"{seat.Name} - ended without a handover";
    }

    /// <summary>Why that seat is its own row and is not brought back with the rest.</summary>
    /// <param name="seat">The seat.</param>
    public static string EndedRowDetail(WorkspaceSeat seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        var why = string.Equals(seat.DrainState, WorkspaceDrainStates.EndedAtLimit, StringComparison.Ordinal)
            ? "It was still running when time ran out and was shut down for it."
            : "It never answered, so nothing was written for it.";
        return why + " It is not brought back with the rest, because there is no handover for it to read.";
    }

    /// <summary>
    /// WHAT REOPENING A SEAT WOULD REALLY DO, decided by the seat's AGENT. This is the one place that
    /// decides it, and every agent falls into it.
    ///
    /// Only some agents can be started on a saved conversation, and the difference is in the drivers, not
    /// here: Claude Code, Copilot and Cursor are launched with <c>--resume &lt;id&gt;</c> and Pi with
    /// <c>--session-id &lt;id&gt;</c>, while Codex, Gemini, Grok and opencode each log "ignoring resume" and
    /// start fresh. Which is which is declared by each agent's own plugin and read through
    /// <see cref="Resumes"/> - never restated here. So the words differ, and an agent this build has never
    /// heard of is worded like CODEX and not like Claude Code - a promise of a conversation that does not
    /// arrive is worse than a plain "this will be a blank session".
    ///
    /// THE SAME CALL IS MADE EITHER WAY: the seat's conversation id is always handed over, and the agent
    /// that cannot use it ignores it. There is no second way round it and there must not be one.
    ///
    /// NOT PROVEN HERE: that a reopened conversation really comes back with its context for Claude Code or
    /// for Pi. That is measured on the isolated rig, and this reports only what the code does.
    /// </summary>
    /// <param name="agent">The seat's agent, as the record holds it.</param>
    /// <param name="conversationId">The seat's saved conversation id, or null when none was recorded.</param>
    public static WayUpReopenOffer ReopenOffer(string? agent, string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return new WayUpReopenOffer(
                CanReopen: false,
                Offer: null,
                What: "No conversation was recorded for this session, so there is nothing to reopen. Start it " +
                      "again yourself when you are ready.");

        var name = AgentName(agent);
        return Resumes(agent)
            ? new WayUpReopenOffer(
                CanReopen: true,
                Offer: "Reopen its saved conversation",
                What: $"{name} is started again on this session's saved conversation, in the same repository, " +
                      "and told that it was stopped and must check the state of its work before acting.")
            : new WayUpReopenOffer(
                CanReopen: true,
                Offer: "Open a fresh session in its repository",
                What: $"{name} cannot be started on a saved conversation, so this opens a NEW, blank session in " +
                      "the same repository with none of the conversation in it. It is told that it was stopped " +
                      "and must check the state of its work before acting.");
    }

    /// <summary>
    /// Whether this agent can be STARTED on a saved conversation, READ FROM THE AGENT ITSELF: the plugin
    /// that owns the driver declares it on
    /// <see cref="AgentPluginLaunchMetadata.CanResumeSavedConversation"/>, and this asks the registry.
    ///
    /// IT IS NOT A LIST OF NAMES HERE, and it must never become one again. A hand-kept list was exactly the
    /// defect: it named Claude Code and Pi, while Copilot and Cursor - both of which pass the id to their
    /// own command line - were told their conversation was lost. A second list in a second place drifts
    /// from the drivers the day an agent changes, and nobody finds out.
    ///
    /// An agent the registry does not know at all answers false, which is the safe way round: see
    /// <see cref="ReopenOffer"/>. A promise of a conversation that does not arrive is worse than a plain
    /// "this will be a blank session".
    /// </summary>
    /// <param name="agent">The seat's agent, as the record holds it.</param>
    public static bool Resumes(string? agent) => Plugin(agent)?.Launch.CanResumeSavedConversation ?? false;

    /// <summary>The agent's name as a person reads it, from the plugin that owns it. An agent the registry
    /// does not know is named as the record spells it, rather than being renamed into something it is not.</summary>
    /// <param name="agent">The seat's agent, as the record holds it.</param>
    public static string AgentName(string? agent)
    {
        var known = Plugin(agent);
        if (known is not null) return known.DisplayName;
        return string.IsNullOrWhiteSpace(agent) ? "This agent" : agent.Trim();
    }

    /// <summary>
    /// The registered plugin for the agent a record names, or null when this build has no such agent.
    ///
    /// A record holds the agent as a STRING, so the spelling is matched loosely against the three names the
    /// plugin itself publishes - its kind, its id and its display name - which keeps "ClaudeCode", "claude"
    /// and "Claude Code" one answer without a table of spellings living here.
    /// </summary>
    /// <param name="agent">The seat's agent, as the record holds it.</param>
    private static IAgentPlugin? Plugin(string? agent)
    {
        var wanted = Normalise(agent);
        if (wanted.Length == 0) return null;
        foreach (var plugin in AgentPluginRegistry.All)
        {
            if (Normalise(plugin.Kind.ToString()) == wanted
                || Normalise(plugin.Id) == wanted
                || Normalise(plugin.DisplayName) == wanted)
                return plugin;
        }
        return null;
    }

    /// <summary>The first line a reopened session is given: one line, because a long prompt parks unsubmitted
    /// in an agent's composer.</summary>
    /// <param name="local">When the Director was shut down, in local time.</param>
    public static string ReopenPrompt(DateTime local) =>
        $"You were stopped when the Director shut down on {When(local)} and this is that same session opened " +
        "again - check the state of your work before you act on anything.";

    /// <summary>
    /// THE SEED FILE, AND IT SAYS ONLY THESE FOUR THINGS (mission document 5.3 item 14, ruling 10.6):
    /// you are a restored session; read this document; what changed while you were gone; verify the state of
    /// your work before acting.
    ///
    /// NO RULES OF CONDUCT. Not "no commits unless the owner asks", not a preamble, not a style note, not a
    /// reminder about tests. On 19 September 2026 a seed line of exactly that kind overrode a commit the
    /// session's own handover had planned, and that is why this rule exists: the handover is the session's
    /// own plan, and a line added underneath it outranks nothing.
    /// </summary>
    /// <param name="handoverPath">The seat's handover, named by its path.</param>
    /// <param name="directorName">The Director that was shut down.</param>
    /// <param name="shutdownLocal">When it was shut down, in local time.</param>
    /// <param name="reason">The owner's own reason, or null when none was given.</param>
    public static string SeedFileText(string handoverPath, string directorName, DateTime shutdownLocal, string? reason)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? "with no reason given" : $"for this reason: {reason.Trim()}";
        return
            "You are a restored session." + Environment.NewLine +
            Environment.NewLine +
            $"Read {handoverPath} - it is your own handover, written before you were shut down." + Environment.NewLine +
            Environment.NewLine +
            $"What changed while you were gone: the Director {directorName} was shut down on " +
            $"{When(shutdownLocal)} {why}, and it has restarted." + Environment.NewLine +
            Environment.NewLine +
            "Verify the state of your work before you act on anything." + Environment.NewLine;
    }

    /// <summary>The seed file's name, beside the handover it points at and never colliding with it.</summary>
    /// <param name="sessionId">The seat's captured session id.</param>
    /// <param name="sessionName">The seat's name.</param>
    public static string SeedFileName(string sessionId, string? sessionName) =>
        $"{DrainPaths.ShortId(sessionId)} - {DrainPaths.Sanitize(sessionName ?? "session")} - what changed.md";

    /// <summary>What kind of shutdown a record came from, in plain words.</summary>
    /// <param name="shutdownKind">The record's shutdown kind, or null on an older record.</param>
    public static string KindLabel(string? shutdownKind) => shutdownKind switch
    {
        WorkspaceShutdownKinds.SmartShutdown => "Smart shutdown - every session was asked to hand over.",
        WorkspaceShutdownKinds.IgnoreAll => "Shut down ignoring all sessions - nothing from it is offered back.",
        null or "" => "Captured the older way, before smart shutdowns were built.",
        _ => $"Shutdown kind '{shutdownKind}', which this build does not know.",
    };

    /// <summary>What became of a whole record, in plain words.</summary>
    /// <param name="cancelledAtLocal">When the owner cancelled and kept working, or null.</param>
    /// <param name="owed">How many seats are still waiting to be brought back.</param>
    /// <param name="backAlready">How many seats have already come back.</param>
    public static string OutcomeLabel(DateTime? cancelledAtLocal, int owed, int backAlready)
    {
        if (cancelledAtLocal is { } cancelled)
            return $"Cancelled on {When(cancelled)} - the sessions kept working, so nothing from it is offered back.";
        if (owed == 0 && backAlready == 0) return "No session was ever marked to come back.";
        if (owed == 0) return $"Every session it owed has come back ({backAlready} in all).";
        return backAlready == 0
            ? $"{SeatsOwedLabel(owed)} None has come back yet."
            : $"{backAlready} came back; {SeatsOwedLabel(owed).ToLowerInvariant()}";
    }

    /// <summary>What became of one seat, in plain words, for the history.</summary>
    /// <param name="seat">The seat.</param>
    public static string SeatOutcome(WorkspaceSeat seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        if (!string.IsNullOrWhiteSpace(seat.RestoredSessionId))
            return $"Came back as {DrainPaths.ShortId(seat.RestoredSessionId)}.";

        // DEALT WITH BY A REOPEN, which is the other way a seat leaves the offer (product issue 3230). A claim
        // with no session id is said as it is: one reopen was started and what came of it was never recorded.
        if (seat.Restore?.ReopenedAtUtc is { } reopenedAt)
        {
            var when = When(DateTime.SpecifyKind(reopenedAt, DateTimeKind.Utc).ToLocalTime());
            return string.IsNullOrWhiteSpace(seat.Restore.ReopenedSessionId)
                ? $"Its saved conversation was reopened on {when}. Which session took it was never recorded."
                : $"Its saved conversation was reopened on {when} as {DrainPaths.ShortId(seat.Restore.ReopenedSessionId)}.";
        }
        if (string.Equals(seat.DrainState, WorkspaceDrainStates.EndedAtLimit, StringComparison.Ordinal))
            return "Ended when time was up, without a handover. Its saved conversation can be reopened.";
        if (string.Equals(seat.DrainState, WorkspaceDrainStates.Unreachable, StringComparison.Ordinal))
            return "Never answered, so nothing was written for it. Its saved conversation can be reopened.";
        if (string.Equals(seat.DrainState, WorkspaceDrainStates.Covered, StringComparison.Ordinal))
            return $"Covered by its lead's handover: {seat.CoveredNote ?? "no note was written"}";
        if (string.Equals(seat.DrainState, WorkspaceDrainStates.Blocked, StringComparison.Ordinal))
            return $"Blocked, and said so: {seat.BlockedReason ?? "no reason was written"}";
        if (string.Equals(seat.DrainState, WorkspaceDrainStates.Declined, StringComparison.Ordinal))
            return "Answered, but what it wrote did not amount to a handover.";
        return seat.Restore?.Decision switch
        {
            WorkspaceRestoreDecisions.Restore => "Handed over and is waiting to be brought back.",
            WorkspaceRestoreDecisions.Close => $"Not coming back: {seat.Restore?.Why ?? "no reason was written"}",
            _ => "Nothing was decided about bringing it back.",
        };
    }

    /// <summary>What became of one seat in a bring back, in plain words.</summary>
    /// <param name="restoredSessionId">The new session's id, or null when it did not come back.</param>
    /// <param name="failure">Why it did not come back, or null.</param>
    public static string BringBackSeatOutcome(string? restoredSessionId, string? failure) =>
        string.IsNullOrWhiteSpace(restoredSessionId)
            ? $"Did not come back: {(string.IsNullOrWhiteSpace(failure) ? "no reason was recorded" : failure)}"
            : $"Came back as {DrainPaths.ShortId(restoredSessionId)}.";

    /// <summary>What a whole bring back came to, in plain words.</summary>
    /// <param name="back">How many seats came back.</param>
    /// <param name="failed">How many did not.</param>
    public static string BringBackMessage(int back, int failed)
    {
        if (failed == 0) return back == 1 ? "One session came back." : $"{back} sessions came back.";
        if (back == 0) return failed == 1 ? "The session could not be brought back." : $"None of the {failed} sessions could be brought back.";
        return $"{back} came back; {failed} could not. Each one says why beside it.";
    }

    /// <summary>
    /// One spelling of an agent's name, so that "ClaudeCode", "claude-code" and "Claude Code" are one
    /// answer. It keeps letters and digits and drops everything else; an agent this build does not know
    /// simply does not match, which is the whole point.
    /// </summary>
    /// <param name="agent">The seat's agent, as the record holds it.</param>
    private static string Normalise(string? agent) =>
        new string((agent ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
