using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Records;

/// <summary>
/// EVERY SEAT'S JOURNAL, HELD ON THE GATEWAY (Factory Definition mission, phase 2, section 5.6; the owner's decision
/// F2). A journal entry is what a seat wrote down about a day's work: what it did, what it found, what is left.
///
/// APPEND-ONLY, LIKE THE ACTIVITY RECORD (<see cref="FactoryActivityRecord"/>). There is no edit and no delete here,
/// on purpose: an entry is the seat's account of a day, and the owner reading it later must be reading what was
/// written then. A wrong entry is corrected by a new one. Because nothing is ever replaced, two writers cannot lose
/// each other's work, so unlike a working document a journal needs no version to send.
///
/// AN ENTRY NAMES ITS ATTACHMENTS AND NEVER HOLDS THEM (decision A5). The ids must be files the same factory already
/// holds, checked before the entry is written, so a journal can never point at a picture nobody can open.
/// </summary>
public sealed class FactoryJournalStore
{
    /// <summary>The most one entry's text may take, in UTF-8 bytes. The largest journal file on any factory's disk on
    /// 14 October 2026 was 33.2 KB (phase 0 inventory), so this leaves a long day plenty of room and still stops a
    /// runaway writer.</summary>
    public const int MaxEntryBytes = 256 * 1024;

    /// <summary>The most attachments one entry may name. The busiest day measured on 14 October 2026 kept 44 files
    /// (42 screenshots and two walk records, onboarding, 2026-10-09).</summary>
    public const int MaxAttachmentsPerEntry = 100;

    /// <summary>How many entries one listing holds when the reader does not say.</summary>
    public const int DefaultListPage = 50;

    /// <summary>The most entries one listing may hold.</summary>
    public const int MaxListPage = 200;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly FactoryAttachmentStore _attachments;

