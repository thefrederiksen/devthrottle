using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Records the repository a session was started in as this machine's most recently used one, for
/// EVERY session this Director creates, whichever surface asked for it.
///
/// WHY IT EXISTS. Until this class the last-used time had exactly one writer in the whole product:
/// the desktop New Session dialog's own Start Session button. Every other way of starting a session -
/// the Cockpit, the phone, a schedule, an agent spawning a worker - left it untouched, so a machine
/// where nobody presses that button showed an empty recently-used list after days of work, and a
/// session started from the Cockpit did not move its repository at all.
///
/// WHY IT SUBSCRIBES RATHER THAN BEING CALLED. Every creation route funnels through
/// <see cref="SessionManager.RaiseSessionCreated"/> - the desktop window, the tunnel create verb, a
/// restore after a restart. One subscription there covers all of them, and a route added later is
/// covered without anybody remembering to call anything. A per-caller write is what produced the
/// one-caller defect above, and adding a second caller would only have moved the hole.
///
/// IT IS THE SAME OBSERVATION THE GATEWAY MAKES for its own catalogue (the Gateway's session history
/// recorder feeds its known-repository store off the same session, the moment it first sees it), and
/// it resolves the repository through the same <see cref="RepositoryUsage.StartedIn"/> rule - so the
/// two catalogues cannot disagree about which repository was used last.
/// </summary>
public sealed class RepositoryUsageRecorder : IDisposable
{
    private readonly SessionManager _sessions;
    private readonly RepositoryRegistry _repositories;
    private bool _disposed;

    public RepositoryUsageRecorder(SessionManager sessions, RepositoryRegistry repositories)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));

        FileLog.Write("[RepositoryUsageRecorder] ctor: recording repository use for every session this Director creates");
        _sessions.OnSessionCreated += Record;
    }

    /// <summary>
    /// One session was created: mark the repository it was started in as used now.
    ///
    /// This is an event handler and therefore an entry point, so it catches: a catalogue write is
    /// support for a picker, and it must never be the reason a session fails to start. The failure is
    /// written to the log with the path it was for, so a repository that stops moving up the list is
    /// answerable rather than silent.
    /// </summary>
    public void Record(Session session)
    {
        if (session is null)
            throw new ArgumentNullException(nameof(session));

        var repository = RepositoryUsage.StartedIn(session.RepoPath, session.PooledWorktree?.Repo);
        FileLog.Write($"[RepositoryUsageRecorder] Record: session={session.Id}, repository={repository ?? "(none)"}");
        if (repository is null)
            return;

        try
        {
            _repositories.MarkUsed(repository);
            FileLog.Write($"[RepositoryUsageRecorder] Record: marked {repository} used");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryUsageRecorder] Record FAILED for {repository}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _sessions.OnSessionCreated -= Record;
        FileLog.Write("[RepositoryUsageRecorder] Dispose: no longer recording repository use");
    }
}
