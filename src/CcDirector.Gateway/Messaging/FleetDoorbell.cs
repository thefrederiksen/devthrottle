using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Messaging;

/// <summary>
/// WHEN A MESSAGE IS DUE A RING, AND WHEN IT IS STUCK (the Message Load mission, slice 2, rulings 3 and 11).
/// Pure functions of one row, the clock and the limits, so the schedule is provable without a database, a
/// Director or a timer.
///
/// A message is DUE when it has never been rung, or when its last ring is at least
/// <see cref="FleetMessageLimits.RingGrace"/> old - and it has been rung fewer than
/// <see cref="FleetMessageLimits.StuckAfterRings"/> times.
///
/// A message is STUCK when it has been rung <see cref="FleetMessageLimits.StuckAfterRings"/> times and the
/// grace after the LAST of those rings has passed without a read. "Stuck after three unanswered rings" is read
/// literally: the third ring is not unanswered until its grace is over. So with the product's numbers a
/// message is rung at 0, 5 and 10 minutes and marked stuck at 15 - never rung a fourth time.
///
/// Both rules count RINGS, not attempts. A ring the Director deferred (the session was working, or the owner
/// had text in the composer) typed nothing, so it is not a ring the recipient failed to answer, and it does
/// not move a message towards stuck.
/// </summary>
public static class FleetRingSchedule
{
    /// <summary>Is this open message due a ring now?</summary>
    public static bool IsDue(FleetMessageEntity message, DateTime nowUtc, FleetMessageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(limits);
        if (message.ReadAtUtc is not null || message.StuckAtUtc is not null) return false;
        if (message.RingCount >= limits.StuckAfterRings) return false;
        return message.LastRungAtUtc is not { } last || nowUtc - last >= limits.RingGrace;
    }

    /// <summary>Has this open message used all its rings and the grace after the last one?</summary>
    public static bool IsStuck(FleetMessageEntity message, DateTime nowUtc, FleetMessageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(limits);
        if (message.ReadAtUtc is not null || message.StuckAtUtc is not null) return false;
        if (message.RingCount < limits.StuckAfterRings) return false;
        return message.LastRungAtUtc is { } last && nowUtc - last >= limits.RingGrace;
    }
}

/// <summary>What the Gateway knows about the session a ring is for, from the pushed roster.</summary>
/// <param name="DirectorId">The Director that owns the session - the one that is asked to ring.</param>
/// <param name="ActivityState">The session's last pushed activity state.</param>
/// <param name="Name">Its roster name, or null.</param>
public sealed record FleetRingTarget(string DirectorId, string ActivityState, string? Name);

/// <summary>What one attempt to ring a session came to. Returned for the log and for tests.</summary>
public enum FleetRingAttempt
{
    /// <summary>The Director typed the doorbell line.</summary>
    Rung,

    /// <summary>The Director looked and declined; nothing was typed.</summary>
    Deferred,

    /// <summary>Nothing is open in the session's inbox.</summary>
    NothingOpen,

    /// <summary>Messages are open but none is due yet (inside the grace after its last ring).</summary>
    NotDue,

    /// <summary>The session is not on a connected Director's roster.</summary>
    NotConnected,

    /// <summary>The Gateway's roster says the session is working; the settled edge will ask again.</summary>
    SkippedWorking,

    /// <summary>The Gateway's roster says the session has exited. A dead session is never rung.</summary>
    SkippedExited,

    /// <summary>The Director did not answer (no stream, timed out, or refused the verb).</summary>
    Unreachable,

    /// <summary>A ring for this session is already in flight.</summary>
    AlreadyRinging,

    /// <summary>The Director answered with an outcome or a deferral reason the contract does not name. Refused and
    /// logged; not a ring.</summary>
    InvalidAnswer,
}

