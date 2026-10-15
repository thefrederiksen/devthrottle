using System.Security.Cryptography;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Records;

/// <summary>
/// THE FILES A FACTORY KEEPS, HELD ON THE GATEWAY (Factory Definition mission, phase 2, section 5.6, decision A5): a
/// screenshot from a walk, the JSON of a walk, a drafted email. One row says what the file is; its bytes are a row of
/// their own, never inside a journal entry, so a journal or a listing is never slowed by a picture in it.
///
/// Measured on 14 October 2026 across every factory on the owner's machine: 484 files, 236 MB, the largest 2.69 MB,
/// one factory holding 216 MB. So each file is capped, and so is the factory's total - with a refusal that says how much is held and what the cap is,
/// because a factory that silently stopped keeping its screenshots would look exactly like one that took none.
///
/// WRITTEN ONCE. A file is never edited or replaced; a changed file is a new attachment with a new id. Nothing is
/// pruned in this phase.
///
/// THE FACTORY TOTAL IS CHECKED BEFORE THE INSERT, NOT BY THE DATABASE. Two writers on two replicas can both pass it
/// at the same moment, so the total can go over the cap by at most one file per writer at that moment. That is the
/// whole of the slack, and it is far smaller than any amount that matters to the cap's purpose, which is to stop a
/// runaway writer rather than to count bytes exactly.
/// </summary>
public sealed class FactoryAttachmentStore
{
    /// <summary>The most one file may hold, in bytes. Set by the phase 0 inventory (pull request 3994): of 484 real
    /// attachments the largest was 2.69 MB, and a 2 MB cap would have refused 16 of them.</summary>
    public const int MaxAttachmentBytes = 4 * 1024 * 1024;

    /// <summary>The most all of one factory's files may hold together, in bytes. Set by the phase 0 inventory: the
    /// largest factory held 216 MB of attachments on 14 October 2026.</summary>
    public const long MaxFactoryBytes = 1024L * 1024 * 1024;

    /// <summary>The longest a content type may be.</summary>
    public const int MaxContentTypeLength = 100;

    /// <summary>How many attachments one listing holds when the reader does not say.</summary>
    public const int DefaultListPage = 200;

