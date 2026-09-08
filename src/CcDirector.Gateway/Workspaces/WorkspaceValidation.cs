using System.Text.Json;
using CcDirector.Core.Agents;
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
/// and check: a seat with an unknown drain state, a "restore" decision carrying no command, or a restore
/// list naming a seat nobody decided to bring back, are all documents that LOOK complete and cannot be
/// acted on. Those are refused here rather than stored and discovered later.
///
/// Nothing here normalizes a broken document into a working one. A null seat list is refused, not
/// replaced with an empty one: quietly turning "the caller sent something wrong" into "the caller sent an
/// empty fleet" is how a workspace ends up recording a machine as safely empty when it was not.
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

    /// <summary>How many fields this build does not recognise one document, or one seat, may carry.</summary>
    public const int MaxUnknownFields = 50;

    /// <summary>How much unrecognised content one document, or one seat, may carry.</summary>
    public const int MaxUnknownBytes = 64 * 1024;

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

        if (doc.SchemaVersion < 1)
            throw new WorkspaceValidationException(
                $"schemaVersion must be 1 or greater (got {doc.SchemaVersion}). A HIGHER version than this " +
                "build knows is accepted on purpose - fields it does not recognise are kept verbatim and " +
                "written back - but a version below 1 is not a workspace.");

        if (string.IsNullOrWhiteSpace(doc.Name))
            throw new WorkspaceValidationException("A workspace needs a name.");

        CapLength("name", doc.Name, MaxShortFieldChars);
        CapLength("description", doc.Description, MaxTextFieldChars);
        CapLength("machine", doc.Machine, MaxShortFieldChars);
        CapLength("directorId", doc.DirectorId, MaxShortFieldChars);
        CapLength("directorName", doc.DirectorName, MaxShortFieldChars);
        CapLength("directorVersionBefore", doc.DirectorVersionBefore, MaxShortFieldChars);
        CapLength("directorVersionAfter", doc.DirectorVersionAfter, MaxShortFieldChars);
        CapLength("drivenBySessionId", doc.DrivenBySessionId, MaxShortFieldChars);
        CapLength("drivenByDirectorId", doc.DrivenByDirectorId, MaxShortFieldChars);
        ValidateIntegrity(doc.Integrity);
        CapLength("reason", doc.Reason, MaxTextFieldChars);
        CapLength("drivenByNote", doc.DrivenByNote, MaxTextFieldChars);

        if (!WorkspaceOrigins.All.Contains(doc.Origin))
            throw new WorkspaceValidationException(
                $"origin must be one of: {string.Join(", ", WorkspaceOrigins.All)}.");

        // outcome is NOT validated, because it is not an input: it is derived from directorOutcome, the
        // seat outcome and the seats themselves. There is nothing a caller can send here to be wrong
        // about, which is the whole reason it was made a view rather than a field.

        // An AUTHORED workspace is not the record of a run, so it says NOTHING about one - and this is
        // the whole list, not the two outcome fields it started as. Refusing only those two left a caller
        // able to create a clean authored workspace and then write a populated restartPerformed block
        // into it with both outcomes absent, which is a record saying a Director restarted. Every field
        // below asserts a capture, a drain, a restart or a restore, and none of them can be true of a
        // list somebody typed.
        if (doc.Origin == WorkspaceOrigins.Authored)
        {
            var claimed = new List<string>();
            if (doc.DirectorOutcome is not null) claimed.Add("directorOutcome");
            if (doc.SeatOutcome is not null) claimed.Add("seatOutcome");
            if (doc.DirectorVersionAfter is not null) claimed.Add("directorVersionAfter");
            if (doc.CompletedAtUtc is not null) claimed.Add("completedAtUtc");
            if (doc.RestartCommand is not null) claimed.Add("restartCommand");
            if (doc.LauncherUpdate is not null) claimed.Add("launcherUpdate");
            if (doc.RestartBlocked is not null) claimed.Add("restartBlocked");
            if (doc.RestartMechanism is not null) claimed.Add("restartMechanism");
            if (doc.RestartPerformed is not null) claimed.Add("restartPerformed");
            if (doc.RestoredBy is not null) claimed.Add("restoredBy");
            if (doc.RestoreAfterRestart is { Count: > 0 }) claimed.Add("restoreAfterRestart");

            var seats = doc.Seats ?? new List<WorkspaceSeat>();
            if (seats.Any(x => x?.DrainState is not null)) claimed.Add("a seat drainState");
            if (seats.Any(x => x?.HandoverPath is not null)) claimed.Add("a seat handoverPath");
            if (seats.Any(x => x?.ClosedAtUtc is not null)) claimed.Add("a seat closedAtUtc");
            if (seats.Any(x => x?.RestoredSessionId is not null)) claimed.Add("a seat restoredSessionId");
            if (seats.Any(x => x?.RestoredSeedFile is not null)) claimed.Add("a seat restoredSeedFile");
            if (seats.Any(x => x?.CoveredBy is not null)) claimed.Add("a seat coveredBy");
            if (seats.Any(x => x?.Restore is { Decision: not WorkspaceRestoreDecisions.Undecided }))
                claimed.Add("a seat restore decision");

            if (claimed.Count > 0)
                throw new WorkspaceValidationException(
                    "An authored workspace is not the record of a run, so it cannot carry " +
                    $"{string.Join(", ", claimed)}. Those are written onto a workspace captured from a " +
                    "Director (POST /gateway/workspaces).");
        }

        if (doc.DirectorOutcome is not null && !WorkspaceDirectorOutcomes.All.Contains(doc.DirectorOutcome))
            throw new WorkspaceValidationException(
                $"directorOutcome must be one of: {string.Join(", ", WorkspaceDirectorOutcomes.All)} " +
                "(or absent while the run has not reached an answer).");

        if (doc.SeatOutcome is { } seatOutcome)
        {
            if (seatOutcome.RestoredCount < 0 || seatOutcome.NotRestoredCount < 0)
                throw new WorkspaceValidationException(
                    "seatOutcome counts cannot be negative.");

            CapLength("seatOutcome.notRestoredWhy", seatOutcome.NotRestoredWhy, MaxTextFieldChars);

            // A seat that did not come back and has no reason beside it is indistinguishable from one
            // nobody noticed, which is the whole thing this field exists to prevent.
            if (seatOutcome.NotRestoredCount > 0 && string.IsNullOrWhiteSpace(seatOutcome.NotRestoredWhy))
                throw new WorkspaceValidationException(
                    "seatOutcome says seats were not restored, so it must say why (notRestoredWhy).");

            // THE COUNTS HAVE A DENOMINATOR, and it is not the size of the fleet: it is the set of seats
            // somebody DECIDED to bring back. Bounding them by the fleet size alone left the unnamed
            // state "some owed seats are not accounted for" falling into "all" - four seats owed, one
            // restored, none missing, and the record says everything came back.
            var seatsHere = doc.Seats ?? new List<WorkspaceSeat>();
            var owedSeats = seatsHere
                .Where(x => x?.Restore is { Decision: WorkspaceRestoreDecisions.Restore })
                .ToList();
            var owed = owedSeats.Count;
            var accounted = (long)seatOutcome.RestoredCount + seatOutcome.NotRestoredCount;
            if (accounted != owed)
                throw new WorkspaceValidationException(
                    $"seatOutcome accounts for {accounted} seat(s), but {owed} seat(s) in this workspace " +
                    "were decided \"restore\". Every seat that was owed has to be either restored or " +
                    "explained.");

            // THE NUMERATOR IS NAMED EVIDENCE, not a number somebody typed. Fixing the denominator alone
            // left "one seat owed, restoredCount 1, no seat naming a restored session, and an empty
            // restore list" deriving scope "all" - a record saying every owed seat came back while naming
            // none that did. A restore driver reading that stops on a false terminal answer and leaves
            // the work missing.
            var namedRestored = owedSeats.Count(x => !string.IsNullOrWhiteSpace(x!.RestoredSessionId));
            if (seatOutcome.RestoredCount != namedRestored)
                throw new WorkspaceValidationException(
                    $"seatOutcome says {seatOutcome.RestoredCount} seat(s) came back, but {namedRestored} " +
                    "seat(s) name a restoredSessionId. A seat that came back says which session it is.");

            // ...and the instruction that was run has to be there. The restore list is what somebody acts
            // on; a terminal answer reached without one is an answer about work nobody was told to do.
            var listed = new HashSet<string>(doc.RestoreAfterRestart ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            var missing = owedSeats
                .Where(x => x!.SessionId is null || !listed.Contains(x.SessionId))
                .Select(x => x!.SessionId ?? "(a seat naming no session)")
                .ToList();
            if (missing.Count > 0)
                throw new WorkspaceValidationException(
                    "seatOutcome is a terminal answer, so every seat decided \"restore\" has to be in " +
                    $"restoreAfterRestart - the list somebody acts on. These are not: {string.Join(", ", missing)}.");

            // scope is NOT validated: it is derived from the two counts above, so there is nothing a
            // caller can send that could be wrong, and nothing that could disagree with them.
        }

        // A captured workspace names the Director it came from. Without it nobody can tell later WHICH
        // machine's fleet this describes, and the whole document becomes unactionable.
        if (doc.Origin == WorkspaceOrigins.Captured && string.IsNullOrWhiteSpace(doc.DirectorId))
            throw new WorkspaceValidationException(
                "A captured workspace must name the directorId it was captured from.");

        // Every extensible object now carries an unknown bag, so every one of them is measured. A cap
        // that covered two levels while the schema had twelve was a cap in name only.
        ValidateUnknownFields("unknown", doc.Unknown);
        ValidateUnknownFields("restartCommand.unknown", doc.RestartCommand?.Unknown);
        ValidateUnknownFields("launcherUpdate.unknown", doc.LauncherUpdate?.Unknown);
        ValidateUnknownFields("restartBlocked.unknown", doc.RestartBlocked?.Unknown);
        ValidateUnknownFields("restartMechanism.unknown", doc.RestartMechanism?.Unknown);
        ValidateUnknownFields("restartPerformed.unknown", doc.RestartPerformed?.Unknown);
        ValidateUnknownFields("restartPerformed.launcherAfter.unknown", doc.RestartPerformed?.LauncherAfter?.Unknown);
        ValidateUnknownFields("restoredBy.unknown", doc.RestoredBy?.Unknown);
        ValidateUnknownFields("seatOutcome.unknown", doc.SeatOutcome?.Unknown);
        foreach (var q in doc.OwnerQuestions ?? new List<WorkspaceOwnerQuestion>())
            ValidateUnknownFields("ownerQuestions[].unknown", q?.Unknown);

        ValidateRestartBlocks(doc);

        if (doc.Seats is null)
            throw new WorkspaceValidationException("seats is required (send an empty list, not null).");
        if (doc.Seats.Count > MaxSeats)
            throw new WorkspaceValidationException($"A workspace carries at most {MaxSeats} seats.");

        var seenSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < doc.Seats.Count; i++)
            ValidateSeat(doc.Seats[i], i, seenSessionIds, doc.Origin == WorkspaceOrigins.Captured);

        if (doc.OwnerQuestions is null)
            throw new WorkspaceValidationException("ownerQuestions is required (send an empty list, not null).");
        if (doc.OwnerQuestions.Count > MaxOwnerQuestions)
            throw new WorkspaceValidationException(
                $"A workspace carries at most {MaxOwnerQuestions} owner questions.");
        for (var i = 0; i < doc.OwnerQuestions.Count; i++)
        {
            var q = doc.OwnerQuestions[i];
            if (q is null)
                throw new WorkspaceValidationException($"ownerQuestions[{i}] is empty.");
            if (string.IsNullOrWhiteSpace(q.Question))
                throw new WorkspaceValidationException(
                    $"ownerQuestions[{i}] needs its text, word for word.");
            CapLength($"ownerQuestions[{i}].question", q.Question, MaxTextFieldChars);
            CapLength($"ownerQuestions[{i}].fromName", q.FromName, MaxShortFieldChars);
            CapLength($"ownerQuestions[{i}].fromSessionId", q.FromSessionId, MaxShortFieldChars);
        }

        ValidateRestoreList(doc);
    }

    /// <summary>
    /// The restore list instructs; everything else explains. It is checked against the SEATS rather than
    /// merely for shape, because every way it can be wrong sends the person acting on it - who, on the
    /// evidence, has never seen this restart - somewhere useless:
    ///
    ///  - an id that is in no seat: looking for a session that was never captured;
    ///  - a seat whose decision is not "restore": bringing back something deliberately closed;
    ///  - a seat with no command: nothing to run;
    ///  - a duplicate: the same seat started twice, and a mission ends up with two of it.
    ///
    /// The CONVERSE is deliberately not enforced - a seat marked "restore" that is not yet in the list is
    /// allowed - because judgments are written seat by seat as each handover is read, and a document
    /// halfway through that is a real state, not a broken one.
    /// </summary>
    private static void ValidateRestoreList(WorkspaceDocument doc)
    {
        if (doc.RestoreAfterRestart is null)
            throw new WorkspaceValidationException(
                "restoreAfterRestart is required (send an empty list, not null).");
        if (doc.RestoreAfterRestart.Count > MaxSeats)
            throw new WorkspaceValidationException(
                $"restoreAfterRestart carries at most {MaxSeats} entries.");

        var byId = new Dictionary<string, WorkspaceSeat>(StringComparer.OrdinalIgnoreCase);
        foreach (var seat in doc.Seats.Where(s => !string.IsNullOrWhiteSpace(s.SessionId)))
            byId[seat.SessionId!] = seat;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in doc.RestoreAfterRestart)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new WorkspaceValidationException("restoreAfterRestart holds an empty entry.");

            if (!seen.Add(id))
                throw new WorkspaceValidationException(
                    $"restoreAfterRestart names \"{id}\" twice - it would be brought back twice.");

            if (!byId.TryGetValue(id, out var seat))
                throw new WorkspaceValidationException(
                    $"restoreAfterRestart names \"{id}\", which is not a seat in this workspace.");

            if (seat.Restore is null || seat.Restore.Decision != WorkspaceRestoreDecisions.Restore)
                throw new WorkspaceValidationException(
                    $"restoreAfterRestart names \"{id}\", whose restore decision is " +
                    $"\"{seat.Restore?.Decision ?? "none"}\" - only a seat decided \"restore\" belongs in " +
                    "the list somebody acts on after the restart.");

            if (string.IsNullOrWhiteSpace(seat.Restore.Command))
                throw new WorkspaceValidationException(
                    $"restoreAfterRestart names \"{id}\", which carries no command to bring it back.");
        }
    }

    /// <summary>
    /// Cap the strings inside the restart blocks. They are as caller-supplied as any other field, and an
    /// authenticated key that could not put a megabyte in "name" must not be able to put one in
    /// "restartBlocked.cause" instead.
    /// </summary>
    private static void ValidateRestartBlocks(WorkspaceDocument doc)
    {
        if (doc.RestartCommand is { } rc)
        {
            CapLength("restartCommand.method", rc.Method, MaxShortFieldChars);
            CapLength("restartCommand.url", rc.Url, MaxPathChars);
            CapLength("restartCommand.note", rc.Note, MaxTextFieldChars);
        }

        if (doc.LauncherUpdate is { } lu)
        {
            CapLength("launcherUpdate.reason", lu.Reason, MaxTextFieldChars);
            CapLength("launcherUpdate.from", lu.From, MaxShortFieldChars);
            CapLength("launcherUpdate.to", lu.To, MaxShortFieldChars);
            CapLength("launcherUpdate.stagedSince", lu.StagedSince, MaxShortFieldChars);
            CapLength("launcherUpdate.note", lu.Note, MaxTextFieldChars);
            CapLength("launcherUpdate.result", lu.Result, MaxTextFieldChars);
            CapLength("launcherUpdate.previousBinaryKeptAt", lu.PreviousBinaryKeptAt, MaxPathChars);
        }

        if (doc.RestartBlocked is { } rb)
        {
            CapLength("restartBlocked.state", rb.State, MaxTextFieldChars);
            CapLength("restartBlocked.cause", rb.Cause, MaxTextFieldChars);
            CapLength("restartBlocked.fix", rb.Fix, MaxTextFieldChars);
            CapLength("restartBlocked.correctedClaim", rb.CorrectedClaim, MaxTextFieldChars);
            CapLength("restartBlocked.guardVerdict", rb.GuardVerdict, MaxTextFieldChars);
            CapLength("restartBlocked.lessonForPhase0", rb.LessonForPhase0, MaxTextFieldChars);
        }

        if (doc.RestartMechanism is { } rm)
        {
            CapLength("restartMechanism.method", rm.Method, MaxTextFieldChars);
            CapLength("restartMechanism.signal", rm.Signal, MaxPathChars);
            CapLength("restartMechanism.launcherVersion", rm.LauncherVersion, MaxShortFieldChars);
            CapLength("restartMechanism.note", rm.Note, MaxTextFieldChars);
        }

        if (doc.RestartPerformed is { } rp)
        {
            CapLength("restartPerformed.atLocal", rp.AtLocal, MaxShortFieldChars);
            CapLength("restartPerformed.directorVersionAfter", rp.DirectorVersionAfter, MaxShortFieldChars);
            CapLength("restartPerformed.verifiedBy", rp.VerifiedBy, MaxTextFieldChars);
            CapLength("restartPerformed.notVerified", rp.NotVerified, MaxTextFieldChars);

            if (rp.LauncherAfter is { } la)
            {
                CapLength("restartPerformed.launcherAfter.version", la.Version, MaxShortFieldChars);
                CapLength("restartPerformed.launcherAfter.startedLocal", la.StartedLocal, MaxShortFieldChars);
                CapLength("restartPerformed.launcherAfter.note", la.Note, MaxTextFieldChars);
            }
        }

        if (doc.RestoredBy is { } rby)
        {
            CapLength("restoredBy.sessionId", rby.SessionId, MaxShortFieldChars);
            CapLength("restoredBy.name", rby.Name, MaxShortFieldChars);
            CapLength("restoredBy.note", rby.Note, MaxTextFieldChars);
            CapLength("restoredBy.method", rby.Method, MaxTextFieldChars);
        }
    }

    private static void ValidateSeat(
        WorkspaceSeat? seat, int index, HashSet<string> seenSessionIds, bool isCaptured)
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

        // The agent is checked against the agents this build can actually START, so a seat can never be
        // stored naming one that no Director could run. Without this the failure surfaces at the far end,
        // where a workspace is being started and the only choices are to guess an agent or to abandon
        // half a fleet - and guessing is what the old local-file feature did.
        // THREE checks, not one. TryParse alone accepts "999" - a value with no member - so IsDefined
        // has to follow it; and it also accepts "ClaudeCode, Codex", which parses to a REAL member by
        // combining the two, so a comma is refused before either. Any of the three alone stores an agent
        // nobody chose and hands it to the code that starts a process.
        // Capped BEFORE it is parsed, so a megabyte of nonsense is refused by its size rather than
        // echoed back inside the "not an agent this Gateway knows" message.
        CapLength($"{where}.agent", seat.Agent, MaxShortFieldChars);

        var agentName = seat.Agent.Trim();
        if (agentName.Contains(',')
            || !Enum.TryParse<AgentKind>(agentName, ignoreCase: true, out var parsedAgent)
            || !Enum.IsDefined(parsedAgent))
            throw new WorkspaceValidationException(
                $"{where}.agent is \"{seat.Agent}\", which is not an agent this Gateway knows. " +
                $"Valid agents: {string.Join(", ", Enum.GetNames<AgentKind>())}.");

        CapLength($"{where}.name", seat.Name, MaxShortFieldChars);
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

        ValidateUnknownFields($"{where}.unknown", seat.Unknown);
        ValidateUnknownFields($"{where}.mission.unknown", seat.Mission?.Unknown);
        ValidateUnknownFields($"{where}.stateAtDrain.unknown", seat.StateAtDrain?.Unknown);
        ValidateUnknownFields($"{where}.restore.unknown", seat.Restore?.Unknown);

        if (seat.Mission is { } mission)
        {
            CapLength($"{where}.mission.id", mission.Id, MaxShortFieldChars);
            CapLength($"{where}.mission.name", mission.Name, MaxShortFieldChars);
        }

        if (seat.ModelDisplay is { } md)
        {
            CapLength($"{where}.modelDisplay.kind", md.Kind, MaxShortFieldChars);
            CapLength($"{where}.modelDisplay.text", md.Text, MaxShortFieldChars);
            CapLength($"{where}.modelDisplay.modelId", md.ModelId, MaxShortFieldChars);
            CapLength($"{where}.modelDisplay.tooltip", md.Tooltip, MaxTextFieldChars);
        }

        if (seat.StateAtDrain is { } state)
        {
            CapLength($"{where}.stateAtDrain.status", state.Status, MaxShortFieldChars);
            CapLength($"{where}.stateAtDrain.activityState", state.ActivityState, MaxShortFieldChars);
            CapLength($"{where}.stateAtDrain.stateLabel", state.StateLabel, MaxShortFieldChars);
            CapLength($"{where}.stateAtDrain.triageBucket", state.TriageBucket, MaxShortFieldChars);
        }

        // A CAPTURED seat is a session that was running, so it names one. Without this the capture path
        // can store an anonymous seat that no later write can ever touch: the seat rule matches incoming
        // seats to stored ones by id, so a seat with none is unmatchable for ever.
        if (isCaptured && string.IsNullOrWhiteSpace(seat.SessionId))
            throw new WorkspaceValidationException(
                $"{where} is in a captured workspace and names no session. A capture reads running " +
                "sessions, so every seat in one has a sessionId.");

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

    /// <summary>
    /// Cap the fields this build does not know a name for.
    ///
    /// They are kept verbatim so a document from a newer build survives a round trip, and that is worth
    /// having - but "we do not know what this is" cannot mean "it is not measured". Without a cap here an
    /// authenticated caller who could not put a megabyte in "name" simply puts it in a field nobody has
    /// invented yet, and repeats it across as many ids as they like.
    /// </summary>
    private static void ValidateUnknownFields(string where, Dictionary<string, JsonElement>? unknown)
    {
        if (unknown is null || unknown.Count == 0) return;

        if (unknown.Count > MaxUnknownFields)
            throw new WorkspaceValidationException(
                $"{where} carries {unknown.Count} fields this build does not know; the limit is {MaxUnknownFields}.");

        var total = 0L;
        foreach (var (key, value) in unknown)
        {
            CapLength($"{where}[] key", key, MaxShortFieldChars);
            // BYTES, not characters: GetRawText().Length counts UTF-16 units, so a cap measured that way
            // admits far more than it says for anything that is not ASCII. The key is counted too - a
            // thousand long names is the same problem as one long value.
            total += System.Text.Encoding.UTF8.GetByteCount(key)
                     + System.Text.Encoding.UTF8.GetByteCount(value.GetRawText());
        }

        if (total > MaxUnknownBytes)
            throw new WorkspaceValidationException(
                $"{where} carries {total} bytes this build does not know; the limit is {MaxUnknownBytes}.");
    }

    private static void CapLength(string field, string? value, int max)
    {
        if (value is not null && value.Length > max)
            throw new WorkspaceValidationException($"{field} is too long (limit {max} characters).");
    }

    /// The integrity block (issue #2723). Two rules, both about a document that would LOOK safe:
    ///
    ///  - a sweep that reports clean while its own known-bad controls never fired is not evidence of
    ///    anything, and must not be stored as though it were;
    ///  - "ready to restart" while the same block carries an unresolved secret finding or an integrity
    ///    problem is the exact shape of a record somebody acts on and should not have.
    /// </summary>
    private static void ValidateIntegrity(WorkspaceIntegrity? integrity)
    {
        if (integrity is null) return;

        CapLength("integrity.notReadyReason", integrity.NotReadyReason, MaxTextFieldChars);

        if (integrity.Problems.Count > MaxSeats * 4)
            throw new WorkspaceValidationException("integrity.problems carries too many entries.");
        foreach (var p in integrity.Problems)
            CapLength("integrity.problems[]", p, MaxTextFieldChars);

        if (integrity.SweepProofFailures.Count > MaxSeats)
            throw new WorkspaceValidationException("integrity.sweepProofFailures carries too many entries.");
        foreach (var f in integrity.SweepProofFailures)
            CapLength("integrity.sweepProofFailures[]", f, MaxTextFieldChars);

        if (integrity.SecretFindings.Count > MaxSeats * 4)
            throw new WorkspaceValidationException("integrity.secretFindings carries too many entries.");
        foreach (var f in integrity.SecretFindings)
        {
            CapLength("integrity.secretFindings[].file", f.File, MaxPathChars);
            CapLength("integrity.secretFindings[].pattern", f.Pattern, MaxShortFieldChars);
            CapLength("integrity.secretFindings[].redactedExcerpt", f.RedactedExcerpt, MaxTextFieldChars);
            CapLength("integrity.secretFindings[].seatSessionId", f.SeatSessionId, MaxShortFieldChars);
        }

        if (integrity.CheckedAtUtc is not null
            && integrity.SweepPatternsTotal > 0
            && integrity.SweepPatternsProved < integrity.SweepPatternsTotal
            && integrity.SweepProofFailures.Count == 0)
            throw new WorkspaceValidationException(
                "integrity says the secret sweep proved fewer patterns than it carries but names no proof " +
                "failure. A sweep that could not be shown able to fail must say why, or its result is " +
                "indistinguishable from a clean one.");

        if (integrity.ReadyToRestart)
        {
            if (integrity.SecretFindings.Count > 0)
                throw new WorkspaceValidationException(
                    "integrity.readyToRestart is true while secretFindings is not empty. A record that " +
                    "says a restart may proceed over an unresolved secret is the one a reader trusts.");
            if (integrity.Problems.Count > 0)
                throw new WorkspaceValidationException(
                    "integrity.readyToRestart is true while integrity.problems is not empty.");
            if (integrity.SweepPatternsTotal == 0 || integrity.SweepPatternsProved < integrity.SweepPatternsTotal)
                throw new WorkspaceValidationException(
                    "integrity.readyToRestart is true but the secret sweep was never proved able to fail.");
        }
    }
}
