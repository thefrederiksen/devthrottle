using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Receipts;

/// <summary>
/// THE API-MODE RECEIPT STORE (mission #4070, phase 2; the event model is
/// <c>docs/missions/api-mode-pi-2026-10-16/event-model.md</c>). One durable row per prompt a Director handled for a
/// session in API mode, per tenant: Queued, then Delivered (with the agent's own acknowledgement, verbatim) or Refused
/// (with the reason and who refused), and beside Delivered the outcome of the turn that carried it (rule 5: a receipt
/// shows its turn's failed or cancelled outcome, never Delivered alone).
///
/// Tenant-scoped through the context's global filter like every other store, so one account's receipts can never
/// answer another account's read; the caller enters the tenant scope (the hub's bound tenant on a push, the request's
/// resolved tenant on a read).
///
/// The properties the ingest leans on, and how each is kept:
///
///  - FORWARD ONLY. Each row carries the position of the last event applied to it (<see cref="ApiModeEventPosition"/>:
///    the Director's generation, when it started, and the sequence inside it). An event of the same generation at or
///    below that sequence, or of an older generation, is a duplicate or a late arrival and changes nothing, so a
///    re-sent batch cannot move a receipt backwards; an event of a later generation (a Director restart) is newer
///    whatever its sequence.
///  - ONE ENDING PER PROMPT WRITTEN TO THE AGENT (rule 1), AND THE LATER RECEIPT OF A RETRY STANDS (event model,
///    section 2, "What a promptId is"). A retry of a delivery that was not delivered reuses its Gateway id, so a
///    <c>Refused</c> receipt is not final: a later Queued, Delivered or Refused under the same prompt id, at a newer
///    position, is the retry's, and it REPLACES the earlier attempt whole (<see cref="Supersede"/>), logged. Delivered
///    is final: a delivered id is never sent again, so any ending or Queued after Delivered is a Director defect,
///    logged and refused, and the delivery stands. The position rule comes first, so an older event replayed after a
///    newer one never supersedes anything.
///  - ONE TURN PER PROMPT. A turn start links the delivered prompts it names to its turn id; a turn end writes its
///    outcome onto exactly those receipts, once. The one exception is a turn whose end was lost in a hole between two
///    generations (<see cref="ApiModeLostTurnEnd"/>): on proof that it is over the Gateway rules it <c>lost</c>
///    (<see cref="RecordTurnOverProven"/>), and the turn's real <c>TurnEnded</c>, should it still arrive, replaces that.
///  - A QUEUED PROMPT WHOSE ENDING WAS LOST (<see cref="ApiModeLostQueuedPrompt"/>). On proof that a later generation
///    did not re-announce it, the Gateway rules it <see cref="ApiModeReceiptState.Unknown"/>
///    (<see cref="RecordQueueLeftProven"/>), and likewise when its Director comes back as a new process
///    (<see cref="RecordDirectorProcessStarted"/>); the prompt's next real event, whatever its sequence, replaces that.
///  - VERBATIM. The acknowledgement and the reasons are stored as received, never shortened or reworded; an oversized
///    one is refused whole rather than cut (<see cref="MaxVerbatimLength"/>). The prompt's own text is kept the same
///    way (<see cref="MaxPromptTextLength"/>): the first event that carries it sets it, and no later event blanks or
///    replaces it.
///  - ATOMIC ACROSS PROCESSES. One transaction per change. The primary key is the prompt, so two Gateways creating the
///    same receipt at once (a deploy has two processes for a moment) collide on the key, and the loser re-reads and
///    decides again, once - the <see cref="History.SessionTurnStore"/> pattern.
///
/// Retention is per row, measured from the Gateway's last change: <see cref="Retention"/>, the session-history
/// retention, applied by <see cref="History.SessionHistorySweep"/>.
/// </summary>
public sealed class ApiModeReceiptStore
{
    /// <summary>How long a receipt lives after its last change: the session-history retention.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    /// <summary>The longest acknowledgement or reason accepted. A Director's refusal reason can carry the agent's
    /// standard error lines, so this is generous; a longer one is a Director defect and is refused, not cut.</summary>
    public const int MaxVerbatimLength = 64 * 1024;

    /// <summary>The longest prompt text accepted: the most characters of event JSON one batch may carry, so the ingest,
    /// which refuses a larger batch whole, can never hand the store one longer. A prompt is the user's own words and is
    /// never cut; the column is unbounded on every provider.</summary>
    public const int MaxPromptTextLength = ApiModeEventBatch.MaxCharacters;

    /// <summary>The longest session, prompt, turn or Director id accepted (the column width).</summary>
    public const int MaxIdLength = 64;

    /// <summary>The longest event-model word accepted for a disposition, queue reason, refuser or outcome.</summary>
    public const int MaxWordLength = 16;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly Func<DateTime> _utcNow;

