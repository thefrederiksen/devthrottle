using System.Collections.Concurrent;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.DevReports;

/// <summary>Whether a session can take the owner's items right now.</summary>
internal enum DevReportSessionReach
{
    /// <summary>Live on the roster and waiting for input: deliver now.</summary>
    Idle,

    /// <summary>Working, or its machine is not connected: hold until the turn ends.</summary>
    Busy,

    /// <summary>The session has ended: refuse new items.</summary>
    Ended,
}

/// <summary>The Gateway's verdict on one session's reach, the id of the Director running it when it is live,
/// and a sentence for the log.</summary>
internal sealed record DevReportSessionLiveness(DevReportSessionReach Reach, string? DirectorId, string Why);

/// <summary>One item's state as the send answers it - exactly the contract's <c>status</c> update.</summary>
internal sealed record DevReportItemUpdate(string Id, string Status, string StatusLabel);

/// <summary>
/// WHEN THE OWNER'S NOTES AND ANSWERS GO INTO THE SESSION (issue #2958, PLAN-phase-2.md "Delivery"). The Gateway
/// owns every ruling here (mission ruling 4):
///
/// <list type="number">
/// <item>An item id the report already holds is the same item. Its CURRENT state is returned and it is never
/// stored or delivered again, whatever its text says now.</item>
/// <item>A later answer to the same question replaces an earlier one still waiting (the store applies it).</item>
/// <item>An ended session refuses every new item, and nothing is stored as deliverable.</item>
/// <item>A working session - or one whose machine is not connected - holds the items. It is never interrupted.
/// An idle one gets them now, in the same request, as ONE prompt.</item>
/// <item>Everything held for a session is SETTLED by one pass, <see cref="SettleAsync"/>: an ended session refuses
/// what it holds, an idle one takes it all as ONE prompt across all its reports, a busy one keeps it. Three
/// things call that pass - the turn end, a Gateway timer, and the owner's own read and send - so no held item
/// depends on a transition the turn-end watcher might never see.</item>
/// <item>Deciding held-or-deliver and settling run under ONE lock per (tenant, session) in this process. That lock
/// keeps this process's own sends in order; it is NOT the at-most-once guarantee, which lives in the database.</item>
/// </list>
///
/// AT MOST ONCE, ACROSS A CRASH AND ACROSS TWO PROCESSES. During a deploy swap two Gateway processes share the
/// database and neither sees the other's lock, so every step of a send is a conditional update in the database:
/// <list type="number">
/// <item>CLAIM. The items a prompt carries move from held to <c>sending</c> stamped with a fresh claim id and time,
/// only where they are still held at that moment. The prompt is composed from the rows this claim took and no
/// others, so an item another process claimed first is never in this prompt.</item>
/// <item>FINISH. After the answer - accepted is delivered; unanswered is delivered-not-confirmed and never retried; a
/// send that never left this Gateway, or a definite Director refusal, typed nothing and goes back to held - the
/// state is written only where the item still carries THIS claim and is still <c>sending</c>.</item>
/// <item>ORPHAN. A settle pass rules an item still <c>sending</c> orphaned - delivered-not-confirmed, never sent again
/// - only once its claim is older than <see cref="SendingClaimTimeout"/>, which is longer than any send can run. That
/// ruling is the same conditional update, so when it lands the late send's own finish writes nothing: exactly one
/// final write wins, and an item is never both delivered and not confirmed.</item>
/// </list>
/// Typing the owner's words into a session twice is worse than telling him we cannot confirm the once.
///
/// KNOWN GAP, NOT CLOSED HERE (review High 3): the idle verdict is read from the pushed roster and the prompt is
/// sent afterwards with <c>WaitForIdle = false</c>, and the Director's prompt verb does not check idle. A turn that
/// starts after that roster read and before the Director writes the prompt receives the owner's words mid-turn.
/// Closing it needs the Director to refuse a prompt to a working session, which ships in a Director release.
/// </summary>
internal sealed class DevReportDelivery
{
    /// <summary>
    /// How old a claim must be before a settle pass may rule its items orphaned. It must be longer than the longest
    /// a send can run, or a settle in another process would rule a send that is still going. A send is one tunnel
    /// command bounded by <see cref="DirectorCommandRouter.DefaultCommandTimeout"/> (30 seconds), plus the database
    /// work around it; five minutes is ten times that bound. The cost of the margin: an item a crash leaves
    /// <c>sending</c> reads "Sending to the session" for up to five minutes before it settles.
    /// </summary>
    public static readonly TimeSpan SendingClaimTimeout = TimeSpan.FromMinutes(5);

