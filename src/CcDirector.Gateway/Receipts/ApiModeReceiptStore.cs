using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
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
///  - FORWARD ONLY. Each row carries the highest event sequence applied to it. An event at or below that is a
///    duplicate or a late arrival and changes nothing, so a re-sent batch cannot move a receipt backwards.
///  - ONE ENDING PER PROMPT (rule 1). Delivered and Refused are final; a second ending is a Director defect, logged
///    and refused, and the first one stands. A late Queued after either changes nothing.
///  - ONE TURN PER PROMPT. A turn start links the delivered prompts it names to its turn id; a turn end writes its
///    outcome onto exactly those receipts, once.
///  - VERBATIM. The acknowledgement and the reasons are stored as received, never shortened or reworded; an oversized
///    one is refused whole rather than cut (<see cref="MaxVerbatimLength"/>).
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
    public bool RecordQueued(string directorId, string sessionId, string promptId, string reason, long sequence, DateTime atUtc)
    {
        RequireId(directorId, nameof(directorId));
        RequireId(sessionId, nameof(sessionId));
        RequireId(promptId, nameof(promptId));
        RequireWord(reason, nameof(reason));
        RequireSequence(sequence);
        var at = Utc(atUtc, nameof(atUtc));

        return Write(nameof(RecordQueued), sessionId, promptId, sequence, ctx =>
        {
            var row = Find(ctx, sessionId, promptId);
            if (row is null)
            {
                ctx.ApiModeReceipts.Add(NewRow(ctx, directorId, sessionId, promptId, ApiModeReceiptState.Queued, sequence, r =>
                {
                    r.QueueReason = reason;
                    r.QueuedAtUtc = at;
                }));
                return "queued";
            }
            if (sequence <= row.LastSequence) return null;
            if (row.State != nameof(ApiModeReceiptState.Queued))
            {
                FileLog.Write($"[ApiModeReceiptStore] RecordQueued session={sessionId} prompt={promptId} seq={sequence}: the receipt is already {row.State}; a prompt that ended cannot be queued again, so nothing changes");
                return null;
            }
            row.QueueReason = reason;
            Touch(row, sequence);
            return "queue reason now " + reason;
        });
    }

    /// <summary>
    /// The agent acknowledged the prompt (<c>PromptDelivered</c>): the receipt. Refused if the prompt was already refused
    /// (rule 1). True when the store changed.
    /// </summary>
    public bool RecordDelivered(string directorId, string sessionId, string promptId, string disposition, string acknowledgement, long sequence, DateTime atUtc)
    {
        RequireId(directorId, nameof(directorId));
        RequireId(sessionId, nameof(sessionId));
        RequireId(promptId, nameof(promptId));
        RequireWord(disposition, nameof(disposition));
        RequireVerbatim(acknowledgement, nameof(acknowledgement));
        RequireSequence(sequence);
        var at = Utc(atUtc, nameof(atUtc));

        return Write(nameof(RecordDelivered), sessionId, promptId, sequence, ctx =>
        {
            var row = Find(ctx, sessionId, promptId);
            if (row is null)
            {
                ctx.ApiModeReceipts.Add(NewRow(ctx, directorId, sessionId, promptId, ApiModeReceiptState.Delivered, sequence, r => Deliver(r, disposition, acknowledgement, at)));
                return "delivered (" + disposition + ")";
            }
            if (sequence <= row.LastSequence) return null;
            if (row.State != nameof(ApiModeReceiptState.Queued))
            {
                FileLog.Write($"[ApiModeReceiptStore] RecordDelivered session={sessionId} prompt={promptId} seq={sequence}: REFUSED, the receipt is already {row.State} (one ending per prompt, event model rule 1); the first ending stands");
                return null;
            }
            row.State = nameof(ApiModeReceiptState.Delivered);
            Deliver(row, disposition, acknowledgement, at);
            Touch(row, sequence);
            return "delivered (" + disposition + ")";
        });
    }

    /// <summary>
    /// The prompt did not enter the conversation (<c>PromptRefused</c>). Refused if the prompt was already delivered
    /// (rule 1). True when the store changed.
    /// </summary>
    /// <param name="refusedBy"><c>agent</c> or <c>director</c>, as the event model words it.</param>
    public bool RecordRefused(string directorId, string sessionId, string promptId, string reason, string refusedBy, long sequence, DateTime atUtc)
    {
        RequireId(directorId, nameof(directorId));
        RequireId(sessionId, nameof(sessionId));
        RequireId(promptId, nameof(promptId));
        RequireVerbatim(reason, nameof(reason));
        RequireWord(refusedBy, nameof(refusedBy));
        RequireSequence(sequence);
        var at = Utc(atUtc, nameof(atUtc));

        return Write(nameof(RecordRefused), sessionId, promptId, sequence, ctx =>
        {
            var row = Find(ctx, sessionId, promptId);
            if (row is null)
            {
                ctx.ApiModeReceipts.Add(NewRow(ctx, directorId, sessionId, promptId, ApiModeReceiptState.Refused, sequence, r => Refuse(r, reason, refusedBy, at)));
                return "refused by " + refusedBy;
            }
            if (sequence <= row.LastSequence) return null;
            if (row.State != nameof(ApiModeReceiptState.Queued))
            {
                FileLog.Write($"[ApiModeReceiptStore] RecordRefused session={sessionId} prompt={promptId} seq={sequence}: REFUSED, the receipt is already {row.State} (one ending per prompt, event model rule 1); the first ending stands");
                return null;
            }
            row.State = nameof(ApiModeReceiptState.Refused);
            Refuse(row, reason, refusedBy, at);
            Touch(row, sequence);
            return "refused by " + refusedBy;
        });
    }

    /// <summary>
    /// A turn began with these prompts (<c>TurnStarted</c>): each delivered prompt it names is linked to the turn, so the
    /// turn's end can be shown beside its receipt. A prompt with no receipt, not delivered, or already carried by
    /// another turn is a Director defect: logged, and that prompt is left as it was. Answers how many receipts changed.
    /// </summary>
    public int RecordTurnStarted(string directorId, string sessionId, string turnId, IReadOnlyList<string> promptIds, long sequence)
    {
        RequireId(directorId, nameof(directorId));
        RequireId(sessionId, nameof(sessionId));
        RequireId(turnId, nameof(turnId));
        ArgumentNullException.ThrowIfNull(promptIds);
        foreach (var promptId in promptIds) RequireId(promptId, nameof(promptIds));
        RequireSequence(sequence);
        if (promptIds.Count == 0) return 0;

        var changed = 0;
        foreach (var promptId in promptIds.Distinct(StringComparer.Ordinal))
        {
            var linked = Write(nameof(RecordTurnStarted), sessionId, promptId, sequence, ctx =>
            {
                var row = Find(ctx, sessionId, promptId);
                if (row is null)
                {
                    FileLog.Write($"[ApiModeReceiptStore] RecordTurnStarted session={sessionId} turn={turnId} seq={sequence}: prompt={promptId} has no receipt; a turn can only carry a delivered prompt, so it is not linked");
                    return null;
                }
                if (sequence <= row.LastSequence) return null;
                if (row.State != nameof(ApiModeReceiptState.Delivered))
                {
                    FileLog.Write($"[ApiModeReceiptStore] RecordTurnStarted session={sessionId} turn={turnId} seq={sequence}: prompt={promptId} is {row.State}, not Delivered; it is not linked");
                    return null;
                }
                if (row.TurnId is not null)
                {
                    FileLog.Write($"[ApiModeReceiptStore] RecordTurnStarted session={sessionId} turn={turnId} seq={sequence}: prompt={promptId} is already carried by turn={row.TurnId}; one turn per prompt, so it stays there");
                    return null;
                }
                row.TurnId = turnId;
                Touch(row, sequence);
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
    public int RecordTurnEnded(string directorId, string sessionId, string turnId, string outcome, string? reason, long sequence, DateTime atUtc)
    {
        RequireId(directorId, nameof(directorId));
        RequireId(sessionId, nameof(sessionId));
        RequireId(turnId, nameof(turnId));
        RequireWord(outcome, nameof(outcome));
        if (reason is not null) RequireVerbatim(reason, nameof(reason));
        RequireSequence(sequence);
        var at = Utc(atUtc, nameof(atUtc));

        lock (_gate)
        {
            try { return EndTurnOnce(sessionId, turnId, outcome, reason, sequence, at); }
            catch (DbUpdateException ex)
            {
                FileLog.Write($"[ApiModeReceiptStore] RecordTurnEnded session={sessionId} turn={turnId}: write lost a race with another writer ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}); re-reading and retrying once");
                return EndTurnOnce(sessionId, turnId, outcome, reason, sequence, at);
            }
        }
    }

    private int EndTurnOnce(string sessionId, string turnId, string outcome, string? reason, long sequence, DateTime at)
    {
        using var ctx = _db.CreateContext();
        using var tx = ctx.Database.BeginTransaction();
        var rows = ctx.ApiModeReceipts.Where(r => r.SessionId == sessionId && r.TurnId == turnId).ToList();
        var changed = 0;
        foreach (var row in rows)
        {
            if (sequence <= row.LastSequence || row.TurnOutcome is not null) continue;
            row.TurnOutcome = outcome;
            row.TurnOutcomeReason = reason;
            row.TurnEndedAtUtc = at;
            Touch(row, sequence);
            changed++;
        }
        ctx.SaveChanges();
        tx.Commit();
        if (rows.Count == 0)
            FileLog.Write($"[ApiModeReceiptStore] RecordTurnEnded session={sessionId} turn={turnId} seq={sequence}: no receipt is carried by this turn; nothing to show its outcome on");
        else if (changed > 0)
            FileLog.Write($"[ApiModeReceiptStore] RecordTurnEnded session={sessionId} turn={turnId} seq={sequence}: outcome={outcome} written on {changed} receipt(s)");
        return changed;
    }

    /// <summary>Every stored receipt of the session in the ambient tenant, oldest prompt first. Empty when there are
    /// none.</summary>
    public IReadOnlyList<ApiModeReceiptDto> Read(string sessionId)
    {
        RequireId(sessionId, nameof(sessionId));
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.ApiModeReceipts.AsNoTracking()
                .Where(r => r.SessionId == sessionId)
                .OrderBy(r => r.FirstSequence)
                .ThenBy(r => r.PromptId)
                .AsEnumerable()
                .Select(ToDto)
                .ToList();
        }
    }

    /// <summary>Every Director that reported a receipt of the session in the ambient tenant, distinct. The ingest asks
    /// it before accepting a team's events into a session (the <c>PushTurns</c> ownership rule).</summary>
    public IReadOnlyList<string> DirectorsOf(string sessionId)
    {
        RequireId(sessionId, nameof(sessionId));
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.ApiModeReceipts.AsNoTracking()
                .Where(r => r.SessionId == sessionId)
                .Select(r => r.DirectorId)
                .Distinct()
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
    private bool Write(string operation, string sessionId, string promptId, long sequence, Func<GatewayDbContext, string?> apply)
    {
        lock (_gate)
        {
            try { return WriteOnce(operation, sessionId, promptId, sequence, apply); }
            catch (DbUpdateException ex)
            {
                FileLog.Write($"[ApiModeReceiptStore] {operation} session={sessionId} prompt={promptId}: write lost a race with another writer ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}); re-reading and retrying once");
                return WriteOnce(operation, sessionId, promptId, sequence, apply);
            }
        }
    }

    private bool WriteOnce(string operation, string sessionId, string promptId, long sequence, Func<GatewayDbContext, string?> apply)
    {
        using var ctx = _db.CreateContext();
        using var tx = ctx.Database.BeginTransaction();
        var did = apply(ctx);
        if (did is null)
            return false;
        ctx.SaveChanges();
        tx.Commit();
        FileLog.Write($"[ApiModeReceiptStore] {operation} session={sessionId} prompt={promptId} seq={sequence}: {did}");
        return true;
    }

    private static ApiModeReceiptEntity? Find(GatewayDbContext ctx, string sessionId, string promptId)
        => ctx.ApiModeReceipts.FirstOrDefault(r => r.SessionId == sessionId && r.PromptId == promptId);

    private ApiModeReceiptEntity NewRow(GatewayDbContext ctx, string directorId, string sessionId, string promptId,
        ApiModeReceiptState state, long sequence, Action<ApiModeReceiptEntity> fill)
    {
        var now = Utc(_utcNow(), "utcNow");
        var row = new ApiModeReceiptEntity
        {
            TenantId = ctx.ActiveTenant ?? throw new InvalidOperationException("no tenant is active on the receipt store's context"),
            SessionId = sessionId,
            PromptId = promptId,
            DirectorId = directorId,
            State = state.ToString(),
            FirstSequence = sequence,
            LastSequence = sequence,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        fill(row);
        return row;
    }

    private void Touch(ApiModeReceiptEntity row, long sequence)
    {
        row.LastSequence = sequence;
        row.UpdatedAtUtc = Utc(_utcNow(), "utcNow");
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

    internal static ApiModeReceiptDto ToDto(ApiModeReceiptEntity row) => new()
    {
        PromptId = row.PromptId,
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

    private static void RequireSequence(long sequence)
    {
        if (sequence < 0)
            throw new ArgumentException($"sequence is negative ({sequence})", nameof(sequence));
    }

    private static DateTime Utc(DateTime value, string name)
    {
        if (value == default)
            throw new ArgumentException($"{name} is not set", name);
        return DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
    }
}