    /// <param name="db">The Gateway database.</param>
    /// <param name="utcNow">The Gateway's clock: when a row is created and changed, and what retention measures.</param>
    public ApiModeReceiptStore(GatewayDatabase db, Func<DateTime> utcNow)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>
    /// The Director is holding the prompt (<c>PromptQueued</c>). Creates the receipt, or refreshes the reason of one still
    /// queued. True when the store changed.
    /// </summary>
    /// <param name="promptText">The prompt's text, verbatim; null or empty when the event carried none.</param>
    public bool RecordQueued(string directorId, string sessionId, string promptId, string reason, ApiModeEventPosition position, DateTime atUtc, string? promptText = null)
    {
        RequireId(directorId, nameof(directorId));
        RequireId(sessionId, nameof(sessionId));
        RequireId(promptId, nameof(promptId));
        RequireWord(reason, nameof(reason));
        RequirePosition(position);
        RequirePromptText(promptText);
        var at = Utc(atUtc, nameof(atUtc));

        return Write(nameof(RecordQueued), sessionId, promptId, position, ctx =>
        {
            var row = Find(ctx, sessionId, promptId);
            if (row is null)
            {
                ctx.ApiModeReceipts.Add(NewRow(ctx, directorId, sessionId, promptId, ApiModeReceiptState.Queued, position, r =>
                {
                    r.QueueReason = reason;
                    r.QueuedAtUtc = at;
                    KeepPromptText(r, promptText);
                }));
                return "queued";
            }
            if (IsUnknown(row))
            {
                if (!ReplacesUnknown(nameof(RecordQueued), row, position)) return null;
                row.State = nameof(ApiModeReceiptState.Queued);
                row.QueueReason = reason;
                KeepPromptText(row, promptText);
                TouchForward(row, position);
                return "queued, replacing the Gateway's " + nameof(ApiModeReceiptState.Unknown) + " ruling";
            }
            if (!IsNewer(position, row)) return null;
            if (row.State == nameof(ApiModeReceiptState.Refused))
            {
                var was = Supersede(nameof(RecordQueued), row, position, ApiModeReceiptState.Queued);
                row.QueueReason = reason;
                row.QueuedAtUtc = at;
                KeepPromptText(row, promptText);
                return "queued again, a retry; it replaces " + was;
            }
            if (row.State != nameof(ApiModeReceiptState.Queued))
            {
                FileLog.Write($"[ApiModeReceiptStore] RecordQueued session={sessionId} prompt={promptId} at={position}: the receipt is already {row.State}; a delivered prompt is never sent again, so nothing changes");
                return null;
            }
            row.QueueReason = reason;
            KeepPromptText(row, promptText);
            Touch(row, position);
            return "queue reason now " + reason;
        });
    }

    /// <summary>
    /// The agent acknowledged the prompt (<c>PromptDelivered</c>): the receipt. After a Refused receipt it is the retry's,
    /// and it replaces the refusal; after a Delivered one it is a second ending, refused (rule 1). True when the store
    /// changed.
    /// </summary>
    /// <param name="promptText">The prompt's text, verbatim; null or empty when the event carried none.</param>
    public bool RecordDelivered(string directorId, string sessionId, string promptId, string disposition, string acknowledgement, ApiModeEventPosition position, DateTime atUtc, string? promptText = null)
    {
        RequireId(directorId, nameof(directorId));
        RequireId(sessionId, nameof(sessionId));
        RequireId(promptId, nameof(promptId));
        RequireWord(disposition, nameof(disposition));
        RequireVerbatim(acknowledgement, nameof(acknowledgement));
        RequirePosition(position);
        RequirePromptText(promptText);
        var at = Utc(atUtc, nameof(atUtc));

        return Write(nameof(RecordDelivered), sessionId, promptId, position, ctx =>
        {
            var row = Find(ctx, sessionId, promptId);
            if (row is null)
            {
                ctx.ApiModeReceipts.Add(NewRow(ctx, directorId, sessionId, promptId, ApiModeReceiptState.Delivered, position, r =>
                {
                    Deliver(r, disposition, acknowledgement, at);
                    KeepPromptText(r, promptText);
                }));
                return "delivered (" + disposition + ")";
            }
            if (IsUnknown(row))
            {
                if (!ReplacesUnknown(nameof(RecordDelivered), row, position)) return null;
                row.State = nameof(ApiModeReceiptState.Delivered);
                Deliver(row, disposition, acknowledgement, at);
                KeepPromptText(row, promptText);
                TouchForward(row, position);
                return "delivered (" + disposition + "), replacing the Gateway's " + nameof(ApiModeReceiptState.Unknown) + " ruling";
            }
            if (!IsNewer(position, row)) return null;
            if (row.State == nameof(ApiModeReceiptState.Refused))
            {
                var was = Supersede(nameof(RecordDelivered), row, position, ApiModeReceiptState.Delivered);
                Deliver(row, disposition, acknowledgement, at);
                KeepPromptText(row, promptText);
                return "delivered (" + disposition + "), a retry; it replaces " + was;
            }
            if (row.State != nameof(ApiModeReceiptState.Queued))
            {
                FileLog.Write($"[ApiModeReceiptStore] RecordDelivered session={sessionId} prompt={promptId} at={position}: REFUSED, the receipt is already {row.State} (one ending per prompt, event model rule 1; a delivered prompt is never sent again); the delivery stands");
                return null;
            }
            row.State = nameof(ApiModeReceiptState.Delivered);
            Deliver(row, disposition, acknowledgement, at);
            KeepPromptText(row, promptText);
            Touch(row, position);
            return "delivered (" + disposition + ")";
        });
    }

