using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A WORKSPACE: a named set of seats, stored on the Gateway.
///
/// A restart index and a workspace are the same object (issue #2722, Phase 3 of #2719). This is the
/// insight the first real Director drain produced on 2026-09-06, and it is why this REPLACES the
/// Director-local workspace files rather than sitting beside them.
///
/// A seat is a repository plus the agent, model, role, mission, controller and opening prompt that make
/// one session. There are two ways to get a workspace and two ways to use one:
///
///   - AUTHORED by hand ("my morning fleet"), or CAPTURED from a running Director, which is exactly what
///     a drain produces (see <see cref="WorkspaceCapture"/>);
///   - started COLD, which is the regular-set-of-sessions case, or started with each seat SEEDED by its
///     own handover, which is the restart case.
///
/// It is stored on the GATEWAY and not on the machine, because it has to be readable when the machine is
/// down and editable from another computer - the machine whose fleet it describes is the machine that is
/// about to be restarted.
///
/// The schema is taken from the index.json written BY HAND during the first drain, not designed fresh:
/// that file already carried everything the operation needed, including the fields that were only added
/// after the restart and the restore had actually happened. Four things it must keep, and why:
///
///   - <see cref="WorkspaceSeat.DrainState"/> with all five values, including "covered" for a seat whose
///     senior's document accounts for it - a real state, not a gap, and no invented file for it;
///   - the per-seat <see cref="WorkspaceSeat.Restore"/> decision, reason and command;
///   - <see cref="RestoreAfterRestart"/>, the short ordered list of what to bring back. This is the field
///     that matters most: on 2026-09-06 a session with no transcript and no knowledge of the drain
///     restored the entire fleet from that one array. The schema exists so a STRANGER can finish the job;
///   - <see cref="WorkspaceSeat.RestoredSessionId"/>, <see cref="WorkspaceSeat.RestoredSeedFile"/> and
///     <see cref="RestartPerformed"/>, written back AFTER the restart. An index that stops at the drain
///     cannot say later whether the restart worked.
/// </summary>
public sealed class WorkspaceDocument
{
    /// <summary>Schema version of this document. 1 is the hand-written index's version and this one.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>The workspace's stable slug id, minted by the author (lowercase, digits, hyphens).
    /// Validated by WorkspaceValidation, never a database default.</summary>
    public string Id { get; set; } = "";

    /// <summary>The workspace's display name, as a person would say it ("morning fleet").</summary>
    public string Name { get; set; } = "";

    /// <summary>Optional free text saying what this workspace is for.</summary>
    public string? Description { get; set; }

    /// <summary>How this workspace came to exist: "authored" (written by hand) or "captured" (folded from
    /// a running Director's live sessions). See <see cref="WorkspaceOrigins"/>.</summary>
    public string Origin { get; set; } = WorkspaceOrigins.Authored;

    /// <summary>The machine the capture came from. Null on an authored workspace, which belongs to no
    /// machine and can be started on any of them.</summary>
    public string? Machine { get; set; }

    /// <summary>The Director the capture came from. Null on an authored workspace. A restarted Director
    /// gets a NEW identifier, so this names the one that WAS running.</summary>
    public string? DirectorId { get; set; }

    /// <summary>The captured Director's display name. Null on an authored workspace.</summary>
    public string? DirectorName { get; set; }

    /// <summary>The Director's version when the drain started. Null on an authored workspace.</summary>
    public string? DirectorVersionBefore { get; set; }

    /// <summary>The Director's version after the restart. Null until the restart has actually happened
    /// and somebody wrote it back.</summary>
    public string? DirectorVersionAfter { get; set; }

    /// <summary>When the capture (or drain) started.</summary>
    public DateTime? StartedAtUtc { get; set; }

    /// <summary>When the whole operation completed. Null while it is still running.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>The session that drove the drain. Its Director (<see cref="DrivenByDirectorId"/>) MUST
    /// differ from <see cref="DirectorId"/>: a driver on the target dies mid-drain and is also the only
    /// thing that performs the restore, so a drain with no surviving driver is a fleet-wide close with
    /// paperwork. Recorded rather than enforced here - this is a record, and the check belongs where the
    /// drain runs.</summary>
    public string? DrivenBySessionId { get; set; }

