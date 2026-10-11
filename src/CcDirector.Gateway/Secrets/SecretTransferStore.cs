using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Secrets;

/// <summary>Where a secret transfer stands (the Secret Handoff mission).</summary>
public static class SecretTransferStates
{
    /// <summary>It waits for the owner's answer.</summary>
    public const string Waiting = "waiting";

    /// <summary>The owner approved it; it is being delivered.</summary>
    public const string Approved = "approved";

    /// <summary>The owner said no. Nothing was moved.</summary>
    public const string Denied = "denied";

    /// <summary>Nobody answered within <see cref="SecretTransferStore.ApprovalLifetime"/>. Nothing was moved.</summary>
    public const string Expired = "expired";

    /// <summary>The receiving machine stored it.</summary>
    public const string Delivered = "delivered";

    /// <summary>It was approved, and was not stored; <c>Outcome</c> says why.</summary>
    public const string Failed = "failed";

    public static bool IsFinished(string state) => state is Denied or Expired or Delivered or Failed;
}

/// <summary>Where the owner gave an answer (the Secret Handoff mission, owner decision 4: approve from anywhere).</summary>
public static class SecretTransferPlaces
{
    public const string Phone = "phone";
    public const string Cockpit = "cockpit";
    public const string Badge = "badge";
    public const string Window = "window";
    public const string Terminal = "terminal";
    public const string Chat = "chat";

    public static readonly IReadOnlyList<string> All = new[] { Phone, Cockpit, Badge, Window, Terminal, Chat };

    /// <summary>"in the Cockpit", "on the phone", ... - the words that follow "approved" or "denied".</summary>
    public static string Words(string? place) => place switch
    {
        Phone => "on the phone",
        Cockpit => "in the Cockpit",
        Badge => "from the session's badge in the Cockpit",
        Window => "in the cc-secrets window (by that machine's own credential)",
        Terminal => "in a terminal (by that machine's own credential)",
        Chat => "in an agent's chat",
        _ => "somewhere unknown",
    };
}

/// <summary>What a new transfer is made of. Validated by the route before it reaches the store.</summary>
public sealed record SecretTransferAsk(
    string EntryName,
    string TargetName,
    string FromMachine,
    string ToMachine,
    bool Replace,
    string? AskedBySessionId,
    string AskedBy,
    string Reason);

/// <summary>An answer that approves or denies, with where it was given and who gave it.</summary>
public sealed record SecretTransferAnswer(
    bool Approve,
    string Where,
    string AnsweredBy,
    string? ApprovalWords,
    string? AcceptedReceiverFingerprint);

/// <summary>
/// THE SECRET TRANSFERS, per account, over the <c>secret_transfers</c> table (the Secret Handoff mission).
///
/// FIRST ANSWER WINS, AT THE DATABASE. <see cref="TryAnswer"/> is one conditional UPDATE that changes a row only while it
/// is still waiting and unexpired, so the phone, the Cockpit, the window and an agent answering at the same moment
/// cannot both win, and the loser is told who did.
///
/// EXPIRY IS SETTLED WHEN THE ROW IS READ. A waiting row past its expiry is moved to expired by the read that finds it
/// (again a conditional update), so there is no timer to keep alive and no moment at which a stale "waiting" is shown.
///
/// NEVER A VALUE. Nothing passed to this store can carry the secret: names, machines, a reason, an answer, an outcome.
/// </summary>
public sealed class SecretTransferStore
{
    /// <summary>How long an approval waits for an answer (owner design: fifteen minutes).</summary>
    public static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(15);

    /// <summary>How many transfers one session may have waiting at once.</summary>
    public const int MaxWaitingPerSession = 3;

    public const int MaxReasonLength = 500;
    public const int MaxWordsLength = 500;
    public const int MaxOutcomeLength = 500;

    private readonly GatewayDatabase _db;

