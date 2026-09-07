using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Workspaces;

/// <summary>A workspace document breaks the content rules. Maps to HTTP 400.</summary>
public sealed class WorkspaceValidationException : Exception
{
    /// <summary>Create the exception with the message the caller is shown verbatim.</summary>
    public WorkspaceValidationException(string message) : base(message) { }
}

/// <summary>
/// The workspace content rules, enforced at the store boundary so nothing reaches the table that a later
/// reader cannot act on. The endpoints translate the exception to a status code.
///
/// The rules are deliberately about MEANING, not size alone. A workspace is the thing somebody reads
/// after a machine has been restarted, when the sessions it describes no longer exist and nobody can go
/// and check: a seat with an unknown drain state, or a "restore" decision carrying no command, is a
/// document that looks complete and cannot be acted on. Those are refused here rather than stored and
/// discovered later.
/// </summary>
public static class WorkspaceValidation
{
    /// <summary>Cap for a name, an id-like field, or a version string.</summary>
    public const int MaxShortFieldChars = 200;

    /// <summary>Cap for prose: a reason, a why, a note, an owner question.</summary>
    public const int MaxTextFieldChars = 4000;

    /// <summary>Cap for a path or a command line.</summary>
    public const int MaxPathChars = 1000;

    /// <summary>Cap for an opening prompt. A long prompt belongs in a FILE with the seat pointing at it,
    /// because a long inline prompt parks in the agent's composer unsubmitted; this cap is what makes
    /// that the only workable shape rather than merely the advised one.</summary>
    public const int MaxOpeningPromptChars = 8000;

    /// <summary>The largest fleet one Director has ever held is well under this. It exists so an
    /// authenticated caller cannot persist a ten-thousand-seat document.</summary>
    public const int MaxSeats = 500;

    /// <summary>Cap on the rolled-up owner questions.</summary>
    public const int MaxOwnerQuestions = 500;

    /// <summary>Validate a workspace id (slug).</summary>
    /// <param name="id">The candidate id.</param>
    public static void ValidateId(string? id)
    {
        // The pattern itself lives in WorkspaceSlug, beside the function that MINTS an id from a display
        // name, so the two can never disagree about what a valid id is.
        if (!WorkspaceSlug.IsValid(id))
            throw new WorkspaceValidationException(
                "A workspace id must be a lowercase slug: letters, digits, and dashes, starting with a " +
                "letter or digit, 2 to 64 characters (like \"morning-fleet\").");
    }

    /// <summary>
    /// Validate a whole document about to be stored. Throws <see cref="WorkspaceValidationException"/>
    /// naming the offending field and what a valid value looks like.
    /// </summary>
    /// <param name="doc">The document to check.</param>
    public static void Validate(WorkspaceDocument? doc)
    {
        if (doc is null)
            throw new WorkspaceValidationException("A workspace body is required.");

        ValidateId(doc.Id);

        if (string.IsNullOrWhiteSpace(doc.Name))
            throw new WorkspaceValidationException("A workspace needs a name.");

        CapLength("name", doc.Name, MaxShortFieldChars);
        CapLength("description", doc.Description, MaxTextFieldChars);
        CapLength("machine", doc.Machine, MaxShortFieldChars);
        CapLength("directorId", doc.DirectorId, MaxShortFieldChars);
        CapLength("directorName", doc.DirectorName, MaxShortFieldChars);
        CapLength("directorVersionBefore", doc.DirectorVersionBefore, MaxShortFieldChars);
        CapLength("directorVersionAfter", doc.DirectorVersionAfter, MaxShortFieldChars);
        CapLength("reason", doc.Reason, MaxTextFieldChars);
        CapLength("drivenByNote", doc.DrivenByNote, MaxTextFieldChars);

        if (!WorkspaceOrigins.All.Contains(doc.Origin))
            throw new WorkspaceValidationException(
                $"origin must be one of: {string.Join(", ", WorkspaceOrigins.All)}.");

        if (doc.Outcome is not null && !WorkspaceOutcomes.All.Contains(doc.Outcome))
            throw new WorkspaceValidationException(
                $"outcome must be one of: {string.Join(", ", WorkspaceOutcomes.All)} (or absent on an " +
                "authored workspace, which is not the record of a run).");

        // A captured workspace names the Director it came from. Without it nobody can tell later WHICH
        // machine's fleet this describes, and the whole document becomes unactionable.
        if (doc.Origin == WorkspaceOrigins.Captured && string.IsNullOrWhiteSpace(doc.DirectorId))
            throw new WorkspaceValidationException(
                "A captured workspace must name the directorId it was captured from.");

        var seats = doc.Seats ?? new List<WorkspaceSeat>();
        if (seats.Count > MaxSeats)
            throw new WorkspaceValidationException($"A workspace carries at most {MaxSeats} seats.");

        var seenSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < seats.Count; i++)
            ValidateSeat(seats[i], i, seenSessionIds);