    /// <summary>The Director the driving session lives on. See <see cref="DrivenBySessionId"/>.</summary>
    public string? DrivenByDirectorId { get; set; }

    /// <summary>Anything that needs saying about who drove it - notably when the rule above was knowingly
    /// bent and why.</summary>
    public string? DrivenByNote { get; set; }

    /// <summary>Why the operation was run at all, in plain words ("update to 2.0.6").</summary>
    public string? Reason { get; set; }

    // THERE IS NO COARSE "outcome" FIELD, and its absence is deliberate.
    //
    // The hand-written index had one, with four words, and two of them welded two independent facts into
    // a single token: "restored" meant the Director was restarted AND the seats came back. That is why
    // the first combination nobody happened to weld - a drain that blocked, no restart, and the seats
    // that had already stopped brought back anyway - had no word at all.
    //
    // Deriving the word from the pair below was tried, and it fixed only half of it: a derived headline
    // cannot CONTRADICT the facts, but it still welds them, because both a refused restart and a session
    // that would not stop derive "blocked" - the two most confusable results a run can have. The fix for
    // that was a warning telling every future reader never to read the field alone, and a rule that has
    // to be obeyed forever by people who were not in the conversation is the weakest kind of fix there is.
    //
    // Nothing displayed it. So it is gone, while going was free, and the two orthogonal facts below are
    // the whole answer. A document written elsewhere that carries an "outcome" keeps it verbatim in
    // Unknown - not modelled, and not lost either.

    /// <summary>
    /// What happened to the DIRECTOR: one of <see cref="WorkspaceDirectorOutcomes"/>, or null while the
    /// run has not reached an answer. Independent of <see cref="SeatOutcome"/> - a Director can be
    /// restarted with nothing brought back, and seats can be brought back with no restart at all.
    /// </summary>
    public string? DirectorOutcome { get; set; }

    /// <summary>
    /// What happened to the SEATS: how many of those owed came back, how many did not, and why. Null
    /// while the run has not reached an answer. Independent of <see cref="DirectorOutcome"/>.
    /// </summary>
    public WorkspaceSeatOutcome? SeatOutcome { get; set; }

    /// <summary>The seats. Started in <see cref="WorkspaceSeat.SortOrder"/> order.</summary>
    public List<WorkspaceSeat> Seats { get; set; } = new();

    /// <summary>Every question a drained session left waiting on the owner, word for word. A question
    /// buried in a paragraph inside a session that no longer exists is a question nobody ever asks.</summary>
    public List<WorkspaceOwnerQuestion> OwnerQuestions { get; set; } = new();

    /// <summary>
    /// THE MOST IMPORTANT FIELD IN THE DOCUMENT: the short ordered list of session ids to bring back,
    /// decided during the drain, that somebody who has never seen this run can act on. Everything else
    /// explains; this instructs.
    ///
    /// IT IS NOT RESTART-ONLY, whatever its name suggests. The name is kept because it is the name in the
    /// hand-written index this schema was taken from, and it is the string a stranger has already been
    /// told to look for - but the list is "what to bring back when this run ends", and a run that BLOCKS
    /// ends without a restart while still having closed every seat that handed over cleanly. Those seats
    /// are brought back from this same list, with no restart in between - which the record says as
    /// <see cref="DirectorOutcome"/> "not-restarted" beside a <see cref="SeatOutcome"/> that counts them.
    /// There is no single word for that, and there is deliberately no field that pretends there is.
    /// </summary>
    public List<string> RestoreAfterRestart { get; set; } = new();

    /// <summary>The restart request itself, recorded so the person who runs it does not have to
    /// reconstruct it. Null on an authored workspace.</summary>
    public WorkspaceRestartCommand? RestartCommand { get; set; }

    /// <summary>What was done about the launcher's own version before the restart could happen.</summary>
    public WorkspaceLauncherUpdate? LauncherUpdate { get; set; }

