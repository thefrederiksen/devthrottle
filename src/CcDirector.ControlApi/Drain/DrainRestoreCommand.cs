using System.Text;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Drain;

/// <summary>
/// Builds the command that brings one seat back.
///
/// It is built by the DRAIN, from the seat's own captured facts, and not asked for: the repository, the
/// agent, the role, the mission and the controller are all things the Director knows and an agent can get
/// wrong, and this command is read after the session is gone, when nobody can check it against anything.
///
/// TWO PLACEHOLDERS ARE DELIBERATELY LEFT IN IT, and they are the honest shape rather than a gap:
///
///  - the NEW Director id, which does not exist yet. A restarted Director gets a new identifier, so any
///    id baked in here at drain time is guaranteed wrong by the time anybody runs the command. The
///    hand-written index wrote "&lt;NEW&gt;" for exactly this reason.
///  - the seed file, which Phase 5 writes when it restores. The drain names the handover the seed will
///    point at, which is the part it knows.
///
/// A placeholder that is obviously a placeholder is safe. An id that looks real and is stale is not.
/// </summary>
public static class DrainRestoreCommand
{
    /// <summary>The token standing in for the identifier of the Director that comes back.</summary>
    public const string NewDirectorToken = "<the NEW director id>";

    /// <summary>
    /// Build the restore command for one seat.
    /// </summary>
    /// <param name="seat">The captured seat.</param>
    /// <param name="handoverPath">The document this seat is restored from, which the seed points at.</param>
    public static string Build(WorkspaceSeat seat, string? handoverPath)
    {
        ArgumentNullException.ThrowIfNull(seat);

        var sb = new StringBuilder();
        sb.Append("cc-devthrottle session spawn ").Append(Quote(seat.RepoPath));
        sb.Append(" --director ").Append(NewDirectorToken);
        if (!string.IsNullOrWhiteSpace(seat.Agent)) sb.Append(" --agent ").Append(seat.Agent);
        sb.Append(" --name ").Append(Quote(seat.Name));
        if (!string.IsNullOrWhiteSpace(seat.Role)) sb.Append(" --role ").Append(seat.Role);
        if (!string.IsNullOrWhiteSpace(seat.Mission?.Id)) sb.Append(" --mission ").Append(seat.Mission!.Id);
        if (!string.IsNullOrWhiteSpace(seat.WorkflowRunId)) sb.Append(" --workflow-run ").Append(seat.WorkflowRunId);

        // The controller is a session id from BEFORE the restart, so it names a session that no longer
        // exists. A restore works DOWN the tree and points each seat at its senior's new id; naming the
        // old one here would be a command that fails, so the seat says who it reported to and leaves the
        // substitution to whoever restores it.
        if (!string.IsNullOrWhiteSpace(seat.ReportsTo))
            sb.Append(" --controlled-by <the new id of ").Append(DrainPaths.ShortId(seat.ReportsTo)).Append('>');
        else
            sb.Append(" --standalone");

        // A long prompt parks in the agent's composer unsubmitted and the seat comes up looking exactly
        // like one that is simply thinking. One line, pointing at a file, is the only shape that works.
        var seed = string.IsNullOrWhiteSpace(handoverPath)
            ? "<the seed file, pointing at this seat's handover>"
            : handoverPath!;
        sb.Append(" --prompt ").Append(Quote(
            $"Read {seed} - it is your whole mandate. You are a RESTORED session with no transcript; " +
            "treat that document as your own history and follow its exact next action."));

        return sb.ToString();
    }

    private static string Quote(string? value) => "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
}