        var questions = doc.OwnerQuestions ?? new List<WorkspaceOwnerQuestion>();
        if (questions.Count > MaxOwnerQuestions)
            throw new WorkspaceValidationException(
                $"A workspace carries at most {MaxOwnerQuestions} owner questions.");
        foreach (var q in questions)
        {
            if (string.IsNullOrWhiteSpace(q.Question))
                throw new WorkspaceValidationException("An owner question needs its text, word for word.");
            CapLength("ownerQuestions[].question", q.Question, MaxTextFieldChars);
            CapLength("ownerQuestions[].fromName", q.FromName, MaxShortFieldChars);
            CapLength("ownerQuestions[].fromSessionId", q.FromSessionId, MaxShortFieldChars);
        }

        // The restore list instructs; everything else explains. An entry naming a seat that is not in
        // this document sends whoever reads it after the restart looking for a session that was never
        // captured, and that reader is - on the evidence - a stranger with no other source.
        var restoreList = doc.RestoreAfterRestart ?? new List<string>();
        if (restoreList.Count > MaxSeats)
            throw new WorkspaceValidationException(
                $"restoreAfterRestart carries at most {MaxSeats} entries.");
        var seatIds = new HashSet<string>(
            seats.Where(s => !string.IsNullOrWhiteSpace(s.SessionId)).Select(s => s.SessionId!),
            StringComparer.OrdinalIgnoreCase);
        foreach (var id in restoreList)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new WorkspaceValidationException("restoreAfterRestart holds an empty entry.");
            if (!seatIds.Contains(id))
                throw new WorkspaceValidationException(
                    $"restoreAfterRestart names \"{id}\", which is not a seat in this workspace.");
        }
    }

    private static void ValidateSeat(WorkspaceSeat? seat, int index, HashSet<string> seenSessionIds)
    {
        if (seat is null)
            throw new WorkspaceValidationException($"seats[{index}] is empty.");

        var where = $"seats[{index}]";

        if (string.IsNullOrWhiteSpace(seat.Name))
            throw new WorkspaceValidationException($"{where} needs a name - it is restored under it.");
        if (string.IsNullOrWhiteSpace(seat.RepoPath))
            throw new WorkspaceValidationException($"{where} needs a repoPath - it is where it is restored.");
        if (string.IsNullOrWhiteSpace(seat.Agent))
            throw new WorkspaceValidationException(
                $"{where} needs an agent - a session continued on a different agent is not the same session.");

        CapLength($"{where}.name", seat.Name, MaxShortFieldChars);
        CapLength($"{where}.agent", seat.Agent, MaxShortFieldChars);
        CapLength($"{where}.model", seat.Model, MaxShortFieldChars);
        CapLength($"{where}.repoPath", seat.RepoPath, MaxPathChars);
        CapLength($"{where}.role", seat.Role, MaxShortFieldChars);
        CapLength($"{where}.reportsTo", seat.ReportsTo, MaxShortFieldChars);
        CapLength($"{where}.parentSessionId", seat.ParentSessionId, MaxShortFieldChars);
        CapLength($"{where}.workflowRunId", seat.WorkflowRunId, MaxShortFieldChars);
        CapLength($"{where}.openingPrompt", seat.OpeningPrompt, MaxOpeningPromptChars);
        CapLength($"{where}.agentArgs", seat.AgentArgs, MaxTextFieldChars);
        CapLength($"{where}.color", seat.Color, MaxShortFieldChars);
        CapLength($"{where}.claudeSessionId", seat.ClaudeSessionId, MaxShortFieldChars);
        CapLength($"{where}.claudeTranscriptPath", seat.ClaudeTranscriptPath, MaxPathChars);
        CapLength($"{where}.handoverPath", seat.HandoverPath, MaxPathChars);
        CapLength($"{where}.blockedReason", seat.BlockedReason, MaxTextFieldChars);
        CapLength($"{where}.restoredSessionId", seat.RestoredSessionId, MaxShortFieldChars);
        CapLength($"{where}.restoredSeedFile", seat.RestoredSeedFile, MaxPathChars);
        CapLength($"{where}.coveredBy", seat.CoveredBy, MaxShortFieldChars);
        CapLength($"{where}.coveredNote", seat.CoveredNote, MaxTextFieldChars);

        if (!string.IsNullOrWhiteSpace(seat.SessionId))
        {
            CapLength($"{where}.sessionId", seat.SessionId, MaxShortFieldChars);
            if (!seenSessionIds.Add(seat.SessionId!))
                throw new WorkspaceValidationException(
                    $"{where} repeats sessionId \"{seat.SessionId}\" - two seats cannot be the same session.");
        }

        if (seat.DrainState is not null && !WorkspaceDrainStates.All.Contains(seat.DrainState))
            throw new WorkspaceValidationException(
                $"{where}.drainState must be one of: {string.Join(", ", WorkspaceDrainStates.All)} " +
                "(or absent before the seat has been drained).");

        // "covered" is a real state, not a gap: the seat reported UP and its senior's document accounts
        // for it. So it must SAY which seat covers it - otherwise it is indistinguishable from a seat
        // nobody ever reached, which is the confusion the state exists to prevent.
        if (seat.DrainState == WorkspaceDrainStates.Covered && string.IsNullOrWhiteSpace(seat.CoveredBy))
            throw new WorkspaceValidationException(
                $"{where} is covered, so it must name the seat whose document accounts for it (coveredBy).");

        // A blocked seat's own words are the entire reason version one never forces: they are the
        // evidence the whole exercise exists to collect.
        if (seat.DrainState == WorkspaceDrainStates.Blocked && string.IsNullOrWhiteSpace(seat.BlockedReason))
            throw new WorkspaceValidationException(
                $"{where} is blocked, so it must say what it is blocked on, in its own words (blockedReason).");

        var restore = seat.Restore;
        if (restore is not null)
        {
            if (!WorkspaceRestoreDecisions.All.Contains(restore.Decision))
                throw new WorkspaceValidationException(
                    $"{where}.restore.decision must be one of: " +
                    $"{string.Join(", ", WorkspaceRestoreDecisions.All)}.");

            CapLength($"{where}.restore.why", restore.Why, MaxTextFieldChars);
            CapLength($"{where}.restore.command", restore.Command, MaxTextFieldChars);

            if (restore.Decision == WorkspaceRestoreDecisions.Restore
                && string.IsNullOrWhiteSpace(restore.Command))
                throw new WorkspaceValidationException(
                    $"{where} is marked for restore, so it must carry the command that brings it back.");
        }
    }

    private static void CapLength(string field, string? value, int max)
    {
        if (value is not null && value.Length > max)
            throw new WorkspaceValidationException($"{field} is too long (limit {max} characters).");
    }
}