    /// <summary>Set when the restart could not be performed: what blocked it and what was done. Version
    /// one never forces, so a blocked restart is a recorded outcome and not a failure to work around.</summary>
    public WorkspaceRestartBlocked? RestartBlocked { get; set; }

    /// <summary>How the restart was actually asked for - which is not always the recorded command.</summary>
    public WorkspaceRestartMechanism? RestartMechanism { get; set; }

    /// <summary>Written back AFTER the restart: what actually came back, and who checked.</summary>
    public WorkspaceRestartPerformed? RestartPerformed { get; set; }

    /// <summary>Written back AFTER the restore: which session did it and how.</summary>
    public WorkspaceRestoredBy? RestoredBy { get; set; }

    /// <summary>When this document was first stored. Stamped by the store, not the caller.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>When this document was last written. Stamped by the store, not the caller.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// Anything in the stored document this build does not know a field for, kept verbatim and written
    /// back out.
    ///
    /// This is what makes the JSON column worth having. Without it, an older Gateway reading a workspace
    /// written by a newer one would deserialize what it recognised, DROP the rest, and destroy it on the
    /// next write - which is exactly the failure the record shape was chosen to avoid, and the reason
    /// this document grew three whole blocks during one real run. A reader that cannot understand a
    /// field must still be able to hand it back.
    ///
    /// EVERY extensible object in this document carries one, not just the document and the seat. Two
    /// levels was a rule that looked complete and was not: the real hand-written index carries
    /// restore.seedPrompt, restore.workInProgress, restartCommand.body and three fields inside
    /// restartBlocked, and every one of those sits INSIDE a typed object - so a two-level rule dropped
    /// the only continuation prompt in the record and the later correction of a wrong incident
    /// classification, while the proof harness, which also looked only two levels deep, reported zero
    /// gaps. An instrument that measures the same shallow shape as the thing it measures cannot find
    /// this.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>
/// One seat in a workspace: everything needed to start this session again, plus - when the workspace was
/// captured from a drain - what the session was doing, where its handover is, and what was decided about it.
/// </summary>
public sealed class WorkspaceSeat
{
    /// <summary>The session id this seat was captured from. Null on an authored seat, which has never
    /// been a session.</summary>
    public string? SessionId { get; set; }

    /// <summary>The session's name. Restored under the same name, so the restart is invisible afterwards.</summary>
    public string Name { get; set; } = "";

    /// <summary>Which agent command line this seat runs (ClaudeCode, Codex, Pi, Gemini, Grok, ...).
    /// Restored on the SAME agent - a session continued on a different agent is not the same session.</summary>
    public string Agent { get; set; } = "";

    /// <summary>The full model id the agent's own records last reported (e.g. "claude-opus-5"), or null
    /// when none has been recorded. Sometimes empty on the Gateway record: a bonus, not a gate.</summary>
    public string? Model { get; set; }

    /// <summary>The folded model verdict as the Gateway stamped it, kept ALONGSIDE <see cref="Model"/>
    /// because the two absences mean opposite things - "it has not finished a turn yet" and "this agent
    /// can never report one" - and a seat that flattens them loses which one it was.</summary>
    public ModelDisplay? ModelDisplay { get; set; }

    /// <summary>The repository / working directory the seat runs in.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>The mission this seat is attached to, if any.</summary>
    public WorkspaceMissionRef? Mission { get; set; }

    /// <summary>The seat's role (Architect, Manager, Worker, ...), passed to --role on restore.</summary>
    public string? Role { get; set; }

    /// <summary>The session this one reports to - its CONTROLLER - passed to --controlled-by. A session
    /// id while the workspace describes a fleet that was running; after a restart every id in here names
    /// a session that no longer exists, which is why a restore works down the tree.</summary>
    public string? ReportsTo { get; set; }

    /// <summary>The session that SPAWNED this one. Distinct from <see cref="ReportsTo"/>: parentage is
    /// history, control is a live relationship, and the reporting chain is built from both.</summary>
    public string? ParentSessionId { get; set; }

    /// <summary>The workflow run this seat is seated on, passed to --workflow-run.</summary>
    public string? WorkflowRunId { get; set; }