    public FactoryJournalStore(GatewayDatabase db, FactoryAttachmentStore attachments)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _attachments = attachments ?? throw new ArgumentNullException(nameof(attachments));
    }

    // ---------- reads ----------

    /// <summary>
    /// The factory's journal entries, newest day first, WITHOUT their text - all of them, one seat's
    /// (<paramref name="seat"/>), or the ones kept by the factory as a whole (<paramref name="factoryOnly"/>), between
    /// two days when they are given. Says how many match in all, so a reader that got a full page knows to narrow.
    /// </summary>
    public FactoryJournalList List(TenantId tenant, string factory, string? seat = null, bool factoryOnly = false,
        string? since = null, string? until = null, int limit = DefaultListPage)
    {
        if (limit is < 1 or > MaxListPage)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"a listing holds 1 to {MaxListPage} entries");
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return FactoryJournalList.Empty;
        if (!FactoryRecordNames.TryOptionalSeat(seat, out var foldedSeat, out _)) return FactoryJournalList.Empty;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.FactoryJournalEntries.AsNoTracking().Where(e => e.Factory == factory);
            if (foldedSeat is not null) rows = rows.Where(e => e.Seat == foldedSeat);
            else if (factoryOnly) rows = rows.Where(e => e.Seat == null);
            // A day is written yyyy-MM-dd, so comparing the text compares the days.
            if (!string.IsNullOrEmpty(since)) rows = rows.Where(e => string.Compare(e.EntryDate, since) >= 0);
            if (!string.IsNullOrEmpty(until)) rows = rows.Where(e => string.Compare(e.EntryDate, until) <= 0);
            var total = rows.Count();
            var page = rows.OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.WrittenAtUtc)
                .Select(e => new FactoryJournalSummary(e.Id, e.Seat, e.EntryDate, e.TextBytes, e.AttachmentIds,
                    e.AuthorKind, e.AuthorId, e.WrittenAtUtc, e.ImportKey))
                .Take(limit)
                .ToList();
            return new FactoryJournalList(page, total);
        }
    }

    /// <summary>One entry, with its text, or null when the factory has no such entry.</summary>
    public FactoryJournalEntryEntity? Get(TenantId tenant, string factory, Guid id)
    {
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.FactoryJournalEntries.AsNoTracking().FirstOrDefault(e => e.Factory == factory && e.Id == id);
        }
    }

    // ---------- the one write ----------

    /// <summary>
    /// Add an entry to a seat's journal - or to the factory's own, when <paramref name="seat"/> is empty. The one-time
    /// upload names the file it came from with <paramref name="importKey"/>, and a file it stored before is answered
    /// <see cref="FactoryRecordOutcome.AlreadyStored"/> (same text) or <see cref="FactoryRecordOutcome.ImportConflict"/>
    /// (other text) rather than added twice.
    /// </summary>
    public FactoryJournalWrite Append(TenantId tenant, string factory, string? seat, string? date, string? text,
        IReadOnlyList<Guid>? attachmentIds, string authorKind, string? authorId, DateTime nowUtc, string? importKey = null)
    {
        FactoryRecordNames.RequireAWriter(authorKind, importKey);
        if (!FactoryNames.TryFactory(factory, out var foldedFactory, out var factoryRefusal))
            return FactoryJournalWrite.Refused(FactoryRecordOutcome.BadFactory, factoryRefusal!);
        if (!FactoryRecordNames.TryOptionalSeat(seat, out var foldedSeat, out var seatRefusal))
            return FactoryJournalWrite.Refused(FactoryRecordOutcome.BadSeat, seatRefusal!);
        // A day left out is today's, by the Gateway's clock. A day given must be one.
        var day = string.IsNullOrWhiteSpace(date) ? nowUtc.ToString(FactoryRecordNames.DateFormat) : date;
        if (!FactoryRecordNames.TryDate(day, out day, out var dateRefusal))
            return FactoryJournalWrite.Refused(FactoryRecordOutcome.BadName, dateRefusal!);
        if (!FactoryRecordNames.TryImportKey(importKey, out importKey, out var keyRefusal))
            return FactoryJournalWrite.Refused(FactoryRecordOutcome.BadName, keyRefusal!);
        factory = foldedFactory;
        text ??= "";
        var ids = (attachmentIds ?? Array.Empty<Guid>()).Distinct().ToList();

        if (importKey is not null && Imported(tenant, factory, importKey, foldedSeat, day, text, ids) is { } known)
            return known;

        if (string.IsNullOrWhiteSpace(text))
            return FactoryJournalWrite.Refused(FactoryRecordOutcome.Empty,
                "a journal entry needs text: an entry that says nothing tells the owner nothing");
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxEntryBytes)
            return FactoryJournalWrite.Refused(FactoryRecordOutcome.TooLarge,
                $"the entry takes {bytes} bytes; one journal entry takes at most {MaxEntryBytes}. Write the day as two " +
                "entries, or keep the long part as a working document and name it");
        if (ids.Count > MaxAttachmentsPerEntry)
            return FactoryJournalWrite.Refused(FactoryRecordOutcome.TooManyAttachments,
                $"the entry names {ids.Count} attachments; one entry names at most {MaxAttachmentsPerEntry}");
        var missing = _attachments.Missing(tenant, factory, ids);
        if (missing.Count > 0)
        {
            FileLog.Write($"[FactoryJournalStore] {factory}/{foldedSeat ?? "(factory)"}: REFUSED an entry naming {missing.Count} attachment(s) the factory does not hold");
            return FactoryJournalWrite.Refused(FactoryRecordOutcome.NoSuchAttachment,
                $"'{factory}' holds no attachment {string.Join(", ", missing)}. Add the file first with " +
                "'cc-devthrottle factory attach add', then name the id it is given");
        }

        var entry = new FactoryJournalEntryEntity
        {
            TenantId = tenant.Value,
            Factory = factory,
            Seat = foldedSeat,
            EntryDate = day,
            Text = text,
            TextBytes = bytes,
            AttachmentIds = string.Join(",", ids),
            AuthorKind = authorKind,
            AuthorId = authorId,
            WrittenAtUtc = nowUtc,
            ImportKey = importKey,
        };
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            ctx.FactoryJournalEntries.Add(entry);
            try
            {
                ctx.SaveChanges();
            }
            catch (DbUpdateException ex)
            {
                ctx.ChangeTracker.Clear();
                // The one key a second writer can take is the upload's name for the file: two uploads at once.
                if (importKey is not null && Imported(tenant, factory, importKey, foldedSeat, day, text, ids) is { } raced)
                    return raced;
                FileLog.Write($"[FactoryJournalStore] {factory}/{foldedSeat ?? "(factory)"}: the write FAILED: {ex.Message}");
                return FactoryJournalWrite.Refused(FactoryRecordOutcome.WriteFailed,
                    $"the entry could not be stored: {FactoryRecordNames.Innermost(ex).Message}");
            }
        }
        FileLog.Write($"[FactoryJournalStore] {factory}/{foldedSeat ?? "(factory)"}: added entry {entry.Id} for {day} ({bytes} bytes, {ids.Count} attachment(s)) by {authorKind} {authorId ?? "(unknown)"}");
        return new FactoryJournalWrite(FactoryRecordOutcome.Written, null, entry);
    }

    /// <summary>What the upload already did with this file, or null when it has not stored it.</summary>
    private FactoryJournalWrite? Imported(TenantId tenant, string factory, string importKey, string? seat, string day,
        string text, IReadOnlyList<Guid> ids)
    {
        FactoryJournalEntryEntity? before;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            before = ctx.FactoryJournalEntries.AsNoTracking()
                .FirstOrDefault(e => e.Factory == factory && e.ImportKey == importKey);
        }
        if (before is null) return null;
        if (before.Seat == seat && before.EntryDate == day && string.Equals(before.Text, text, StringComparison.Ordinal)
            && before.AttachmentIds == string.Join(",", ids))
            return new FactoryJournalWrite(FactoryRecordOutcome.AlreadyStored, null, before);
        FileLog.Write($"[FactoryJournalStore] {factory}: REFUSED an upload of '{importKey}' - stored before as entry {before.Id} with other content");
        return new FactoryJournalWrite(FactoryRecordOutcome.ImportConflict,
            $"the upload stored '{importKey}' before, as entry {before.Id} of {before.EntryDate}, and the file now says " +
            "something else. Nothing was changed: an entry is never edited, and a person decides whether the new text " +
            "is a new entry", before);
    }
}

/// <summary>One entry, everything about it EXCEPT its text. <see cref="AttachmentIds"/> is comma separated.</summary>
public sealed record FactoryJournalSummary(Guid Id, string? Seat, string EntryDate, int TextBytes, string AttachmentIds,
    string AuthorKind, string? AuthorId, DateTime WrittenAtUtc, string? ImportKey);

/// <summary>A page of entries, newest day first, and how many match in all.</summary>
public sealed record FactoryJournalList(IReadOnlyList<FactoryJournalSummary> Entries, int Total)
{
    public static readonly FactoryJournalList Empty = new(Array.Empty<FactoryJournalSummary>(), 0);
}

/// <summary>What adding an entry did, or why it was refused.</summary>
public sealed record FactoryJournalWrite(FactoryRecordOutcome Outcome, string? Refusal, FactoryJournalEntryEntity? Entry)
{
    public bool Ok => Outcome is FactoryRecordOutcome.Written or FactoryRecordOutcome.AlreadyStored;

    public static FactoryJournalWrite Refused(FactoryRecordOutcome outcome, string refusal) => new(outcome, refusal, null);
}
