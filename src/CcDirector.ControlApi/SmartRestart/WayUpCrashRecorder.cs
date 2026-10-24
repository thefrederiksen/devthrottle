using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.SmartRestart;

/// <summary>
/// THE CRASHED DIRECTOR'S SESSIONS GO ON THE GATEWAY AS A RESTART RECORD (issue #4120), so the way up offers them
/// back the way it offers a smart shutdown's.
///
/// WHY IT IS NEEDED. A restart record is written on the way DOWN - by File, Smart Restart, by closing the window, or
/// by the operating system shutting down. A Director that is killed or crashes has no way down, so it wrote no record,
/// and the way up only reads records: a Linux Director killed by the out-of-memory killer took 17 sessions with it, and
/// the next Director found all 17 in its crash journal, logged them, and offered nothing. This sends that journal to
/// the Gateway, which keeps it as a record of kind <see cref="WorkspaceShutdownKinds.Crash"/>, every seat died with its
/// Director. From there the start-up offer, File, Restart history and "Reopen its saved conversation" are the ones that
/// already exist.
///
/// RUN BEFORE THE START-UP OFFER IS LOOKED FOR, so a crash found at this start-up is offered at this start-up. Every
/// pending journal is sent, not only the ones claimed now: a send that failed last time is sent again, and one that
/// worked is answered "already recorded", because the record id is derived from the dead process.
///
/// NEVER THROWS. A journal that cannot be sent is logged and the rest are still sent; the start-up offer of a smart
/// shutdown must not be lost because a crash record could not be written.
/// </summary>
public sealed class WayUpCrashRecorder
{
    // The crash journal is read off the Director's disk and sent as the Gateway's crash-journal shape, which is the
    // same document under the same names: the interrupted-list verb has always sent it this way.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IWayUpGateway _gateway;

    /// <summary>Create the recorder.</summary>
    /// <param name="gateway">The Gateway seam the way up already uses.</param>
    public WayUpCrashRecorder(IWayUpGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    /// <summary>What happened to each journal: written, already there, or not sent and why.</summary>
    public sealed record Outcome(int Recorded, int AlreadyRecorded, IReadOnlyList<string> Failures);

    /// <summary>Send every journal that holds a session.</summary>
    /// <param name="journals">The pending crash journals of this Director's instance.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<Outcome> RecordAsync(IReadOnlyList<DirectorCrashJournalData> journals, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(journals);
        FileLog.Write($"[WayUpCrashRecorder] RecordAsync: journals={journals.Count}");

        var recorded = 0;
        var already = 0;
        var failures = new List<string>();
        foreach (var journal in journals.Where(j => j.Sessions.Count > 0))
        {
            var id = WorkspaceCrashRecords.IdFor(journal.DirectorId, journal.Pid);
            try
            {
                var dto = JsonSerializer.Deserialize<CrashJournalDto>(JsonSerializer.Serialize(journal, Json), Json)
                          ?? throw new InvalidOperationException("the journal could not be read back as the Gateway's shape");
                if (await _gateway.RecordCrashAsync(dto, ct).ConfigureAwait(false)) recorded++;
                else already++;
                FileLog.Write($"[WayUpCrashRecorder] {id}: {journal.Sessions.Count} session(s) of Director {journal.DirectorId} (process {journal.Pid}) are on the Gateway");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                FileLog.Write($"[WayUpCrashRecorder] {id} FAILED: the crash journal of Director {journal.DirectorId} (process {journal.Pid}) could not be kept on the Gateway, so its sessions are not offered back yet: {ex.Message}");
                failures.Add($"{id}: {ex.Message}");
            }
        }

        FileLog.Write($"[WayUpCrashRecorder] RecordAsync: recorded={recorded}, alreadyRecorded={already}, failed={failures.Count}");
        return new Outcome(recorded, already, failures);
    }
}