    /// <summary>The prompt this seat is started with when the workspace is started COLD. Null when the
    /// seat is started SEEDED instead, where the seed points at <see cref="HandoverPath"/>.
    ///
    /// A long prompt goes in a FILE and the prompt is one line pointing at it: a long inline prompt parks
    /// in the agent's composer unsubmitted, and the session then comes up looking exactly like one that
    /// is simply thinking.</summary>
    public string? OpeningPrompt { get; set; }

    /// <summary>Extra command line arguments passed to the agent literally. No defaults are applied, so a
    /// value that pins a model must also supply the permission preset.</summary>
    public string? AgentArgs { get; set; }

    /// <summary>The seat's colour on the desktop rail, when the user set one.</summary>
    public string? Color { get; set; }

    /// <summary>Position in the list. Seats are started in this order.</summary>
    public int SortOrder { get; set; }

    /// <summary>What the session was doing when the capture ran. Null on an authored seat.</summary>
    public WorkspaceSeatState? StateAtDrain { get; set; }

    /// <summary>The agent's own session id, for anything the handover missed.</summary>
    public string? ClaudeSessionId { get; set; }

    /// <summary>The agent's transcript path, for anything the handover missed.</summary>
    public string? ClaudeTranscriptPath { get; set; }

    /// <summary>When the session was created. Null on an authored seat.</summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>The handover document this seat is restored from. For a "covered" seat this points at its
    /// SENIOR's document - no file is ever invented for a seat that reported up the chain.</summary>
    public string? HandoverPath { get; set; }

    /// <summary>Exactly one of <see cref="WorkspaceDrainStates"/>, or null on an authored seat and on a
    /// capture taken before any drain has happened. Never write a state you could not verify.</summary>
    public string? DrainState { get; set; }

    /// <summary>What a "blocked" seat said it was blocked on, in its own words.</summary>
    public string? BlockedReason { get; set; }

    /// <summary>What was decided about bringing this seat back, and why.</summary>
    public WorkspaceSeatRestore? Restore { get; set; }

    /// <summary>When this session was verified genuinely gone - not when it was flagged. Marking a session
    /// done is a flag and the reap is asynchronous, so a time stamped at the flag is not true.</summary>
    public DateTime? ClosedAtUtc { get; set; }

    /// <summary>The id of the session that came back for this seat. Written only after the restored
    /// session has answered a demand it could answer only from its handover.</summary>
    public string? RestoredSessionId { get; set; }

    /// <summary>The seed file the restored session was pointed at, kept beside the workspace record so
    /// anybody can read what a restored session was actually told.</summary>
    public string? RestoredSeedFile { get; set; }

    /// <summary>For a "covered" seat: which seat's document accounts for it.</summary>
    public string? CoveredBy { get; set; }

    /// <summary>For a "covered" seat: why reporting up was the right answer for this one.</summary>
    public string? CoveredNote { get; set; }

    /// <summary>Anything on this seat this build does not know a field for, kept verbatim. See
    /// <see cref="WorkspaceDocument.Unknown"/> for why.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>The mission a seat is attached to: the stable id and the display name cached on the session.</summary>
public sealed class WorkspaceMissionRef
{
    /// <summary>The mission's stable id, or null when the session is attached to no mission.</summary>
    public string? Id { get; set; }

    /// <summary>The mission's display name as cached on the session.</summary>
    public string? Name { get; set; }
    /// <summary>Anything in this object this build does not know a field for, kept verbatim.
    /// See <see cref="WorkspaceDocument.Unknown"/> for why every extensible object carries one.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

}

/// <summary>What a session was doing at capture time - the raw facts, exactly as the Gateway gave them.</summary>
public sealed class WorkspaceSeatState
{
    /// <summary>Process lifecycle: Starting / Running / Exiting / Exited / Failed.</summary>
    public string? Status { get; set; }

    /// <summary>The mechanical activity state: Starting / Idle / Working / WaitingForInput / ...</summary>
    public string? ActivityState { get; set; }

    /// <summary>The Gateway's folded label for a human ("Needs you", "Snoozed"), recorded verbatim.</summary>
    public string? StateLabel { get; set; }

