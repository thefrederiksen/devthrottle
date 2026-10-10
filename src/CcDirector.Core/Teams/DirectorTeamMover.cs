using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Teams;

/// <summary>
/// The hosted Gateway's side of changing a Director's team (screen D3). Implemented over HTTP by the setup
/// engine; faked in tests.
/// </summary>
public interface IDirectorTeamService
{
    /// <summary>The teams the signed-in person may move this Director to.</summary>
    Task<OperationResult<HostedTeamsAnswer>> ListTeamsAsync(CancellationToken ct);

    /// <summary>
    /// Move the Director whose own id is <paramref name="directorId"/> to <paramref name="teamId"/> (null = the
    /// personal account). The Gateway finds that Director's working key on the signed-in account, refuses while it
    /// has a session registered, and on success revokes that key and returns the new one, with where the revoked key
    /// was working.
    /// </summary>
    Task<OperationResult<DirectorMoveAnswer>> MoveAsync(string directorId, string? teamId, CancellationToken ct);
}

/// <summary>
/// The Gateway's yes to a move: the new device key (empty when its reply carried none), and where the key it revoked
/// was working (review RM-F8). <paramref name="MovedFrom"/> is null when the Gateway did not say - one older than the
/// answer - and the Director then knows nothing about what it left.
/// </summary>
public sealed record DirectorMoveAnswer(string DeviceKey, DirectorMovedFrom? MovedFrom);

/// <summary>Where the key a move revoked was working, as the Gateway said: a team, or null for the personal account.</summary>
public sealed record DirectorMovedFrom(string? TeamId);

/// <summary>
/// Changes the team this Director works for (screen D3) - only with every session closed, so no session ever
/// starts on one team and finishes on another.
///
/// The rule is enforced HERE, not only by a disabled button, and it holds for the WHOLE move (review finding
/// F3): session creation is held before the sessions are counted and released only after the new key is stored
/// and the connection re-applied, so no path - Settings, a Gateway-started spawn, a schedule - can start a
/// session in between. The Gateway refuses too; that is the backstop, not the rule.
///
/// Once the Gateway has answered yes, the move HAS happened: the old key is revoked. Every local failure after
/// that point says so, says what was not stored, and says what to do (review finding F4). The team is recorded
/// FIRST and the key LAST, so the screen can never name the old team while the Director holds the new team's
/// key: if the key cannot be saved, the screen names the team the Gateway now has it on, and the message says
/// the Director is disconnected until it is connected again.
/// </summary>
public sealed class DirectorTeamMover
{
    private readonly IDirectorTeamService _service;
    private readonly Func<string, SessionCreationHold> _holdSessionCreation;
    private readonly Action<DirectorTeam, string> _persistTeam;
    private readonly Action<string> _persistKey;
    private readonly Func<Task>? _reapply;
    private readonly Func<DirectorTeam, DirectorMoveAnswer, NameAfterMoveOutcome>? _followName;

    /// <param name="service">The Gateway's move route.</param>
    /// <param name="holdSessionCreation">Holds new sessions with the given reason, reporting how many there are.</param>
    /// <param name="persistTeam">Stores the new team for this Director, with the new key it is recorded for (only its
    /// fingerprint is stored), so a later refusal of that key can name the team (review RM-F6).</param>
    /// <param name="persistKey">Stores the new device key for this Director.</param>
    /// <param name="reapply">Switches the running Gateway connection to the new key; null when there is none.</param>
    /// <param name="followName">Renames the Director for the new team when its name is still the one the team question
    /// suggested (live proof F4), given the Gateway's answer, and says what happened; null to leave names alone.</param>
    public DirectorTeamMover(
        IDirectorTeamService service,
        Func<string, SessionCreationHold> holdSessionCreation,
        Action<DirectorTeam, string> persistTeam,
        Action<string> persistKey,
        Func<Task>? reapply,
        Func<DirectorTeam, DirectorMoveAnswer, NameAfterMoveOutcome>? followName = null)
    {
        _followName = followName;
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _holdSessionCreation = holdSessionCreation ?? throw new ArgumentNullException(nameof(holdSessionCreation));
        _persistTeam = persistTeam ?? throw new ArgumentNullException(nameof(persistTeam));
        _persistKey = persistKey ?? throw new ArgumentNullException(nameof(persistKey));
        _reapply = reapply;
    }

