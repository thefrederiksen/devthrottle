using CcDirector.ControlApi.Drain;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.SmartRestart;

/// <summary>What bringing sessions back needs from the Gateway, behind a seam so its rules are provable
/// without one.</summary>
public interface IBringBackGateway
{
    /// <summary>Ask the Gateway's restore door to restore these seats onto this Director. Throws with the
    /// Gateway's reason when the restore is not taken.</summary>
    /// <param name="workspaceId">The record.</param>
    /// <param name="directorId">The Director to restore onto - this one.</param>
    /// <param name="seatSessionIds">The captured session ids to bring back.</param>
    /// <param name="seeds">A seed file per seat (captured session id to path), or null when the caller wrote
    /// none. The way up writes one per seat before it asks, and the door is the only way they reach the
    /// Director that performs the restore.</param>
    /// <param name="ct">Cancellation.</param>
    Task RequestRestoreAsync(
        string workspaceId,
        string directorId,
        IReadOnlyList<string> seatSessionIds,
        IReadOnlyDictionary<string, string>? seeds,
        CancellationToken ct);

    /// <summary>The workspace as it is stored now, or null when there is none.</summary>
    Task<WorkspaceDocument?> GetWorkspaceAsync(string workspaceId, CancellationToken ct);
}

/// <summary>The real seam, over the Director's existing outbound Gateway client.</summary>
public sealed class GatewayClientBringBackGateway : IBringBackGateway
{
    private readonly GatewayClient _client;

    /// <summary>Create the seam over a connected Gateway client.</summary>
    /// <param name="client">The Director's Gateway client.</param>
    public GatewayClientBringBackGateway(GatewayClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public Task RequestRestoreAsync(
        string workspaceId,
        string directorId,
        IReadOnlyList<string> seatSessionIds,
        IReadOnlyDictionary<string, string>? seeds,
        CancellationToken ct)
        => _client.RequestWorkspaceRestoreAsync(workspaceId,
            new WorkspaceRestoreRequest
            {
                DirectorId = directorId,
                Seats = seatSessionIds.ToList(),

                // A SEED FILE IS NOT OPTIONAL DETAIL - it is the document the restored session is pointed at.
                // The way up writes one per seat and then asks here, so dropping them would bring every seat
                // back with nothing to read. An EMPTY dictionary is sent as none, so the wire carries the
                // same thing the cancel path has always carried.
                Seeds = seeds is { Count: > 0 } ? new Dictionary<string, string>(seeds, StringComparer.OrdinalIgnoreCase) : null,
            }, ct);

    /// <inheritdoc />
    public Task<WorkspaceDocument?> GetWorkspaceAsync(string workspaceId, CancellationToken ct)
        => _client.GetWorkspaceAsync(workspaceId, ct);
}

/// <summary>
/// HOW A CANCELLED SMART SHUTDOWN BRINGS ITS CLOSED SESSIONS BACK ON A REAL DIRECTOR: by asking the
/// Gateway's restore door for a restore onto THIS Director, exactly as the owner's command line does.
///
/// WHY NOT RUN <see cref="DirectorRestore"/> DIRECTLY. The Gateway grants one restore lease per workspace
/// at that door, and refuses every mark from a Director that does not hold it. A restore started from
/// inside the Director holds no lease, so its first mark would be refused and nothing would come back.
/// Going through the door is not a second path - it IS the existing path: the Gateway relays the order
/// back down to this Director, which runs the same <see cref="DirectorRestore"/> it always runs, leads
/// first, each seat under its real owner.
///
/// The door answers "taken", not "done". Each seat's result is written onto the record as it happens,
/// so this reads the record until every seat asked for has an answer, and stops waiting after
/// <see cref="Patience"/>: a seat with no answer by then is reported as exactly that.
///
/// THE WAY UP GOES THROUGH THIS SAME CLASS (product issue 3395). "Bring back" on the start-up window and on
/// File, Restart history used to construct <see cref="DirectorRestore"/> in the Director instead - the very
/// thing the paragraph above says cannot work - so its first mark was refused and nothing ever came back.
/// The way up needs one thing the cancel path does not, a seed file per seat, so that is a parameter here
/// rather than a second copy of this class: see <see cref="DirectorRestoreWayUp"/>.
/// </summary>
public sealed class GatewaySmartShutdownBringBack
{
    /// <summary>How long the record is read for answers. The Gateway waits up to thirty seconds per
    /// create and seats are started one at a time, so this is generous for a full Director.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly Func<IBringBackGateway?> _gateway;
    private readonly string _directorId;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Create it.</summary>
    /// <param name="gateway">The Gateway seam, read at the moment of use so it is the host's CURRENT
    /// client. Null when this Director has no Gateway client.</param>
    /// <param name="directorId">This Director's own id: the restore is asked for onto it.</param>
    /// <param name="utcNow">Test seam for the clock.</param>
    /// <param name="delay">Test seam for the wait between two reads of the record.</param>
    public GatewaySmartShutdownBringBack(
        Func<IBringBackGateway?> gateway,
        string directorId,
        Func<DateTime>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _directorId = string.IsNullOrWhiteSpace(directorId)
            ? throw new ArgumentException("directorId is required", nameof(directorId))
            : directorId;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>Bring the named seats back. Matches <see cref="BringBackClosedSessions"/>. It is the
    /// entry point into another part of the product, so a restore that cannot start is caught HERE and
    /// handed back as the sentence that says why; it is never thrown into a run that is half way through
    /// telling sessions the restart is off.</summary>
    /// <param name="workspaceId">The record.</param>
    /// <param name="seatSessionIds">The captured session ids to bring back.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<SmartShutdownBringBack> BringBackAsync(
        string workspaceId, IReadOnlyList<string> seatSessionIds, CancellationToken ct)
        => BringBackAsync(workspaceId, seatSessionIds, seeds: null, ct);