    /// <summary>The Gateway's folded triage bucket, recorded verbatim.</summary>
    public string? TriageBucket { get; set; }

    /// <summary>How many turns this session had taken - half of how much is at stake in this one.</summary>
    public int? TurnCount { get; set; }

    /// <summary>How many files it had uncommitted - the other half.</summary>
    public int? UncommittedCount { get; set; }
    /// <summary>Anything in this object this build does not know a field for, kept verbatim.
    /// See <see cref="WorkspaceDocument.Unknown"/> for why every extensible object carries one.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

}

/// <summary>The decision about bringing one seat back.</summary>
public sealed class WorkspaceSeatRestore
{
    /// <summary>One of <see cref="WorkspaceRestoreDecisions"/>.</summary>
    public string Decision { get; set; } = WorkspaceRestoreDecisions.Undecided;

    /// <summary>Why that decision, in plain words. Read from the handover's "exact next action".</summary>
    public string? Why { get; set; }

    /// <summary>The command that brings it back. Required when the decision is "restore".</summary>
    public string? Command { get; set; }
    /// <summary>Anything in this object this build does not know a field for, kept verbatim.
    /// See <see cref="WorkspaceDocument.Unknown"/> for why every extensible object carries one.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

}

/// <summary>One question a session left waiting on the owner, word for word, with who asked it.</summary>
public sealed class WorkspaceOwnerQuestion
{
    /// <summary>The session that asked.</summary>
    public string? FromSessionId { get; set; }

    /// <summary>That session's name, so the question is attributable after the session is gone.</summary>
    public string? FromName { get; set; }

    /// <summary>The question, word for word.</summary>
    public string Question { get; set; } = "";
    /// <summary>Anything in this object this build does not know a field for, kept verbatim.
    /// See <see cref="WorkspaceDocument.Unknown"/> for why every extensible object carries one.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

}

/// <summary>The restart request itself, recorded so it can be handed to whoever has the scope to run it.</summary>
public sealed class WorkspaceRestartCommand
{
    /// <summary>The HTTP method.</summary>
    public string? Method { get; set; }

    /// <summary>The URL, with the machine named.</summary>
    public string? Url { get; set; }

    /// <summary>The confirmProtected flag the guard requires for the main Director and slots 1 to 4.</summary>
    public bool ConfirmProtected { get; set; }

    /// <summary>Anything the runner needs to know - notably that an agent's session key is refused here.</summary>
    public string? Note { get; set; }
    /// <summary>Anything in this object this build does not know a field for, kept verbatim.
    /// See <see cref="WorkspaceDocument.Unknown"/> for why every extensible object carries one.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

}

/// <summary>What was done about the LAUNCHER's own version, which nothing owns automatically yet.</summary>
public sealed class WorkspaceLauncherUpdate
{
    /// <summary>Why the launcher had to be touched at all.</summary>
    public string? Reason { get; set; }

    /// <summary>The version before.</summary>
    public string? From { get; set; }

    /// <summary>The version after.</summary>
    public string? To { get; set; }

    /// <summary>How long the newer build had been staged and unapplied.</summary>
    public string? StagedSince { get; set; }

    /// <summary>Anything else about the swap.</summary>
    public string? Note { get; set; }

    /// <summary>When it was applied.</summary>
    public DateTime? AppliedAtUtc { get; set; }

    /// <summary>What the swap produced, checked rather than assumed.</summary>
    public string? Result { get; set; }

    /// <summary>Where the previous binary was kept, so the swap can be undone by hand.</summary>
    public string? PreviousBinaryKeptAt { get; set; }
    /// <summary>Anything in this object this build does not know a field for, kept verbatim.
    /// See <see cref="WorkspaceDocument.Unknown"/> for why every extensible object carries one.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

}

/// <summary>Why a restart could not be performed, and what came of it. Never forcing is the point.</summary>
public sealed class WorkspaceRestartBlocked
{
    /// <summary>Where this blockage stands now.</summary>
    public string? State { get; set; }

    /// <summary>What actually caused it, observed rather than guessed.</summary>
    public string? Cause { get; set; }