    /// <summary>
    /// The prompt did not enter the conversation (<c>PromptRefused</c>). After a Refused receipt it is the retry's, and
    /// its reason replaces the earlier one; after a Delivered one it is a second ending, refused (rule 1). True when the
    /// store changed.
    /// </summary>
    /// <param name="refusedBy"><c>agent</c> or <c>director</c>, as the event model words it.</param>
    /// <param name="promptText">The prompt's text, verbatim; null or empty when the event carried none.</param>
    public bool RecordRefused(string directorId, string sessionId, string promptId, string reason, string refusedBy, ApiModeEventPosition position, DateTime atUtc, string? promptText = null)
    {
        RequireId(directorId, nameof(directorId));
        RequireId(sessionId, nameof(sessionId));
        RequireId(promptId, nameof(promptId));
        RequireVerbatim(reason, nameof(reason));
        RequireWord(refusedBy, nameof(refusedBy));
        RequirePosition(position);
        RequirePromptText(promptText);
        var at = Utc(atUtc, nameof(atUtc));

        return Write(nameof(RecordRefused), sessionId, promptId, position, ctx =>
        {
            var row = Find(ctx, sessionId, promptId);
            if (row is null)
            {
                ctx.ApiModeReceipts.Add(NewRow(ctx, directorId, sessionId, promptId, ApiModeReceiptState.Refused, position, r =>
                {
                    Refuse(r, reason, refusedBy, at);
                    KeepPromptText(r, promptText);
                }));
                return "refused by " + refusedBy;
            }
            if (IsUnknown(row))
            {
                if (!ReplacesUnknown(nameof(RecordRefused), row, position)) return null;
                row.State = nameof(ApiModeReceiptState.Refused);
                Refuse(row, reason, refusedBy, at);
                KeepPromptText(row, promptText);
                TouchForward(row, position);
                return "refused by " + refusedBy + ", replacing the Gateway's " + nameof(ApiModeReceiptState.Unknown) + " ruling";
            }
            if (!IsNewer(position, row)) return null;
            if (row.State == nameof(ApiModeReceiptState.Refused))
            {
                var was = Supersede(nameof(RecordRefused), row, position, ApiModeReceiptState.Refused);
                Refuse(row, reason, refusedBy, at);
                KeepPromptText(row, promptText);
                return "refused by " + refusedBy + ", a retry; it replaces " + was;
            }
            if (row.State != nameof(ApiModeReceiptState.Queued))
            {
                FileLog.Write($"[ApiModeReceiptStore] RecordRefused session={sessionId} prompt={promptId} at={position}: REFUSED, the receipt is already {row.State} (one ending per prompt, event model rule 1; a delivered prompt is never sent again); the delivery stands");
                return null;
            }
            row.State = nameof(ApiModeReceiptState.Refused);
            Refuse(row, reason, refusedBy, at);
            KeepPromptText(row, promptText);
            Touch(row, position);
            return "refused by " + refusedBy;
        });
    }

    /// <summary>
    /// A turn began with these prompts (<c>TurnStarted</c>): each delivered prompt it names is linked to the turn, so the
    /// turn's end can be shown beside its receipt. A prompt with no receipt, not delivered, or already carried by
    /// another turn is a Director defect: logged, and that prompt is left as it was. A prompt ruled lost with no turn (its
    /// turn's start fell in the hole too, <see cref="ApiModeLostTurnEnd"/>) is linked whatever this event's position, so
    /// the turn's real end can replace the lost ending; the lost ending itself is cleared only when this event is newer
    /// than the row's last position. Answers how many receipts changed.
    /// </summary>
    public int RecordTurnStarted(string directorId, string sessionId, string turnId, IReadOnlyList<string> promptIds, ApiModeEventPosition position)
    {
        RequireId(directorId, nameof(directorId));
        RequireId(sessionId, nameof(sessionId));
        RequireId(turnId, nameof(turnId));
        ArgumentNullException.ThrowIfNull(promptIds);
        foreach (var promptId in promptIds) RequireId(promptId, nameof(promptIds));
        RequirePosition(position);
        if (promptIds.Count == 0) return 0;

        var changed = 0;
        foreach (var promptId in promptIds.Distinct(StringComparer.Ordinal))
        {
            var linked = Write(nameof(RecordTurnStarted), sessionId, promptId, position, ctx =>
            {
                var row = Find(ctx, sessionId, promptId);
                if (row is null)
                {
                    FileLog.Write($"[ApiModeReceiptStore] RecordTurnStarted session={sessionId} turn={turnId} at={position}: prompt={promptId} has no receipt; a turn can only carry a delivered prompt, so it is not linked");
                    return null;
                }
                if (row.TurnId is null && row.TurnOutcome == ApiModeLostTurnEnd.Outcome)
                {
                    // THE REAL START LINKS THE LOST RECEIPT, WHATEVER ITS POSITION (ApiModeLostTurnEnd): the receipt was
                    // ruled lost because its turn's start fell in the hole too, and here that turn is after all. Linked,
                    // its real TurnEnded can replace the lost ending. Only a start newer than the row's last position
                    // clears it: an older one is an earlier turn arriving out of order, already proven over, and
                    // re-opening it would leave the real TurnEnded to the forward-only rule, which drops it. The position
                    // never moves backwards.
                    row.TurnId = turnId;
                    if (IsNewer(position, row))
                    {
                        FileLog.Write($"[ApiModeReceiptStore] RecordTurnStarted session={sessionId} turn={turnId} at={position}: prompt={promptId} was ruled {ApiModeLostTurnEnd.Outcome} with no turn; its turn's real start, newer than the ruling, replaces the lost ending");
                        EndTurn(row, null, null, null);
                        Touch(row, position);
                        return "carried by turn " + turnId + ", replacing the lost ending";
                    }
                    FileLog.Write($"[ApiModeReceiptStore] RecordTurnStarted session={sessionId} turn={turnId} at={position}: prompt={promptId} was ruled {ApiModeLostTurnEnd.Outcome} with no turn; its turn's real start is older than the ruling, so it is linked and stays {ApiModeLostTurnEnd.Outcome} until the turn's real end");
                    row.UpdatedAtUtc = Utc(_utcNow(), "utcNow");
                    return "carried by turn " + turnId + ", still " + ApiModeLostTurnEnd.Outcome;
                }
                if (!IsNewer(position, row)) return null;
                if (row.State != nameof(ApiModeReceiptState.Delivered))
                {
                    FileLog.Write($"[ApiModeReceiptStore] RecordTurnStarted session={sessionId} turn={turnId} at={position}: prompt={promptId} is {row.State}, not Delivered; it is not linked");
                    return null;
                }
                if (row.TurnId is not null)
                {
                    FileLog.Write($"[ApiModeReceiptStore] RecordTurnStarted session={sessionId} turn={turnId} at={position}: prompt={promptId} is already carried by turn={row.TurnId}; one turn per prompt, so it stays there");
                    return null;
                }
                row.TurnId = turnId;
                Touch(row, position);
                return "carried by turn " + turnId;
            });
            if (linked) changed++;
        }
        return changed;
    }

