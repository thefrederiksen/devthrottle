namespace CcDirector.ControlApi.Drain;

/// <summary>
/// Builds the command that brings one seat back.
///
/// It is built by the DRAIN, from facts it holds, and not asked for: this command is read after the session is
/// gone, when nobody can check it against anything.
///
/// IT ASKS THE DIRECTOR TO DO THE RESTORE (the Message Load mission, slice 6; owner decision 2, 17 September
/// 2026). It used to be a <c>cc-devthrottle session spawn</c> line carrying <c>--controlled-by</c> with the
/// seat's owner - a placeholder for an owner restarted in the same drain, the real id for one on another
/// Director. A session key may name only itself or the user as an owner, so whoever runs such a line with a
/// session key is refused for every seat owned by somebody else. So the line no longer names an owner at all:
/// it names the workspace and the seat, and the Director reads the owner from the seat facts the Gateway
/// captured, resolves a restarted owner to its new id, and starts the seat on its own credential
/// (<see cref="DirectorRestore"/>).
///
/// ONE PLACEHOLDER IS DELIBERATELY LEFT IN IT, and it is the honest shape rather than a gap: the NEW Director id,
/// which does not exist yet. A restarted Director gets a new identifier, so any id baked in here at drain time is
/// guaranteed wrong by the time anybody runs the command. A placeholder that is obviously a placeholder is safe.
/// An id that looks real and is stale is not.
/// </summary>
public static class DrainRestoreCommand
{
    /// <summary>The token standing in for the identifier of the Director that comes back.</summary>
    public const string NewDirectorToken = "<the NEW director id>";

    /// <summary>
    /// Build the restore command for one seat.
    /// </summary>
    /// <param name="workspaceId">The workspace this drain is recorded in.</param>
    /// <param name="sessionId">The seat's captured session id.</param>
    public static string Build(string workspaceId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) throw new ArgumentException("workspaceId is required", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId is required", nameof(sessionId));

        return $"cc-devthrottle director restore {Quote(workspaceId)} --director {Quote(NewDirectorToken)} --seat {sessionId}";
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
