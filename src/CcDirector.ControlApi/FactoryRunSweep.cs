using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// THE START-UP SWEEP OF KIT FOLDERS (Factory Definition mission, phase 5). A seat a smart shutdown or drain captured
/// keeps its kit folder when it ends, because the restore hands the folder back; this is what deletes such a folder once
/// nothing can restore it any more.
///
/// It may delete a folder only when it can POSITIVELY show that nothing names it, so it first reads, IN FULL, everything
/// that could: the persisted sessions, every crash journal, and every workspace document on the Gateway that belongs to
/// this machine (or names none). When any of those cannot be read, it deletes nothing and says why - a sweep that went
/// ahead on a partial list would delete exactly the folder the missing record would have restored. The deciding, under
/// the lock that guards a create's kit, is <see cref="SessionManager.SweepFactoryRunFolders"/>.
/// </summary>
internal static class FactoryRunSweep
{
    /// <summary>
    /// Read what names a kit folder, then sweep. Returns the folders deleted, or null when the sweep did not run
    /// because something could not be read (the reason is in the log).
    /// </summary>
    /// <param name="sessions">This Director's sessions; the sweep itself.</param>
    /// <param name="gateway">The Gateway's workspaces.</param>
    /// <param name="machine">This machine's name, as a workspace record spells it.</param>
    /// <param name="persistedNames">The session ids and kit folders the persisted sessions name. Throws when they cannot
    /// be read.</param>
    /// <param name="journalNames">The session ids every crash journal names. Throws when one cannot be read.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<IReadOnlyList<string>?> RunAsync(SessionManager sessions, IWayUpGateway gateway, string machine,
        Func<IEnumerable<string>> persistedNames, Func<IEnumerable<string>> journalNames, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(gateway);
        FileLog.Write($"[FactoryRunSweep] RunAsync: machine={machine}");
        var named = new List<string>();
        try
        {
            named.AddRange(persistedNames());
            named.AddRange(journalNames());
            var summaries = await gateway.ListWorkspacesAsync(ct).ConfigureAwait(false);
            var read = 0;
            foreach (var summary in summaries.Where(w => string.IsNullOrWhiteSpace(w.Machine)
                         || string.Equals(w.Machine, machine, StringComparison.OrdinalIgnoreCase)))
            {
                var doc = await gateway.GetWorkspaceAsync(summary.Id, ct).ConfigureAwait(false);
                // Listed a moment ago and deleted since: a record that is gone names nothing.
                if (doc is null) continue;
                read++;
                foreach (var seat in doc.Seats)
                {
                    if (seat.SessionId is { } id) named.Add(id);
                    if (seat.RestoredSessionId is { } restored) named.Add(restored);
                    if (seat.FactoryKitDirectory is { } folder) named.Add(folder);
                }
            }
            FileLog.Write($"[FactoryRunSweep] RunAsync: read {read} workspace record(s), {named.Count} name(s) in all");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FileLog.Write($"[FactoryRunSweep] RunAsync FAILED: not swept, because what names a kit folder could not all be read: {ex.Message}");
            return null;
        }
        return sessions.SweepFactoryRunFolders(named);
    }

    /// <summary>The session ids and kit folders the persisted sessions name. Throws when the file is there and cannot
    /// be read, so the sweep stops.</summary>
    public static IEnumerable<string> PersistedNames(SessionStateStore store)
    {
        var loaded = store.Load();
        if (!loaded.Success)
            throw new InvalidOperationException($"the persisted sessions at {store.FilePath} could not be read: {loaded.ErrorMessage}");
        return loaded.Sessions.SelectMany(s => new[] { s.Id.ToString(), s.FactoryKitDirectory ?? "" });
    }
}
