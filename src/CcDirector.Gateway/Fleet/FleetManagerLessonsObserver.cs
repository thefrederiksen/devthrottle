using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// KEEPS THE MARKED FLEET MANAGER'S DIRECTOR HOLDING THE CURRENT LESSONS (issue #3559), so a compaction or a clear
/// injects them through the session-start hook. The owner's complaint was a Fleet Manager that forgot a correction;
/// a compaction is the moment that happens, and it is the one moment neither the first prompt nor a marked event
/// reaches.
///
/// THE GATEWAY STAMPS; THE DIRECTOR CARRIES - the same split as <see cref="FleetRoleObserver"/>. The finished block
/// (<see cref="FleetManagerLessons.Build"/>) is sent down with the <c>set-fleet-manager-lessons</c> verb; the Director
/// stores it on the session and puts it first in the preamble file it already maintains. It never sees a row.
///
/// WHEN IT IS SENT, so the preamble is current when the compaction happens:
///  - the lessons changed (<see cref="LessonsChanged"/>, called by every route that writes a lesson);
///  - the marked session is pushed and the Director does not yet hold this block (<see cref="Observe"/>): its first
///    push after a mark moves, or after a send that found no stream;
///  - the first snapshot of a Director's NEW connection (<see cref="DirectorReconnected"/>): the Director dropped every
///    session's lessons when that connection opened, so it is forgotten and stamped again. Never the ten-second re-push;
///  - the Fleet Manager reconcile, on its interval (<see cref="Refresh"/>), as the backstop for a mark moved by hand to
///    a session that has not pushed since.
///
/// ONE SEND PER CHANGE. What each account's Fleet Manager Director was last sent is remembered, and a push that
/// changes nothing sends nothing. A refusal (a Director older than this verb) is remembered too, so it is not asked
/// again on every push; it is asked again when the lessons change or it reconnects.
///
/// THE OLD FLEET MANAGER IS CLEARED when the mark moves away from a session this observer gave lessons to, so a
/// session that is no longer the Fleet Manager is not told to obey the Fleet Manager's lessons.
/// </summary>
public sealed class FleetManagerLessonsObserver
{
    public const string Verb = "set-fleet-manager-lessons";

    /// <summary>How long a push may reuse the account's marked session id before reading it again. Every push of every
    /// session asks; without this each one would be a database read. The backstop refresh always reads it fresh.</summary>
    public static readonly TimeSpan MarkedIdReuse = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<TenantId, string?> _markedSessionId;
    private readonly Func<TenantId, string?> _lessonsBlock;
    private readonly Func<TenantId, string, string?> _directorOf;
    private readonly Func<string, DirectorCommand, CancellationToken, Task<DirectorCommandResult?>> _sendCommand;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TenantId, string, string, bool>? _isSessionOfDirector;

    // Per account: the marked session id the push path last read, and when.
    private readonly ConcurrentDictionary<TenantId, (string? Id, DateTime ReadAtUtc)> _marked = new();

    // Per account: the block each session was last stamped with, and on which Director. Written only after the
    // Director answered (accepted or refused), so a send with no stream is retried.
    private readonly ConcurrentDictionary<TenantId, ConcurrentDictionary<string, (string DirectorId, string Block)>> _sent = new();

    // Per account: the block as it is now, read once and kept until a lesson changes.
    private readonly ConcurrentDictionary<TenantId, string> _blocks = new();