/// <summary>
/// THE DOORBELL'S GATEWAY HALF (the Message Load mission, slice 2). For every session with open messages it
/// asks the owning Director to ring - on the session's settled edge, and on a heartbeat - and it turns
/// unanswered rings into a stuck record and a notice to the sender.
///
/// THE GATEWAY DECIDES WHEN TO ASK; THE DIRECTOR DECIDES WHETHER IT IS SAFE (ruling 7). The Gateway's activity
/// state is not trusted to say "safe to type" - one blue flip in six is a repaint (issue 2853) - so this class
/// only uses it to avoid asking pointlessly (a session the roster says is working will produce a settled edge,
/// and an exited one is never rung). The check that decides whether a line is typed runs on the Director,
/// against the terminal it can see.
///
/// A RING COVERS EVERY UNREAD MESSAGE, AND IS COUNTED ONLY ON THE ONES THAT WERE DUE. The doorbell line says how
/// many messages wait, and one ring is enough for all of them; but a message rung two minutes ago that is
/// carried along by a new message's first ring has not had its grace, so its count does not move. That keeps
/// every message's stuck clock its own.
///
/// STUCK (ruling 11): marked on the heartbeat, under the store lock, only for a message still unread. The sender
/// gets one system notice per stuck message, written by the Gateway (no sender, exempt from the relationship and
/// rate rules). A notice that is itself stuck produces no further notice - it has nobody to report to. A read
/// undoes stuck (see <see cref="FleetMessageStore.ReadInbox"/>).
///
/// EVERY DECISION LEAVES A LOG LINE, so "why was this session never rung" is answerable from the Gateway log.
/// </summary>
public sealed class FleetDoorbell
{
    /// <summary>The heartbeat cadence the Gateway runs <see cref="SweepAsync"/> on - the same fifteen seconds as
    /// the Directors' own heartbeat and the turn-end reconcile.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>How many Directors one heartbeat asks at the same time (inspection 4, ruling 6).</summary>
    public const int RingParallelism = 8;

    /// <summary>How long one ring may take before the heartbeat stops waiting for it and counts it unreachable.</summary>
    public static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Ask one Director to ring one session. Null when the Director could not be reached or refused.</summary>
    public delegate Task<FleetRingResponse?> RingAsync(
        TenantId tenant, string directorId, string sessionId, int unreadCount, CancellationToken ct);

    /// <summary>Run a pass once per tenant, inside that tenant's scope, passing the tenant.</summary>
    public delegate Task ForEachTenantAsync(Func<TenantId, Task> pass, CancellationToken ct);

