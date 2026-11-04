using System.Text.Json;
using CcDirector.Core.Dictation;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Inbox;

/// <summary>
/// THE FACTORY INBOX STORE (Factory Inbox mission, phase 1): the messages the owner leaves in a factory, the bundles its
/// Secretary hands to its Boss, the Boss's one decision on each message, and the factory's to-do list.
///
/// This store decides nothing about WHO may call it - that is the routes' ruling (<c>Api.FactoryInboxEndpoints</c>), made
/// from the caller's own recorded session history. It decides only what a call may do to the rows: a message is
/// received once, a claim held by an ended session may be taken by the next, a bundle holds only what its Secretary
/// received, and a message is decided once.
///
/// EXCLUSIVITY ACROSS GATEWAY PROCESSES. A claim is taken by one conditional update, as a dev report action claim is
/// (<c>DevReports.DevReportStore.ClaimUnactedItems</c>), and one decision per message is held by a unique index, so two
/// replicas cannot both win. The lock only keeps this process's own writers in line.
/// </summary>
public sealed class FactoryInboxStore
{
    /// <summary>The longest message, in characters (UTF-16 code units).</summary>
    public const int MaxTextLength = 20000;

    /// <summary>The longest "why" on a decision, in characters.</summary>
    public const int MaxWhyLength = 4000;

    /// <summary>The longest reference a Secretary may attach.</summary>
    public const int MaxLinkRefLength = 400;

    /// <summary>The most links one message may carry.</summary>
    public const int MaxLinks = 20;

    /// <summary>The most messages a thread read returns when the reader does not say.</summary>
    public const int DefaultThread = 100;

    /// <summary>The most messages one thread read may return.</summary>
    public const int MaxThread = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FactoryInboxStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    // ---------------------------------------------------------------- the owner's send

    /// <summary>
    /// Log one message the owner left in a factory, in state <c>sent</c>. The input is checked here and a bad one is
    /// refused with a sentence (<see cref="FactoryInboxRefusedException"/>): an empty or too-long text, an unknown note,
    /// source or surface. A SPOKEN message must carry the speech model's raw text and the corrections applied to it, and
    /// applying exactly those corrections to the raw text must give the text - the verbatim rule (CLAUDE.md): what was
    /// said is what is kept, changed only by the owner's hand-listed corrections. A typed message carries neither.
    /// </summary>
    public FactoryInboxMessageEntity Send(TenantId tenant, FactoryInboxSendInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Factory);
        FileLog.Write($"[FactoryInboxStore] Send: tenant={tenant.ToLogString()} factory={input.Factory} source={input.Source} surface={input.Surface}");

        var text = input.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
            throw new FactoryInboxRefusedException("message_empty", "The message is empty. Write or say what you want the factory to know.");
        if (text.Length > MaxTextLength)
            throw new FactoryInboxRefusedException("message_too_long", $"The message is {text.Length} characters; the limit is {MaxTextLength}.");
        if (!FactoryInboxWords.Notes.All.Contains(input.Note))
            throw new FactoryInboxRefusedException("bad_note", $"The note must be one of {string.Join(", ", FactoryInboxWords.Notes.All)}, not '{input.Note}'.");
        if (!FactoryInboxWords.Sources.All.Contains(input.Source))
            throw new FactoryInboxRefusedException("bad_source", $"The source must be one of {string.Join(", ", FactoryInboxWords.Sources.All)}, not '{input.Source}'.");
        if (input.Surface is not (FactoryInboxWords.Surfaces.Phone or FactoryInboxWords.Surfaces.Cockpit or FactoryInboxWords.Surfaces.Session))
            throw new ArgumentException($"'{input.Surface}' is not a factory inbox surface.", nameof(input));

