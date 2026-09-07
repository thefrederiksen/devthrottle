namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The ONE fold that turns a Director's live sessions into a <see cref="WorkspaceDocument"/> - the capture
/// half of "a restart index and a workspace are the same object" (issue #2722).
///
/// It is a pure function over facts the Gateway already holds, and that is deliberate on two counts:
///
///  - THE FACTS ARE READ, NEVER ASKED FOR. The Director knows every one of these fields. Asking an agent
///    for its own repository, role or model gets them wrong often enough to matter, and a fact you can
///    read is never a fact you request.
///  - IT LIVES IN THE CONTRACTS ASSEMBLY, beside <see cref="SessionOrdering"/> and <see cref="ModelDisplay"/>,
///    so the fold can be run over a roster from anywhere - the Gateway's own live cache, or a saved
///    roster JSON - without dragging the Gateway host in. That is what makes the capture checkable against
///    a hand-written index instead of only against itself.
///
/// What it does NOT do: decide anything. Every judgment in a drain - is this seat drained or covered, does
/// it come back or stay closed, which four ids go in the restore list - is made by a reader of a handover
/// and written onto the document afterwards. The capture fills in the facts and leaves those fields at
/// their honest empty values (<see cref="WorkspaceRestoreDecisions.Undecided"/>, a null drain state), so a
/// document nobody has judged yet cannot be mistaken for one that has been.
/// </summary>
public static class WorkspaceCapture
{
    /// <summary>
    /// Fold one Director's live sessions into a workspace document.
    /// </summary>
    /// <param name="request">What to call it and which Director it describes. Its Id and Name are used
    /// verbatim - validation belongs to the store, not to the fold.</param>
    /// <param name="sessions">The sessions to capture. The caller has already filtered these to the one
    /// Director; the fold does not filter, so a caller cannot get a partial capture that looks whole.</param>
    /// <param name="directorName">The Director's display name, or null when the caller does not know it.</param>
    /// <param name="directorVersion">The Director's version at capture time, recorded as the BEFORE
    /// version - the after version is written back once a restart has actually happened.</param>
    /// <param name="machine">The machine the Director runs on. Falls back to the machine the sessions
    /// themselves report when the caller passes null.</param>
    /// <param name="nowUtc">The capture time. Injected so a capture is reproducible in a test.</param>
    public static WorkspaceDocument Capture(
        WorkspaceCaptureRequest request,
        IReadOnlyList<SessionDto> sessions,
        string? directorName,
        string? directorVersion,
        string? machine,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sessions);

        var at = nowUtc.ToUniversalTime();

        // The sessions carry the machine name themselves. Preferring the caller's value keeps a capture
        // of a Director whose sessions have all exited from losing the machine entirely.
        var resolvedMachine = !string.IsNullOrWhiteSpace(machine)
            ? machine!.Trim()
            : sessions.Select(s => s.MachineName).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));

        var seats = sessions
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.CreatedAt)
            .Select((s, index) => CaptureSeat(s, index))
            .ToList();

        return new WorkspaceDocument
        {
            SchemaVersion = 1,
            Id = request.Id,
            Name = request.Name,
            Description = request.Description,
            Origin = WorkspaceOrigins.Captured,
            Machine = resolvedMachine,
            DirectorId = request.DirectorId,
            DirectorName = directorName,
            DirectorVersionBefore = directorVersion,
            DirectorVersionAfter = null,
            StartedAtUtc = at,
            CompletedAtUtc = null,
            DrivenBySessionId = request.DrivenBySessionId,
            DrivenByDirectorId = request.DrivenByDirectorId,
            DrivenByNote = request.DrivenByNote,
            Reason = request.Reason,
            Outcome = WorkspaceOutcomes.Draining,
            Seats = seats,
        };
    }

    /// <summary>
    /// Fold one live session into one seat. Public because the diff tool folds sessions one at a time to
    /// say WHICH seat differs from the hand-written index and in which field.
    /// </summary>
    /// <param name="s">The session, exactly as the Gateway gives it.</param>
    /// <param name="sortOrder">The seat's position in the workspace.</param>
    public static WorkspaceSeat CaptureSeat(SessionDto s, int sortOrder)
    {
        ArgumentNullException.ThrowIfNull(s);

        return new WorkspaceSeat
        {
            SessionId = s.SessionId,
            Name = s.Name ?? "",
            Agent = s.Agent,

            // Both halves of the model, never one flattened into the other: the id when the agent's
            // records name one, and the folded verdict which is the only thing that says WHICH absence
            // this is - not recorded YET, versus never going to be.
            Model = string.IsNullOrWhiteSpace(s.CurrentModel) ? null : s.CurrentModel,
            ModelDisplay = s.ModelDisplay,

            RepoPath = s.RepoPath,
            Mission = (s.MissionId is null && string.IsNullOrWhiteSpace(s.MissionName))
                ? null
                : new WorkspaceMissionRef
                {
                    Id = s.MissionId?.ToString(),
                    Name = string.IsNullOrWhiteSpace(s.MissionName) ? null : s.MissionName,
                },

            // The RESOLVED role, which is what a restore passes to --role. It already accounts for an
            // explicitly declared Architect; ExplicitRole is the fallback only because a Director-local
            // response cannot resolve a role at all (that needs the fleet view).
            Role = FirstNonEmpty(s.SessionRole, s.ExplicitRole),

            ReportsTo = string.IsNullOrWhiteSpace(s.ControllerSessionId) ? null : s.ControllerSessionId,
            ParentSessionId = string.IsNullOrWhiteSpace(s.ParentSessionId) ? null : s.ParentSessionId,
            WorkflowRunId = s.WorkflowRunId?.ToString(),

            // A live session does not carry the prompt it was started with, so a captured seat has no
            // opening prompt. Left null rather than filled with something plausible: a restart seeds each
            // seat from its own handover, and a cold start of a captured workspace needs a prompt written
            // for it. See the gaps named in the capture diff report.
            OpeningPrompt = null,

            SortOrder = sortOrder,

            StateAtDrain = new WorkspaceSeatState
            {
                Status = s.Status,
                ActivityState = s.ActivityState,
                StateLabel = s.StateLabel,
                TriageBucket = s.TriageBucket,
                TurnCount = s.TurnCount,
                UncommittedCount = s.UncommittedCount,
            },

            ClaudeSessionId = s.ClaudeSessionId,
            ClaudeTranscriptPath = s.ClaudeTranscriptPath,
            CreatedAt = s.CreatedAt,

            // Everything below is a JUDGMENT, and the capture makes none of them. A handover has not been
            // written yet, let alone read.
            HandoverPath = null,
            DrainState = null,
            BlockedReason = null,
            Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided },
            ClosedAtUtc = null,
            RestoredSessionId = null,
            RestoredSeedFile = null,
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }
}
