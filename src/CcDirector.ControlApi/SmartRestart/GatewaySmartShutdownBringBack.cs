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
/// AN ANSWER MEANS AN ANSWER TO THIS RUN. A seat's Failure stays on the record until that seat reaches its
/// next "started" mark, so on a retry the record still carries last time's reasons at the moment the door
/// answers. They are told apart by the clock: a Failure counts only when it was written at or after the
/// moment this run asked. A restored session id needs no such test - a seat comes back once.
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

        // THE MOMENT THE DOOR WAS ASKED, read BEFORE asking, because it is what tells this run's answers from
        // the last run's (product issue 3395, both reviewers). The door answers "taken" before the far end
        // runs anything, a seat's Failure is cleared only when that seat reaches its NEW "started" mark, and
        // the first read below has no delay in front of it - so on a second press after a failed first one
        // every seat still carries last time's Failure, and reading those as answers would report "nothing
        // came back" while the restore is only just starting.
        //
        // WHOSE CLOCK, said plainly because it is the gap in this test. AttemptedAtUtc is written by the
        // Gateway and this moment is read on the Director, so the two are only as close as the machines'
        // clocks are. A Gateway BEHIND this Director fails safe: this run's own failure is not recognised as
        // an answer, the seat waits out the patience, and it is then reported as an earlier attempt's reason -
        // slow and honest, never a wrong answer. A Gateway far enough AHEAD could still let an old failure
        // through; that residual gap is the size of the skew and nothing here guards it.
        var askedAtUtc = _utcNow();

        try
        {
            await gateway.RequestRestoreAsync(workspaceId, _directorId, seatSessionIds, seeds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return CouldNotStart($"the restore was not taken: {ex.Message}");
        }

        var until = askedAtUtc + Patience;
        WorkspaceDocument? lastRead = null;
        while (true)
        {
            // A READ THAT THROWS IS NOT A REFUSAL. The restore was taken - it is running on this very Director -
            // so a dropped connection or a Gateway restarting mid-wait must never leave here as "nothing was
            // brought back". It is retried until the patience runs out and then reported as what it is: taken,
            // and the record could not be read.
            string? readFailure = null;
            try
            {
                lastRead = await gateway.GetWorkspaceAsync(workspaceId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                readFailure = ex.Message;
                FileLog.Write($"[GatewaySmartShutdownBringBack] BringBackAsync: the record could not be read: {ex.Message}");
            }

            var outcomes = Read(lastRead, seatSessionIds, askedAtUtc, final: _utcNow() >= until, readFailure);
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
    /// <param name="doc">The record as it was last read, or null when there is none.</param>
    /// <param name="seatSessionIds">The seats asked for.</param>
    /// <param name="askedAtUtc">The moment the restore door was asked. A Failure counts as THIS run's answer
    /// only when it was written at or after it; an older one belongs to an earlier attempt and is not an
    /// answer to this one.</param>
    /// <param name="final">The patience has run out, so whatever the record says is the answer.</param>
    /// <param name="readFailure">Why the last read of the record failed, or null when it succeeded. While there
    /// is still time a failed read is nothing at all and is retried; at the deadline it is reported as a record
    /// that could not be read, never as a restore that was refused.</param>
    private static List<SeatRestoreOutcome>? Read(
        WorkspaceDocument? doc,
        IReadOnlyList<string> seatSessionIds,
        DateTime askedAtUtc,
        bool final,
        string? readFailure)
    {
        if (readFailure is not null && !final) return null;

        var outcomes = new List<SeatRestoreOutcome>();
        foreach (var id in seatSessionIds)
        {
            var seat = doc?.Seats.FirstOrDefault(s => string.Equals(s.SessionId, id, StringComparison.OrdinalIgnoreCase));

            // A RESTORED SESSION ID IS AN ANSWER WHENEVER IT IS THERE, without looking at the clock: a seat is
            // restored once and for all, so a restored id from an earlier attempt is still the true answer to
            // "did this session come back".
            if (seat is not null && !string.IsNullOrWhiteSpace(seat.RestoredSessionId))
            {
                outcomes.Add(new SeatRestoreOutcome(id, seat.Name, seat.RestoredSessionId, null, null));
                continue;
            }

            var failure = seat?.Restore?.Failure is { Length: > 0 } f ? f : null;
            var failedThisRun = failure is not null
                && seat!.Restore!.AttemptedAtUtc is { } attempted
                && attempted >= askedAtUtc;

            if (failedThisRun)
                outcomes.Add(new SeatRestoreOutcome(id, seat!.Name, null, null, failure));
            else if (!final)
                return null;
            else if (readFailure is not null)
                outcomes.Add(new SeatRestoreOutcome(id, seat?.Name ?? id, null, null,
                    "the restore was taken, and the record could not be read to say what became of this " +
                    $"session ({readFailure}). It may yet come back; check the session list."));
            else if (failure is not null)
                outcomes.Add(new SeatRestoreOutcome(id, seat?.Name ?? id, null, null,
                    $"the restore was taken, and after {Patience.TotalMinutes:0} minutes the only thing the " +
                    $"record says about this session is from an earlier attempt: {failure} It may yet come " +
                    "back; check the session list."));
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