    /// <summary>What was done about it.</summary>
    public string? Fix { get; set; }

    /// <summary>An earlier claim in this same record that later turned out to be wrong, corrected in
    /// place rather than quietly deleted. A record nobody can audit is not evidence.</summary>
    public string? CorrectedClaim { get; set; }

    /// <summary>Whether a guard that refused was RIGHT to refuse - so nobody later "fixes" correct
    /// behaviour into a hole.</summary>
    public string? GuardVerdict { get; set; }

    /// <summary>What this blockage teaches the code that will one day do it automatically.</summary>
    public string? LessonForPhase0 { get; set; }
    /// <summary>Anything in this object this build does not know a field for, kept verbatim.
    /// See <see cref="WorkspaceDocument.Unknown"/> for why every extensible object carries one.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

}

/// <summary>How the restart was actually asked for.</summary>
public sealed class WorkspaceRestartMechanism
{
    /// <summary>The route taken - a Gateway relay, a local lifecycle signal, or a human at the machine.</summary>
    public string? Method { get; set; }

    /// <summary>The named lifecycle signal, when that is what was used. Its six-byte key says WHICH data
    /// root the launcher is serving, so recording it says more than "a signal was sent".</summary>
    public string? Signal { get; set; }

    /// <summary>The launcher process that received it.</summary>
    public int? LauncherPid { get; set; }

    /// <summary>That launcher's version, read after it settled.</summary>
    public string? LauncherVersion { get; set; }

    /// <summary>Anything else about the mechanism.</summary>
    public string? Note { get; set; }
    /// <summary>Anything in this object this build does not know a field for, kept verbatim.
    /// See <see cref="WorkspaceDocument.Unknown"/> for why every extensible object carries one.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

}

/// <summary>Written back after the restart: what actually came back, who checked, and what they did NOT check.</summary>
public sealed class WorkspaceRestartPerformed
{
    /// <summary>When it happened, in the machine's own local time as recorded.</summary>
    public string? AtLocal { get; set; }

    /// <summary>The Director process that came back.</summary>
    public int? DirectorPid { get; set; }

    /// <summary>Its version. If an update was the point and this did not move, say so plainly.</summary>
    public string? DirectorVersionAfter { get; set; }

    /// <summary>The launcher as it stood afterwards.</summary>
    public WorkspaceLauncherAfter? LauncherAfter { get; set; }

    /// <summary>Who verified it came back, and from which artifacts.</summary>
    public string? VerifiedBy { get; set; }

    /// <summary>What was NOT verified. Not optional: a record that lists only what was proven reads as if
    /// everything else was.</summary>
    public string? NotVerified { get; set; }
    /// <summary>Anything in this object this build does not know a field for, kept verbatim.
    /// See <see cref="WorkspaceDocument.Unknown"/> for why every extensible object carries one.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

}

/// <summary>The launcher as it stood after the restart - which may not be the one that performed it, since
/// a restarted launcher can apply its own staged update seconds later.</summary>
public sealed class WorkspaceLauncherAfter
{
    /// <summary>The launcher process id afterwards.</summary>
    public int? Pid { get; set; }

    /// <summary>Its version, read once it settled rather than the instant it started.</summary>
    public string? Version { get; set; }

    /// <summary>When it started, in local time as recorded.</summary>
    public string? StartedLocal { get; set; }

    /// <summary>Anything else - notably a launcher that updated itself seconds after coming up.</summary>
    public string? Note { get; set; }
    /// <summary>Anything in this object this build does not know a field for, kept verbatim.
    /// See <see cref="WorkspaceDocument.Unknown"/> for why every extensible object carries one.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

}

/// <summary>Written back after the restore: which session did it, when, and how.</summary>
public sealed class WorkspaceRestoredBy
{
    /// <summary>The session that performed the restore.</summary>
    public string? SessionId { get; set; }

    /// <summary>That session's name.</summary>
    public string? Name { get; set; }

    /// <summary>When the restore finished.</summary>
    public DateTime? AtUtc { get; set; }

    /// <summary>Anything a later reader needs - notably seats satisfied by something other than a spawn.</summary>
    public string? Note { get; set; }

