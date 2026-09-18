using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.DevReports;

/// <summary>
/// The Gateway's record of dev reports (issue #2958): each report, every version's bytes, the owner's notes and
/// answers with their delivery state, and the agent's replies. It survives a restart because it is the database.
///
/// EVERY OPERATION TAKES THE TENANT AND READS THROUGH A CONTEXT SCOPED TO IT. A report of another account is
/// therefore not found - the global query filter never returns its row - so its existence does not leak.
///
/// This store records; it does not rule. Whether an item is held or delivered, and the words for it, are decided
/// by <see cref="DevReportDelivery"/> and <see cref="DevReportItemStates"/>. Every change of an item's delivery
/// state is a conditional update in the database (see "state changes" below), because during a deploy swap two
/// Gateway processes write this database at once and no in-memory lock spans both. This store's own lock only
/// keeps a publish or an item insert on this process from interleaving with another.
/// </summary>
internal sealed class DevReportStore
{
    /// <summary>The largest report, in UTF-8 bytes (mission ruling 6, sized by the Manager 2026-09-16):
    /// exactly this many is allowed, one more is refused.</summary>
    public const long MaxReportBytes = 10L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <exception cref="ArgumentNullException">The database is null.</exception>
    public DevReportStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Publish a report for a session: a new report when the key is new for that session, otherwise a new
    /// version of the existing one - even when the bytes are identical, because the owner asked for a reload.
    /// </summary>
    public (DevReportEntity Report, bool Created) Publish(
        TenantId tenant, string sessionId, string key, string html, string status, string title, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(html);
        FileLog.Write($"[DevReportStore] Publish: tenant={tenant.ToLogString()} sid={sessionId} key={key} chars={html.Length}");

        var bytes = Encoding.UTF8.GetBytes(html);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        lock (_gate)
        {
            // ONE RETRY when another Gateway process published the same key or version first (phase 2 inspection, Low 1).
            // During a deploy swap two processes share this database and this lock spans only one of them, so both can
            // read "no report yet" and both insert; the unique index refuses the loser. Re-reading makes the second
            // attempt a new version of the report the other process wrote - the answer one process gives - instead of
            // a raw 500. A second failure is a fault and surfaces.
            try { return PublishOnce(tenant, sessionId, key, html, status, title, nowUtc, bytes, hash, BeforePublishWriteForTests); }
            catch (DbUpdateException ex)
            {
                FileLog.Write($"[DevReportStore] Publish: sid={sessionId} key={key} lost a race with another writer " +
                              $"({ex.InnerException?.Message ?? ex.Message}); re-reading and retrying once");
                return PublishOnce(tenant, sessionId, key, html, status, title, nowUtc, bytes, hash, beforeWrite: null);
            }
        }
    }

    /// <summary>Runs after a publish has read the report and before it writes, on the first attempt only - so a test can
    /// stand in for another process publishing the same key in between. Null outside tests.</summary>
    internal Action? BeforePublishWriteForTests { get; set; }

    private (DevReportEntity Report, bool Created) PublishOnce(
        TenantId tenant, string sessionId, string key, string html, string status, string title, DateTime nowUtc,
        byte[] bytes, string hash, Action? beforeWrite)
    {
        using var ctx = _db.CreateContext(tenant);
        using var tx = ctx.Database.BeginTransaction();
        var report = ctx.DevReports.FirstOrDefault(r => r.SessionId == sessionId && r.Key == key);
        var created = report is null;
        if (report is null)
        {
            report = new DevReportEntity
            {
                TenantId = tenant.Value,
                SessionId = sessionId,
                Key = key,
                PublishedAtUtc = nowUtc,
            };
            ctx.DevReports.Add(report);
        }
        report.Version += 1;
        report.Title = title;
        report.Status = status;
        report.UpdatedAtUtc = nowUtc;

        ctx.DevReportVersions.Add(new DevReportVersionEntity
        {
            TenantId = tenant.Value,
            ReportId = report.Id,
            Version = report.Version,
            Html = html,
            ByteHash = hash,
            ByteLength = bytes.LongLength,
            PublishedAtUtc = nowUtc,
            Status = status,
            Title = title,
        });
        beforeWrite?.Invoke();
        ctx.SaveChanges();
        tx.Commit();
        FileLog.Write($"[DevReportStore] Publish: report={report.Id} version={report.Version} created={created} bytes={bytes.LongLength}");
        return (report, created);
    }