        string? raw = null, corrections = null;
        if (input.Source == FactoryInboxWords.Sources.Spoken)
        {
            if (input.RawText is null || input.Corrections is null)
                throw new FactoryInboxRefusedException("spoken_without_transcript",
                    "A spoken message carries the speech model's raw text and the corrections applied to it.");
            if (input.RawText.Length > MaxTextLength)
                throw new FactoryInboxRefusedException("message_too_long", $"The raw transcript is {input.RawText.Length} characters; the limit is {MaxTextLength}.");
            var (applied, _) = TranscriptEditEngine.Apply(input.RawText, input.Corrections);
            if (!string.Equals(applied, text, StringComparison.Ordinal))
                throw new FactoryInboxRefusedException("spoken_text_not_verbatim",
                    "The spoken message's text is not its raw transcript with only the listed corrections applied, so it was not kept. " +
                    "What was said is what is kept.");
            raw = input.RawText;
            corrections = JsonSerializer.Serialize(input.Corrections.Select(c => new FactoryInboxCorrection(c.Find, c.Replace)), Json);
        }
        else if (input.RawText is not null || input.Corrections is not null)
        {
            throw new FactoryInboxRefusedException("typed_with_transcript", "A typed message carries no raw transcript and no corrections.");
        }

        var row = new FactoryInboxMessageEntity
        {
            TenantId = tenant.Value,
            Factory = input.Factory,
            Text = text,
            RawText = raw,
            CorrectionsJson = corrections,
            Note = input.Note,
            Source = input.Source,
            Surface = input.Surface,
            SentBy = input.SentBy,
            SentAtUtc = input.NowUtc,
            State = FactoryInboxWords.States.Sent,
        };
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            ctx.FactoryInboxMessages.Add(row);
            ctx.SaveChanges();
        }
        FileLog.Write($"[FactoryInboxStore] Send: logged {row.Id} in {row.Factory}");
        return row;
    }

    // ---------------------------------------------------------------- reads

    /// <summary>One message of the account, or null.</summary>
    public FactoryInboxMessageEntity? Get(TenantId tenant, Guid id)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.FactoryInboxMessages.AsNoTracking().FirstOrDefault(m => m.Id == id);
    }

    /// <summary>One factory's messages, newest first, at most <paramref name="limit"/>; and how many it holds in all.</summary>
    public (IReadOnlyList<FactoryInboxMessageEntity> Messages, int Total) Thread(TenantId tenant, string factory, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factory);
        using var ctx = _db.CreateContext(tenant);
        var query = ctx.FactoryInboxMessages.AsNoTracking().Where(m => m.Factory == factory);
        var total = query.Count();
        var rows = query.ToList().OrderByDescending(m => m.SentAtUtc).ThenByDescending(m => m.Id).Take(limit).ToList();
        FileLog.Write($"[FactoryInboxStore] Thread: factory={factory} total={total} returned={rows.Count}");
        return (rows, total);
    }

    /// <summary>The decisions on these messages, by message id.</summary>
    public IReadOnlyDictionary<Guid, FactoryInboxDecisionEntity> DecisionsOf(TenantId tenant, IReadOnlyCollection<Guid> messageIds)
    {
        if (messageIds.Count == 0) return new Dictionary<Guid, FactoryInboxDecisionEntity>();
        using var ctx = _db.CreateContext(tenant);
        return ctx.FactoryInboxDecisions.AsNoTracking().Where(d => messageIds.Contains(d.MessageId)).ToList()
            .ToDictionary(d => d.MessageId);
    }

    /// <summary>When each of these bundles was handed to the Boss, by bundle id.</summary>
    public IReadOnlyDictionary<Guid, DateTime> HandedTimes(TenantId tenant, IReadOnlyCollection<Guid> bundleIds)
    {
        if (bundleIds.Count == 0) return new Dictionary<Guid, DateTime>();
        using var ctx = _db.CreateContext(tenant);
        return ctx.FactoryInboxBundles.AsNoTracking().Where(b => bundleIds.Contains(b.Id)).ToList()
            .ToDictionary(b => b.Id, b => b.HandedAtUtc);
    }

    /// <summary>For every factory of the account with a message: how many are not yet decided, and the newest one.</summary>
    public IReadOnlyDictionary<string, FactoryInboxFactoryCounts> CountsByFactory(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.FactoryInboxMessages.AsNoTracking().ToList();
        var counts = rows.GroupBy(m => m.Factory, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => new FactoryInboxFactoryCounts(
                g.Count(m => m.State != FactoryInboxWords.States.Decided),
                g.OrderByDescending(m => m.SentAtUtc).ThenByDescending(m => m.Id).First()),
            StringComparer.Ordinal);
        FileLog.Write($"[FactoryInboxStore] CountsByFactory: tenant={tenant.ToLogString()} factories={counts.Count}");
        return counts;
    }

    /// <summary>What a factory's summary is built from: the messages sent, and the decisions made, from since up to and
    /// including until.</summary>
    public (IReadOnlyList<FactoryInboxMessageEntity> Sent, IReadOnlyList<FactoryInboxDecisionEntity> Decided) Since(
        TenantId tenant, string factory, DateTime sinceUtc, DateTime untilUtc)
    {
        using var ctx = _db.CreateContext(tenant);
        var sent = ctx.FactoryInboxMessages.AsNoTracking()
            .Where(m => m.Factory == factory && m.SentAtUtc >= sinceUtc && m.SentAtUtc <= untilUtc).ToList()
            .OrderBy(m => m.SentAtUtc).ThenBy(m => m.Id).ToList();
        var decided = ctx.FactoryInboxDecisions.AsNoTracking()
            .Where(d => d.Factory == factory && d.DecidedAtUtc >= sinceUtc && d.DecidedAtUtc <= untilUtc).ToList()
            .OrderBy(d => d.DecidedAtUtc).ThenBy(d => d.Id).ToList();
        return (sent, decided);
    }

    /// <summary>One factory's to-do list, newest first.</summary>
    public IReadOnlyList<FactoryTodoEntity> Todos(TenantId tenant, string factory)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.FactoryTodos.AsNoTracking().Where(t => t.Factory == factory).ToList()
            .OrderByDescending(t => t.AddedAtUtc).ThenByDescending(t => t.Id).ToList();
    }

    // ---------------------------------------------------------------- the Secretary

    /// <summary>The sessions OTHER than <paramref name="sessionId"/> holding a claim on one of the factory's received,
    /// not yet bundled messages - the caller asks which of them have ended, and those claims may be taken.</summary>
    public IReadOnlyList<string> OtherClaimants(TenantId tenant, string factory, string sessionId)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.FactoryInboxMessages.AsNoTracking()
            .Where(m => m.Factory == factory && m.State == FactoryInboxWords.States.Received && m.BundleId == null
                        && m.ClaimSessionId != null && m.ClaimSessionId != sessionId)
            .Select(m => m.ClaimSessionId!)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// What a Secretary has to file: the factory's messages not yet received, the received ones whose claim is held by an
    /// ended session (<paramref name="endedClaimants"/>), and the ones this session holds and has not yet handed on.
    /// Oldest first.
    /// </summary>
    public IReadOnlyList<FactoryInboxMessageEntity> ToFile(TenantId tenant, string factory, string sessionId,
        IReadOnlyCollection<string> endedClaimants)
    {
        using var ctx = _db.CreateContext(tenant);
        var rows = Claimable(ctx.FactoryInboxMessages.AsNoTracking(), factory, sessionId, endedClaimants).ToList()
            .OrderBy(m => m.SentAtUtc).ThenBy(m => m.Id).ToList();
        FileLog.Write($"[FactoryInboxStore] ToFile: factory={factory} sid={sessionId} count={rows.Count}");
        return rows;
    }

    /// <summary>
    /// A Secretary RECEIVES messages: claims them for <paramref name="sessionId"/> and marks them received, in one
    /// conditional update. <paramref name="ids"/> null means every message it may file; named ids that it may not file
    /// (another live session holds them, they are already with the Boss, or they are not this factory's) are refused.
    /// Then the merges and links are written on the claimed rows. Returns the messages this call holds.
    /// </summary>
    public IReadOnlyList<FactoryInboxMessageEntity> Receive(TenantId tenant, string factory, string sessionId,
        IReadOnlyCollection<Guid>? ids, IReadOnlyCollection<string> endedClaimants,
        IReadOnlyList<FactoryInboxMerge> merges, IReadOnlyList<FactoryInboxLink> links, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(merges);
        ArgumentNullException.ThrowIfNull(links);
        FileLog.Write($"[FactoryInboxStore] Receive: factory={factory} sid={sessionId} ids={ids?.Count.ToString() ?? "(all)"} merges={merges.Count} links={links.Count}");
        CheckLinks(links);

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var claimable = Claimable(ctx.FactoryInboxMessages, factory, sessionId, endedClaimants);
            var candidateIds = claimable.AsNoTracking().Select(m => m.Id).ToList();
            if (ids is not null)
            {
                var notHers = ids.Where(id => !candidateIds.Contains(id)).ToList();
                if (notHers.Count > 0)
                    throw new FactoryInboxRefusedException("not_yours_to_receive",
                        $"These messages cannot be received by this session: {string.Join(", ", notHers.Select(i => i.ToString("D")))}. " +
                        "Each is not in this factory's inbox, already with the Boss, or held by another Secretary that is still running.");
                candidateIds = candidateIds.Where(ids.Contains).ToList();
            }
            var mentioned = merges.SelectMany(m => new[] { m.Id, m.Into }).Concat(links.Select(l => l.Id)).Distinct().ToList();
            if (candidateIds.Count == 0)
            {
                if (mentioned.Count > 0)
                    throw new FactoryInboxRefusedException("nothing_to_receive", "There is nothing to receive, so the merges and links were not written.");
                FileLog.Write($"[FactoryInboxStore] Receive: factory={factory} sid={sessionId} nothing to receive");
                return [];
            }

            using var tx = ctx.Database.BeginTransaction();
            ctx.FactoryInboxMessages
                .Where(m => candidateIds.Contains(m.Id) && m.BundleId == null
                            && (m.ClaimSessionId == null || m.ClaimSessionId == sessionId || (m.ClaimSessionId != null && endedClaimants.Contains(m.ClaimSessionId))))
                .ExecuteUpdate(set => set
                    .SetProperty(m => m.State, FactoryInboxWords.States.Received)
                    .SetProperty(m => m.ClaimSessionId, sessionId)
                    .SetProperty(m => m.ClaimedAtUtc, nowUtc));
            var held = ctx.FactoryInboxMessages
                .Where(m => candidateIds.Contains(m.Id) && m.ClaimSessionId == sessionId && m.ClaimedAtUtc == nowUtc)
                .ToList();
            foreach (var m in held.Where(m => m.ReceivedAtUtc is null))
            {
                m.ReceivedAtUtc = nowUtc;
                m.ReceivedBySessionId = sessionId;
            }

            // Merges and links may name any message this session now holds, whether this call or an earlier one took it.
            var mine = ctx.FactoryInboxMessages
                .Where(m => m.Factory == factory && m.ClaimSessionId == sessionId && m.BundleId == null && m.State == FactoryInboxWords.States.Received)
                .ToList()
                .ToDictionary(m => m.Id);
            foreach (var h in held) mine[h.Id] = h;
            var notHeld = mentioned.Where(id => !mine.ContainsKey(id)).ToList();
            if (notHeld.Count > 0)
                throw new FactoryInboxRefusedException("merge_or_link_not_held",
                    $"A merge or link names messages this session does not hold: {string.Join(", ", notHeld.Select(i => i.ToString("D")))}. Nothing was written.");
            foreach (var merge in merges)
            {
                if (merge.Id == merge.Into)
                    throw new FactoryInboxRefusedException("merge_into_itself", $"Message {merge.Id:D} cannot be merged into itself. Nothing was written.");
                if (merges.Any(other => other.Id == merge.Into))
                    throw new FactoryInboxRefusedException("merge_chain",
                        $"Message {merge.Into:D} is itself merged into another; merge each repeat into the one message it repeats. Nothing was written.");
                if (mine[merge.Into].MergedIntoId is not null)
                    throw new FactoryInboxRefusedException("merge_chain",
                        $"Message {merge.Into:D} is itself merged into another; merge each repeat into the one message it repeats. Nothing was written.");
                mine[merge.Id].MergedIntoId = merge.Into;
            }
            foreach (var group in links.GroupBy(l => l.Id))
            {
                var target = mine[group.Key];
                var all = ReadLinks(target.LinksJson).Concat(group.Select(l => new FactoryInboxLinkDto(l.Kind, l.Ref.Trim()))).Distinct().ToList();
                if (all.Count > MaxLinks)
                    throw new FactoryInboxRefusedException("too_many_links", $"Message {group.Key:D} would carry {all.Count} links; the limit is {MaxLinks}. Nothing was written.");
                target.LinksJson = JsonSerializer.Serialize(all, Json);
            }
            ctx.SaveChanges();
            tx.Commit();

            var result = held.OrderBy(m => m.SentAtUtc).ThenBy(m => m.Id).ToList();
            FileLog.Write($"[FactoryInboxStore] Receive: factory={factory} sid={sessionId} received={result.Count}");
            return result;
        }
    }

    /// <summary>
    /// A Secretary HANDS ONE BUNDLE to the Boss: every message it holds and has not handed on (or only the named ones), as
    /// one new bundle, each message now <c>with-boss</c>. Nothing held means nothing is handed and a refusal says so.
    /// </summary>
    public (FactoryInboxBundleEntity Bundle, IReadOnlyList<FactoryInboxMessageEntity> Messages) HandBundle(
        TenantId tenant, string factory, string sessionId, IReadOnlyCollection<Guid>? ids, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        FileLog.Write($"[FactoryInboxStore] HandBundle: factory={factory} sid={sessionId} ids={ids?.Count.ToString() ?? "(all)"}");
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var heldIds = ctx.FactoryInboxMessages.AsNoTracking()
                .Where(m => m.Factory == factory && m.ClaimSessionId == sessionId && m.BundleId == null && m.State == FactoryInboxWords.States.Received)
                .Select(m => m.Id).ToList();
            if (ids is not null)
            {
                var notHeld = ids.Where(id => !heldIds.Contains(id)).ToList();
                if (notHeld.Count > 0)
                    throw new FactoryInboxRefusedException("not_yours_to_hand",
                        $"These messages are not received by this session and waiting to be handed on: {string.Join(", ", notHeld.Select(i => i.ToString("D")))}. Receive them first.");
                heldIds = heldIds.Where(ids.Contains).ToList();
            }
            if (heldIds.Count == 0)
                throw new FactoryInboxRefusedException("nothing_to_hand",
                    "This session holds no received message to hand on. Run the inbox list, receive what is new, then hand the bundle.");

            // A message merged into one that is not in this bundle would reach the Boss without the message it repeats.
            var merged = ctx.FactoryInboxMessages.AsNoTracking().Where(m => heldIds.Contains(m.Id) && m.MergedIntoId != null)
                .Select(m => new { m.Id, Into = m.MergedIntoId!.Value }).ToList();
            var orphan = merged.Where(m => !heldIds.Contains(m.Into)).ToList();
            if (orphan.Count > 0)
                throw new FactoryInboxRefusedException("merge_split",
                    $"Message {orphan[0].Id:D} is merged into {orphan[0].Into:D}, which is not in this bundle. Hand them together.");

            using var tx = ctx.Database.BeginTransaction();
            var bundle = new FactoryInboxBundleEntity
            {
                TenantId = tenant.Value,
                Factory = factory,
                HandedBySessionId = sessionId,
                HandedAtUtc = nowUtc,
            };
            ctx.FactoryInboxBundles.Add(bundle);
            ctx.SaveChanges();
            var moved = ctx.FactoryInboxMessages
                .Where(m => heldIds.Contains(m.Id) && m.ClaimSessionId == sessionId && m.BundleId == null)
                .ExecuteUpdate(set => set
                    .SetProperty(m => m.BundleId, bundle.Id)
                    .SetProperty(m => m.State, FactoryInboxWords.States.WithBoss)
                    .SetProperty(m => m.ClaimSessionId, (string?)null)
                    .SetProperty(m => m.ClaimedAtUtc, (DateTime?)null));
            if (moved != heldIds.Count)
            {
                tx.Rollback();
                FileLog.Write($"[FactoryInboxStore] HandBundle: factory={factory} sid={sessionId} moved {moved} of {heldIds.Count} - another writer moved the rest; rolled back");
                throw new FactoryInboxRefusedException("bundle_changed_underneath",
                    "Some of these messages were taken by another session while the bundle was being made, so no bundle was handed. List the inbox again.");
            }
            tx.Commit();
            var messages = ctx.FactoryInboxMessages.AsNoTracking().Where(m => m.BundleId == bundle.Id).ToList()
                .OrderBy(m => m.SentAtUtc).ThenBy(m => m.Id).ToList();
            FileLog.Write($"[FactoryInboxStore] HandBundle: factory={factory} bundle={bundle.Id} messages={messages.Count}");
            return (bundle, messages);
        }
    }

    // ---------------------------------------------------------------- the Boss

    /// <summary>The factory's open bundles, oldest first, with their messages. The first read by a Boss records which
    /// session read each bundle and when.</summary>
    public IReadOnlyList<(FactoryInboxBundleEntity Bundle, IReadOnlyList<FactoryInboxMessageEntity> Messages)> OpenBundles(
        TenantId tenant, string factory, string readerSessionId, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            ctx.FactoryInboxBundles.Where(b => b.Factory == factory && b.ClosedAtUtc == null && b.ReadBySessionId == null)
                .ExecuteUpdate(set => set
                    .SetProperty(b => b.ReadBySessionId, readerSessionId)
                    .SetProperty(b => b.ReadAtUtc, nowUtc));
            var bundles = ctx.FactoryInboxBundles.AsNoTracking().Where(b => b.Factory == factory && b.ClosedAtUtc == null).ToList()
                .OrderBy(b => b.HandedAtUtc).ThenBy(b => b.Id).ToList();
            var bundleIds = bundles.Select(b => (Guid?)b.Id).ToList();
            var messages = ctx.FactoryInboxMessages.AsNoTracking().Where(m => bundleIds.Contains(m.BundleId)).ToList();
            var result = bundles
                .Select(b => (b, (IReadOnlyList<FactoryInboxMessageEntity>)messages.Where(m => m.BundleId == b.Id)
                    .OrderBy(m => m.SentAtUtc).ThenBy(m => m.Id).ToList()))
                .ToList();
            FileLog.Write($"[FactoryInboxStore] OpenBundles: factory={factory} reader={readerSessionId} open={result.Count}");
            return result;
        }
    }

    /// <summary>
    /// The Boss DECIDES ONE MESSAGE that is with it. The caller has already ruled who may decide and, for delegated, that
    /// the named session is one this Boss started; this checks the rows: the message is this factory's and with the Boss,
    /// it is not a repeat merged into another (decide that one, and its repeats are decided with it), and it is not decided
    /// yet. To do adds one row to the factory's to-do list. When the last message of a bundle is decided the bundle closes.
    /// </summary>
    public FactoryInboxDecided Decide(TenantId tenant, string factory, string bossSessionId, Guid messageId,
        string kind, string? delegatedSessionId, string? why, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bossSessionId);
        FileLog.Write($"[FactoryInboxStore] Decide: factory={factory} message={messageId} kind={kind} by={bossSessionId}");
        if (!FactoryInboxWords.Decisions.All.Contains(kind))
            throw new FactoryInboxRefusedException("bad_decision", $"The decision must be one of {string.Join(", ", FactoryInboxWords.Decisions.All)}, not '{kind}'.");
        var reason = string.IsNullOrWhiteSpace(why) ? null : why.Trim();
        if (FactoryInboxWords.Decisions.NeedsWhy(kind) && reason is null)
            throw new FactoryInboxRefusedException("why_required",
                $"A decision of '{FactoryInboxWords.DecisionLabel(kind)}' says why, in your words, so the owner can read it.");
        if (reason is not null && reason.Length > MaxWhyLength)
            throw new FactoryInboxRefusedException("why_too_long", $"The why is {reason.Length} characters; the limit is {MaxWhyLength}.");
        if (kind == FactoryInboxWords.Decisions.Delegated && string.IsNullOrWhiteSpace(delegatedSessionId))
            throw new FactoryInboxRefusedException("delegated_without_session",
                "Delegated names the session you started to do it. Start the session first, then decide with its id.");
        if (kind != FactoryInboxWords.Decisions.Delegated && delegatedSessionId is not null)
            throw new FactoryInboxRefusedException("session_only_for_delegated", "Only a delegated decision names a session.");

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var message = ctx.FactoryInboxMessages.FirstOrDefault(m => m.Id == messageId && m.Factory == factory)
                ?? throw new FactoryInboxRefusedException("message_not_found", $"There is no message {messageId:D} in this factory's inbox.", 404);
            if (message.State == FactoryInboxWords.States.Decided)
                throw new FactoryInboxRefusedException("already_decided", $"Message {messageId:D} is already decided.", 409);
            if (message.State != FactoryInboxWords.States.WithBoss || message.BundleId is null)
                throw new FactoryInboxRefusedException("not_with_the_boss",
                    $"Message {messageId:D} is not with the Boss yet; its Secretary has not handed it over.", 409);
            if (message.MergedIntoId is { } into)
                throw new FactoryInboxRefusedException("merged_message",
                    $"Message {messageId:D} repeats {into:D}. Decide {into:D}, and this one is decided with it.", 409);

            using var tx = ctx.Database.BeginTransaction();
            FactoryTodoEntity? todo = null;
            if (kind == FactoryInboxWords.Decisions.Todo)
            {
                todo = new FactoryTodoEntity
                {
                    TenantId = tenant.Value,
                    Factory = factory,
                    Text = message.Text,
                    FromMessageId = message.Id,
                    AddedBySessionId = bossSessionId,
                    AddedAtUtc = nowUtc,
                };
                ctx.FactoryTodos.Add(todo);
            }

            var repeats = ctx.FactoryInboxMessages.Where(m => m.MergedIntoId == message.Id && m.State != FactoryInboxWords.States.Decided).ToList();
            var decisions = new List<FactoryInboxDecisionEntity>();
            foreach (var target in repeats.Prepend(message))
            {
                var decision = new FactoryInboxDecisionEntity
                {
                    TenantId = tenant.Value,
                    MessageId = target.Id,
                    Factory = factory,
                    Kind = kind,
                    Why = reason,
                    DelegatedSessionId = delegatedSessionId,
                    TodoId = todo?.Id,
                    DecidedBySessionId = bossSessionId,
                    DecidedAtUtc = nowUtc,
                };
                decisions.Add(decision);
                ctx.FactoryInboxDecisions.Add(decision);
                target.State = FactoryInboxWords.States.Decided;
            }
            try
            {
                ctx.SaveChanges();
            }
            catch (DbUpdateException ex)
            {
                tx.Rollback();
                ctx.ChangeTracker.Clear();
                if (ctx.FactoryInboxDecisions.AsNoTracking().Any(d => d.MessageId == messageId))
                    throw new FactoryInboxRefusedException("already_decided", $"Message {messageId:D} is already decided.", 409);
                FileLog.Write($"[FactoryInboxStore] Decide FAILED on {messageId} and no other writer decided it: {ex.Message}");
                throw;
            }

            // The bundle closes with its last decision.
            var bundleId = message.BundleId;
            var open = ctx.FactoryInboxMessages.AsNoTracking().Count(m => m.BundleId == bundleId && m.State != FactoryInboxWords.States.Decided);
            if (open == 0)
                ctx.FactoryInboxBundles.Where(b => b.Id == bundleId && b.ClosedAtUtc == null)
                    .ExecuteUpdate(set => set.SetProperty(b => b.ClosedAtUtc, nowUtc));
            tx.Commit();

            // YOUR INBOX (phase 3) writes here, exactly once for this decision, when
            // FactoryInboxWords.Decisions.TellsTheOwner(kind) - and never for delegated, to do or noted.
            FileLog.Write($"[FactoryInboxStore] Decide: {messageId} -> {kind} (repeats {repeats.Count}, bundle {bundleId} {(open == 0 ? "closed" : $"{open} open")})");
            return new FactoryInboxDecided(message, decisions[0], todo, repeats.Count);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The links stored on a message.</summary>
    public static IReadOnlyList<FactoryInboxLinkDto> ReadLinks(string? json)
        => string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<FactoryInboxLinkDto>>(json, Json) ?? [];

    /// <summary>The corrections stored on a spoken message.</summary>
    public static IReadOnlyList<FactoryInboxCorrection> ReadCorrections(string? json)
        => string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<FactoryInboxCorrection>>(json, Json) ?? [];

    private static IQueryable<FactoryInboxMessageEntity> Claimable(IQueryable<FactoryInboxMessageEntity> rows, string factory,
        string sessionId, IReadOnlyCollection<string> endedClaimants)
        => rows.Where(m => m.Factory == factory && m.BundleId == null
                           && (m.State == FactoryInboxWords.States.Sent
                               || (m.State == FactoryInboxWords.States.Received
                                   && (m.ClaimSessionId == null || m.ClaimSessionId == sessionId || (m.ClaimSessionId != null && endedClaimants.Contains(m.ClaimSessionId))))));

    private static void CheckLinks(IReadOnlyList<FactoryInboxLink> links)
    {
        foreach (var link in links)
        {
            if (!FactoryInboxWords.LinkKinds.All.Contains(link.Kind))
                throw new FactoryInboxRefusedException("bad_link_kind",
                    $"A link's kind must be one of {string.Join(", ", FactoryInboxWords.LinkKinds.All)}, not '{link.Kind}'. Nothing was written.");
            if (string.IsNullOrWhiteSpace(link.Ref))
                throw new FactoryInboxRefusedException("bad_link_ref", "A link names what it points at (a run, report or record id). Nothing was written.");
            if (link.Ref.Trim().Length > MaxLinkRefLength)
                throw new FactoryInboxRefusedException("bad_link_ref", $"A link's reference is at most {MaxLinkRefLength} characters. Nothing was written.");
        }
    }
}