    /// <summary>
    /// The turn is over (<c>TurnEnded</c>): its outcome and reason are written onto the receipt of every prompt it
    /// carried, once. A turn that carried no stored receipt changes nothing. Answers how many receipts changed.
    /// </summary>
    /// <param name="outcome"><c>done</c>, <c>cancelled</c> or <c>failed</c>, as the event model words it.</param>
    /// <param name="reason">The turn's stated reason, verbatim; the event model requires one for every outcome but
    /// <c>done</c>, and that requirement is the ingest's to enforce on the typed event.</param>
    public int RecordTurnEnded(string directorId, string sessionId, string turnId, string outcome, string? reason, ApiModeEventPosition position, DateTime atUtc)
    {
        RequireId(directorId, nameof(directorId));
        RequireId(sessionId, nameof(sessionId));
        RequireId(turnId, nameof(turnId));
        RequireWord(outcome, nameof(outcome));
        if (reason is not null) RequireVerbatim(reason, nameof(reason));
        RequirePosition(position);
        var at = Utc(atUtc, nameof(atUtc));

        lock (_gate)
        {
            try { return EndTurnOnce(sessionId, turnId, outcome, reason, position, at); }
            catch (DbUpdateException ex)
            {
                FileLog.Write($"[ApiModeReceiptStore] RecordTurnEnded session={sessionId} turn={turnId}: write lost a race with another writer ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}); re-reading and retrying once");
                return EndTurnOnce(sessionId, turnId, outcome, reason, position, at);
            }
        }
    }

    private int EndTurnOnce(string sessionId, string turnId, string outcome, string? reason, ApiModeEventPosition position, DateTime at)
    {
        using var ctx = _db.CreateContext();
        using var tx = ctx.Database.BeginTransaction();
        var rows = ctx.ApiModeReceipts.Where(r => r.SessionId == sessionId && r.TurnId == turnId).ToList();
        var open = rows.Where(r => r.TurnOutcome is null).ToList();
        var changed = 0;
        if (open.Count > 0)
        {
            foreach (var row in open)
            {
                if (!IsNewer(position, row)) continue;
                EndTurn(row, outcome, reason, at);
                Touch(row, position);
                changed++;
            }
        }
        else
        {
            // THE REAL ENDING REPLACES THE LOST ONE, WHATEVER ITS POSITION (ApiModeLostTurnEnd): "lost" is the Gateway's
            // inference, never history, so the forward-only rule must not drop the turn's own TurnEnded. The position
            // still never moves backwards. Only when no receipt of this turn id is still open: a receipt still waiting
            // under the same id belongs to a turn running now, and this TurnEnded is that turn's. Turn ids are unique only
            // per Director process, and the phase 2 review (observation C) found no path today that keeps one session id
            // across two processes, so this guards a case that should not arise rather than one that does.
            foreach (var row in rows.Where(r => r.TurnOutcome == ApiModeLostTurnEnd.Outcome))
            {
                FileLog.Write($"[ApiModeReceiptStore] RecordTurnEnded session={sessionId} turn={turnId} prompt={row.PromptId} at={position}: the turn's real ending ({outcome}) arrived after the Gateway ruled it lost; it replaces the lost ending");
                EndTurn(row, outcome, reason, at);
                if (IsNewer(position, row)) Touch(row, position);
                else row.UpdatedAtUtc = Utc(_utcNow(), "utcNow");
                changed++;
            }
        }
        ctx.SaveChanges();
        tx.Commit();
        if (rows.Count == 0)
            FileLog.Write($"[ApiModeReceiptStore] RecordTurnEnded session={sessionId} turn={turnId} at={position}: no receipt is carried by this turn; nothing to show its outcome on");
        else if (changed > 0)
            FileLog.Write($"[ApiModeReceiptStore] RecordTurnEnded session={sessionId} turn={turnId} at={position}: outcome={outcome} written on {changed} receipt(s)");
        return changed;
    }

    private static void EndTurn(ApiModeReceiptEntity row, string? outcome, string? reason, DateTime? at)
    {
        row.TurnOutcome = outcome;
        row.TurnOutcomeReason = reason;
        row.TurnEndedAtUtc = at;
    }