    private readonly DevReportStore _store;
    private readonly Func<TenantId, string, DevReportSessionLiveness> _liveness;
    private readonly Func<TenantId, string, SessionVerbClient?> _route;
    private readonly Func<DateTime> _nowUtc;
    private readonly CancellationToken _sendLifetime;
    private readonly Func<TenantId, IDisposable>? _enterTenantScope;
    private readonly ConcurrentDictionary<(string Tenant, string SessionId), SemaphoreSlim> _locks = new();

    /// <param name="store">The record.</param>
    /// <param name="liveness">The session's reach, from the pushed roster and the session history.</param>
    /// <param name="route">A tunnel caller for (tenant, director id), or null when that Director is not connected.</param>
    /// <param name="sendLifetime">The token a claimed send runs on: the GATEWAY's lifetime, never a request's. A
    /// drain that has claimed items runs to its end whoever called it, so an owner whose browser goes away mid-send
    /// cannot strand his items in <c>sending</c> (phase 2 inspection, Medium 1).</param>
    /// <param name="nowUtc">The clock, as a seam.</param>
    /// <param name="enterTenantScope">Enters the account's scope for the send. REQUIRED on a hosted Gateway in
    /// practice: the tunnel refuses a command with no account in scope, and a settle raised by the watcher's
    /// catch-up sweep or the settle timer arrives with none. Optional only because self-host is one partition.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public DevReportDelivery(
        DevReportStore store,
        Func<TenantId, string, DevReportSessionLiveness> liveness,
        Func<TenantId, string, SessionVerbClient?> route,
        CancellationToken sendLifetime,
        Func<DateTime>? nowUtc = null,
        Func<TenantId, IDisposable>? enterTenantScope = null)
    {
        _enterTenantScope = enterTenantScope;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
        _sendLifetime = sendLifetime;
    }

    /// <summary>The session's reach right now, for the report's <c>sessionEnded</c> verdict.</summary>
    public DevReportSessionLiveness Liveness(TenantId tenant, string sessionId) => _liveness(tenant, sessionId);

    /// <summary>
    /// The owner sends items on a report. Answers one update per item, in the order sent.
    /// </summary>
    /// <param name="ct">Cancels only the wait for the session's lock. Once the lock is held the send runs to its end
    /// on the Gateway's lifetime, whatever becomes of the caller.</param>
    /// <param name="senderKind">The credential kind behind the send, recorded on the delivered prompt.</param>
    public async Task<IReadOnlyList<DevReportItemUpdate>> SendAsync(
        TenantId tenant, DevReportEntity report, IReadOnlyList<DevReportItem> items, string senderKind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(items);
        FileLog.Write($"[DevReportDelivery] SendAsync: tenant={tenant.ToLogString()} report={report.Id} sid={report.SessionId} items={items.Count}");

        var gate = LockFor(tenant, report.SessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ids = items.Select(i => i.Id).Distinct(StringComparer.Ordinal).ToList();
            var known = _store.FindItems(tenant, report.Id, ids);

            // Rule 1: an id the report already holds is that item. Within one batch the first occurrence counts.
            var seen = new HashSet<string>(known.Keys, StringComparer.Ordinal);
            var fresh = new List<DevReportItem>();
            foreach (var item in items)
                if (seen.Add(item.Id)) fresh.Add(item);

            var live = _liveness(tenant, report.SessionId);
            var refused = new HashSet<string>(StringComparer.Ordinal);
            if (live.Reach == DevReportSessionReach.Ended)
            {
                // Rule 3: refused, and not stored - a refused item stays in the owner's queue on the page.
                foreach (var item in fresh) refused.Add(item.Id);
                FileLog.Write($"[DevReportDelivery] SendAsync: sid={report.SessionId} has ended ({live.Why}); refused {refused.Count} new item(s)");
            }
            else if (fresh.Count > 0)
            {
                _store.AddItems(tenant, report, fresh, DevReportItemStates.HeldState, senderKind, _nowUtc());
            }

            // Rules 4 and 5: the settle pass delivers to an idle session now, as one prompt with anything else held
            // for it, refuses what an ended session still holds, and leaves a busy session's items held.
            await SettleLockedAsync(tenant, report.SessionId, live).ConfigureAwait(false);

            var after = _store.FindItems(tenant, report.Id, ids);
            var updates = new List<DevReportItemUpdate>(items.Count);
            foreach (var item in items)
            {
                if (after.TryGetValue(item.Id, out var row))
                    updates.Add(new DevReportItemUpdate(item.Id, row.Status, row.StatusLabel));
                else if (refused.Contains(item.Id))
                    updates.Add(new DevReportItemUpdate(item.Id, DevReportItemStates.SessionEndedState.Status,
                        DevReportItemStates.SessionEndedState.Label));
                else
                    throw new InvalidOperationException(
                        $"item {item.Id} on report {report.Id} was neither stored nor refused; the send has no state for it");
            }
            return updates;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// THE ONE SETTLE PASS for a session's unsettled items. Any item left in <c>sending</c> under a claim older than
    /// <see cref="SendingClaimTimeout"/> is settled delivered-not-confirmed and never sent again. Then, by the session's reach: ended refuses every held item
    /// "This session has ended"; idle delivers them all as one prompt; busy leaves them held. Returns how many
    /// items this pass sent.
    /// </summary>
    /// <param name="ct">Cancels only the wait for the session's lock; a claimed send runs on the Gateway's lifetime.</param>
    public async Task<int> SettleAsync(TenantId tenant, string sessionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        sessionId = NormalizeSessionId(sessionId);
        var gate = LockFor(tenant, sessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var live = _liveness(tenant, sessionId);
            return await SettleLockedAsync(tenant, sessionId, live).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> SettleLockedAsync(TenantId tenant, string sessionId, DevReportSessionLiveness live)
    {
        SettleOrphanedSends(tenant, sessionId);

        switch (live.Reach)
        {
            case DevReportSessionReach.Ended:
                RefuseWaitingForEndedSession(tenant, sessionId, live);
                return 0;
            case DevReportSessionReach.Idle:
                return await DrainLockedAsync(tenant, sessionId, live).ConfigureAwait(false);
            case DevReportSessionReach.Busy:
                var waiting = _store.WaitingItemsForSession(tenant, sessionId).Count;
                if (waiting > 0)
                    FileLog.Write($"[DevReportDelivery] Settle: sid={sessionId} {waiting} item(s) stay held, reach={live.Reach} ({live.Why})");
                return 0;
            default:
                throw new InvalidOperationException($"a session reach this delivery does not know: {live.Reach}");
        }
    }

    /// <summary>Items in <c>sending</c> under a claim older than <see cref="SendingClaimTimeout"/> are settled
    /// delivered-not-confirmed and never sent again. A younger claim is left alone: its send may still be running,
    /// in this process or in another one sharing the database.</summary>
    private void SettleOrphanedSends(TenantId tenant, string sessionId)
    {
        var now = _nowUtc();
        var settled = _store.SettleExpiredClaims(tenant, sessionId, now - SendingClaimTimeout, now);
        if (settled > 0)
            FileLog.Write($"[DevReportDelivery] Settle: sid={sessionId} {settled} item(s) were left sending past the claim timeout; " +
                          "settled as sent, not confirmed, and not sent again");
    }

    private void RefuseWaitingForEndedSession(TenantId tenant, string sessionId, DevReportSessionLiveness live)
    {
        var refused = _store.RefuseWaiting(tenant, sessionId, DevReportItemStates.SessionEndedState);
        if (refused > 0)
            FileLog.Write($"[DevReportDelivery] Settle: sid={sessionId} has ended ({live.Why}); refused {refused} held item(s)");
    }

    private async Task<int> DrainLockedAsync(TenantId tenant, string sessionId, DevReportSessionLiveness live)
    {
        var waiting = _store.WaitingItemsForSession(tenant, sessionId).Count;
        if (waiting == 0) return 0;

        if (string.IsNullOrEmpty(live.DirectorId))
        {
            FileLog.Write($"[DevReportDelivery] Drain: sid={sessionId} {waiting} item(s) stay held, no Director named ({live.Why})");
            return 0;
        }

        var route = _route(tenant, live.DirectorId);
        if (route is null)
        {
            FileLog.Write($"[DevReportDelivery] Drain: sid={sessionId} {waiting} item(s) stay held, director {live.DirectorId} is not connected");
            return 0;
        }

        // CLAIM, in the database, BEFORE the prompt is composed: the prompt carries exactly the rows this claim took. A
        // crash from here until the finish below leaves them sending, and a settle pass rules them sent-not-confirmed
        // once the claim is past the timeout - never re-sent.
        var claimId = Guid.NewGuid();
        var open = _store.ClaimWaiting(tenant, sessionId, claimId, _nowUtc());
        if (open.Count == 0)
        {
            FileLog.Write($"[DevReportDelivery] Drain: sid={sessionId} claim={claimId} took nothing; another send claimed the items first");
            return 0;
        }

        string text;
        PromptRequest request;
        try
        {
            (text, request) = ComposePrompt(tenant, open);
        }
        catch
        {
            // Nothing left the Gateway, so the claim is released rather than left to be ruled "sent, not confirmed".
            _store.FinishClaim(tenant, claimId, DevReportItemStates.HeldState, _nowUtc());
            throw;
        }

        // THE ACCOUNT'S SCOPE IS ENTERED FOR THE SEND, HERE, whatever called us. A turn end from a live push arrives
        // inside the tunnel connection's scope and an owner's send inside the request's, but the watcher's catch-up
        // sweep and the settle timer carry none, and the tunnel then drops the command as "never left the Gateway".
        //
        // THE SEND RUNS ON THE GATEWAY'S LIFETIME, NEVER THE CALLER'S TOKEN, and the claim is always finished (phase 2
        // inspection, Medium 1). A request token fired when the owner's browser went away; it used to escape here past
        // every finish and strand the claimed items in "sending" for five minutes, then rule them "sent, not confirmed"
        // though the prompt may never have left. Now: stopping before the send starts typed nothing, so the items go
        // back to held; a send that throws once it has started may have reached the Director, so it is finished as
        // not confirmed - never held, because a held item would be typed a second time.
        SessionVerbClient.PromptSendOutcome sent;
        if (_sendLifetime.IsCancellationRequested)
        {
            _store.FinishClaim(tenant, claimId, DevReportItemStates.HeldState, _nowUtc());
            FileLog.Write($"[DevReportDelivery] Drain: sid={sessionId} claim={claimId} the Gateway is stopping; {open.Count} item(s) back to held, nothing sent");
            _sendLifetime.ThrowIfCancellationRequested();
        }
        using (_enterTenantScope?.Invoke(tenant))
        {
            try
            {
                sent = await route.SendPromptAsync(sessionId, request, _sendLifetime).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var finished = _store.FinishClaim(tenant, claimId,
                    DevReportItemStates.AfterSend(DevReportItemStates.SendOutcome.Unconfirmed), _nowUtc());
                FileLog.Write($"[DevReportDelivery] Drain FAILED: sid={sessionId} claim={claimId} the send threw once started, " +
                              $"{finished} item(s) finished sent-not-confirmed: {ex.Message}");
                throw;
            }
        }
        var outcome = sent.Kind switch
        {
            SessionVerbClient.PromptSendKind.Accepted => DevReportItemStates.SendOutcome.Accepted,
            SessionVerbClient.PromptSendKind.NeverLeftTheGateway => DevReportItemStates.SendOutcome.NeverLeft,
            SessionVerbClient.PromptSendKind.DirectorRefused => DevReportItemStates.SendOutcome.Refused,
            SessionVerbClient.PromptSendKind.Unanswered => DevReportItemStates.SendOutcome.Unconfirmed,
            _ => throw new InvalidOperationException($"a prompt send kind this delivery does not know: {sent.Kind}"),
        };

        // FINISH, only where the items still carry this claim and are still sending. Fewer written than claimed means a
        // settle pass ruled this send orphaned first; that ruling stands and is not overwritten.
        var written = _store.FinishClaim(tenant, claimId, DevReportItemStates.AfterSend(outcome), _nowUtc());
        FileLog.Write($"[DevReportDelivery] Drain: sid={sessionId} claim={claimId} items={open.Count} outcome={outcome} written={written} " +
                      $"chars={text.Length}{(sent.Detail.Length > 0 ? " detail=" + sent.Detail : "")}");
        if (written != open.Count)
            FileLog.Write($"[DevReportDelivery] Drain: sid={sessionId} claim={claimId} {open.Count - written} item(s) were settled by another " +
                          "pass before this send finished; their state was left as that pass wrote it");

        if (outcome == DevReportItemStates.SendOutcome.Refused)
        {
            // The Director refused and typed nothing, so the items are held again. Settle on what the Gateway knows now:
            // an ended session refuses them. Otherwise they wait for the next settle pass - never re-sent in this one, so
            // a Director that keeps refusing a session the roster still shows idle cannot loop here.
            var now = _liveness(tenant, sessionId);
            if (now.Reach == DevReportSessionReach.Ended)
                RefuseWaitingForEndedSession(tenant, sessionId, now);
            else
                FileLog.Write($"[DevReportDelivery] Drain: sid={sessionId} the Director refused; {written} item(s) held for the next settle pass, reach={now.Reach} ({now.Why})");
        }

        return outcome is DevReportItemStates.SendOutcome.Accepted or DevReportItemStates.SendOutcome.Unconfirmed ? written : 0;
    }

    private (string Text, PromptRequest Request) ComposePrompt(TenantId tenant, IReadOnlyList<DevReportItemEntity> open)
    {
        var reports = new List<DevReportPromptFold.FoldReport>();
        foreach (var group in open.GroupBy(i => i.ReportId))
        {
            var report = _store.Get(tenant, group.Key)
                ?? throw new InvalidOperationException($"held items name report {group.Key}, which the account does not hold");
            var foldItems = group
                .Select(row => new DevReportPromptFold.FoldItem(ToItem(row),
                    row.Kind == DevReportItem.Answer && _store.HasDeliveredAnswer(tenant, report.Id, row.QuestionId)))
                .ToList();
            reports.Add(new DevReportPromptFold.FoldReport(report.Id, report.Key, report.Title, report.Version, foldItems));
        }

        var text = DevReportPromptFold.Compose(reports, DevReportPromptFold.MintBoundary(reports));
        var senders = open.Select(i => i.SenderKind).Distinct(StringComparer.Ordinal).ToList();
        var request = new PromptRequest
        {
            Text = text,
            AppendEnter = true,
            WaitForIdle = false,
            // The owner's turn: not an agent prompting another, so the Director counts it as the owner's.
            AgentDriven = false,
            Provenance = new SubmissionProvenanceDto
            {
                Route = SubmissionRoutes.GatewayDevReport,
                IdentityKind = senders.Count == 1 && senders[0].Length > 0 ? senders[0] : SubmissionIdentityKinds.Unknown,
            },
        };
        return (text, request);
    }

    /// <summary>A session id in the one form reports are stored under: a GUID's canonical lower-case form, the
    /// form a session key's identity carries. The roster and the path can spell the same session differently.</summary>
    public static string NormalizeSessionId(string sessionId)
        => Guid.TryParse(sessionId, out var id) ? id.ToString("D") : sessionId;

    private SemaphoreSlim LockFor(TenantId tenant, string sessionId)
        => _locks.GetOrAdd((tenant.Value, sessionId), _ => new SemaphoreSlim(1, 1));

    private static DevReportItem ToItem(DevReportItemEntity row) => new(
        row.ClientItemId,
        row.Kind,
        row.Text,
        row.AnchorJson is null ? null : DevReportAnchor.FromJson(row.AnchorJson),
        row.QuestionId,
        row.Question,
        row.OptionValue,
        row.OptionLabel,
        row.Comment);
}