    /// <summary>The most attachments one listing may hold.</summary>
    public const int MaxListPage = 1000;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FactoryAttachmentStore(GatewayDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    // ---------- reads ----------

    /// <summary>
    /// The factory's attachments, newest first, WITHOUT their bytes - all of them, or one seat's. Also says how many
    /// there are in all and how many bytes the factory holds, which is what its cap counts.
    /// </summary>
    public FactoryAttachmentList List(TenantId tenant, string factory, string? seat = null, int limit = DefaultListPage)
    {
        if (limit is < 1 or > MaxListPage)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"a listing holds 1 to {MaxListPage} attachments");
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return FactoryAttachmentList.Empty;
        if (!FactoryRecordNames.TryOptionalSeat(seat, out var foldedSeat, out _)) return FactoryAttachmentList.Empty;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var factoryBytes = FactoryBytes(ctx, factory);
            var rows = ctx.FactoryAttachments.AsNoTracking().Where(a => a.Factory == factory);
            if (foldedSeat is not null) rows = rows.Where(a => a.Seat == foldedSeat);
            var total = rows.Count();
            var page = rows.OrderByDescending(a => a.WrittenAtUtc).ThenBy(a => a.Name).Take(limit).ToList();
            return new FactoryAttachmentList(page, total, factoryBytes);
        }
    }

    /// <summary>
    /// ONE PAGE OF THE FACTORY'S ATTACHMENTS, without bytes, newest first (Factory Definition, phase 6; review of #4032,
    /// finding 1). <paramref name="before"/> is the id of the last attachment the page before showed. The cursor is an
    /// attachment, not a time: the one-time upload keeps many at one instant. The order is time kept, then name, then
    /// id, and the attachments that share the cursor's time are placed by name and id, so each is on exactly one page.
    /// </summary>
    /// <returns>The page, or null when <paramref name="before"/> is not an attachment of this factory.</returns>
    public FactoryAttachmentPage? Page(TenantId tenant, string factory, Guid? before, int limit)
    {
        if (limit is < 1 or > MaxListPage)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"a page holds 1 to {MaxListPage} attachments");
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return FactoryAttachmentPage.Empty;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var factoryBytes = FactoryBytes(ctx, factory);
            var rows = ctx.FactoryAttachments.AsNoTracking().Where(a => a.Factory == factory);
            var total = rows.Count();
            var ordered = rows.OrderByDescending(a => a.WrittenAtUtc).ThenBy(a => a.Name).ThenBy(a => a.Id);
            if (before is not { } cursorId)
            {
                var first = ordered.Take(limit).ToList();
                return new FactoryAttachmentPage(first, total, 0, first.Count < total ? first[^1].Id : null, factoryBytes);
            }

            if (rows.FirstOrDefault(a => a.Id == cursorId) is not { } cursor) return null;
            var at = cursor.WrittenAtUtc;
            // The attachments kept at the cursor's instant, in the page order; those after the cursor come first.
            var tied = rows.Where(a => a.WrittenAtUtc == at).OrderBy(a => a.Name).ThenBy(a => a.Id).Select(a => a.Id).ToList();
            var tiedAfter = tied.Skip(tied.IndexOf(cursorId) + 1).ToList();
            var older = ordered.Where(a => a.WrittenAtUtc < at);
            var left = tiedAfter.Count + older.Count();

            var page = new List<FactoryAttachmentEntity>();
            if (tiedAfter.Count > 0)
            {
                var take = tiedAfter.Take(limit).ToList();
                var byId = rows.Where(a => take.Contains(a.Id)).ToDictionary(a => a.Id);
                page.AddRange(take.Select(id => byId[id]));
            }
            if (page.Count < limit) page.AddRange(older.Take(limit - page.Count).ToList());
            var newer = total - left;
            return new FactoryAttachmentPage(page, total, newer, newer + page.Count < total ? page[^1].Id : null, factoryBytes);
        }
    }

    /// <summary>What one attachment is, without its bytes, or null when the factory holds no such attachment.</summary>
    public FactoryAttachmentEntity? Get(TenantId tenant, string factory, Guid id)
    {
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.FactoryAttachments.AsNoTracking().FirstOrDefault(a => a.Factory == factory && a.Id == id);
        }
    }

    /// <summary>
    /// The factory's attachments among <paramref name="ids"/>, in ONE read (a journal page names up to
    /// <c>MaxAttachmentsPerEntry</c> files per entry; one read per file would open thousands of contexts). An id the
    /// factory does not hold is absent from the answer.
    /// </summary>
    public IReadOnlyDictionary<Guid, FactoryAttachmentEntity> Find(TenantId tenant, string factory, IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0 || !FactoryNames.TryFactory(factory, out factory, out _)) return new Dictionary<Guid, FactoryAttachmentEntity>();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.FactoryAttachments.AsNoTracking()
                .Where(a => a.Factory == factory && ids.Contains(a.Id))
                .ToDictionary(a => a.Id);
        }
    }

    /// <summary>One attachment's bytes, or null when the factory holds no such attachment.</summary>
    public byte[]? Content(TenantId tenant, string factory, Guid id)
    {
        if (Get(tenant, factory, id) is null) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.FactoryAttachmentContents.AsNoTracking().FirstOrDefault(c => c.AttachmentId == id);
            // An attachment row with no bytes is a store that lost them, never an empty file: an empty file has a row
            // of zero bytes. Said out loud rather than answered as empty.
            return row?.Content ?? throw new InvalidOperationException(
                $"attachment {id} of '{factory}' is listed but its bytes are missing from factory_attachment_contents");
        }
    }

    /// <summary>
    /// Which of these ids are NOT attachments of the factory. A journal entry may name only files its factory holds,
    /// and the journal store asks this before it writes.
    /// </summary>
    public IReadOnlyList<Guid> Missing(TenantId tenant, string factory, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return Array.Empty<Guid>();
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return ids.ToList();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var held = ctx.FactoryAttachments.AsNoTracking()
                .Where(a => a.Factory == factory && ids.Contains(a.Id))
                .Select(a => a.Id)
                .ToHashSet();
            return ids.Where(id => !held.Contains(id)).ToList();
        }
    }

    // ---------- writes ----------

    /// <summary>
    /// Keep one file. The bytes and the row describing them are written in one transaction, so there is never a row
    /// without its bytes or bytes without their row. The one-time upload names the file with
    /// <paramref name="importKey"/>; a file it stored before is answered <see cref="FactoryRecordOutcome.AlreadyStored"/>
    /// when the bytes are the same and <see cref="FactoryRecordOutcome.ImportConflict"/> when they are not.
    /// </summary>
    public FactoryAttachmentWrite Add(TenantId tenant, string factory, string? seat, string name, string? contentType,
        byte[] content, string authorKind, string? authorId, DateTime nowUtc, string? importKey = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        FactoryRecordNames.RequireAWriter(authorKind, importKey);
        if (!FactoryNames.TryFactory(factory, out var foldedFactory, out var factoryRefusal))
            return FactoryAttachmentWrite.Refused(FactoryRecordOutcome.BadFactory, factoryRefusal!);
        if (!FactoryRecordNames.TryOptionalSeat(seat, out var foldedSeat, out var seatRefusal))
            return FactoryAttachmentWrite.Refused(FactoryRecordOutcome.BadSeat, seatRefusal!);
        if (!FactoryRecordNames.TryName(name, "attachment name", out var foldedName, out var nameRefusal))
            return FactoryAttachmentWrite.Refused(FactoryRecordOutcome.BadName, nameRefusal!);
        if (!FactoryRecordNames.TryImportKey(importKey, out importKey, out var keyRefusal))
            return FactoryAttachmentWrite.Refused(FactoryRecordOutcome.BadName, keyRefusal!);
        var type = (contentType ?? "").Trim();
        if (type.Length == 0 || type.Length > MaxContentTypeLength || type.Any(c => c < 0x21 || c > 0x7e))
            return FactoryAttachmentWrite.Refused(FactoryRecordOutcome.BadName,
                $"an attachment's content type is one word such as image/png, at most {MaxContentTypeLength} characters, " +
                $"so '{contentType}' cannot be one");
        factory = foldedFactory;

        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);

            if (importKey is not null)
            {
                var before = ctx.FactoryAttachments.AsNoTracking()
                    .FirstOrDefault(a => a.Factory == factory && a.ImportKey == importKey);
                if (before is not null)
                {
                    if (before.Sha256 == sha && before.Bytes == content.LongLength)
                        return new FactoryAttachmentWrite(FactoryRecordOutcome.AlreadyStored, null, before);
                    FileLog.Write($"[FactoryAttachmentStore] {factory}: REFUSED an upload of '{importKey}' - stored before as {before.Id} with other bytes");
                    return new FactoryAttachmentWrite(FactoryRecordOutcome.ImportConflict,
                        $"the upload stored '{importKey}' before, as attachment {before.Id}, and the file now holds other " +
                        "bytes. Nothing was changed: the Gateway's copy and the machine's file disagree, and a person " +
                        "decides which one stands", before);
                }
            }

            if (content.Length > MaxAttachmentBytes)
            {
                FileLog.Write($"[FactoryAttachmentStore] {factory}/{foldedName}: REFUSED - {content.Length} bytes is over the cap of {MaxAttachmentBytes}");
                return FactoryAttachmentWrite.Refused(FactoryRecordOutcome.TooLarge,
                    $"'{foldedName}' holds {content.Length} bytes; one attachment holds at most {MaxAttachmentBytes} " +
                    $"({MaxAttachmentBytes / (1024 * 1024)} MB). Nothing was stored. Make the file smaller - a screenshot " +
                    "at a lower resolution, or as a JPEG - and add it again");
            }

            var held = FactoryBytes(ctx, factory);
            if (held + content.Length > MaxFactoryBytes)
            {
                FileLog.Write($"[FactoryAttachmentStore] {factory}/{foldedName}: REFUSED - the factory holds {held} bytes, cap {MaxFactoryBytes}");
                return FactoryAttachmentWrite.Refused(FactoryRecordOutcome.FactoryFull,
                    $"'{factory}' already holds {held} bytes of attachments, and with '{foldedName}' ({content.Length} " +
                    $"bytes) it would hold {held + content.Length}; a factory holds at most {MaxFactoryBytes} bytes " +
                    $"({MaxFactoryBytes / (1024 * 1024)} MB) of attachments together. Nothing was stored. Ask the owner: " +
                    "the factory has reached the most it may keep");
            }

            var row = new FactoryAttachmentEntity
            {
                TenantId = tenant.Value,
                Factory = factory,
                Seat = foldedSeat,
                Name = foldedName,
                ContentType = type,
                Bytes = content.LongLength,
                Sha256 = sha,
                AuthorKind = authorKind,
                AuthorId = authorId,
                WrittenAtUtc = nowUtc,
                ImportKey = importKey,
            };
            using var tx = ctx.Database.BeginTransaction();
            ctx.FactoryAttachments.Add(row);
            ctx.FactoryAttachmentContents.Add(new FactoryAttachmentContentEntity
            {
                TenantId = tenant.Value,
                AttachmentId = row.Id,
                Content = content,
            });
            try
            {
                ctx.SaveChanges();
                tx.Commit();
            }
            catch (DbUpdateException ex)
            {
                tx.Rollback();
                ctx.ChangeTracker.Clear();
                // The one key a second writer can take is the upload's name for the file: two uploads of one machine at
                // once. That is reported as what it is; anything else failed for its own reason.
                if (importKey is not null
                    && ctx.FactoryAttachments.AsNoTracking().FirstOrDefault(a => a.Factory == factory && a.ImportKey == importKey) is { } other)
                    return other.Sha256 == sha
                        ? new FactoryAttachmentWrite(FactoryRecordOutcome.AlreadyStored, null, other)
                        : new FactoryAttachmentWrite(FactoryRecordOutcome.ImportConflict,
                            $"another upload stored '{importKey}' a moment ago, with other bytes. Nothing was changed", other);
                FileLog.Write($"[FactoryAttachmentStore] {factory}/{foldedName}: the write FAILED: {ex.Message}");
                return FactoryAttachmentWrite.Refused(FactoryRecordOutcome.WriteFailed,
                    $"'{foldedName}' could not be stored: {FactoryRecordNames.Innermost(ex).Message}");
            }

            FileLog.Write($"[FactoryAttachmentStore] {factory}/{foldedName}: kept attachment {row.Id} ({content.Length} bytes, {type}) by {authorKind} {authorId ?? "(unknown)"}");
            return new FactoryAttachmentWrite(FactoryRecordOutcome.Written, null, row);
        }
    }

    /// <summary>How many bytes of attachments the factory holds together - what its cap counts.</summary>
    private static long FactoryBytes(GatewayDbContext ctx, string factory)
        => ctx.FactoryAttachments.AsNoTracking().Where(a => a.Factory == factory).Sum(a => (long?)a.Bytes) ?? 0;
}