    /// <summary>
    /// Why a move cannot happen right now - "Close the 2 running sessions first." - or null when it can.
    /// </summary>
    public static string? RefusalFor(int runningSessions)
    {
        if (runningSessions < 0)
            throw new ArgumentOutOfRangeException(nameof(runningSessions), "A session count cannot be negative.");
        return runningSessions switch
        {
            0 => null,
            1 => "Close the 1 running session first.",
            _ => $"Close the {runningSessions} running sessions first.",
        };
    }

    /// <summary>The words for a move the Gateway made but whose team this computer could not record.</summary>
    public static string MovedButTeamNotRecorded(string teamName, string error) =>
        $"The Gateway moved this Director to {teamName}, but this computer could not record the team ({error}). " +
        $"Its old key no longer works, so it is not connected. Connect it again from the Gateway tab and choose {teamName}.";

    /// <summary>The words for a move the Gateway made but whose new key could not be saved.</summary>
    public static string MovedButKeyNotSaved(string teamName, string error) =>
        $"The Gateway moved this Director to {teamName}, but the new key could not be saved ({error}). " +
        $"Its old key no longer works, so it is not connected. Connect it again from the Gateway tab and choose {teamName}.";

    /// <summary>The words for a move that is stored but that the running connection could not switch to.</summary>
    public static string MovedButNotApplied(string teamName, string error) =>
        $"This Director now works for {teamName} and its new key is saved, but the running connection could not " +
        $"switch to it ({error}). Restart the Director to finish.";

    /// <summary>The words for a move that happened in full but whose suggested name could not follow it.</summary>
    public static string MovedButNotRenamed(string teamName, string error) =>
        $"This Director now works for {teamName} and is connected, but its name could not be changed to match ({error}). " +
        "Rename it from File, Rename this director.";

    /// <summary>What happened to the name, said beside a later failure of the same move (review RM-F7).</summary>
    public static string NameNotChangedNote(string error) =>
        $"Its name could not be changed to match ({error}); rename it from File, Rename this director.";

    /// <summary>What happened to the name when it changed but its record did not save, said beside a later failure.</summary>
    public static string NameChangedWillNotFollowNote(string newName, string error) =>
        $"Its name is now \"{newName}\", but this computer could not record that it is the suggested name ({error}), " +
        "so its name will not change on later moves.";

    /// <summary>What happened to the old suggestion record when it could not be removed, said beside the move's result
    /// (review RM-F8): it is tied to a key the Director no longer holds, so it renames nothing.</summary>
    public static string OldRecordNotRemovedNote(string error) =>
        $"This computer could not remove the record of its old suggested name ({error}); that record is tied to a key " +
        "this Director no longer uses, so it will not rename it.";

    /// <summary>The words for a move that happened in full but whose old suggestion record could not be removed.</summary>
    public static string MovedButOldRecordNotRemoved(string teamName, string error) =>
        $"This Director now works for {teamName} and is connected. {OldRecordNotRemovedNote(error)}";

    /// <summary>The words for a move whose name followed, but whose suggestion record could not be saved.</summary>
    public static string MovedRenamedButWillNotFollow(string teamName, string newName, string error) =>
        $"This Director now works for {teamName} and is connected, and its name is now \"{newName}\", but this computer " +
        $"could not record that the name is the suggested one ({error}), so its name will not change on later moves.";