/// <summary>What the owner's send logs. <see cref="RawText"/> and <see cref="Corrections"/> only for a spoken message.</summary>
public sealed record FactoryInboxSendInput(
    string Factory, string? Text, string Note, string Source, string Surface, string SentBy, DateTime NowUtc,
    string? RawText = null, IReadOnlyList<TranscriptEdit>? Corrections = null);

/// <summary>A Secretary's merge: <see cref="Id"/> repeats <see cref="Into"/>.</summary>
public sealed record FactoryInboxMerge(Guid Id, Guid Into);

/// <summary>A Secretary's link on message <see cref="Id"/>: what it is about.</summary>
public sealed record FactoryInboxLink(Guid Id, string Kind, string Ref);

/// <summary>A link as it is stored and shown.</summary>
public sealed record FactoryInboxLinkDto(string Kind, string Ref);

/// <summary>One hand-listed correction applied to a spoken message, as it is stored.</summary>
public sealed record FactoryInboxCorrection(string Find, string Replace);

/// <summary>One factory's count of messages not yet decided, and its newest message.</summary>
public sealed record FactoryInboxFactoryCounts(int Waiting, FactoryInboxMessageEntity Last);

/// <summary>A decision made: the message, its decision row, the to-do it added, and how many repeats went with it.</summary>
public sealed record FactoryInboxDecided(FactoryInboxMessageEntity Message, FactoryInboxDecisionEntity Decision,
    FactoryTodoEntity? Todo, int Repeats);

/// <summary>A call the factory inbox refuses, with the sentence that says why and what to do. The route answers it as
/// <see cref="Status"/> with <see cref="Code"/>.</summary>
public sealed class FactoryInboxRefusedException : Exception
{
    public FactoryInboxRefusedException(string code, string message, int status = 400) : base(message)
    {
        Code = code;
        Status = status;
    }

    /// <summary>The refusal's code.</summary>
    public string Code { get; }

    /// <summary>The HTTP status it is answered with.</summary>
    public int Status { get; }
}