/// <summary>A page of a factory's attachments, newest first, with how many match in all and how many bytes the factory
/// holds together.</summary>
public sealed record FactoryAttachmentList(IReadOnlyList<FactoryAttachmentEntity> Attachments, int Total, long FactoryBytes)
{
    public static readonly FactoryAttachmentList Empty = new(Array.Empty<FactoryAttachmentEntity>(), 0, 0);
}

/// <summary>One page of a factory's attachments: the page, how many the factory holds, how many newer ones the pages
/// before held, the cursor of the next page (null when this is the last), and the bytes the factory holds in all.</summary>
public sealed record FactoryAttachmentPage(IReadOnlyList<FactoryAttachmentEntity> Attachments, int Total, int Newer,
    Guid? NextBefore, long FactoryBytes)
{
    public static readonly FactoryAttachmentPage Empty = new(Array.Empty<FactoryAttachmentEntity>(), 0, 0, null, 0);
}

/// <summary>What keeping a file did. A refusal carries no attachment; an upload's answer about a file it stored before
/// carries that attachment.</summary>
public sealed record FactoryAttachmentWrite(FactoryRecordOutcome Outcome, string? Refusal, FactoryAttachmentEntity? Attachment)
{
    public bool Ok => Outcome is FactoryRecordOutcome.Written or FactoryRecordOutcome.AlreadyStored;

    public static FactoryAttachmentWrite Refused(FactoryRecordOutcome outcome, string refusal) => new(outcome, refusal, null);
}
