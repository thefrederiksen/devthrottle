using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// Builds the plain description the Smart shutdown dialog is given from the Director's own
/// <see cref="Session"/> objects, so the window itself never touches a session.
///
/// Working means mid-turn: <see cref="ActivityState.Working"/> or <see cref="ActivityState.Starting"/>,
/// the same two states <see cref="Session"/> itself counts as working. Every other live state is
/// waiting. A session whose process has exited is not running, so it is left out: there is nothing
/// to shut down and nothing to hand over.
///
/// A question box is open when <see cref="Session.PendingInteraction"/> is set. That property is
/// currently never populated by the product (see PendingInteraction.cs), so today this always reads
/// false for a real session; the reading is in place for when the terminal detection fills it.
/// </summary>
public static class SmartShutdownSessionReader
{
    public static IReadOnlyList<SmartShutdownSession> Read(IEnumerable<Session> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var described = new List<SmartShutdownSession>();
        foreach (var session in sessions)
        {
            if (session.ActivityState == ActivityState.Exited)
            {
                FileLog.Write($"[SmartShutdownSessionReader] Read: sessionId={session.Id} has exited, left out");
                continue;
            }

            described.Add(new SmartShutdownSession(
                DisplayNameOf(session),
                IsWorking: session.ActivityState is ActivityState.Working or ActivityState.Starting,
                HasQuestionBoxOpen: session.PendingInteraction is not null));
        }

        FileLog.Write($"[SmartShutdownSessionReader] Read: described={described.Count}, " +
                      $"working={described.Count(s => s.IsWorking)}, " +
                      $"questionBoxes={described.Count(s => s.HasQuestionBoxOpen)}");
        return described;
    }

    // The same rule the session rail uses (SessionViewModel.DisplayName): the owner's name for the
    // session, or the repository folder's name when it has none.
    //
    // The folder name is read from the PATH's own shape, never from the separator of the machine
    // running this code: Path.GetFileName honours only the host's separator, so handed a Windows
    // path on macOS or Linux it finds no separator and returns the whole path. RepositoryPaths
    // .FolderName understands both separators and ignores a trailing one, so no TrimEnd is needed
    // here.
    private static string DisplayNameOf(Session session) =>
        session.CustomName ?? RepositoryPaths.FolderName(session.RepoPath);
}