    private readonly FleetMessageStore _store;
    private readonly FleetMessageService _messages;
    private readonly Func<TenantId, string, FleetRingTarget?> _locate;
    private readonly RingAsync _ring;
    private readonly ForEachTenantAsync _forEachTenant;
    private readonly FleetMessageLimits _limits;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _ringTimeout;
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), byte> _inFlight = new();

    /// <param name="store">The inbox.</param>
    /// <param name="messages">Writes the stuck notice through the one send path.</param>
    /// <param name="locate">Where a session lives, from the pushed roster; null when it is not on a connected Director.</param>
    /// <param name="ring">Sends the <c>ring</c> verb.</param>
    /// <param name="forEachTenant">The per-tenant pass the heartbeat runs in.</param>
    /// <param name="limits">The grace and the ring count; the product's when null. A proof run may shorten the grace.</param>
    /// <param name="clock">The clock; injected so the schedule is deterministic in tests.</param>
    /// <param name="ringTimeout">How long one ring may take; <see cref="RingTimeout"/> when null.</param>
    public FleetDoorbell(
        FleetMessageStore store,
        FleetMessageService messages,
        Func<TenantId, string, FleetRingTarget?> locate,
        RingAsync ring,
        ForEachTenantAsync forEachTenant,
        FleetMessageLimits? limits = null,
        Func<DateTime>? clock = null,
        TimeSpan? ringTimeout = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _locate = locate ?? throw new ArgumentNullException(nameof(locate));
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        _forEachTenant = forEachTenant ?? throw new ArgumentNullException(nameof(forEachTenant));
        _limits = limits ?? FleetMessageLimits.Default;
        _clock = clock ?? (() => DateTime.UtcNow);
        _ringTimeout = ringTimeout ?? RingTimeout;
    }

    /// <summary>The limits this doorbell schedules with.</summary>
    public FleetMessageLimits Limits => _limits;

    /// <summary>
    /// The settled edge: a session's turn just ended. Ring it if anything is due. Returns at once; the ring runs
    /// in the background, inside whatever tenant scope the caller is in.
    /// </summary>
    public void OnSettled(TenantId tenant, string sessionId)
    {
        if (!tenant.IsValid || string.IsNullOrWhiteSpace(sessionId)) return;
        _ = RingOnSettledAsync(tenant, sessionId);
    }

    private async Task RingOnSettledAsync(TenantId tenant, string sessionId)
    {
        try
        {
            await RingSessionAsync(tenant, sessionId, "settled", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetDoorbell] settled-edge ring FAILED: sid={Short(sessionId)}: {ex.Message}");
        }
    }

    /// <summary>
    /// The heartbeat: in every tenant, mark what is stuck and notify its senders, then ring every session that has
    /// a message due.
    /// </summary>
    public Task SweepAsync(CancellationToken ct = default) =>
        _forEachTenant(tenant => SweepTenantAsync(tenant, ct), ct);

    /// <summary>
    /// ONE TENANT'S HEARTBEAT, BOUNDED (inspection 4, ruling 6). Whatever the backlog, the database is read a
    /// fixed number of times: the stuck scan (rows at the ring cap only), one read of every unread message with its
    /// recipient and without its text, and - when anything was rung - one read and one save to record the rings.
    /// The rings themselves run <see cref="RingParallelism"/> at a time, each abandoned after the ring timeout,
    /// so one slow Director cannot hold up the rest. The tick logs how long it took.
    /// </summary>
    internal async Task SweepTenantAsync(TenantId tenant, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var stuck = MarkStuckAndNotify(tenant);
        var now = _clock();
        var unread = _store.UnreadForScheduling(tenant);
        var plans = unread
            .GroupBy(m => m.RecipientSessionId, StringComparer.Ordinal)
            .Select(g => new RingPlan(g.Key, g.Where(m => m.StuckAtUtc is null).ToList(), g.Count()))
            .Where(p => p.Open.Count > 0)
            .ToList();

        var held = new ConcurrentBag<(TenantId, string)>();
        var rung = new ConcurrentBag<(string Sid, IReadOnlyCollection<string> Due, int Unread)>();
        var attempts = new ConcurrentDictionary<FleetRingAttempt, int>();
        try
        {
            await Parallel.ForEachAsync(plans,
                new ParallelOptions { MaxDegreeOfParallelism = RingParallelism, CancellationToken = ct },
                async (plan, token) =>
                {
                    var key = (tenant, plan.SessionId);
                    FleetRingAttempt attempt;
                    if (!_inFlight.TryAdd(key, 0))
                    {
                        attempt = FleetRingAttempt.AlreadyRinging;
                    }
                    else
                    {
                        // The key stays held until the ring is RECORDED below, so a settled edge in between cannot
                        // ring the same due message a second time.
                        held.Add(key);
                        var (a, due) = await RingPlannedAsync(tenant, plan, "heartbeat", now, token).ConfigureAwait(false);
                        attempt = a;
                        if (a == FleetRingAttempt.Rung) rung.Add((plan.SessionId, due, plan.UnreadCount));
                    }
                    attempts.AddOrUpdate(attempt, 1, (_, n) => n + 1);
                }).ConfigureAwait(false);

            if (!rung.IsEmpty)
            {
                BeforeRingsRecorded?.Invoke();
                var marked = _store.MarkRungMany(tenant,
                    rung.Select(r => (r.Sid, r.Due)).ToList(), now, _limits.StuckAfterRings);
                foreach (var r in rung)
                    FileLog.Write($"[FleetDoorbell] RUNG: sid={Short(r.Sid)} trigger=heartbeat unread={r.Unread} ids=[{string.Join(",", r.Due)}]");
                FileLog.Write($"[FleetDoorbell] heartbeat recorded {marked} ring(s) for {rung.Count} session(s)");
            }
        }
        finally
        {
            foreach (var key in held) _inFlight.TryRemove(key, out _);
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            FileLog.Write($"[FleetDoorbell] heartbeat tick: tenant={tenant} stuck={stuck.Count} unread={unread.Count} " +
                          $"sessions={plans.Count} {string.Join(" ", attempts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))} " +
                          $"took={ms:0}ms");
        }
    }

    /// <summary>Test seam: runs after a heartbeat's rings have answered and before they are recorded, while every
    /// rung session is still held. Production never sets it.</summary>
    internal Action? BeforeRingsRecorded { get; set; }

    /// <summary>One session's part of a heartbeat: its open messages and how many are unread in all.</summary>
    private sealed record RingPlan(string SessionId, IReadOnlyList<FleetMessageEntity> Open, int UnreadCount);

    /// <summary>
    /// Ring one session if anything in its inbox is due. The trigger is only for the log.
    /// </summary>
    public async Task<FleetRingAttempt> RingSessionAsync(TenantId tenant, string sessionId, string trigger, CancellationToken ct)
    {
        var key = (tenant, sessionId.Trim().ToLowerInvariant());
        if (!_inFlight.TryAdd(key, 0))
            return FleetRingAttempt.AlreadyRinging;
        try
        {
            var sid = key.Item2;
            var now = _clock();
            var open = _store.OpenMessagesFor(tenant, sid);
            if (open.Count == 0) return FleetRingAttempt.NothingOpen;
            var plan = new RingPlan(sid, open, _store.CountUnread(tenant, sid));
            var (attempt, due) = await RingPlannedAsync(tenant, plan, trigger, now, ct).ConfigureAwait(false);
            if (attempt == FleetRingAttempt.Rung)
            {
                var marked = _store.MarkRung(tenant, sid, due, now, _limits.StuckAfterRings);
                FileLog.Write($"[FleetDoorbell] RUNG: sid={Short(sid)} trigger={trigger} unread={plan.UnreadCount} counted={marked} ids=[{string.Join(",", due)}]");
            }
            return attempt;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Decide and ask, and return what came of it with the ids that were due. Writes nothing: the caller records a
    /// ring, so the heartbeat can record all of its rings in one save.
    /// </summary>
    private async Task<(FleetRingAttempt Attempt, IReadOnlyCollection<string> Due)> RingPlannedAsync(
        TenantId tenant, RingPlan plan, string trigger, DateTime now, CancellationToken ct)
    {
        var sid = plan.SessionId;
        if (plan.Open.Count == 0) return (FleetRingAttempt.NothingOpen, []);

        var due = plan.Open.Where(m => FleetRingSchedule.IsDue(m, now, _limits)).Select(m => m.MessageId).ToList();
        if (due.Count == 0) return (FleetRingAttempt.NotDue, due);

        var target = _locate(tenant, sid);
        if (target is null)
        {
            FileLog.Write($"[FleetDoorbell] ring SKIPPED (not connected): sid={Short(sid)} trigger={trigger} due={due.Count}");
            return (FleetRingAttempt.NotConnected, due);
        }

        var activity = (target.ActivityState ?? "").Trim();
        if (activity.Equals("Exited", StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[FleetDoorbell] ring SKIPPED (exited): sid={Short(sid)} trigger={trigger} due={due.Count}");
            return (FleetRingAttempt.SkippedExited, due);
        }
        if (activity.Equals("Working", StringComparison.OrdinalIgnoreCase)
            || activity.Equals("Starting", StringComparison.OrdinalIgnoreCase))
        {
            // Not logged: a long turn would write this line every heartbeat. The settled edge asks again.
            return (FleetRingAttempt.SkippedWorking, due);
        }

        // The line says how many messages wait, stuck ones included - they are still in the inbox and a read
        // returns them.
        var answer = await AskWithTimeoutAsync(tenant, target.DirectorId, sid, plan.UnreadCount, trigger, ct).ConfigureAwait(false);
        if (answer is null)
        {
            FileLog.Write($"[FleetDoorbell] ring UNREACHABLE: sid={Short(sid)} director={Short(target.DirectorId)} trigger={trigger}");
            return (FleetRingAttempt.Unreachable, due);
        }

        if (string.Equals(answer.Outcome, FleetRingOutcomes.Rung, StringComparison.Ordinal))
            return (FleetRingAttempt.Rung, due);

        // THE WIRE IS CHECKED, NOT TRUSTED (inspection 4, ruling 7). A Director that says something the contract
        // does not name - a renamed reason, a new outcome from a newer build - is refused and logged, never read as
        // a deferral or a ring.
        if (!string.Equals(answer.Outcome, FleetRingOutcomes.Deferred, StringComparison.Ordinal)
            || !FleetRingDeferReasons.IsKnown(answer.Reason))
        {
            FileLog.Write($"[FleetDoorbell] ring answer REFUSED (not in the contract): sid={Short(sid)} trigger={trigger} " +
                          $"outcome=\"{answer.Outcome}\" reason=\"{answer.Reason}\" detail={answer.Detail}");
            return (FleetRingAttempt.InvalidAnswer, due);
        }

        FileLog.Write($"[FleetDoorbell] ring DEFERRED ({answer.Reason}): sid={Short(sid)} trigger={trigger} unread={plan.UnreadCount} detail={answer.Detail}");
        return (FleetRingAttempt.Deferred, due);
    }

    /// <summary>Send one ring, and stop waiting for it after the ring timeout. A ring that answers late is not
    /// counted; if the Director did type the line, the next ring may type a second one, which ruling 8 calls
    /// harmless.</summary>
    private async Task<FleetRingResponse?> AskWithTimeoutAsync(
        TenantId tenant, string directorId, string sid, int unread, string trigger, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_ringTimeout);
        try
        {
            return await _ring(tenant, directorId, sid, unread, cts.Token).WaitAsync(_ringTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            FileLog.Write($"[FleetDoorbell] ring TIMED OUT after {_ringTimeout.TotalSeconds:0.#}s: sid={Short(sid)} director={Short(directorId)} trigger={trigger}");
            return null;
        }
    }

    /// <summary>
    /// Mark every message in this tenant that has used its rings as stuck, and queue one notice to each sender -
    /// in ONE write (inspection 4, ruling 4): a failure anywhere before the save leaves every message open, and
    /// the next heartbeat marks and notifies it exactly once. Returns the rows marked.
    /// </summary>
    public IReadOnlyList<FleetMessageEntity> MarkStuckAndNotify(TenantId tenant)
    {
        var now = _clock();
        var marked = _store.MarkStuckWithNotices(
            tenant,
            now,
            m => FleetRingSchedule.IsStuck(m, now, _limits),
            m => m.SenderSessionId is null
                ? null // a stuck system notice has nobody to tell
                : new FleetMessageDraft(m.SenderSessionId, null, null, null, FleetMessageKinds.System,
                    StuckNoticeText(m, _locate(tenant, m.RecipientSessionId)?.Name, _limits)),
            minRings: _limits.StuckAfterRings,
            limits: _messages.Limits);
        foreach (var (m, notice) in marked)
        {
            FileLog.Write($"[FleetDoorbell] STUCK: id={m.MessageId} to={Short(m.RecipientSessionId)} from={Short(m.SenderSessionId)} " +
                          $"rings={m.RingCount} notice={notice?.MessageId ?? "(none)"}");
        }
        return marked.Select(x => x.Stuck).ToList();
    }

    /// <summary>The notice a sender receives when its message is marked stuck.</summary>
    public static string StuckNoticeText(FleetMessageEntity message, string? recipientName, FleetMessageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(limits);
        var who = string.IsNullOrWhiteSpace(recipientName)
            ? Short(message.RecipientSessionId)
            : $"{recipientName} ({Short(message.RecipientSessionId)})";
        return $"Your message {message.MessageId} to {who} is stuck: its doorbell rang {message.RingCount} times, " +
               $"{FleetMessagePolicy.Describe(limits.RingGrace)} apart, and the session has not read its inbox. " +
               "The message stays in that inbox and is delivered if the session reads it. Do not send it again. " +
               "If you needed an answer, carry on without it and say so in your report.";
    }

    private static string Short(string? id) => string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 8 ? id : id[..8]);
}
