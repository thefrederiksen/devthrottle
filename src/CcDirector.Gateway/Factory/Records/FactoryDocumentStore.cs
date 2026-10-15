using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Records;

/// <summary>
/// A FACTORY'S WORKING DOCUMENTS, HELD ON THE GATEWAY (Factory Definition mission, phase 2, section 5.6; the owner's
/// decision F2). A working document is a named file a seat reads and rewrites run after run - the outreach ledger,
/// the playbook, <c>focus.md</c>, the health log.
///
/// WHAT THIS STORE IS FOR. On 14 October 2026 every such file existed only under <c>%LOCALAPPDATA%\cc-factory\</c> on
/// one machine: 236 files, 23 MB, no backup, not in git - including the outreach ledger, whose rule is "never a row
/// deleted". A seat could run nowhere else, and one disk was the only copy.
///
/// SHAPED LIKE FACTORY MEMORY, FOR THE SAME REASONS (<see cref="Memory.FactoryMemoryStore"/>):
///
/// APPEND-ONLY, AND THE KEY IS THE CONCURRENCY CONTROL. A write never edits a row; it inserts the next version, and
/// the primary key (tenant, factory, name, version) makes a second writer's insert impossible rather than merely
/// unlikely - the hosted Gateway runs several replicas over one database.
///
/// A WRITER SAYS WHICH VERSION IT READ, AND A STALE WRITE IS REFUSED WITH THE CURRENT TEXT. Two runs writing the
/// ledger at once: the first is stored, the second is told the ledger moved and is handed what it now says, so it
/// can merge its rows in and write again. Silently winning would delete the first run's rows - exactly what the
/// ledger's rule forbids.
///
/// EVERY VERSION IS KEPT. The cap is on one version's text; nothing is pruned, because "what did the ledger say on
/// Tuesday" must stay answerable. History is read a page at a time and WITHOUT text, as a brief's history is.
///
/// NO REGISTRATION IS REQUIRED, as for memory: the one-time upload stores the records of every factory folder on a
/// machine, including a factory that was never registered, and refusing those would leave them on the one disk.
/// </summary>
public sealed class FactoryDocumentStore
{
    /// <summary>
    /// The most one version of a document may take, in UTF-8 bytes. Set by the phase 0 inventory (pull request 3994):
    /// the largest working document on any factory's disk on 14 October 2026 was 1.66 MB, and none was over 2 MB.
    /// </summary>
    public const int MaxDocumentBytes = 2 * 1024 * 1024;

    /// <summary>How many versions one page of a history holds when the reader does not say.</summary>
    public const int DefaultHistoryPage = 50;

    /// <summary>The most versions one page of a history may hold.</summary>
    public const int MaxHistoryPage = 100;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FactoryDocumentStore(GatewayDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    // ---------- reads ----------

    /// <summary>The factory's documents as they stand: the newest version of each, WITHOUT its text.</summary>
    public IReadOnlyList<FactoryDocumentSummary> List(TenantId tenant, string factory)
    {
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return Array.Empty<FactoryDocumentSummary>();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            // The newest version of each name, found without reading any text: the key holds (name, version), so this
            // is answered from the key alone.
            var newest = ctx.FactoryDocumentVersions.AsNoTracking()
                .Where(d => d.Factory == factory)
                .GroupBy(d => d.Name)
                .Select(g => new { Name = g.Key, Version = g.Max(d => d.Version) })
                .ToList();
            var summaries = new List<FactoryDocumentSummary>(newest.Count);
            foreach (var n in newest)
            {
                var head = Versions(ctx, factory, n.Name).Where(d => d.Version == n.Version)
                    .Select(Summary).First();
                summaries.Add(head);
            }
            return summaries.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>One document's newest version, with its text, or null when the name has never been written.</summary>
    public FactoryDocumentVersionEntity? Get(TenantId tenant, string factory, string name)
    {
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return null;
        if (!FactoryRecordNames.TryName(name, "document name", out name, out _)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return Newest(ctx, factory, name);
        }
    }

    /// <summary>One version of one document, with its text, or null when there is no such version.</summary>
    public FactoryDocumentVersionEntity? Find(TenantId tenant, string factory, string name, int version)
    {
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return null;
        if (!FactoryRecordNames.TryName(name, "document name", out name, out _)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return Versions(ctx, factory, name).FirstOrDefault(d => d.Version == version);
        }
    }

    /// <summary>
    /// ONE PAGE of one document's history, newest first, WITHOUT ANY VERSION'S TEXT. <paramref name="before"/> is the
    /// version the page starts below - null for the newest page - and the page says where the next one starts.
    /// </summary>
    public FactoryDocumentHistoryPage History(TenantId tenant, string factory, string name, int? before = null,
        int limit = DefaultHistoryPage)
    {
        if (limit is < 1 or > MaxHistoryPage)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"a page of the history holds 1 to {MaxHistoryPage} versions");
        if (before is < 1)
            throw new ArgumentOutOfRangeException(nameof(before), before, "versions count from 1");
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return FactoryDocumentHistoryPage.Empty;
        if (!FactoryRecordNames.TryName(name, "document name", out name, out _)) return FactoryDocumentHistoryPage.Empty;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var all = Versions(ctx, factory, name);
            var total = all.Count();
            var below = before is { } b ? all.Where(d => d.Version < b) : all;
            var rows = below.OrderByDescending(d => d.Version).Select(Summary).Take(limit + 1).ToList();
            var more = rows.Count > limit;
            var page = more ? rows.GetRange(0, limit) : rows;
            return new FactoryDocumentHistoryPage(page, total, more ? page[^1].Version : null);
        }
    }