    /// <summary>
    /// An event that proves every earlier turn of the session is over (<see cref="ApiModeLostTurnEnd.Proves"/>) arrived
    /// in a later generation than a turn's last event: that turn's <c>TurnEnded</c> fell in a hole and will never come.
    /// Every Delivered receipt whose turn has no outcome - with its turn id, or with none when the turn's start was lost
    /// too - other than <paramref name="continuing"/>'s own, is given the lost ending
    /// (<see cref="ApiModeLostTurnEnd.Outcome"/>) under <see cref="ApiModeLostTurnEnd.Applies"/>, the condition the live
    /// ruling applies too. Answers how many receipts changed.
    /// </summary>
    /// <param name="proof">What proved it, for the log line.</param>
    public int RecordTurnOverProven(string directorId, string sessionId, ApiModeLostTurnEnd.Continuing continuing, string proof, ApiModeEventPosition position)
    {
        RequireId(directorId, nameof(directorId));
        RequireId(sessionId, nameof(sessionId));
        if (continuing.TurnId is not null) RequireId(continuing.TurnId, "continuing.TurnId");
        ArgumentNullException.ThrowIfNull(continuing.PromptIds, "continuing.PromptIds");
        foreach (var promptId in continuing.PromptIds) RequireId(promptId, "continuing.PromptIds");
        ArgumentException.ThrowIfNullOrWhiteSpace(proof);
        RequirePosition(position);

        lock (_gate)
        {
            try { return ProveTurnsOverOnce(sessionId, continuing, proof, position); }
            catch (DbUpdateException ex)
            {
                FileLog.Write($"[ApiModeReceiptStore] RecordTurnOverProven session={sessionId}: write lost a race with another writer ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}); re-reading and retrying once");
                return ProveTurnsOverOnce(sessionId, continuing, proof, position);
            }
        }
    }

    private int ProveTurnsOverOnce(string sessionId, ApiModeLostTurnEnd.Continuing continuing, string proof, ApiModeEventPosition position)
    {
        using var ctx = _db.CreateContext();
        using var tx = ctx.Database.BeginTransaction();
        var delivered = nameof(ApiModeReceiptState.Delivered);
        var candidates = ctx.ApiModeReceipts
            .Where(r => r.SessionId == sessionId && r.State == delivered && r.TurnOutcome == null)
            .ToList();
        var changed = 0;
        foreach (var row in candidates)
        {
            if (!ApiModeLostTurnEnd.Applies(position, continuing, row.PromptId, ApiModeReceiptState.Delivered, row.TurnId, row.TurnOutcome, row.LastGeneration, row.LastGenerationStartedUtc))
                continue;
            FileLog.Write($"[ApiModeReceiptStore] RecordTurnOverProven session={sessionId} prompt={row.PromptId} turn={row.TurnId ?? "(its start was lost too)"}: its last event was at {row.LastGeneration}#{row.LastSequence} and {proof} proves the turn is over; its TurnEnded was lost when the Director reconnected, so the turn's ending is ruled {ApiModeLostTurnEnd.Outcome}");
            // No reason and no end time: how and when the turn ended is not known.
            row.TurnOutcome = ApiModeLostTurnEnd.Outcome;
            row.TurnOutcomeReason = null;
            row.TurnEndedAtUtc = null;
            Touch(row, position);
            changed++;
        }
        ctx.SaveChanges();
        tx.Commit();
        return changed;
    }