    /// <summary>A session's reports, newest update first. A null session lists the whole account.</summary>
    public IReadOnlyList<DevReportEntity> List(TenantId tenant, string? sessionId)
    {
        using var ctx = _db.CreateContext(tenant);
        var query = ctx.DevReports.AsNoTracking();
        if (!string.IsNullOrEmpty(sessionId))
            query = query.Where(r => r.SessionId == sessionId);
        return query.ToList().OrderByDescending(r => r.UpdatedAtUtc).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>One report, or null when the account has no report with that id.</summary>
    public DevReportEntity? Get(TenantId tenant, Guid reportId)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.DevReports.AsNoTracking().FirstOrDefault(r => r.Id == reportId);
    }

    /// <summary>One version of a report - the latest when <paramref name="version"/> is null - or null.</summary>
    public DevReportVersionEntity? GetVersion(TenantId tenant, Guid reportId, int? version)
    {
        using var ctx = _db.CreateContext(tenant);
        var query = ctx.DevReportVersions.AsNoTracking().Where(v => v.ReportId == reportId);
        return version is { } n
            ? query.FirstOrDefault(v => v.Version == n)
            : query.OrderByDescending(v => v.Version).FirstOrDefault();
    }

    /// <summary>A report's items in the order the owner sent them.</summary>
    public IReadOnlyList<DevReportItemEntity> Items(TenantId tenant, Guid reportId)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.DevReportItems.AsNoTracking().Where(i => i.ReportId == reportId).OrderBy(i => i.Sequence).ToList();
    }

    /// <summary>How many items of each report are not yet settled (queued, held or sending).</summary>
    public IReadOnlyDictionary<Guid, int> OpenItemCounts(TenantId tenant, IReadOnlyCollection<Guid> reportIds)
    {
        if (reportIds.Count == 0) return new Dictionary<Guid, int>();
        using var ctx = _db.CreateContext(tenant);
        return ctx.DevReportItems.AsNoTracking()
            .Where(i => reportIds.Contains(i.ReportId)
                        && (i.Status == DevReportItemStates.Queued || i.Status == DevReportItemStates.Held
                            || i.Status == DevReportItemStates.Sending))
            .Select(i => i.ReportId)
            .ToList()
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>A report's replies, oldest first.</summary>
    public IReadOnlyList<DevReportReplyEntity> Replies(TenantId tenant, Guid reportId)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.DevReportReplies.AsNoTracking().Where(r => r.ReportId == reportId).ToList()
            .OrderBy(r => r.AtUtc).ThenBy(r => r.Id).ToList();
    }

    /// <summary>Store the agent's reply on its report, verbatim.</summary>
    public DevReportReplyEntity AddReply(TenantId tenant, Guid reportId, string text, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var reply = new DevReportReplyEntity { TenantId = tenant.Value, ReportId = reportId, Text = text, AtUtc = nowUtc };
            ctx.DevReportReplies.Add(reply);
            ctx.SaveChanges();
            FileLog.Write($"[DevReportStore] AddReply: report={reportId} reply={reply.Id} chars={text.Length}");
            return reply;
        }
    }

    /// <summary>The stored items of a report whose client ids are among <paramref name="clientIds"/>, by client id.</summary>
    public IReadOnlyDictionary<string, DevReportItemEntity> FindItems(TenantId tenant, Guid reportId, IReadOnlyCollection<string> clientIds)
    {
        if (clientIds.Count == 0) return new Dictionary<string, DevReportItemEntity>(StringComparer.Ordinal);
        using var ctx = _db.CreateContext(tenant);
        return ctx.DevReportItems.AsNoTracking()
            .Where(i => i.ReportId == reportId && clientIds.Contains(i.ClientItemId))
            .ToList()
            .ToDictionary(i => i.ClientItemId, StringComparer.Ordinal);
    }

    /// <summary>
    /// Store NEW items (the caller has already excluded ids the report holds) in the given state, in send order,
    /// and apply rule 2: a new answer REPLACES every answer to the same question in this report that is still
    /// waiting to go - stored earlier or earlier in this same batch. Returns the stored rows in order.
    /// </summary>
    public IReadOnlyList<DevReportItemEntity> AddItems(
        TenantId tenant, DevReportEntity report, IReadOnlyList<DevReportItem> items, DevReportItemStates.State state, string senderKind, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            using var tx = ctx.Database.BeginTransaction();
            var existing = ctx.DevReportItems.AsNoTracking().Where(i => i.ReportId == report.Id).ToList();
            var sequence = existing.Count == 0 ? 0 : existing.Max(i => i.Sequence);
            var added = new List<DevReportItemEntity>(items.Count);

            foreach (var item in items)
            {
                if (item.Kind == DevReportItem.Answer)
                {
                    // An earlier answer in this batch is not in the database yet, so it is replaced in memory.
                    foreach (var earlier in added.Where(i => IsEarlierAnswer(i, item.QuestionId) && DevReportItemStates.IsWaiting(i.Status)))
                    {
                        earlier.Status = DevReportItemStates.ReplacedState.Status;
                        earlier.StatusLabel = DevReportItemStates.ReplacedState.Label;
                        earlier.ReplacedBy = item.Id;
                        FileLog.Write($"[DevReportStore] AddItems: report={report.Id} item={earlier.ClientItemId} replaced by {item.Id}");
                    }

                    // A stored one is replaced only WHERE IT IS STILL WAITING, in the database: another Gateway process
                    // may have claimed it for a send since it was read, and an item that is going must not be relabelled.
                    foreach (var earlier in existing.Where(i => IsEarlierAnswer(i, item.QuestionId)))
                    {
                        var replaced = ctx.DevReportItems
                            .Where(i => i.Id == earlier.Id
                                        && (i.Status == DevReportItemStates.Queued || i.Status == DevReportItemStates.Held))
                            .ExecuteUpdate(set => set
                                .SetProperty(i => i.Status, DevReportItemStates.ReplacedState.Status)
                                .SetProperty(i => i.StatusLabel, DevReportItemStates.ReplacedState.Label)
                                .SetProperty(i => i.ReplacedBy, item.Id));
                        if (replaced > 0)
                            FileLog.Write($"[DevReportStore] AddItems: report={report.Id} item={earlier.ClientItemId} replaced by {item.Id}");
                    }
                }

                var row = new DevReportItemEntity
                {
                    TenantId = tenant.Value,
                    ReportId = report.Id,
                    SessionId = report.SessionId,
                    ClientItemId = item.Id,
                    Kind = item.Kind,
                    Text = item.Text,
                    AnchorJson = item.Anchor?.ToJson(),
                    QuestionId = item.QuestionId,
                    Question = item.Question,
                    OptionValue = item.OptionValue,
                    OptionLabel = item.OptionLabel,
                    Comment = item.Comment,
                    Status = state.Status,
                    StatusLabel = state.Label,
                    Sequence = ++sequence,
                    SenderKind = senderKind,
                    SentAtUtc = nowUtc,
                };
                ctx.DevReportItems.Add(row);
                added.Add(row);
            }
            ctx.SaveChanges();
            tx.Commit();
            FileLog.Write($"[DevReportStore] AddItems: report={report.Id} stored={added.Count} status={state.Status}");
            return added;
        }
    }

    /// <summary>Every item of a session still waiting to go (queued or held), across all its reports, in the
    /// order the owner sent them.</summary>
    public IReadOnlyList<DevReportItemEntity> WaitingItemsForSession(TenantId tenant, string sessionId)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.DevReportItems.AsNoTracking()
            .Where(i => i.SessionId == sessionId
                        && (i.Status == DevReportItemStates.Queued || i.Status == DevReportItemStates.Held))
            .ToList()
            .OrderBy(i => i.SentAtUtc).ThenBy(i => i.ReportId).ThenBy(i => i.Sequence)
            .ToList();
    }

    // ---------------------------------------------------------------- state changes
    //
    // EVERY STATE CHANGE BELOW IS ONE CONDITIONAL UPDATE IN THE DATABASE, never a read followed by a write. Two
    // Gateway processes share this database during a deploy swap, and neither holds the other's lock, so the
    // database is the only place a "still held" or "still mine" check can be true at the moment of the write.

    /// <summary>
    /// Claim every item of a session that is still waiting (queued or held) for ONE send: each moves to
    /// <c>sending</c> and carries <paramref name="claimId"/>, only where it is still waiting at the moment of the
    /// update. Returns the rows this claim took, in send order - never a row another claim took first.
    /// </summary>
    public IReadOnlyList<DevReportItemEntity> ClaimWaiting(TenantId tenant, string sessionId, Guid claimId, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        using var ctx = _db.CreateContext(tenant);
        var claimed = ctx.DevReportItems
            .Where(i => i.SessionId == sessionId
                        && (i.Status == DevReportItemStates.Queued || i.Status == DevReportItemStates.Held))
            .ExecuteUpdate(set => set
                .SetProperty(i => i.Status, DevReportItemStates.SendingState.Status)
                .SetProperty(i => i.StatusLabel, DevReportItemStates.SendingState.Label)
                .SetProperty(i => i.ClaimId, claimId)
                .SetProperty(i => i.ClaimedAtUtc, nowUtc));
        FileLog.Write($"[DevReportStore] ClaimWaiting: sid={sessionId} claim={claimId} claimed={claimed}");
        if (claimed == 0) return [];
        return ctx.DevReportItems.AsNoTracking()
            .Where(i => i.ClaimId == claimId && i.Status == DevReportItemStates.Sending)
            .ToList()
            .OrderBy(i => i.SentAtUtc).ThenBy(i => i.ReportId).ThenBy(i => i.Sequence)
            .ToList();
    }

    /// <summary>
    /// Write the state a send came to - only on the items that still carry <paramref name="claimId"/> AND are still
    /// <c>sending</c>. An item a settle pass has already ruled orphaned is not touched: exactly one final write wins.
    /// Returns how many items took this state.
    /// </summary>
    public int FinishClaim(TenantId tenant, Guid claimId, DevReportItemStates.State state, DateTime nowUtc)
    {
        using var ctx = _db.CreateContext(tenant);
        var mine = ctx.DevReportItems.Where(i => i.ClaimId == claimId && i.Status == DevReportItemStates.Sending);
        var written = state.Status == DevReportItemStates.Delivered
            ? mine.ExecuteUpdate(set => set
                .SetProperty(i => i.Status, state.Status)
                .SetProperty(i => i.StatusLabel, state.Label)
                .SetProperty(i => i.DeliveredAtUtc, nowUtc))
            : mine.ExecuteUpdate(set => set
                .SetProperty(i => i.Status, state.Status)
                .SetProperty(i => i.StatusLabel, state.Label));
        FileLog.Write($"[DevReportStore] FinishClaim: claim={claimId} -> {state.Status} ({state.Label}) items={written}");
        return written;
    }

    /// <summary>
    /// Settle a session's items still <c>sending</c> under a claim taken at or before <paramref name="claimedAtOrBeforeUtc"/>:
    /// they become delivered, not confirmed, and are never sent again. A claim younger than that is left alone - its
    /// send may still be running in another process. Returns how many items were settled.
    /// </summary>
    public int SettleExpiredClaims(TenantId tenant, string sessionId, DateTime claimedAtOrBeforeUtc, DateTime nowUtc)
    {
        using var ctx = _db.CreateContext(tenant);
        var state = DevReportItemStates.UnconfirmedState;
        var settled = ctx.DevReportItems
            .Where(i => i.SessionId == sessionId && i.Status == DevReportItemStates.Sending
                        && i.ClaimedAtUtc != null && i.ClaimedAtUtc <= claimedAtOrBeforeUtc)
            .ExecuteUpdate(set => set
                .SetProperty(i => i.Status, state.Status)
                .SetProperty(i => i.StatusLabel, state.Label)
                .SetProperty(i => i.DeliveredAtUtc, nowUtc));
        if (settled > 0)
            FileLog.Write($"[DevReportStore] SettleExpiredClaims: sid={sessionId} settled={settled} (claimed at or before {claimedAtOrBeforeUtc:O})");
        return settled;
    }

    /// <summary>Refuse every item of a session still waiting (queued or held) - only where it is still waiting at the
    /// moment of the update. Returns how many were refused.</summary>
    public int RefuseWaiting(TenantId tenant, string sessionId, DevReportItemStates.State state)
    {
        using var ctx = _db.CreateContext(tenant);
        var refused = ctx.DevReportItems
            .Where(i => i.SessionId == sessionId
                        && (i.Status == DevReportItemStates.Queued || i.Status == DevReportItemStates.Held))
            .ExecuteUpdate(set => set
                .SetProperty(i => i.Status, state.Status)
                .SetProperty(i => i.StatusLabel, state.Label));
        FileLog.Write($"[DevReportStore] RefuseWaiting: sid={sessionId} -> {state.Status} ({state.Label}) items={refused}");
        return refused;
    }

    private static bool IsEarlierAnswer(DevReportItemEntity row, string questionId)
        => row.Kind == DevReportItem.Answer && string.Equals(row.QuestionId, questionId, StringComparison.Ordinal);

    /// <summary>The sessions of the account that have any item not yet settled (queued, held or sending) - what the
    /// settle sweep visits.</summary>
    public IReadOnlyList<string> SessionsWithOpenItems(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.DevReportItems.AsNoTracking()
            .Where(i => i.Status == DevReportItemStates.Queued || i.Status == DevReportItemStates.Held
                        || i.Status == DevReportItemStates.Sending)
            .Select(i => i.SessionId)
            .Distinct()
            .ToList()
            .OrderBy(sid => sid, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when the report already delivered an answer to this question - so a newer answer is a change.</summary>
    public bool HasDeliveredAnswer(TenantId tenant, Guid reportId, string questionId)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.DevReportItems.AsNoTracking().Any(i =>
            i.ReportId == reportId && i.Kind == DevReportItem.Answer && i.QuestionId == questionId
            && i.Status == DevReportItemStates.Delivered);
    }
}
