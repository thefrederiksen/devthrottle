using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.SmartRestart;

/// <summary>
/// The crash journal's half of "one dead session, two doors" (issue #4120, review round 2). A crashed Director's sessions
/// are offered back by its crash record on this Director's screen AND by the Interrupted sessions list, which reads the
/// journal and is the door a person or an agent away from the machine has. A seat reopened from the record has its row
/// removed from the journal here, so the Interrupted sessions list stops offering it; the other way round, the Gateway's
/// Interrupted restore claims the record's seat before it starts anything.
/// </summary>
public static class CrashJournalRows
{
    /// <summary>
    /// Remove a reopened seat's row from the dirty journal its crash record was built from. The journal is found by the
    /// record's id, which is derived from the dead process (<see cref="WorkspaceCrashRecords.IdFor(string, int, DateTimeOffset)"/>).
    /// Returns false when no pending journal matches, or the row is already gone - a restore from the Interrupted
    /// sessions list removes it too. Never throws: the seat has already been started, and a row left behind is then
    /// refused by the Gateway, which finds the record's seat already reopened.
    /// </summary>
    /// <param name="recordId">The crash record's id.</param>
    /// <param name="seatSessionId">The dead session's id.</param>
    /// <param name="directory">The crash-journal folder; this Director's own when null.</param>
    public static bool RemoveReopened(string recordId, string seatSessionId, string? directory = null)
    {
        FileLog.Write($"[CrashJournalRows] RemoveReopened: record={recordId}, seat={seatSessionId}");
        var journal = DirectorCrashJournal.ListPendingRecoveries(directory)
            .FirstOrDefault(j => string.Equals(WorkspaceCrashRecords.IdFor(j.DirectorId, j.Pid, j.StartedAtUtc), recordId, StringComparison.Ordinal));
        if (journal is null)
        {
            FileLog.Write($"[CrashJournalRows] RemoveReopened: no pending crash journal belongs to {recordId}; nothing to remove");
            return false;
        }

        var removed = DirectorCrashJournal.RemoveSession(journal.DirectorId, journal.Pid, seatSessionId, directory);
        FileLog.Write($"[CrashJournalRows] RemoveReopened: record={recordId}, seat={seatSessionId}, removed={removed}");
        return removed;
    }
}