    /// <summary>
    /// An event that comes after every re-announcement of its generation (<see cref="ApiModeLostQueuedPrompt.Proves"/>)
    /// arrived: every Queued receipt of the session whose last event is of an older generation was neither re-announced
    /// nor ended, and is ruled <see cref="ApiModeReceiptState.Unknown"/> under
    /// <see cref="ApiModeLostQueuedPrompt.Applies"/>, the condition the live ruling applies too. Everything else on the
    /// receipt - its queue reason, its prompt's text - is kept. Answers how many receipts changed.
    /// </summary>
    /// <param name="provingEvent">The event that proved it: <see cref="ApiModeLostQueuedPrompt.Proves"/> must accept it.
    /// The prompts it names are not ruled; a still-queued prompt its <c>TurnStarted</c> carries keeps reading Queued and
    /// moves to its position (<see cref="ApiModeLostQueuedPrompt.IsCarriedBy"/>).</param>
    /// <param name="proof">What proved it, for the log line.</param>
    public int RecordQueueLeftProven(string directorId, string sessionId, SessionEvent provingEvent, string proof, ApiModeEventPosition position)
    {
        RequireId(directorId, nameof(directorId));
        RequireId(sessionId, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(provingEvent);
        if (!ApiModeLostQueuedPrompt.Proves(provingEvent, out var ownPromptIds))
            throw new ArgumentException($"{provingEvent.GetType().Name} does not prove that a queue was left", nameof(provingEvent));
        foreach (var promptId in ownPromptIds) RequireId(promptId, nameof(provingEvent));
        ArgumentException.ThrowIfNullOrWhiteSpace(proof);
        RequirePosition(position);

        lock (_gate)
        {
            try { return ProveQueueLeftOnce(sessionId, provingEvent, ownPromptIds, proof, position); }
            catch (DbUpdateException ex)
            {
                FileLog.Write($"[ApiModeReceiptStore] RecordQueueLeftProven session={sessionId}: write lost a race with another writer ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}); re-reading and retrying once");
                return ProveQueueLeftOnce(sessionId, provingEvent, ownPromptIds, proof, position);
            }
        }
    }

    private int ProveQueueLeftOnce(string sessionId, SessionEvent provingEvent, IReadOnlyCollection<string> ownPromptIds, string proof, ApiModeEventPosition position)
    {
        using var ctx = _db.CreateContext();
        using var tx = ctx.Database.BeginTransaction();
        var queued = nameof(ApiModeReceiptState.Queued);
        var candidates = ctx.ApiModeReceipts
            .Where(r => r.SessionId == sessionId && r.State == queued)
            .ToList();
        var changed = 0;
        foreach (var row in candidates)
        {
            if (ApiModeLostQueuedPrompt.IsCarriedBy(provingEvent, position, row.PromptId, ApiModeReceiptState.Queued, row.LastGeneration, row.LastGenerationStartedUtc))
            {
                FileLog.Write($"[ApiModeReceiptStore] RecordQueueLeftProven session={sessionId} prompt={row.PromptId}: it was queued at {row.LastGeneration}#{row.LastSequence} and {proof} carries it, a record that it was sent; it is not ruled {nameof(ApiModeReceiptState.Unknown)}, stays Queued and moves to {position}");
                Touch(row, position);
                continue;
            }
            if (!ApiModeLostQueuedPrompt.Applies(position, ownPromptIds, row.PromptId, ApiModeReceiptState.Queued, row.LastGeneration, row.LastGenerationStartedUtc))
                continue;
            FileLog.Write($"[ApiModeReceiptStore] RecordQueueLeftProven session={sessionId} prompt={row.PromptId}: it was queued at {row.LastGeneration}#{row.LastSequence} and {proof} comes after every re-announcement of its generation, which did not re-announce it; the event that took it out of the queue was lost, so the receipt is ruled {nameof(ApiModeReceiptState.Unknown)}");
            row.State = nameof(ApiModeReceiptState.Unknown);
            Touch(row, position);
            changed++;
        }
        ctx.SaveChanges();
        tx.Commit();
        return changed;
    }

    /// <summary>
    /// The Director <paramref name="directorId"/> said Hello as a process that started at
    /// <paramref name="processStartedUtc"/>: every Queued receipt it holds, in the ambient tenant, whose last generation
    /// started before that process did was queued by a process that has ended, and is ruled
    /// <see cref="ApiModeReceiptState.Unknown"/> under <see cref="ApiModeLostQueuedPrompt.AppliesToEndedProcess"/>. There
    /// is no proving event, so the receipt keeps the position of its last event; its queue reason and its prompt's text
    /// are kept. A default start time rules nothing. Answers how many receipts changed.
    /// </summary>
    public int RecordDirectorProcessStarted(string directorId, DateTime processStartedUtc)
    {
        RequireId(directorId, nameof(directorId));
        if (processStartedUtc == default)
        {
            FileLog.Write($"[ApiModeReceiptStore] RecordDirectorProcessStarted director={directorId}: the Hello carried no process start time, so it proves nothing");
            return 0;
        }

        lock (_gate)
        {
            try { return ProveProcessEndedOnce(directorId, processStartedUtc); }
            catch (DbUpdateException ex)
            {
                FileLog.Write($"[ApiModeReceiptStore] RecordDirectorProcessStarted director={directorId}: write lost a race with another writer ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}); re-reading and retrying once");
                return ProveProcessEndedOnce(directorId, processStartedUtc);
            }
        }
    }

    private int ProveProcessEndedOnce(string directorId, DateTime processStartedUtc)
    {
        using var ctx = _db.CreateContext();
        using var tx = ctx.Database.BeginTransaction();
        var queued = nameof(ApiModeReceiptState.Queued);
        var candidates = ctx.ApiModeReceipts
            .Where(r => r.DirectorId == directorId && r.State == queued)
            .ToList();
        var changed = 0;
        foreach (var row in candidates)
        {
            if (!ApiModeLostQueuedPrompt.AppliesToEndedProcess(processStartedUtc, ApiModeReceiptState.Queued, row.LastGenerationStartedUtc))
                continue;
            FileLog.Write($"[ApiModeReceiptStore] RecordDirectorProcessStarted director={directorId} session={row.SessionId} prompt={row.PromptId}: it was queued at {row.LastGeneration}#{row.LastSequence}, a generation started at {row.LastGenerationStartedUtc:O}, and the Director now runs as a process started at {processStartedUtc.ToUniversalTime():O}; the process that held its queue has ended, so the receipt is ruled {nameof(ApiModeReceiptState.Unknown)}");
            row.State = nameof(ApiModeReceiptState.Unknown);
            row.UpdatedAtUtc = Utc(_utcNow(), "utcNow");
            changed++;
        }
        ctx.SaveChanges();
        tx.Commit();
        return changed;
    }

    /// <summary>Every stored receipt of the session in the ambient tenant, oldest prompt first. Empty when there are
    /// none.</summary>
    public IReadOnlyList<ApiModeReceiptDto> Read(string sessionId)
        => ReadWithPositions(sessionId).Select(r => r.Receipt).ToList();

    /// <summary>What <see cref="Read"/> answers, in the same order, each with the position of the last event applied to
    /// it: for a live ruling that starts from the store and must tell an event the store already ruled on from a newer
    /// one.</summary>
    internal IReadOnlyList<ApiModeStoredReceipt> ReadWithPositions(string sessionId)
    {
        RequireId(sessionId, nameof(sessionId));
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.ApiModeReceipts.AsNoTracking()
                .Where(r => r.SessionId == sessionId)
                .OrderBy(r => r.FirstGenerationStartedUtc)
                .ThenBy(r => r.FirstSequence)
                .ThenBy(r => r.PromptId)
                .AsEnumerable()
                .Select(r => new ApiModeStoredReceipt(ToDto(r), new ApiModeEventPosition(r.LastGeneration, r.LastGenerationStartedUtc, r.LastSequence))
                {
                    First = new ApiModeConversation.ApiModeConversationPlace(r.FirstGenerationStartedUtc, r.FirstSequence),
                })
                .ToList();
        }
    }