    public SecretTransferStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Record a new transfer. When <paramref name="answer"/> is given the transfer is born answered (the owner's click
    /// in the window, his "yes" in a terminal, or his words in an agent's chat): approved or denied in the same write,
    /// so there is never a moment it waits for an answer that was already given. Null when the session already has
    /// <see cref="MaxWaitingPerSession"/> transfers waiting.
    /// </summary>
    public SecretTransferEntity? Create(TenantId tenant, SecretTransferAsk ask, SecretTransferAnswer? answer, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(ask);
        FileLog.Write($"[SecretTransferStore] Create: tenant={tenant.ToLogString()}, entry={ask.EntryName}, from={ask.FromMachine}, to={ask.ToMachine}, answered={answer is not null}");
        try
        {
            var now = Utc(nowUtc);
            using var ctx = _db.CreateContext(tenant);
            if (answer is null)
            {
                // At most three waiting per asker - a session by its id, anyone else by who they are - so no caller
                // can fill the owner's phone with requests.
                var waiting = ask.AskedBySessionId is { } asker
                    ? ctx.SecretTransfers.AsNoTracking()
                        .Count(t => t.AskedBySessionId == asker && t.State == SecretTransferStates.Waiting && t.ExpiresAtUtc > now)
                    : ctx.SecretTransfers.AsNoTracking()
                        .Count(t => t.AskedBySessionId == null && t.AskedBy == ask.AskedBy
                                    && t.State == SecretTransferStates.Waiting && t.ExpiresAtUtc > now);
                if (waiting >= MaxWaitingPerSession)
                {
                    FileLog.Write($"[SecretTransferStore] Create: REFUSED, {ask.AskedBySessionId ?? ask.AskedBy} already has {waiting} waiting");
                    return null;
                }
            }
            var row = new SecretTransferEntity
            {
                TransferId = Guid.NewGuid().ToString("N"),
                EntryName = ask.EntryName,
                TargetName = ask.TargetName,
                FromMachine = ask.FromMachine,
                ToMachine = ask.ToMachine,
                Replace = ask.Replace,
                AskedBySessionId = ask.AskedBySessionId,
                AskedBy = Cap(ask.AskedBy, 256),
                Reason = Cap(ask.Reason.Trim(), MaxReasonLength),
                State = SecretTransferStates.Waiting,
                CreatedAtUtc = now,
                ExpiresAtUtc = now + ApprovalLifetime,
            };
            if (answer is not null)
            {
                row.State = answer.Approve ? SecretTransferStates.Approved : SecretTransferStates.Denied;
                row.AnsweredWhere = answer.Where;
                row.AnsweredBy = Cap(answer.AnsweredBy, 256);
                row.ApprovalWords = answer.ApprovalWords is null ? null : Cap(answer.ApprovalWords, MaxWordsLength);
                row.AnsweredAtUtc = now;
                row.AcceptedReceiverFingerprint = answer.AcceptedReceiverFingerprint;
                if (!answer.Approve)
                {
                    row.Outcome = $"Denied {SecretTransferPlaces.Words(answer.Where)}. Nothing was moved.";
                    row.FinishedAtUtc = now;
                }
            }
            row.TenantId = ctx.ActiveTenant!;
            ctx.SecretTransfers.Add(row);
            ctx.SaveChanges();
            FileLog.Write($"[SecretTransferStore] Create: transfer={row.TransferId}, state={row.State}");
            return row;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SecretTransferStore] Create FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>One transfer by its id, with its expiry settled; null when this account has none with that id.</summary>
    public SecretTransferEntity? Find(TenantId tenant, string? transferId, DateTime nowUtc)
    {
        var id = Key(transferId);
        if (id is null) return null;
        ExpireOverdue(tenant, nowUtc, id);
        using var ctx = _db.CreateContext(tenant);
        return ctx.SecretTransfers.AsNoTracking().FirstOrDefault(t => t.TransferId == id);
    }

    /// <summary>Every transfer still waiting or being delivered, and every one that ended at or after
    /// <paramref name="finishedSinceUtc"/>; newest first, with expiry settled.</summary>
    public IReadOnlyList<SecretTransferEntity> List(TenantId tenant, DateTime finishedSinceUtc, DateTime nowUtc)
    {
        ExpireOverdue(tenant, nowUtc, null);
        var since = Utc(finishedSinceUtc);
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.SecretTransfers.AsNoTracking()
            .Where(t => t.State == SecretTransferStates.Waiting || t.State == SecretTransferStates.Approved
                        || (t.FinishedAtUtc != null && t.FinishedAtUtc >= since))
            .ToList();
        FileLog.Write($"[SecretTransferStore] List: tenant={tenant.ToLogString()}, count={rows.Count}");
        return rows.OrderByDescending(t => t.CreatedAtUtc).ThenBy(t => t.TransferId, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Answer a WAITING, unexpired transfer. True when this call answered it; false when it had already been answered or
    /// had expired, and then nothing changed.
    /// </summary>
    public bool TryAnswer(TenantId tenant, string transferId, SecretTransferAnswer answer, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(answer);
        FileLog.Write($"[SecretTransferStore] TryAnswer: tenant={tenant.ToLogString()}, transfer={transferId}, approve={answer.Approve}, where={answer.Where}");
        try
        {
            var id = Key(transferId);
            if (id is null) return false;
            var now = Utc(nowUtc);
            var state = answer.Approve ? SecretTransferStates.Approved : SecretTransferStates.Denied;
            var by = Cap(answer.AnsweredBy, 256);
            var words = answer.ApprovalWords is null ? null : Cap(answer.ApprovalWords, MaxWordsLength);
            var outcome = answer.Approve ? null : $"Denied {SecretTransferPlaces.Words(answer.Where)}. Nothing was moved.";
            DateTime? finished = answer.Approve ? null : now;
            using var ctx = _db.CreateContext(tenant);
            var changed = ctx.SecretTransfers
                .Where(t => t.TransferId == id && t.State == SecretTransferStates.Waiting && t.ExpiresAtUtc > now)
                .ExecuteUpdate(u => u
                    .SetProperty(t => t.State, state)
                    .SetProperty(t => t.AnsweredWhere, answer.Where)
                    .SetProperty(t => t.AnsweredBy, by)
                    .SetProperty(t => t.ApprovalWords, words)
                    .SetProperty(t => t.AnsweredAtUtc, now)
                    .SetProperty(t => t.AcceptedReceiverFingerprint, answer.AcceptedReceiverFingerprint)
                    .SetProperty(t => t.Outcome, outcome)
                    .SetProperty(t => t.FinishedAtUtc, finished)) == 1;
            FileLog.Write($"[SecretTransferStore] TryAnswer: transfer={id}, changed={changed}");
            return changed;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SecretTransferStore] TryAnswer FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>End an APPROVED transfer: delivered or failed, with the sentence that says how. True when this call
    /// ended it.</summary>
    public bool TryFinish(TenantId tenant, string transferId, bool delivered, string outcome, DateTime nowUtc)
    {
        var id = Key(transferId) ?? throw new ArgumentException($"'{transferId}' is not a transfer id.", nameof(transferId));
        var state = delivered ? SecretTransferStates.Delivered : SecretTransferStates.Failed;
        var text = Cap(outcome, MaxOutcomeLength);
        var now = Utc(nowUtc);
        using var ctx = _db.CreateContext(tenant);
        var changed = ctx.SecretTransfers
            .Where(t => t.TransferId == id && t.State == SecretTransferStates.Approved)
            .ExecuteUpdate(u => u
                .SetProperty(t => t.State, state)
                .SetProperty(t => t.Outcome, text)
                .SetProperty(t => t.FinishedAtUtc, now)) == 1;
        FileLog.Write($"[SecretTransferStore] TryFinish: transfer={id}, state={state}, changed={changed}");
        return changed;
    }

    /// <summary>An approved transfer still not finished this long after its approval was orphaned: a delivery takes two
    /// Director commands of at most 90 seconds each, so only a Gateway that stopped mid-delivery leaves one this old.</summary>
    public static readonly TimeSpan ApprovedOrphanedAfter = TimeSpan.FromMinutes(10);

    public const string OrphanedOutcome = "The Gateway stopped while moving it, so whether it was stored is not known. "
                                          + "Check 'cc-secrets list' on the receiving machine before asking again.";

    /// <summary>Move every waiting transfer past its expiry to expired, and end every approved transfer a stopped Gateway
    /// orphaned (or just the one with <paramref name="onlyId"/>).</summary>
    private void ExpireOverdue(TenantId tenant, DateTime nowUtc, string? onlyId)
    {
        var now = Utc(nowUtc);
        using var ctx = _db.CreateContext(tenant);
        var orphanedBefore = now - ApprovedOrphanedAfter;
        var orphans = ctx.SecretTransfers.Where(t => t.State == SecretTransferStates.Approved
                                                     && t.AnsweredAtUtc != null && t.AnsweredAtUtc <= orphanedBefore);
        if (onlyId is not null) orphans = orphans.Where(t => t.TransferId == onlyId);
        var orphaned = orphans.ExecuteUpdate(u => u
            .SetProperty(t => t.State, SecretTransferStates.Failed)
            .SetProperty(t => t.Outcome, OrphanedOutcome)
            .SetProperty(t => t.FinishedAtUtc, now));
        if (orphaned > 0)
            FileLog.Write($"[SecretTransferStore] ExpireOverdue: tenant={tenant.ToLogString()}, orphaned={orphaned}");
        var query = ctx.SecretTransfers.Where(t => t.State == SecretTransferStates.Waiting && t.ExpiresAtUtc <= now);
        if (onlyId is not null) query = query.Where(t => t.TransferId == onlyId);
        var expired = query.ExecuteUpdate(u => u
            .SetProperty(t => t.State, SecretTransferStates.Expired)
            .SetProperty(t => t.Outcome, "Expired: nobody answered within 15 minutes. Nothing was moved.")
            .SetProperty(t => t.FinishedAtUtc, t => t.ExpiresAtUtc));
        if (expired > 0)
            FileLog.Write($"[SecretTransferStore] ExpireOverdue: tenant={tenant.ToLogString()}, expired={expired}");
    }

    private static string? Key(string? transferId)
    {
        var id = transferId?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(id) || id.Length != 32 || !id.All(Uri.IsHexDigit) ? null : id;
    }

    private static string Cap(string value, int max) => value.Length > max ? value[..max] : value;

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