    /// <summary>How it was done, so the next restore can be done the same way.</summary>
    public string? Method { get; set; }
    /// <summary>Anything in this object this build does not know a field for, kept verbatim.
    /// See <see cref="WorkspaceDocument.Unknown"/> for why every extensible object carries one.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

}

/// <summary>How a workspace came to exist.</summary>
public static class WorkspaceOrigins
{
    /// <summary>Written by hand - "my morning fleet". Belongs to no machine.</summary>
    public const string Authored = "authored";

    /// <summary>Folded from a running Director's live sessions, which is what a drain produces.</summary>
    public const string Captured = "captured";

    /// <summary>Every valid origin.</summary>
    public static readonly string[] All = { Authored, Captured };
}

/// <summary>
/// What happened to the DIRECTOR. One of the two orthogonal facts that replaced the coarse four-word
/// outcome the hand-written index carried.
/// </summary>
public static class WorkspaceDirectorOutcomes
{
    /// <summary>No restart happened. Nothing about the machine changed - not the Director identifier,
    /// not its version - so every command in this document that names the Director is still right.</summary>
    public const string NotRestarted = "not-restarted";

    /// <summary>The Director was restarted. It has a NEW identifier, and every command in this document
    /// that names the old one has to be rewritten before it is run.</summary>
    public const string Restarted = "restarted";

    /// <summary>A restart was asked for and refused - the launcher could not take it, or the Director was
    /// not empty. Distinct from <see cref="NotRestarted"/>, where nobody asked.</summary>
    public const string RestartRefused = "restart-refused";

    /// <summary>Every valid value.</summary>
    public static readonly string[] All = { NotRestarted, Restarted, RestartRefused };
}

/// <summary>
/// How much of what was owed came back. The scope of <see cref="WorkspaceSeatOutcome"/>.
/// </summary>
public static class WorkspaceSeatOutcomes
{
    /// <summary>No seat was ever marked for restore, so nothing was owed. Deliberately NOT the same value
    /// as <see cref="None"/>: "nothing was owed" and "everything owed is missing" are two different facts,
    /// and welding two facts into one token is exactly the defect this pair of fields exists to end.</summary>
    public const string NothingToRestore = "nothing-to-restore";

    /// <summary>Seats were owed and not one came back.</summary>
    public const string None = "none";

    /// <summary>Some of the seats owed came back and some did not. The why says which and on what grounds.</summary>
    public const string Some = "some";

    /// <summary>Every seat that was owed came back.</summary>
    public const string All_ = "all";

    /// <summary>Every valid value.</summary>
    public static readonly string[] All = { NothingToRestore, None, Some, All_ };
}

/// <summary>
/// What happened to the SEATS: how many came back, how many did not, and why any was deliberately left
/// closed.
///
/// The count and the reason are here rather than left to be inferred from the seats, because a seat
/// deliberately NOT restored - its work is finished, or its Architect re-seats it - is a decision
/// somebody made, and a reader counting rows cannot tell that from a restore that simply failed.
/// </summary>
public sealed class WorkspaceSeatOutcome
{
    /// <summary>
    /// One of <see cref="WorkspaceSeatOutcomes"/>. DERIVED from the two counts, never stored, and not
    /// settable - a value written here is accepted and discarded. A word beside the numbers it
    /// summarises is a second source of truth, and two copies diverge the moment either is written
    /// alone.
    ///
    /// The four values are kept, and the fourth is the point. "Nothing was owed" and "everything owed is
    /// missing" are a success and a total failure, and a single "none" would weld them - which is the
    /// absence-shaped hazard in miniature: an empty restore list reading as success is how a run that
    /// restored nothing certifies itself.
    /// </summary>
    public string Scope
    {
        get => (RestoredCount, NotRestoredCount) switch
        {
            (0, 0) => WorkspaceSeatOutcomes.NothingToRestore,
            (0, _) => WorkspaceSeatOutcomes.None,
            (_, 0) => WorkspaceSeatOutcomes.All_,
            _ => WorkspaceSeatOutcomes.Some,
        };

        // Accepted and discarded, so a document written by any other build deserializes without the
        // value landing somewhere it would be written back out beside the derived one.
        set { }
    }