    /// <param name="markedSessionId">The account's marked Fleet Manager session, or null.</param>
    /// <param name="lessonsBlock">The account's confirmed lessons block as it is now, or null when it has none.</param>
    /// <param name="directorOf">The Director that holds a session of the account, however stale, or null.</param>
    /// <param name="sendCommand">The down-channel command sender. A null result means that Director has no stream.</param>
    /// <param name="utcNow">The clock the marked-id reuse is measured on; the system clock when null.</param>
    /// <param name="isSessionOfDirector">Whether a session, given (tenant, directorId, sessionId), is that Director's own,
    /// asked before a PUSH stamps the marked session down to the pushing Director (devthrottle_internal#2311, #3552
    /// review: the Fleet Manager lessons). In a team any Director may list any session id, so a colleague's Director
    /// listing the marked id would otherwise be sent the lessons. Production asks the session store's one answer for which
    /// Director holds a session (PushedSessionStore.IsHoldersRow, OR-F1); outside a team it answers yes. Null takes every
    /// push, as before.</param>
    public FleetManagerLessonsObserver(Func<TenantId, string?> markedSessionId, Func<TenantId, string?> lessonsBlock,
        Func<TenantId, string, string?> directorOf,
        Func<string, DirectorCommand, CancellationToken, Task<DirectorCommandResult?>> sendCommand,
        Func<DateTime>? utcNow = null,
        Func<TenantId, string, string, bool>? isSessionOfDirector = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _isSessionOfDirector = isSessionOfDirector;
        _markedSessionId = markedSessionId ?? throw new ArgumentNullException(nameof(markedSessionId));
        _lessonsBlock = lessonsBlock ?? throw new ArgumentNullException(nameof(lessonsBlock));
        _directorOf = directorOf ?? throw new ArgumentNullException(nameof(directorOf));
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));
    }

    /// <summary>
    /// Which Director a session's lessons go to, when not every Director that lists its id is the session's own
    /// (devthrottle_internal#2311): the roster's own answer <paramref name="located"/> when it is the session's, otherwise
    /// the first other holder, then the Director the session's key row names, that is. Null when none is - never merely
    /// whichever Director lists the id. <paramref name="isOwn"/> is the one team rule, asked per candidate.
    /// </summary>
    public static string? OwnDirectorOf(string? located, IEnumerable<string> holders, string? keyed, Func<string, bool> isOwn)
    {
        ArgumentNullException.ThrowIfNull(holders);
        ArgumentNullException.ThrowIfNull(isOwn);
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(located)) candidates.Add(located);
        candidates.AddRange(holders);
        if (!string.IsNullOrEmpty(keyed)) candidates.Add(keyed);
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
            if (isOwn(candidate))
                return candidate;
        return null;
    }

    /// <summary>A lesson was kept, confirmed, edited or removed: read the block again and stamp it down now.</summary>
    public async Task LessonsChanged(TenantId tenant)
    {
        FileLog.Write($"[FleetManagerLessonsObserver] LessonsChanged: tenant={tenant.ToLogString()}");
        try
        {
            await Refresh(tenant).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Called by a route AFTER the lesson was saved: a failed read here, of the mark or of the block, must not
            // answer the owner with an error for a write that succeeded (a retry would keep the lesson twice), and is
            // logged rather than lost in a discarded task. The backstop refresh tries again.
            FileLog.Write($"[FleetManagerLessonsObserver] LessonsChanged FAILED: tenant={tenant.ToLogString()}: {ex.Message}");
        }
    }

    /// <summary>One session was pushed. Only the marked Fleet Manager matters; every other push costs a comparison.</summary>
    public Task Observe(TenantId tenant, string directorId, string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(directorId)) return Task.CompletedTask;
        return ObserveMarkedAsync(tenant, directorId, sessionId);
    }

    private async Task ObserveMarkedAsync(TenantId tenant, string directorId, string sessionId)
    {
        try
        {
            var marked = MarkedForPush(tenant);
            if (!SameId(marked, sessionId)) return;
            // Only the marked session's own Director is stamped: a Director that merely lists its id is not.
            if (_isSessionOfDirector is not null && !_isSessionOfDirector(tenant, directorId, marked!))
            {
                FileLog.Write($"[FleetManagerLessonsObserver] Observe IGNORED: tenant={tenant.ToLogString()}, director={directorId} lists the marked session {marked} but it is not that Director's own");
                return;
            }
            await StampAsync(tenant, directorId, marked!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Called on the hub's push path: a failed read, of the mark or of the block, must never fail the push. The
            // backstop refresh tries again.
            FileLog.Write($"[FleetManagerLessonsObserver] Observe FAILED: tenant={tenant.ToLogString()}, sid={sessionId}: {ex.Message}");
        }
    }

    /// <summary>The first snapshot of a NEW connection of a Director arrived (<c>PushedSessionStore
    /// .SessionsArrivedOnNewConnection</c>) - never the ten-second re-push on the same connection. The Director dropped
    /// every session's lessons when that connection opened, so it holds none: forget what it was sent, and stamp the
    /// marked Fleet Manager again wherever it is.</summary>
    public async Task DirectorReconnected(TenantId tenant, string directorId)
    {
        ForgetDirector(tenant, directorId);
        try
        {
            await Refresh(tenant).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Raised on the pushing Director's own call: it must never fail the push. The backstop refresh tries again.
            FileLog.Write($"[FleetManagerLessonsObserver] DirectorReconnected FAILED: tenant={tenant.ToLogString()}, director={directorId}: {ex.Message}");
        }
    }

    /// <summary>The backstop: make sure the marked Fleet Manager's Director holds the current block, and that a session
    /// no longer marked holds none.</summary>
    public Task Refresh(TenantId tenant)
    {
        // Read both again: a refresh is the answer to a block or a mark that may have changed since they were cached.
        _blocks.TryRemove(tenant, out _);
        var marked = _markedSessionId(tenant);
        _marked[tenant] = (marked, _utcNow());
        if (string.IsNullOrEmpty(marked)) return ClearFormerAsync(tenant, null);
        var director = _directorOf(tenant, marked);
        return director is null ? ClearFormerAsync(tenant, marked) : StampAsync(tenant, director, marked);
    }

    /// <summary>Forget what one Director was sent, so the next push of the marked session stamps it again.</summary>
    public void ForgetDirector(TenantId tenant, string directorId)
    {
        if (!_sent.TryGetValue(tenant, out var sent)) return;
        foreach (var (sid, entry) in sent)
            if (string.Equals(entry.DirectorId, directorId, StringComparison.Ordinal))
                sent.TryRemove(sid, out _);
    }

    private async Task StampAsync(TenantId tenant, string directorId, string marked)
    {
        await ClearFormerAsync(tenant, marked).ConfigureAwait(false);
        var block = _blocks.GetOrAdd(tenant, t => _lessonsBlock(t) ?? "");
        var sent = _sent.GetOrAdd(tenant, _ => new ConcurrentDictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase));
        if (sent.TryGetValue(marked, out var last) && last.DirectorId == directorId && last.Block == block) return;
        // A Fleet Manager that was never given lessons, while there are none, needs no empty stamp.
        if (block.Length == 0 && !sent.ContainsKey(marked)) return;
        if (await SendAsync(tenant, directorId, marked, block).ConfigureAwait(false))
            sent[marked] = (directorId, block);
    }

    /// <summary>Clear the lessons from every session this observer gave them to that is no longer the marked one.</summary>
    private async Task ClearFormerAsync(TenantId tenant, string? marked)
    {
        if (!_sent.TryGetValue(tenant, out var sent)) return;
        foreach (var (sid, entry) in sent.ToArray())
        {
            if (SameId(sid, marked) || entry.Block.Length == 0) continue;
            if (await SendAsync(tenant, entry.DirectorId, sid, "").ConfigureAwait(false))
                sent.TryRemove(sid, out _);
        }
    }

    /// <returns>True when the Director answered, accepted or refused; false when it has no stream (retry later).</returns>
    private async Task<bool> SendAsync(TenantId tenant, string directorId, string sessionId, string block)
    {
        try
        {
            var command = new DirectorCommand
            {
                CommandId = Guid.NewGuid().ToString("N"),
                Verb = Verb,
                SessionId = sessionId,
                PayloadJson = JsonSerializer.Serialize(
                    new SetFleetManagerLessonsRequest { Lessons = block.Length == 0 ? null : block }, JsonOptions),
            };
            var result = await _sendCommand(directorId, command, CancellationToken.None).ConfigureAwait(false);
            if (result is null)
            {
                FileLog.Write($"[FleetManagerLessonsObserver] sid={sessionId}: no stream for director={directorId}; lessons not delivered, "
                              + "retried on the session's next push or by the reconcile");
                return false;
            }
            if (result.Status != DirectorCommandStatus.Ok)
            {
                // Most likely a Director older than this verb. Remembered so it is not asked on every push; a compaction
                // on it injects no lessons until it is updated, and the workflow's start routine re-reads them.
                FileLog.Write($"[FleetManagerLessonsObserver] sid={sessionId}: director={directorId} REFUSED the lessons: "
                              + $"{result.Status} {result.Error}");
                return true;
            }
            FileLog.Write($"[FleetManagerLessonsObserver] tenant={tenant.ToLogString()}, sid={sessionId}: lessons "
                          + $"{(block.Length == 0 ? "cleared" : "stamped, " + block.Length + " characters")} on director={directorId}");
            return true;
        }
        catch (Exception ex)
        {
            // Fire-and-forget off the hub's push path: a failed send must not fail the push, and is not recorded as sent.
            FileLog.Write($"[FleetManagerLessonsObserver] sid={sessionId}: lessons send FAILED for director={directorId}: {ex.Message}");
            return false;
        }
    }

    private string? MarkedForPush(TenantId tenant)
    {
        var now = _utcNow();
        if (_marked.TryGetValue(tenant, out var cached) && now - cached.ReadAtUtc < MarkedIdReuse) return cached.Id;
        var marked = _markedSessionId(tenant);
        _marked[tenant] = (marked, now);
        return marked;
    }

    private static bool SameId(string? a, string? b)
        => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