    // ---------- writes ----------

    /// <summary>
    /// Write a document: add the next version with this text. <paramref name="expectedVersion"/> is the version the
    /// writer last read - 0 (or null) meaning "I believe this document does not exist yet". A mismatch is refused
    /// with the current version, text and all, attached.
    ///
    /// The one-time upload writes through here too, naming the file with <paramref name="importKey"/>. A file it
    /// stored before is answered <see cref="FactoryRecordOutcome.AlreadyStored"/> when the text is the same and
    /// <see cref="FactoryRecordOutcome.ImportConflict"/> when it is not - never written a second time.
    /// </summary>
    public FactoryDocumentWrite Write(TenantId tenant, string factory, string name, string? text,
        int? expectedVersion, string authorKind, string? authorId, DateTime nowUtc, string? importKey = null)
    {
        FactoryRecordNames.RequireAWriter(authorKind, importKey);
        if (!FactoryNames.TryFactory(factory, out var foldedFactory, out var factoryRefusal))
            return FactoryDocumentWrite.Refused(FactoryRecordOutcome.BadFactory, factoryRefusal!, null);
        if (!FactoryRecordNames.TryName(name, "document name", out var foldedName, out var nameRefusal))
            return FactoryDocumentWrite.Refused(FactoryRecordOutcome.BadName, nameRefusal!, null);
        if (!FactoryRecordNames.TryImportKey(importKey, out importKey, out var keyRefusal))
            return FactoryDocumentWrite.Refused(FactoryRecordOutcome.BadName, keyRefusal!, null);
        factory = foldedFactory;
        name = foldedName;
        text ??= "";

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var head = Newest(ctx, factory, name);

            if (importKey is not null && Imported(ctx, factory, name, importKey, text) is { } known)
                return known;

            var current = head?.Version ?? 0;
            var expected = expectedVersion ?? 0;
            if (expected != current)
            {
                // THE TWO-WRITER REFUSAL. The current version, text and all, rides the refusal so the caller can merge
                // rather than read again and race again.
                FileLog.Write($"[FactoryDocumentStore] {factory}/{name}: REFUSED a stale write (expected {expected}, current {current})");
                return FactoryDocumentWrite.Refused(FactoryRecordOutcome.Stale,
                    current == 0
                        ? $"'{name}' does not exist yet, so write it expecting version 0"
                        : $"'{name}' is at version {current}, not {expected}; read it again, merge, and write once more",
                    head);
            }

            var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
            if (bytes > MaxDocumentBytes)
                return FactoryDocumentWrite.Refused(FactoryRecordOutcome.TooLarge,
                    $"'{name}' would take {bytes} bytes; one version of a working document takes at most {MaxDocumentBytes}",
                    head);

            var written = new FactoryDocumentVersionEntity
            {
                TenantId = tenant.Value,
                Factory = factory,
                Name = name,
                Version = current + 1,
                Text = text,
                TextBytes = bytes,
                AuthorKind = authorKind,
                AuthorId = authorId,
                WrittenAtUtc = nowUtc,
                ImportKey = importKey,
            };

            // THE KEY DOES THE ARBITRATION. A concurrent writer that got here first owns version current+1, and this
            // insert then violates the primary key - reported as the same stale refusal a slow reader gets, because
            // that is what happened.
            ctx.FactoryDocumentVersions.Add(written);
            try
            {
                ctx.SaveChanges();
            }
            catch (DbUpdateException ex)
            {
                ctx.ChangeTracker.Clear();
                var headNow = Newest(ctx, factory, name);

                // ONLY A SECOND WRITER IS REPORTED AS ONE, as in factory memory: a key violation is one cause of this
                // exception and not the only one. The head decides.
                if (headNow is not null && headNow.Version >= written.Version)
                {
                    FileLog.Write($"[FactoryDocumentStore] {factory}/{name}: version {written.Version} was taken by another writer");
                    return FactoryDocumentWrite.Refused(FactoryRecordOutcome.Stale,
                        $"another writer took version {written.Version} of '{name}' first; read it again, merge, and write once more",
                        headNow);
                }

                FileLog.Write($"[FactoryDocumentStore] {factory}/{name}: the write FAILED and no other writer holds version {written.Version}: {ex.Message}");
                return FactoryDocumentWrite.Refused(FactoryRecordOutcome.WriteFailed,
                    $"'{name}' could not be stored: {FactoryRecordNames.Innermost(ex).Message}", headNow);
            }

            FileLog.Write($"[FactoryDocumentStore] {factory}/{name}: wrote version {written.Version} ({bytes} bytes) by {authorKind} {authorId ?? "(unknown)"}");
            return FactoryDocumentWrite.Written(written);
        }
    }

    /// <summary>
    /// What the upload already did with this file, or null when it has not stored it. Same text: nothing to do. Other
    /// text: refused and nothing changed, because the file on the machine and the document on the Gateway now
    /// disagree and only a person can say which is right.
    /// </summary>
    private static FactoryDocumentWrite? Imported(GatewayDbContext ctx, string factory, string name, string importKey, string text)
    {
        var before = ctx.FactoryDocumentVersions.AsNoTracking()
            .FirstOrDefault(d => d.Factory == factory && d.ImportKey == importKey);
        if (before is null) return null;
        if (before.Name == name && string.Equals(before.Text, text, StringComparison.Ordinal))
            return new FactoryDocumentWrite(FactoryRecordOutcome.AlreadyStored, null, before);
        FileLog.Write($"[FactoryDocumentStore] {factory}/{name}: REFUSED an upload of '{importKey}' - stored before as '{before.Name}' version {before.Version} with other text");
        return FactoryDocumentWrite.Refused(FactoryRecordOutcome.ImportConflict,
            $"the upload stored '{importKey}' before, as version {before.Version} of '{before.Name}', and the file now " +
            "says something else. Nothing was changed: the Gateway's copy and the machine's file disagree, and a " +
            "person decides which one stands", before);
    }

    private static FactoryDocumentSummary Summary(FactoryDocumentVersionEntity d)
        => new(d.Name, d.Version, d.TextBytes, d.AuthorKind, d.AuthorId, d.WrittenAtUtc);

    private static IQueryable<FactoryDocumentVersionEntity> Versions(GatewayDbContext ctx, string factory, string name)
        => ctx.FactoryDocumentVersions.AsNoTracking().Where(d => d.Factory == factory && d.Name == name);

    private static FactoryDocumentVersionEntity? Newest(GatewayDbContext ctx, string factory, string name)
        => Versions(ctx, factory, name).OrderByDescending(d => d.Version).FirstOrDefault();
}