    /// <summary>How many seats came back.</summary>
    public int RestoredCount { get; set; }

    /// <summary>How many that were owed did not.</summary>
    public int NotRestoredCount { get; set; }

    /// <summary>Why any seat was not restored, in plain words. Required whenever
    /// <see cref="NotRestoredCount"/> is above zero: a missing seat with no reason beside it is
    /// indistinguishable from one nobody noticed.</summary>
    public string? NotRestoredWhy { get; set; }
    /// <summary>Anything in this object this build does not know a field for, kept verbatim.
    /// See <see cref="WorkspaceDocument.Unknown"/> for why every extensible object carries one.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

}

/// <summary>How far one seat got through the drain. Exactly these five, from the director-restart skill.</summary>
public static class WorkspaceDrainStates
{
    /// <summary>It wrote a handover, somebody READ it, and it stopped.</summary>
    public const string Drained = "drained";

    /// <summary>It answered and said it cannot reach a clean stop. Nothing is forced.</summary>
    public const string Blocked = "blocked";

    /// <summary>It did not answer within the wait.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>It reported UP to its senior instead of writing its own document, and the senior's
    /// document accounts for it. This is the chain working, not a gap - and no file is invented for it.</summary>
    public const string Covered = "covered";

    /// <summary>It answered and refused, or its answer did not amount to a handover.</summary>
    public const string Declined = "declined";

    /// <summary>Every valid drain state.</summary>
    public static readonly string[] All = { Drained, Blocked, Unreachable, Covered, Declined };
}

/// <summary>What was decided about bringing a seat back.</summary>
public static class WorkspaceRestoreDecisions
{
    /// <summary>Nothing has been decided yet - the state a fresh capture starts in. Distinct from
    /// <see cref="Close"/>, which is a decision somebody made.</summary>
    public const string Undecided = "undecided";

    /// <summary>Bring it back.</summary>
    public const string Restore = "restore";

    /// <summary>Deliberately not brought back - the work is finished, or a senior re-seats it.</summary>
    public const string Close = "close";

    /// <summary>Every valid decision.</summary>
    public static readonly string[] All = { Undecided, Restore, Close };
}

/// <summary>One row of the workspace list: enough to choose one without fetching every document.</summary>
public sealed class WorkspaceSummaryDto
{
    /// <summary>The workspace's slug id.</summary>
    public string Id { get; set; } = "";

    /// <summary>Its display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>What it is for.</summary>
    public string? Description { get; set; }

    /// <summary>"authored" or "captured".</summary>
    public string Origin { get; set; } = "";

    /// <summary>The machine it was captured from, or null when authored.</summary>
    public string? Machine { get; set; }

    /// <summary>The Director it was captured from, or null when authored.</summary>
    public string? DirectorName { get; set; }

    /// <summary>How many seats it holds.</summary>
    public int SeatCount { get; set; }

    /// <summary>When it was first stored.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>When it was last written.</summary>
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>The body of a capture request: name the workspace and say which Director to fold.</summary>
public sealed class WorkspaceCaptureRequest
{
    /// <summary>The slug to store it under. Required - the caller mints it, so the caller can find it again.</summary>
    public string Id { get; set; } = "";

    /// <summary>The display name. Required.</summary>
    public string Name { get; set; } = "";

    /// <summary>What this workspace is for.</summary>
    public string? Description { get; set; }

    /// <summary>Which Director's live sessions to capture. Required.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>Why the capture is being taken ("update to 2.0.6").</summary>
    public string? Reason { get; set; }

    /// <summary>The session driving this - recorded on the document so a reader can see whether the
    /// driver would have survived the restart.</summary>
    public string? DrivenBySessionId { get; set; }

    /// <summary>The Director the driving session lives on. See <see cref="DrivenBySessionId"/>.</summary>
    public string? DrivenByDirectorId { get; set; }

    /// <summary>Anything that needs saying about who drove it.</summary>
    public string? DrivenByNote { get; set; }
}