    /// <summary>Retention: every receipt in the ambient tenant the Gateway last changed before the cutoff is removed.
    /// Answers how many.</summary>
    public int PurgeOlderThan(DateTime cutoffUtc)
    {
        var cutoff = Utc(cutoffUtc, nameof(cutoffUtc));
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var removed = ctx.ApiModeReceipts.Where(r => r.UpdatedAtUtc < cutoff).ExecuteDelete();
            if (removed > 0)
                FileLog.Write($"[ApiModeReceiptStore] PurgeOlderThan: removed {removed} receipt(s) last changed before {cutoff:O}");
            return removed;
        }
    }

    // ----- one change, in one transaction, retried once on a lost race -----

    /// <summary>Run one change to one receipt. <paramref name="apply"/> answers what it did (for the log), or null when it
    /// changed nothing - a duplicate, a late event, or a refused one it has already logged.</summary>
    private bool Write(string operation, string sessionId, string promptId, ApiModeEventPosition position, Func<GatewayDbContext, string?> apply)
    {
        lock (_gate)
        {
            try { return WriteOnce(operation, sessionId, promptId, position, apply); }
            catch (DbUpdateException ex)
            {
                FileLog.Write($"[ApiModeReceiptStore] {operation} session={sessionId} prompt={promptId}: write lost a race with another writer ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}); re-reading and retrying once");
                return WriteOnce(operation, sessionId, promptId, position, apply);
            }
        }
    }

    private bool WriteOnce(string operation, string sessionId, string promptId, ApiModeEventPosition position, Func<GatewayDbContext, string?> apply)
    {
        using var ctx = _db.CreateContext();
        using var tx = ctx.Database.BeginTransaction();
        var did = apply(ctx);
        if (did is null)
            return false;
        ctx.SaveChanges();
        tx.Commit();
        FileLog.Write($"[ApiModeReceiptStore] {operation} session={sessionId} prompt={promptId} at={position}: {did}");
        return true;
    }

    private static ApiModeReceiptEntity? Find(GatewayDbContext ctx, string sessionId, string promptId)
        => ctx.ApiModeReceipts.FirstOrDefault(r => r.SessionId == sessionId && r.PromptId == promptId);

    private ApiModeReceiptEntity NewRow(GatewayDbContext ctx, string directorId, string sessionId, string promptId,
        ApiModeReceiptState state, ApiModeEventPosition position, Action<ApiModeReceiptEntity> fill)
    {
        var now = Utc(_utcNow(), "utcNow");
        var row = new ApiModeReceiptEntity
        {
            TenantId = ctx.ActiveTenant ?? throw new InvalidOperationException("no tenant is active on the receipt store's context"),
            SessionId = sessionId,
            PromptId = promptId,
            DirectorId = directorId,
            State = state.ToString(),
            FirstGenerationStartedUtc = position.GenerationStartedUtc,
            FirstSequence = position.Sequence,
            LastGeneration = position.Generation,
            LastGenerationStartedUtc = position.GenerationStartedUtc,
            LastSequence = position.Sequence,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        fill(row);
        return row;
    }

    /// <summary>Whether the event at <paramref name="position"/> comes after the last one applied to the row.</summary>
    private static bool IsNewer(ApiModeEventPosition position, ApiModeReceiptEntity row)
        => position.IsAfter(row.LastGeneration, row.LastGenerationStartedUtc, row.LastSequence);

    private static bool IsUnknown(ApiModeReceiptEntity row) => row.State == nameof(ApiModeReceiptState.Unknown);

    /// <summary>Whether a real receipt event replaces the row's Unknown ruling (<see cref="ApiModeLostQueuedPrompt.IsReplacedBy"/>):
    /// whatever its sequence, unless it is of an older generation, a replay, logged and set aside.</summary>
    private static bool ReplacesUnknown(string operation, ApiModeReceiptEntity row, ApiModeEventPosition position)
    {
        if (ApiModeLostQueuedPrompt.IsReplacedBy(position, row.LastGeneration, row.LastGenerationStartedUtc))
        {
            FileLog.Write($"[ApiModeReceiptStore] {operation} session={row.SessionId} prompt={row.PromptId} at={position}: the prompt's real event arrived after the Gateway ruled it {nameof(ApiModeReceiptState.Unknown)} at {row.LastGeneration}#{row.LastSequence}; it replaces the ruling");
            return true;
        }
        FileLog.Write($"[ApiModeReceiptStore] {operation} session={row.SessionId} prompt={row.PromptId} at={position}: an event of a generation older than the {nameof(ApiModeReceiptState.Unknown)} ruling at {row.LastGeneration}#{row.LastSequence} is a replay; nothing changes");
        return false;
    }

    /// <summary><see cref="Touch"/> that never moves the row's position backwards: a real event that replaces an Unknown
    /// ruling may be older than the proof the ruling was made at.</summary>
    private void TouchForward(ApiModeReceiptEntity row, ApiModeEventPosition position)
    {
        if (IsNewer(position, row)) Touch(row, position);
        else row.UpdatedAtUtc = Utc(_utcNow(), "utcNow");
    }

    private void Touch(ApiModeReceiptEntity row, ApiModeEventPosition position)
    {
        row.LastGeneration = position.Generation;
        row.LastGenerationStartedUtc = position.GenerationStartedUtc;
        row.LastSequence = position.Sequence;
        row.UpdatedAtUtc = Utc(_utcNow(), "utcNow");
    }

    /// <summary>
    /// A retry's event replaces a Refused receipt (event model section 2: the later receipt stands). Everything the
    /// earlier attempt wrote is cleared - its queue reason, its refusal - so the row reads as the retry alone, and the
    /// row's first position moves to the retry's event: receipts are read, and turns numbered, in the order their
    /// prompts arrived, and the retry arrived now. Only the caller may decide a row is Refused and the event newer; the
    /// caller then writes the retry's own fields. Answers what was replaced, for the log line.
    /// The prompt's text is kept, not cleared: a retry under the same id is the same prompt, so it carries the same
    /// words (event model, "What a promptId is"). Clearing it would let a retry's event without text blank the row,
    /// which <see cref="KeepPromptText"/> forbids; the caller hands the retry's text to KeepPromptText, which fills it
    /// only when the refused attempt carried none.
    /// </summary>
    private string Supersede(string operation, ApiModeReceiptEntity row, ApiModeEventPosition position, ApiModeReceiptState now)
    {
        if (row.State != nameof(ApiModeReceiptState.Refused))
            throw new InvalidOperationException($"{operation}: only a Refused receipt is superseded, and prompt={row.PromptId} is {row.State}");
        var was = $"the refusal by {row.RefusedBy} at {row.LastGeneration}#{row.LastSequence} (\"{row.RefusalReason}\")";
        FileLog.Write($"[ApiModeReceiptStore] {operation} session={row.SessionId} prompt={row.PromptId} at={position}: SUPERSEDE, the prompt was refused and this is its retry under the same id; the later receipt ({now}) stands and replaces {was}");
        row.State = now.ToString();
        row.QueueReason = null;
        row.QueuedAtUtc = null;
        row.RefusalReason = null;
        row.RefusedBy = null;
        row.RefusedAtUtc = null;
        row.Disposition = null;
        row.Acknowledgement = null;
        row.DeliveredAtUtc = null;
        row.TurnId = null;
        row.TurnOutcome = null;
        row.TurnOutcomeReason = null;
        row.TurnEndedAtUtc = null;
        row.FirstGenerationStartedUtc = position.GenerationStartedUtc;
        row.FirstSequence = position.Sequence;
        Touch(row, position);
        return was;
    }

    private static void Deliver(ApiModeReceiptEntity row, string disposition, string acknowledgement, DateTime at)
    {
        row.Disposition = disposition;
        row.Acknowledgement = acknowledgement;
        row.DeliveredAtUtc = at;
    }

    private static void Refuse(ApiModeReceiptEntity row, string reason, string refusedBy, DateTime at)
    {
        row.RefusalReason = reason;
        row.RefusedBy = refusedBy;
        row.RefusedAtUtc = at;
    }

    /// <summary>The first event that carries the prompt's text sets it, exactly as received; a later event never blanks
    /// or replaces it, and an event with no text (null or empty) leaves the row as it is.</summary>
    private static void KeepPromptText(ApiModeReceiptEntity row, string? promptText)
    {
        if (row.PromptText is null && !string.IsNullOrEmpty(promptText))
            row.PromptText = promptText;
    }

    internal static ApiModeReceiptDto ToDto(ApiModeReceiptEntity row) => new()
    {
        PromptId = row.PromptId,
        PromptText = row.PromptText,
        State = Enum.Parse<ApiModeReceiptState>(row.State),
        QueueReason = row.QueueReason,
        Disposition = row.Disposition,
        Acknowledgement = row.Acknowledgement,
        RefusalReason = row.RefusalReason,
        RefusedBy = row.RefusedBy,
        TurnId = row.TurnId,
        TurnOutcome = row.TurnOutcome,
        TurnOutcomeReason = row.TurnOutcomeReason,
        QueuedAtUtc = row.QueuedAtUtc,
        DeliveredAtUtc = row.DeliveredAtUtc,
        RefusedAtUtc = row.RefusedAtUtc,
        TurnEndedAtUtc = row.TurnEndedAtUtc,
        UpdatedAtUtc = row.UpdatedAtUtc,
    };

    // ----- validation: a malformed change is a Director defect, refused whole before anything is written -----

    private static void RequireId(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > MaxIdLength)
            throw new ArgumentException($"{name} is longer than {MaxIdLength} characters", name);
    }

    private static void RequireWord(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > MaxWordLength)
            throw new ArgumentException($"{name} is longer than {MaxWordLength} characters", name);
    }

    private static void RequireVerbatim(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length > MaxVerbatimLength)
            throw new ArgumentException($"{name} is {value.Length} characters; at most {MaxVerbatimLength} are stored, and a verbatim field is never cut", name);
    }

    private static void RequirePromptText(string? value)
    {
        if (value is not null && value.Length > MaxPromptTextLength)
            throw new ArgumentException($"promptText is {value.Length} characters; at most {MaxPromptTextLength} are stored, and a prompt's text is never cut", "promptText");
    }

    /// <summary>A default position (never constructed) has no generation: a caller defect, refused.</summary>
    private static void RequirePosition(ApiModeEventPosition position)
    {
        if (string.IsNullOrEmpty(position.Generation))
            throw new ArgumentException("the event position has no generation", nameof(position));
    }

    private static DateTime Utc(DateTime value, string name)
    {
        if (value == default)
            throw new ArgumentException($"{name} is not set", name);
        return DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
    }
}
