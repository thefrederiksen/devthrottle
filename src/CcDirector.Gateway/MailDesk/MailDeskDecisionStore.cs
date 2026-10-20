using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// WHAT THE MAIL DESK DECIDED FOR EACH EMAIL (Mail Desk mission, phase 1, contract sections 2 and 5): one row per
/// screened email, what the owner reads to review the Mail Desk. Phase 1 builds the insert (phase 3 writes through it)
/// and every read the routes need.
///
/// NO BODY TEXT, EVER: the insert takes no body and the table has no column for one. A message is recorded once per
/// channel - a second report of the same message key returns the row already kept and writes nothing.
/// </summary>
public sealed class MailDeskDecisionStore
{
    /// <summary>How many decisions a read holds when the reader does not say.</summary>
    public const int DefaultList = 50;

    /// <summary>The most decisions one read may hold.</summary>
    public const int MaxList = 500;

    /// <summary>The longest a subject is kept. A longer one is cut here, because a subject is the sender's text, not
    /// ours, and the row must still be kept.</summary>
    public const int MaxSubjectLength = 1000;

    /// <summary>The longest a reason or an outcome's detail is kept.</summary>
    public const int MaxReasonLength = 1000;

    /// <summary>The longest a message key may be - its column's bound. A longer key is refused rather than cut: a cut
    /// key could make two messages one, and SQLite would keep what PostgreSQL refuses, so the two would disagree.</summary>
    public const int MaxMessageKeyLength = 512;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public MailDeskDecisionStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    // ---------- the one write ----------

