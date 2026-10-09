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
    /// <param name="authorSubject">In a team's tenant, the person behind the publishing session
    /// (<see cref="DevReportAuthor"/>); null in a personal account's tenant. Recorded when the report is created and
    /// never changed by a later version: the report is its first author's, and only a restore of that same seat can
    /// publish to it again (<see cref="DevReportInheritance"/>).</param>
    public (DevReportEntity Report, bool Created) Publish(
        TenantId tenant, string sessionId, string key, string html, string status, string title, DateTime nowUtc,
        string? authorSubject = null)
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
            try { return PublishOnce(tenant, sessionId, key, html, status, title, nowUtc, authorSubject, bytes, hash, BeforePublishWriteForTests); }
            catch (DbUpdateException ex)
            {
                FileLog.Write($"[DevReportStore] Publish: sid={sessionId} key={key} lost a race with another writer " +
                              $"({ex.InnerException?.Message ?? ex.Message}); re-reading and retrying once");
                return PublishOnce(tenant, sessionId, key, html, status, title, nowUtc, authorSubject, bytes, hash, beforeWrite: null);
            }
        }
    }

    /// <summary>Runs after a publish has read the report and before it writes, on the first attempt only - so a test can
    /// stand in for another process publishing the same key in between. Null outside tests.</summary>
    internal Action? BeforePublishWriteForTests { get; set; }

    private (DevReportEntity Report, bool Created) PublishOnce(
        TenantId tenant, string sessionId, string key, string html, string status, string title, DateTime nowUtc,
        string? authorSubject, byte[] bytes, string hash, Action? beforeWrite)
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
                AuthorSubject = authorSubject,
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

    /// <summary>
    /// Pass every report of <paramref name="fromSessionId"/> to <paramref name="toSessionId"/>, with its notes and
    /// answers, so that the new session publishing the same key writes a new version of the SAME report, at the same
    /// link. The report ids, versions, items and replies are untouched - only who they belong to changes.
    ///
    /// THIS STORE DOES NOT DECIDE WHO MAY ASK. <see cref="DevReportInheritance"/> does, and it is the only caller.
    ///
    /// A report whose key the new session has ALREADY published is left where it is and named in the answer: the
    /// natural key (session, key) is unique, and the report the new session is publishing to now is the one it will
    /// keep publishing to. Asking again is safe - the second time the old session has nothing left to pass.
    /// </summary>
    public (int Passed, IReadOnlyList<string> KeptKeys) PassToSession(TenantId tenant, string fromSessionId, string toSessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(fromSessionId);
        ArgumentException.ThrowIfNullOrEmpty(toSessionId);
        if (string.Equals(fromSessionId, toSessionId, StringComparison.Ordinal))
            throw new ArgumentException("a session's reports cannot pass to itself.", nameof(toSessionId));
        FileLog.Write($"[DevReportStore] PassToSession: tenant={tenant.ToLogString()} from={fromSessionId} to={toSessionId}");

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            using var tx = ctx.Database.BeginTransaction();
            var alreadyThere = ctx.DevReports.AsNoTracking().Where(r => r.SessionId == toSessionId).Select(r => r.Key).ToList()
                .ToHashSet(StringComparer.Ordinal);
            var passing = new List<Guid>();
            var kept = new List<string>();
            foreach (var report in ctx.DevReports.Where(r => r.SessionId == fromSessionId).ToList())
            {
                if (alreadyThere.Contains(report.Key))
                {
                    kept.Add(report.Key);
                    continue;
                }
                report.SessionId = toSessionId;
                passing.Add(report.Id);
            }
            ctx.SaveChanges();

            // The items carry the session too: a turn end drains what is held for ONE session across its reports, so a
            // note the owner wrote before the restart is delivered to the session that holds the report now.
            var items = passing.Count == 0
                ? 0
                : ctx.DevReportItems.Where(i => passing.Contains(i.ReportId))
                    .ExecuteUpdate(set => set.SetProperty(i => i.SessionId, toSessionId));
            tx.Commit();
            FileLog.Write($"[DevReportStore] PassToSession: from={fromSessionId} to={toSessionId} passed={passing.Count} items={items} kept={kept.Count}");
            return (passing.Count, kept);
        }
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

    /// <summary>
    /// ONE PAGE of the account's reports - or one session's - newest update first, for the owner's Reports list. The
    /// database sorts and cuts the page, so a list read costs the same with ten reports as with ten thousand; reading
    /// every row and sorting it here is what made the whole-account list time out. The order is total - update time,
    /// newest first, then session id, then key, all ordinal - because a session's key is unique, so a page never
    /// repeats or skips a report. <paramref name="after"/> is the last report of the page before (null for the first
    /// page); the answer says whether any report is older than this page.
    /// </summary>
    public DevReportListPage ListPage(TenantId tenant, string? sessionId, int limit, DevReportListPosition? after)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), limit, "A page holds at least one report.");
        using var ctx = _db.CreateContext(tenant);
        var query = ctx.DevReports.AsNoTracking();
        if (!string.IsNullOrEmpty(sessionId))
            query = query.Where(r => r.SessionId == sessionId);
        if (after is { } a)
        {
            var at = a.UpdatedAtUtc;
            var sid = a.SessionId;
            var key = a.Key;
            query = query.Where(r => r.UpdatedAtUtc < at
                                     || (r.UpdatedAtUtc == at && (string.Compare(r.SessionId, sid) > 0
                                         || (r.SessionId == sid && string.Compare(r.Key, key) > 0))));
        }
        var rows = query
            .OrderByDescending(r => r.UpdatedAtUtc).ThenBy(r => r.SessionId).ThenBy(r => r.Key)
            .Take(limit + 1)
            .ToList();
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        FileLog.Write($"[DevReportStore] ListPage: tenant={tenant.ToLogString()} sid={sessionId ?? "(all)"} limit={limit} after={(after is null ? "none" : "set")} count={rows.Count} more={more}");
        return new DevReportListPage(rows, more);
    }

    /// <summary>
    /// The reports one person wrote in a team's tenant, newest update first (devthrottle_internal#2309). Read by the
    /// author column and its index, so the author's Reports page grows with what they wrote, not with the team's history.
    /// </summary>
    public IReadOnlyList<DevReportEntity> ListByAuthor(TenantId tenant, string authorSubject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorSubject);
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.DevReports.AsNoTracking().Where(r => r.AuthorSubject == authorSubject).ToList()
            .OrderByDescending(r => r.UpdatedAtUtc).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
        FileLog.Write($"[DevReportStore] ListByAuthor: tenant={tenant.ToLogString()} count={rows.Count}");
        return rows;
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
    /// Store the account owner's NEW items (the caller has already excluded ids the report holds) in the given state, in
    /// send order, and apply rule 2: a new answer REPLACES every answer of the owner's to the same question in this report
    /// that is still waiting to go - stored earlier or earlier in this same batch. A team member's answer is never stored
    /// here: it goes through <see cref="AddMemberAnswer"/>. Returns the stored rows in order.
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
                    foreach (var earlier in added.Where(i => IsEarlierOwnersAnswer(i, item.QuestionId) && DevReportItemStates.IsWaiting(i.Status)))
                    {
                        earlier.Status = DevReportItemStates.ReplacedState.Status;
                        earlier.StatusLabel = DevReportItemStates.ReplacedState.Label;
                        earlier.ReplacedBy = item.Id;
                        FileLog.Write($"[DevReportStore] AddItems: report={report.Id} item={earlier.ClientItemId} replaced by {item.Id}");
                    }

                    // A stored one is replaced only WHERE IT IS STILL WAITING, in the database: another Gateway process
                    // may have claimed it for a send since it was read, and an item that is going must not be relabelled.
                    foreach (var earlier in existing.Where(i => IsEarlierOwnersAnswer(i, item.QuestionId)))
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

    /// <summary>
    /// A TEAM MEMBER'S ANSWER, TAKEN IN ONE TRANSACTION (devthrottle_internal#2307, review F2, F3 and F4). Everything that
    /// decides whether it is taken is decided in the database, at the moment of the write:
    /// <list type="number">
    /// <item>THE VERSION THEY HOLD. The first statement is a conditional write on the member's own recipient row,
    /// <c>update dev_report_recipients set SentVersion = SentVersion where report, member and SentVersion = version</c>. It
    /// matches nothing when a newer version was sent to them since the page read the question - from this process or the
    /// other one during a deploy - and then nothing is written (<see cref="MemberAnswerOutcome.VersionNotHeld"/>). When it
    /// matches it holds that row until the answer commits, so a send of a newer version waits behind it, and so does a
    /// second answer by the same person to the same report.</item>
    /// <item>ONE ANSWER. Under that hold, a not-refused answer by the same person to the same question means this one is
    /// not taken (<see cref="MemberAnswerOutcome.AlreadyAnswered"/>). The unique index
    /// <see cref="Data.GatewayDbContext.MemberAnswerIndexName"/> is the database's own guarantee of the same rule.</item>
    /// <item>THE CHOICE AND THE WORDS TOGETHER. The answer is stored HELD, carrying the version it was given on and no words
    /// of the member's own; the member's comment, when there is one, is stored for <paramref name="toSubject"/> through the
    /// person-only table in the SAME transaction. Neither exists without the other, and both exist before anything is sent
    /// to a session - the send is the delivery's settle pass afterwards, which never sends one item twice.</item>
    /// </list>
    /// </summary>
    /// <exception cref="ArgumentException">The item is not an answer, carries words of the member's own, or the comment
    /// breaks the comment rules.</exception>
    public MemberAnswerOutcome AddMemberAnswer(TenantId team, DevReportEntity report, int sentVersion, DevReportItem item,
        string answererSubject, string words, string toSubject, string senderKind, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(answererSubject);
        ArgumentNullException.ThrowIfNull(words);
        if (item.Kind != DevReportItem.Answer || item.Comment.Length > 0 || item.Text.Length > 0)
            throw new ArgumentException("A team member's item is an answer with no words of their own: no note, no text, no comment.", nameof(item));
        var comment = words.Length == 0
            ? null
            : DevReportPersonComments.NewRow(team, report.Id, answererSubject, toSubject, words, nowUtc, item.QuestionId);

        BeforeMemberAnswerWriteForTests?.Invoke();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(team);
            using var tx = ctx.Database.BeginTransaction();
            var holds = ctx.DevReportRecipients
                .Where(r => r.ReportId == report.Id && r.RecipientSubject == answererSubject && r.SentVersion == sentVersion)
                .ExecuteUpdate(set => set.SetProperty(r => r.SentVersion, r => r.SentVersion));
            if (holds != 1)
            {
                FileLog.Write($"[DevReportStore] AddMemberAnswer: report={report.Id} version={sentVersion} is not the version the member holds - nothing stored");
                return MemberAnswerOutcome.VersionNotHeld;
            }
            var answered = ctx.DevReportItems.AsNoTracking().Any(i =>
                i.ReportId == report.Id && i.AnswererSubject == answererSubject && i.QuestionId == item.QuestionId
                && i.Status != DevReportItemStates.Refused);
            if (answered)
            {
                FileLog.Write($"[DevReportStore] AddMemberAnswer: report={report.Id} question={item.QuestionId} already answered by this member - nothing stored");
                return MemberAnswerOutcome.AlreadyAnswered;
            }

            var sequence = ctx.DevReportItems.AsNoTracking().Where(i => i.ReportId == report.Id).Max(i => (long?)i.Sequence) ?? 0;
            var held = DevReportItemStates.HeldState;
            ctx.DevReportItems.Add(new DevReportItemEntity
            {
                TenantId = team.Value,
                ReportId = report.Id,
                SessionId = report.SessionId,
                ClientItemId = item.Id,
                Kind = item.Kind,
                QuestionId = item.QuestionId,
                Question = item.Question,
                OptionValue = item.OptionValue,
                OptionLabel = item.OptionLabel,
                Status = held.Status,
                StatusLabel = held.Label,
                Sequence = sequence + 1,
                SenderKind = senderKind,
                SentAtUtc = nowUtc,
                AnswererSubject = answererSubject,
                SourceVersion = sentVersion,
            });
            if (comment is not null)
                ctx.DevReportComments.Add(comment);
            ctx.SaveChanges();
            tx.Commit();
            FileLog.Write($"[DevReportStore] AddMemberAnswer: report={report.Id} question={item.QuestionId} version={sentVersion} " +
                          $"stored held, comment={(comment is null ? "none" : "chars=" + words.Length)}");
            return MemberAnswerOutcome.Stored;
        }
    }

    /// <summary>Runs after the answer route has read the version and before the answer's transaction begins - so a test can
    /// stand in for a newer send, or another process's answer, landing in between (review F2, F3). Null outside tests.</summary>
    internal Action? BeforeMemberAnswerWriteForTests { get; set; }

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

    private static bool IsEarlierOwnersAnswer(DevReportItemEntity row, string questionId)
        => row.Kind == DevReportItem.Answer && string.Equals(row.QuestionId, questionId, StringComparison.Ordinal)
           && row.AnswererSubject is null;

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

    /// <summary>True when the report already delivered an answer to this question from the same person - the account
    /// owner when <paramref name="answererSubject"/> is null, otherwise that team member - so a newer answer is a
    /// change.</summary>
    public bool HasDeliveredAnswer(TenantId tenant, Guid reportId, string questionId, string? answererSubject = null)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.DevReportItems.AsNoTracking().Any(i =>
            i.ReportId == reportId && i.Kind == DevReportItem.Answer && i.QuestionId == questionId
            && i.AnswererSubject == answererSubject
            && i.Status == DevReportItemStates.Delivered);
    }

    /// <summary>
    /// The answers <paramref name="answererSubject"/> gave on the given reports (devthrottle_internal#2307), one per
    /// (report, question): the latest that was not refused, since a refused answer never reached the session and the
    /// question is still waiting on them. With <paramref name="includeRefused"/>, a refused answer stands when there is
    /// no other: what a comment given with it is about, for its author (delta review D1).
    /// </summary>
    public IReadOnlyDictionary<(Guid ReportId, string QuestionId), DevReportItemEntity> AnswersBy(
        TenantId tenant, string answererSubject, IReadOnlyCollection<Guid> reportIds, bool includeRefused = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(answererSubject);
        ArgumentNullException.ThrowIfNull(reportIds);
        if (reportIds.Count == 0) return new Dictionary<(Guid, string), DevReportItemEntity>();
        using var ctx = _db.CreateContext(tenant);
        return ctx.DevReportItems.AsNoTracking()
            .Where(i => i.AnswererSubject == answererSubject && i.Kind == DevReportItem.Answer
                        && reportIds.Contains(i.ReportId) && (includeRefused || i.Status != DevReportItemStates.Refused))
            .ToList()
            .GroupBy(i => (i.ReportId, i.QuestionId))
            .ToDictionary(g => g.Key, g => g
                .OrderBy(i => i.Status == DevReportItemStates.Refused)
                .ThenByDescending(i => i.Sequence).First());
    }
}

/// <summary>What <see cref="DevReportStore.AddMemberAnswer"/> did.</summary>
internal enum MemberAnswerOutcome
{
    /// <summary>The answer is stored held, with the member's comment when there was one.</summary>
    Stored,
    /// <summary>The member holds a different version from the one answered; nothing was stored.</summary>
    VersionNotHeld,
    /// <summary>The member already has an answer to this question that was not refused; nothing was stored.</summary>
    AlreadyAnswered,
}