    /// <summary>
    /// Bring the named seats back, each pointed at its own seed file. The way up (product issue 3395) writes a
    /// seed per seat before it asks, and this is the same door with those seeds on it: there is deliberately no
    /// second way in, because a restore started anywhere else holds no lease and cannot write what it did.
    /// </summary>
    /// <param name="workspaceId">The record.</param>
    /// <param name="seatSessionIds">The captured session ids to bring back.</param>
    /// <param name="seeds">A seed file per seat (captured session id to path), or null for none.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<SmartShutdownBringBack> BringBackAsync(
        string workspaceId,
        IReadOnlyList<string> seatSessionIds,
        IReadOnlyDictionary<string, string>? seeds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seatSessionIds);
        FileLog.Write($"[GatewaySmartShutdownBringBack] BringBackAsync: workspace={workspaceId}, " +
                      $"seats={seatSessionIds.Count}, seeds={seeds?.Count ?? 0}");

        var gateway = _gateway();
        if (gateway is null)
            return CouldNotStart("this Director is no longer connected to a Gateway, and a restore is asked for through it.");

        try
        {
            await gateway.RequestRestoreAsync(workspaceId, _directorId, seatSessionIds, seeds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return CouldNotStart($"the restore was not taken: {ex.Message}");
        }

        var until = _utcNow() + Patience;
        while (true)
        {
            var doc = await gateway.GetWorkspaceAsync(workspaceId, ct).ConfigureAwait(false);
            var outcomes = Read(doc, seatSessionIds, final: _utcNow() >= until);
            if (outcomes is not null)
            {
                FileLog.Write(
                    $"[GatewaySmartShutdownBringBack] BringBackAsync: back={outcomes.Count(o => o.RestoredSessionId is not null)}, " +
                    $"not={outcomes.Count(o => o.Failure is not null)}");
                return new SmartShutdownBringBack(outcomes, null);
            }
            await _delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One outcome per seat once EVERY seat has an answer on the record, or - when
    /// <paramref name="final"/> - whatever is there, with the unanswered seats saying so. Null while
    /// some seat has no answer yet and there is still time.</summary>
    private static List<SeatRestoreOutcome>? Read(WorkspaceDocument? doc, IReadOnlyList<string> seatSessionIds, bool final)
    {
        var outcomes = new List<SeatRestoreOutcome>();
        foreach (var id in seatSessionIds)
        {
            var seat = doc?.Seats.FirstOrDefault(s => string.Equals(s.SessionId, id, StringComparison.OrdinalIgnoreCase));
            if (seat is not null && !string.IsNullOrWhiteSpace(seat.RestoredSessionId))
                outcomes.Add(new SeatRestoreOutcome(id, seat.Name, seat.RestoredSessionId, null, null));
            else if (seat?.Restore?.Failure is { Length: > 0 } failure)
                outcomes.Add(new SeatRestoreOutcome(id, seat.Name, null, null, failure));
            else if (!final)
                return null;
            else
                outcomes.Add(new SeatRestoreOutcome(id, seat?.Name ?? id, null, null,
                    $"the restore was taken, and after {Patience.TotalMinutes:0} minutes the record still says " +
                    "nothing about this session. It may yet come back; check the session list."));
        }
        return outcomes;
    }

    private static SmartShutdownBringBack CouldNotStart(string why)
    {
        FileLog.Write($"[GatewaySmartShutdownBringBack] BringBackAsync FAILED: {why}");
        return new SmartShutdownBringBack(Array.Empty<SeatRestoreOutcome>(), why);
    }
}