    /// <summary>
    /// Move this Director to <paramref name="target"/>. Refused, with nothing sent and nothing stored, while any
    /// session is running; no session can start until it returns. The Gateway's refusal is returned as it is.
    /// </summary>
    /// <param name="directorId">This Director's own id - the device id it was set up with.</param>
    /// <param name="target">The team to move to.</param>
    /// <param name="ct">Cancels the Gateway call.</param>
    public async Task<OperationResult<DirectorTeam>> MoveAsync(string directorId, TeamChoice target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directorId))
            throw new ArgumentException("directorId is required", nameof(directorId));
        ArgumentNullException.ThrowIfNull(target);

        using var hold = _holdSessionCreation(
            $"this Director is moving to {target.Name}. Start the session again once the move is done.");
        var running = hold.SessionsAtHold;
        FileLog.Write($"[DirectorTeamMover] MoveAsync: director={directorId}, target={(target.IsPersonal ? "personal account" : "team " + target.TeamId)}, sessionsAtHold={running}");

        var refusal = RefusalFor(running);
        if (refusal is not null)
        {
            FileLog.Write($"[DirectorTeamMover] MoveAsync: REFUSED by the Director - {running} session(s) running");
            return OperationResult<DirectorTeam>.Fail(refusal);
        }

        var answered = await _service.MoveAsync(directorId, target.TeamId, ct).ConfigureAwait(false);
        if (!answered.Success)
        {
            FileLog.Write($"[DirectorTeamMover] MoveAsync: the Gateway refused: {answered.ErrorMessage}");
            return OperationResult<DirectorTeam>.Fail(answered.ErrorMessage ?? "The Gateway refused the move.");
        }
        var answer = answered.Value ?? throw new InvalidOperationException("The Gateway accepted the move but its answer is missing.");
        var newKey = answer.DeviceKey;

        // From here on the Gateway has moved the Director and revoked its old key. A local failure is caught so
        // the person is told THAT, rather than "the move failed" (finding F4) - which is why this service method
        // catches, against the usual entry-points-only rule.
        var team = target.ToTeam();
        if (string.IsNullOrWhiteSpace(newKey))
        {
            FileLog.Write("[DirectorTeamMover] MoveAsync: the Gateway accepted the move but sent no key");
            return OperationResult<DirectorTeam>.Fail(MovedButKeyNotSaved(team.Name, "the Gateway sent no new key"));
        }

        // The name follows BEFORE the team is recorded: recording the team redraws the title bar, which then shows the
        // new name and the new chip together, and the re-apply below sends the new name to the Fleet Map in its Hello.
        // A name that cannot follow does not undo the move; the person is told once the move is finished.
        string? renameError = null;
        string? oldRecordError = null;
        NameChangedButNotRecordedException? renamedButNotRecorded = null;
        if (_followName is not null)
        {
            try
            {
                var outcome = _followName(team, answer);
                oldRecordError = outcome.OldRecordNotRemoved;
                FileLog.Write($"[DirectorTeamMover] MoveAsync: name {(outcome.NewName is null ? "kept" : "followed the move")}" +
                              (oldRecordError is null ? "" : ", the old suggestion record NOT removed"));
            }
            catch (NameChangedButNotRecordedException ex)
            {
                FileLog.Write($"[DirectorTeamMover] MoveAsync FAILED: moved on the Gateway, name changed, its record NOT saved: {ex.InnerException?.Message}");
                renamedButNotRecorded = ex;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorTeamMover] MoveAsync FAILED: moved on the Gateway, name NOT changed: {ex.Message}");
                renameError = ex.Message;
            }
        }

        // Every failure is reported (review RM-F7): a later step that also fails carries what happened to the name, and
        // an old suggestion record that could not be removed is said too (review RM-F8).
        var nameNote = renamedButNotRecorded is not null
            ? NameChangedWillNotFollowNote(renamedButNotRecorded.NewName,
                renamedButNotRecorded.InnerException?.Message ?? renamedButNotRecorded.Message)
            : renameError is not null ? NameNotChangedNote(renameError) : null;
        var notes = string.Join(" ", new[] { nameNote, oldRecordError is null ? null : OldRecordNotRemovedNote(oldRecordError) }
            .Where(n => n is not null));
        OperationResult<DirectorTeam> Failed(string message) =>
            OperationResult<DirectorTeam>.Fail(notes.Length == 0 ? message : message + " " + notes);

        try
        {
            _persistTeam(team, newKey);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorTeamMover] MoveAsync FAILED: moved on the Gateway, team NOT recorded: {ex.Message}");
            return Failed(MovedButTeamNotRecorded(team.Name, ex.Message));
        }

        try
        {
            _persistKey(newKey);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorTeamMover] MoveAsync FAILED: moved on the Gateway, team recorded, key NOT saved: {ex.Message}");
            return Failed(MovedButKeyNotSaved(team.Name, ex.Message));
        }

        if (_reapply is not null)
        {
            try
            {
                await _reapply().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorTeamMover] MoveAsync: moved and stored, re-apply FAILED: {ex.Message}");
                return Failed(MovedButNotApplied(team.Name, ex.Message));
            }
        }

        if (renameError is not null)
            return OperationResult<DirectorTeam>.Fail(MovedButNotRenamed(team.Name, renameError));
        if (renamedButNotRecorded is not null)
            return OperationResult<DirectorTeam>.Fail(MovedRenamedButWillNotFollow(
                team.Name, renamedButNotRecorded.NewName, renamedButNotRecorded.InnerException?.Message ?? renamedButNotRecorded.Message));
        if (oldRecordError is not null)
            return OperationResult<DirectorTeam>.Fail(MovedButOldRecordNotRemoved(team.Name, oldRecordError));

        FileLog.Write("[DirectorTeamMover] MoveAsync: moved; team and new device key stored, connection re-applied");
        return OperationResult<DirectorTeam>.Ok(team);
    }
}