    /// <summary>
    /// Record one decision. Every word is checked against <see cref="MailDeskWords"/> and a route names its factory and
    /// seat; anything else is a caller defect and throws. The channel must be one of this account's channels (removed
    /// or not). A message already recorded on the channel returns the kept row with <c>Inserted</c> false.
    /// </summary>
    public MailDeskDecisionInsert Insert(TenantId tenant, MailDeskDecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Require(input);
        var row = new MailDeskDecisionEntity
        {
            TenantId = tenant.Value,
            ChannelId = input.ChannelId,
            MessageKey = input.MessageKey,
            ReceivedUtc = input.ReceivedUtc,
            SenderAddress = input.SenderAddress.Trim().ToLowerInvariant(),
            Subject = Cut(input.Subject ?? "", MaxSubjectLength),
            Layer = input.Layer,
            Decision = input.Decision,
            RouteFactory = input.Decision == MailDeskWords.Decisions.Route ? input.RouteFactory : null,
            RouteSeat = input.Decision == MailDeskWords.Decisions.Route ? input.RouteSeat : null,
            Reason = Cut(input.Reason ?? "", MaxReasonLength),
            Confidence = input.Confidence,
            CostMicros = input.CostMicros,
            Mode = input.Mode,
            Outcome = input.Outcome,
            OutcomeDetail = input.OutcomeDetail is null ? null : Cut(input.OutcomeDetail, MaxReasonLength),
            DecidedUtc = input.DecidedUtc,
            RulesVersion = input.RulesVersion,
            AttemptCount = input.AttemptCount,
        };

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            if (!ctx.MailDeskChannels.AsNoTracking().Any(c => c.Id == input.ChannelId))
                throw new ArgumentException($"Mail Desk channel {input.ChannelId} is not a channel of this account.", nameof(input));
            if (Kept(ctx, input) is { } before) return new MailDeskDecisionInsert(false, before);
            ctx.MailDeskDecisions.Add(row);
            try
            {
                ctx.SaveChanges();
            }
            catch (DbUpdateException ex)
            {
                ctx.ChangeTracker.Clear();
                // Another replica recorded the same message first: the unique index refused this one.
                if (Kept(ctx, input) is { } raced) return new MailDeskDecisionInsert(false, raced);
                FileLog.Write($"[MailDeskDecisionStore] Insert FAILED on channel {input.ChannelId} and no other writer holds the message: {ex.Message}");
                throw;
            }
        }
        FileLog.Write($"[MailDeskDecisionStore] Insert: {row.Id} on channel {row.ChannelId}: {row.Layer} -> {row.Decision} ({row.Mode})");
        return new MailDeskDecisionInsert(true, row);
    }

    /// <summary>
    /// THE SCREENING'S ONE WRITE (phase 3, sections 1 and 8): record a decision once per (channel, message key), and
    /// change a kept one ONLY when it is <c>failed</c> - that email is being screened again, so its row is updated in
    /// place with the new decision, cost and attempt count. A kept decision that is not <c>failed</c> is never changed:
    /// the row is returned with <c>Inserted</c> false and nothing is written. Answers whether a row was written.
    /// </summary>
    public MailDeskDecisionWrite Record(TenantId tenant, MailDeskDecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Require(input);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var kept = ctx.MailDeskDecisions.FirstOrDefault(d => d.ChannelId == input.ChannelId && d.MessageKey == input.MessageKey);
            if (kept is null)
            {
                var inserted = Insert(tenant, input);
                return new MailDeskDecisionWrite(inserted.Inserted ? MailDeskDecisionWriteKind.Inserted : MailDeskDecisionWriteKind.Kept, inserted.Decision);
            }
            if (kept.Decision != MailDeskWords.Decisions.Failed)
                return new MailDeskDecisionWrite(MailDeskDecisionWriteKind.Kept, kept);

            kept.ReceivedUtc = input.ReceivedUtc;
            kept.SenderAddress = input.SenderAddress.Trim().ToLowerInvariant();
            kept.Subject = Cut(input.Subject ?? "", MaxSubjectLength);
            kept.Layer = input.Layer;
            kept.Decision = input.Decision;
            kept.RouteFactory = input.Decision == MailDeskWords.Decisions.Route ? input.RouteFactory : null;
            kept.RouteSeat = input.Decision == MailDeskWords.Decisions.Route ? input.RouteSeat : null;
            kept.Reason = Cut(input.Reason ?? "", MaxReasonLength);
            kept.Confidence = input.Confidence;
            kept.CostMicros = input.CostMicros;
            kept.Mode = input.Mode;
            kept.Outcome = input.Outcome;
            kept.OutcomeDetail = input.OutcomeDetail is null ? null : Cut(input.OutcomeDetail, MaxReasonLength);
            kept.DecidedUtc = input.DecidedUtc;
            kept.RulesVersion = input.RulesVersion;
            kept.AttemptCount = input.AttemptCount;
            ctx.SaveChanges();
            FileLog.Write($"[MailDeskDecisionStore] Record: {kept.Id} on channel {kept.ChannelId} screened again (attempt {kept.AttemptCount}): {kept.Layer} -> {kept.Decision} ({kept.Mode})");
            return new MailDeskDecisionWrite(MailDeskDecisionWriteKind.Updated, kept);
        }
    }

    /// <summary>
    /// WRITE WHAT FILING CAME TO (phase 4 contract, section 2.3) on the decision of one message of one channel. A
    /// decision whose outcome is already set is left as it is - a retried move after a channel failure comes back
    /// <c>already-gone</c> for what was moved before, and the first <c>filed</c> stands. Answers what was done.
    /// </summary>
    public MailDeskFilingWrite RecordFiling(TenantId tenant, Guid channelId, string messageKey, string outcome, string detail)
    {
        if (!MailDeskWords.IsOneOf(outcome, MailDeskWords.Outcomes.All))
            throw new ArgumentException($"'{outcome}' is not a Mail Desk outcome; it is one of {MailDeskWords.InWords(MailDeskWords.Outcomes.All)}.", nameof(outcome));
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var kept = ctx.MailDeskDecisions.FirstOrDefault(d => d.ChannelId == channelId && d.MessageKey == messageKey);
            if (kept is null) return MailDeskFilingWrite.NoSuchDecision;
            if (kept.Outcome is not null)
            {
                FileLog.Write($"[MailDeskDecisionStore] RecordFiling: {kept.Id} on channel {channelId} already holds outcome {kept.Outcome}; {outcome} ignored");
                return MailDeskFilingWrite.AlreadySet;
            }
            kept.Outcome = outcome;
            kept.OutcomeDetail = Cut(detail, MaxReasonLength);
            ctx.SaveChanges();
            FileLog.Write($"[MailDeskDecisionStore] RecordFiling: {kept.Id} on channel {channelId}: {kept.Decision} -> {outcome}");
            return MailDeskFilingWrite.Written;
        }
    }

    // ---------- reads ----------

    /// <summary>
    /// The decisions of one channel waiting to be filed (phase 4 contract, section 2.1): <c>archive</c> or <c>junk</c>,
    /// made in <c>live</c> mode, with no outcome yet - oldest receive time first.
    /// </summary>
    public IReadOnlyList<MailDeskDecisionEntity> AwaitingFiling(TenantId tenant, Guid channelId)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.MailDeskDecisions.AsNoTracking()
                .Where(d => d.ChannelId == channelId && d.Mode == MailDeskWords.Modes.Live && d.Outcome == null
                            && (d.Decision == MailDeskWords.Decisions.Archive || d.Decision == MailDeskWords.Decisions.Junk))
                .OrderBy(d => d.ReceivedUtc).ThenBy(d => d.MessageKey)
                .ToList();
        }
    }

    /// <summary>The decision kept for one message of one channel, or null when it was never screened.</summary>
    public MailDeskDecisionEntity? Find(TenantId tenant, Guid channelId, string messageKey)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.MailDeskDecisions.AsNoTracking().FirstOrDefault(d => d.ChannelId == channelId && d.MessageKey == messageKey);
        }
    }

    /// <summary>
    /// The decisions newest first, every filter applied: decided at or after <see cref="MailDeskDecisionFilter.SinceUtc"/>,
    /// one layer, one decision, one channel address (every channel row that ever had that address, removed or not).
    /// The total and the counts by decision are over everything that matches, not only the page.
    /// </summary>
    public MailDeskDecisionList List(TenantId tenant, MailDeskDecisionFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.N is < 1 or > MaxList)
            throw new ArgumentOutOfRangeException(nameof(filter), filter.N, $"a decisions read holds 1 to {MaxList} decisions");
        if (filter.Layer is not null && !MailDeskWords.IsOneOf(filter.Layer, MailDeskWords.Layers.All))
            throw new ArgumentException($"'{filter.Layer}' is not a Mail Desk layer; it is one of {MailDeskWords.InWords(MailDeskWords.Layers.All)}.", nameof(filter));
        if (filter.Decision is not null && !MailDeskWords.IsOneOf(filter.Decision, MailDeskWords.Decisions.All))
            throw new ArgumentException($"'{filter.Decision}' is not a Mail Desk decision; it is one of {MailDeskWords.InWords(MailDeskWords.Decisions.All)}.", nameof(filter));

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.MailDeskDecisions.AsNoTracking();
            if (filter.ChannelAddress is not null)
            {
                var address = filter.ChannelAddress.Trim().ToLowerInvariant();
                var channelIds = ctx.MailDeskChannels.AsNoTracking().Where(c => c.Address == address).Select(c => c.Id).ToList();
                if (channelIds.Count == 0) return MailDeskDecisionList.Empty;
                rows = rows.Where(d => channelIds.Contains(d.ChannelId));
            }
            if (filter.SinceUtc is { } since) rows = rows.Where(d => d.DecidedUtc >= since);
            if (filter.Layer is not null) rows = rows.Where(d => d.Layer == filter.Layer);
            if (filter.Decision is not null) rows = rows.Where(d => d.Decision == filter.Decision);

            var byDecision = MailDeskDecisionCounts.From(rows.GroupBy(d => d.Decision).Select(g => new { g.Key, Count = g.Count() })
                .ToList().Select(g => (g.Key, g.Count)));
            var page = rows.OrderByDescending(d => d.DecidedUtc).ThenByDescending(d => d.Id).Take(filter.N).ToList();
            return new MailDeskDecisionList(page, byDecision.Total, byDecision);
        }
    }

    /// <summary>One decision, or null when the account has no such decision.</summary>
    public MailDeskDecisionEntity? Get(TenantId tenant, Guid id)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.MailDeskDecisions.AsNoTracking().FirstOrDefault(d => d.Id == id);
        }
    }

    /// <summary>
    /// The counts by decision for each channel, of the decisions made at or after <paramref name="fromUtc"/> and before
    /// <paramref name="toUtc"/> - what "today" is once the route has turned the account's local midnight into instants.
    /// A channel with no decisions in the span is not in the map; <see cref="MailDeskDecisionCounts.Zero"/> stands for it.
    /// </summary>
    public IReadOnlyDictionary<Guid, MailDeskDecisionCounts> CountsByChannel(TenantId tenant, DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc < fromUtc)
            throw new ArgumentException($"The span ends ({toUtc:o}) before it starts ({fromUtc:o}).", nameof(toUtc));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var groups = ctx.MailDeskDecisions.AsNoTracking()
                .Where(d => d.DecidedUtc >= fromUtc && d.DecidedUtc < toUtc)
                .GroupBy(d => new { d.ChannelId, d.Decision })
                .Select(g => new { g.Key.ChannelId, g.Key.Decision, Count = g.Count() })
                .ToList();
            return groups.GroupBy(g => g.ChannelId)
                .ToDictionary(g => g.Key, g => MailDeskDecisionCounts.From(g.Select(x => (x.Decision, x.Count))));
        }
    }

    /// <summary>
    /// Every decision made at or after <paramref name="fromUtc"/> and before <paramref name="toUtc"/>, oldest first -
    /// all of them, never a page, because the 08:00 email lists every junked email of its day (phase 3 contract,
    /// section 9) so a wrongly junked one is seen.
    /// </summary>
    public IReadOnlyList<MailDeskDecisionEntity> DecidedBetween(TenantId tenant, DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc < fromUtc)
            throw new ArgumentException($"The span ends ({toUtc:o}) before it starts ({fromUtc:o}).", nameof(toUtc));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.MailDeskDecisions.AsNoTracking()
                .Where(d => d.DecidedUtc >= fromUtc && d.DecidedUtc < toUtc)
                .OrderBy(d => d.DecidedUtc).ThenBy(d => d.Id)
                .ToList();
        }
    }

    /// <summary>
    /// What the Mail Desk's model calls have cost the account, ever, in millionths of a dollar: the sum of the cost on
    /// every decision row (phase 3 contract, section 1 - a failed screening keeps its call's cost on its row too).
    /// </summary>
    public long SpentMicros(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            // Summed here rather than in the database: SQLite and PostgreSQL disagree on the type of a sum of nothing.
            return ctx.MailDeskDecisions.AsNoTracking().Where(d => d.CostMicros != null).Select(d => d.CostMicros!.Value).ToList().Sum();
        }
    }

    private static MailDeskDecisionEntity? Kept(GatewayDbContext ctx, MailDeskDecisionInput input)
        => ctx.MailDeskDecisions.AsNoTracking().FirstOrDefault(d => d.ChannelId == input.ChannelId && d.MessageKey == input.MessageKey);

    private static void Require(MailDeskDecisionInput input)
    {
        if (string.IsNullOrWhiteSpace(input.MessageKey))
            throw new ArgumentException("A Mail Desk decision needs the message's key.", nameof(input));
        if (input.MessageKey.Length > MaxMessageKeyLength)
            throw new ArgumentException($"A Mail Desk message key takes at most {MaxMessageKeyLength} characters; this one is {input.MessageKey.Length}.", nameof(input));
        // An empty address is a sender the mailbox gave none for (phase 3, section 3): it is recorded as empty.
        if (input.SenderAddress is null || input.SenderAddress.Trim().Length > MailDeskAddresses.MaxLength)
            throw new ArgumentException($"A Mail Desk decision needs the sender's address (empty when the mailbox gave none), at most {MailDeskAddresses.MaxLength} characters.", nameof(input));
        if (input.RulesVersion < 0)
            throw new ArgumentException($"A rules version is not below zero; {input.RulesVersion} is.", nameof(input));
        if (input.AttemptCount < 1)
            throw new ArgumentException($"A decision is made on attempt 1 or later; {input.AttemptCount} is not.", nameof(input));
        if (!MailDeskWords.IsOneOf(input.Layer, MailDeskWords.Layers.All))
            throw new ArgumentException($"'{input.Layer}' is not a Mail Desk layer; it is one of {MailDeskWords.InWords(MailDeskWords.Layers.All)}.", nameof(input));
        if (!MailDeskWords.IsOneOf(input.Decision, MailDeskWords.Decisions.All))
            throw new ArgumentException($"'{input.Decision}' is not a Mail Desk decision; it is one of {MailDeskWords.InWords(MailDeskWords.Decisions.All)}.", nameof(input));
        if (!MailDeskWords.IsOneOf(input.Mode, MailDeskWords.Modes.All))
            throw new ArgumentException($"'{input.Mode}' is not a Mail Desk mode; it is one of {MailDeskWords.InWords(MailDeskWords.Modes.All)}.", nameof(input));
        if (input.Outcome is not null && !MailDeskWords.IsOneOf(input.Outcome, MailDeskWords.Outcomes.All))
            throw new ArgumentException($"'{input.Outcome}' is not a Mail Desk outcome; it is one of {MailDeskWords.InWords(MailDeskWords.Outcomes.All)}.", nameof(input));
        if (input.Decision == MailDeskWords.Decisions.Route && (string.IsNullOrWhiteSpace(input.RouteFactory) || string.IsNullOrWhiteSpace(input.RouteSeat)))
            throw new ArgumentException("A Mail Desk decision to route names the factory and the seat.", nameof(input));
        if (input.Confidence is < 0 or > 1)
            throw new ArgumentException($"A model's confidence is between 0 and 1; {input.Confidence} is not.", nameof(input));
        if (input.CostMicros is < 0)
            throw new ArgumentException($"A model call's cost is not negative; {input.CostMicros} is.", nameof(input));
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>One decision to record. There is no body field, on purpose.</summary>
public sealed record MailDeskDecisionInput(
    Guid ChannelId,
    string MessageKey,
    DateTime ReceivedUtc,
    string SenderAddress,
    string Subject,
    string Layer,
    string Decision,
    string? RouteFactory,
    string? RouteSeat,
    string Reason,
    double? Confidence,
    long? CostMicros,
    string Mode,
    string? Outcome,
    string? OutcomeDetail,
    DateTime DecidedUtc,
    int RulesVersion = 0,
    int AttemptCount = 1);

/// <summary>What <see cref="MailDeskDecisionStore.Record"/> did with one decision.</summary>
public enum MailDeskDecisionWriteKind
{
    /// <summary>The message had no decision; this one was written.</summary>
    Inserted,
    /// <summary>The message held a <c>failed</c> decision; it was decided again in place.</summary>
    Updated,
    /// <summary>The message already held a decision that is not <c>failed</c>; nothing was written.</summary>
    Kept,
}

/// <summary>What <see cref="MailDeskDecisionStore.RecordFiling"/> did.</summary>
public enum MailDeskFilingWrite
{
    /// <summary>The outcome was written.</summary>
    Written,
    /// <summary>The decision already held an outcome; it stands and nothing was written.</summary>
    AlreadySet,
    /// <summary>The channel has no decision for that message; nothing was written.</summary>
    NoSuchDecision,
}

/// <summary>The result of <see cref="MailDeskDecisionStore.Record"/>: what was done, and the row as it now stands.</summary>
public sealed record MailDeskDecisionWrite(MailDeskDecisionWriteKind Kind, MailDeskDecisionEntity Decision);

/// <summary>The result of <see cref="MailDeskDecisionStore.Insert"/>: the row kept, and whether this call wrote it.</summary>
public sealed record MailDeskDecisionInsert(bool Inserted, MailDeskDecisionEntity Decision);

/// <summary>The filters of a decisions read (contract section 5, <c>GET /decisions</c>). Null is "any".</summary>
public sealed record MailDeskDecisionFilter(
    DateTime? SinceUtc = null,
    string? Layer = null,
    string? Decision = null,
    string? ChannelAddress = null,
    int N = MailDeskDecisionStore.DefaultList);

/// <summary>One page of decisions, newest first, with the total and the counts by decision of everything matching.</summary>
public sealed record MailDeskDecisionList(IReadOnlyList<MailDeskDecisionEntity> Decisions, int Total, MailDeskDecisionCounts ByDecision)
{
    public static readonly MailDeskDecisionList Empty = new(Array.Empty<MailDeskDecisionEntity>(), 0, MailDeskDecisionCounts.Zero);
}

/// <summary>How many decisions of each kind: every decision word listed, zeros included, in the words' own order.</summary>
public sealed record MailDeskDecisionCounts(int Total, IReadOnlyDictionary<string, int> ByDecision)
{
    public static readonly MailDeskDecisionCounts Zero = From(Array.Empty<(string, int)>());

    /// <summary>Counts from (decision, count) pairs, every decision word present.</summary>
    public static MailDeskDecisionCounts From(IEnumerable<(string Decision, int Count)> counts)
    {
        var map = MailDeskWords.Decisions.All.ToDictionary(d => d, _ => 0, StringComparer.Ordinal);
        foreach (var (decision, count) in counts)
        {
            if (!map.ContainsKey(decision))
                throw new InvalidOperationException($"A stored Mail Desk decision is '{decision}', which is not a decision word.");
            map[decision] += count;
        }
        return new MailDeskDecisionCounts(map.Values.Sum(), map);
    }

    /// <summary>The counts of several channels added together - the account's whole day.</summary>
    public static MailDeskDecisionCounts Sum(IEnumerable<MailDeskDecisionCounts> parts)
        => From(parts.SelectMany(p => p.ByDecision.Select(kv => (kv.Key, kv.Value))));
}
