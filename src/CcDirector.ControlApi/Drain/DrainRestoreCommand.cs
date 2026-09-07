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
    /// <param name="controllerIsBeingRestarted">True when this seat's controller is ALSO a seat in this
    /// drain, and will therefore be destroyed and come back with a new id. False when the controller lives
    /// on another Director: it survives the restart, so its CURRENT id is the right one and a placeholder
    /// would send whoever runs this command looking for a new id that will never exist.</param>
    public static string Build(WorkspaceSeat seat, string? handoverPath, bool controllerIsBeingRestarted = true)
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

        // A controller that is being restarted WITH this seat comes back under a new id, so the old one is
        // guaranteed wrong and the command carries a placeholder for whoever restores down the tree. A
        // controller on ANOTHER Director is not being restarted at all: it keeps the id it has, and a
        // placeholder there would send the reader hunting for a new id nobody is ever going to mint.
        if (string.IsNullOrWhiteSpace(seat.ReportsTo))
            sb.Append(" --standalone");
        else if (controllerIsBeingRestarted)
            sb.Append(" --controlled-by <the new id of ").Append(DrainPaths.ShortId(seat.ReportsTo)).Append('>');
        else
            sb.Append(" --controlled-by ").Append(seat.ReportsTo);

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