/// <summary>One version of a working document, everything about it EXCEPT its text.</summary>
public sealed record FactoryDocumentSummary(string Name, int Version, int TextBytes, string AuthorKind, string? AuthorId,
    DateTime WrittenAtUtc);

/// <summary>One page of a document's history, newest first. <see cref="NextBefore"/> is where the next page starts, or
/// null when this page reaches the first version.</summary>
public sealed record FactoryDocumentHistoryPage(IReadOnlyList<FactoryDocumentSummary> Versions, int Total, int? NextBefore)
{
    public static readonly FactoryDocumentHistoryPage Empty = new(Array.Empty<FactoryDocumentSummary>(), 0, null);
}

/// <summary>
/// The result of a write. A refusal carries the CURRENT version where there is one, text and all, because a stale
/// writer needs exactly that to merge.
/// </summary>
public sealed record FactoryDocumentWrite(FactoryRecordOutcome Outcome, string? Refusal, FactoryDocumentVersionEntity? Document)
{
    public bool Ok => Outcome is FactoryRecordOutcome.Written or FactoryRecordOutcome.AlreadyStored;

    public static FactoryDocumentWrite Written(FactoryDocumentVersionEntity document) => new(FactoryRecordOutcome.Written, null, document);

    public static FactoryDocumentWrite Refused(FactoryRecordOutcome outcome, string refusal, FactoryDocumentVersionEntity? current)
        => new(outcome, refusal, current);
}
