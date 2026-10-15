using System.Globalization;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Talk;

namespace CcDirector.Gateway.Factory.Records;

/// <summary>
/// THE RECORDS TAB OF A FACTORY'S PAGE, FOLDED ONCE (Factory Definition mission, phase 6, sections 5.6 and 5.7; critical
/// rule 7). Beside Memory: what the factory produced, held on the Gateway - its journals by seat, its working documents
/// with every version, and its attachments. Every sentence and label is composed here, and the Cockpit draws it.
///
/// The tab only READS. A record is written by a session of the factory (<c>cc-devthrottle factory journal|doc|attach</c>)
/// or by the one-time upload, and a journal entry is never edited, so the owner's screen offers nothing to write.
///
/// Pure: it reads what it is handed and returns finished display objects, so every sentence is proven by a unit test.
/// </summary>
public static class FactoryRecordsTabFold
{
    /// <summary>How many entries one page of a journal shows, newest day first.</summary>
    public const int JournalPage = 20;

    /// <summary>How many attachments one page of the tab lists, newest first.</summary>
    public const int AttachmentPage = 100;

    /// <summary>How many versions one page of a document's history holds.</summary>
    public const int HistoryPage = 20;

    public static string AttachmentHref(string factory, Guid id) =>
        $"/gateway/factories/{Uri.EscapeDataString(factory)}/records/attachments/{id}";

    // ---------- the tab ----------

    /// <summary>The Records tab.</summary>
    public static FactoryRecordsTabViewDto Tab(RegisteredFactoryDto factory, IReadOnlyList<FactoryJournalGroup> journals,
        IReadOnlyList<FactoryDocumentSummary> documents, FactoryAttachmentPage attachments, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(journals);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(zone);

        var groups = journals
            // The factory's own journal first, then the seats in the order the registry lists them, then seats the
            // registry no longer holds, by id.
            .OrderBy(g => g.Seat is null ? 0 : SeatIndex(factory, g.Seat) is { } i ? 1 + i : 1000)
            .ThenBy(g => g.Seat, StringComparer.Ordinal)
            .Select(g => new FactoryRecordsJournalGroupDto
            {
                Seat = g.Seat,
                Title = SeatTitle(factory, g.Seat),
                SummaryText = $"{Entries(g.Entries)}, the newest of {g.NewestDate}.",
                OpenLabel = "Read",
            }).ToList();
        var entries = journals.Sum(g => g.Entries);

        return new FactoryRecordsTabViewDto
        {
            Factory = factory.Factory,
            Heading = $"The records of {factory.Title}",
            IntroText = $"What {factory.Title} has produced, kept on the Gateway so it does not depend on one machine's disk: " +
                        "a journal per seat, working documents with every version, and attachments. The factory's sessions write " +
                        "them with cc-devthrottle factory journal, doc and attach. A journal entry is never edited, and every " +
                        "version of a document is kept.",
            Journals = new FactoryRecordsSectionDto
            {
                Heading = "Journals",
                SummaryText = groups.Count == 0
                    ? ""
                    : $"{FactoriesScreenFold.Count(groups.Count, "journal")}, {Entries(entries)} in all.",
                EmptyText = groups.Count == 0 ? $"No journal entry yet. A seat of {factory.Title} adds one with cc-devthrottle factory journal add." : null,
            },
            JournalGroups = groups,
            Documents = new FactoryRecordsSectionDto
            {
                Heading = "Working documents",
                SummaryText = documents.Count == 0 ? "" : $"{FactoriesScreenFold.Count(documents.Count, "document")}, each as it stands now.",
                EmptyText = documents.Count == 0 ? $"No working document yet. A seat of {factory.Title} writes one with cc-devthrottle factory doc write." : null,
            },
            DocumentColumns = new List<string> { "Document", "Version", "Written by", "When", "Size" },
            DocumentRows = documents.OrderBy(d => d.Name, StringComparer.Ordinal).Select(d => new FactoryRecordsDocumentRowDto
            {
                Name = d.Name,
                VersionText = $"Version {d.Version}",
                WhoText = $"Written by {Who(d.AuthorKind, d.AuthorId)}",
                WhenText = When(d.WrittenAtUtc, zone, nowUtc),
                SizeText = Size(d.TextBytes),
                OpenLabel = "Open",
                HistoryLabel = d.Version > 1 ? "History" : null,
            }).ToList(),
            Attachments = new FactoryRecordsSectionDto
            {
                Heading = "Attachments",
                SummaryText = AttachmentsSummary(attachments),
                EmptyText = attachments.Total == 0 ? $"No attachment yet. A seat of {factory.Title} keeps one with cc-devthrottle factory attach add." : null,
            },
            AttachmentColumns = new List<string> { "File", "Seat", "Type", "Size", "Kept by", "When" },
            AttachmentRows = attachments.Attachments.Select(a => AttachmentRow(factory, a, zone, nowUtc)).ToList(),
            AttachmentsNextBefore = attachments.NextBefore,
            AttachmentsOlderLabel = attachments.NextBefore is null ? null : OlderFilesLabel,
        };
    }

    public const string OlderFilesLabel = "Older files";

    /// <summary>A later page of the attachments, for the tab's attachment table.</summary>
    public static FactoryRecordsAttachmentPageViewDto AttachmentPageView(RegisteredFactoryDto factory, FactoryAttachmentPage page,
        TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(zone);
        return new FactoryRecordsAttachmentPageViewDto
        {
            SummaryText = AttachmentsSummary(page),
            Rows = page.Attachments.Select(a => AttachmentRow(factory, a, zone, nowUtc)).ToList(),
            NextBefore = page.NextBefore,
            OlderLabel = page.NextBefore is null ? null : OlderFilesLabel,
            NewestLabel = "Newest files",
        };
    }

    // "143 files, 2.1 MB in all." and, when the page is not all of them, which ones it lists.
    private static string AttachmentsSummary(FactoryAttachmentPage page) =>
        page.Total == 0
            ? ""
            : $"{FactoriesScreenFold.Count(page.Total, "file")}, {Size(page.FactoryBytes)} in all." +
              (page.Attachments.Count < page.Total ? Shown(page.Newer, page.Attachments.Count, "File", "Files", "listed") : "");

    // " Entries 21 to 40 are shown, newest first.", or " Entry 21 is shown." for a page of one.
    private static string Shown(int newer, int count, string one, string many, string verb) =>
        count == 1
            ? $" {one} {newer + 1} is {verb}."
            : $" {many} {newer + 1} to {newer + count} are {verb}, newest first.";

    internal static FactoryRecordsAttachmentRowDto AttachmentRow(RegisteredFactoryDto factory, FactoryAttachmentEntity a,
        TimeZoneInfo zone, DateTime nowUtc) => new()
    {
        Id = a.Id,
        Name = a.Name,
        SeatText = SeatTitle(factory, a.Seat),
        ContentType = a.ContentType,
        SizeText = Size(a.Bytes),
        WhoText = Who(a.AuthorKind, a.AuthorId),
        WhenText = When(a.WrittenAtUtc, zone, nowUtc),
        DownloadLabel = "Download",
        DownloadHref = AttachmentHref(factory.Factory, a.Id),
    };

    // ---------- one journal ----------

    /// <summary>One seat's journal (or the factory's own), newest day first, each entry with its text and its files.</summary>
    /// <param name="attachmentsOf">The files an entry names, as the attachment store holds them.</param>
    public static FactoryRecordsJournalViewDto Journal(RegisteredFactoryDto factory, string? seat, FactoryJournalPage page,
        Func<FactoryJournalEntryEntity, IReadOnlyList<FactoryAttachmentEntity>> attachmentsOf, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(page);
        var (entries, total) = (page.Entries, page.Total);
        ArgumentNullException.ThrowIfNull(attachmentsOf);
        ArgumentNullException.ThrowIfNull(zone);
        return new FactoryRecordsJournalViewDto
        {
            Heading = seat is null ? $"The journal of {factory.Title} as a whole" : $"The journal of {SeatTitle(factory, seat)}",
            SummaryText = total == 0
                ? ""
                : Entries(total) + "." +
                  (entries.Count < total ? Shown(page.Newer, entries.Count, "Entry", "Entries", "shown") : ""),
            Entries = entries.Select(e => new FactoryRecordsJournalEntryDto
            {
                Id = e.Id,
                DateText = e.EntryDate,
                LineText = $"Written by {Who(e.AuthorKind, e.AuthorId)} {When(e.WrittenAtUtc, zone, nowUtc)}.",
                Text = e.Text,
                Attachments = attachmentsOf(e).Select(a => AttachmentRow(factory, a, zone, nowUtc)).ToList(),
            }).ToList(),
            EmptyText = total == 0 ? "This journal has no entry." : null,
            NextBefore = page.NextBefore,
            OlderLabel = page.NextBefore is null ? null : "Older entries",
            NewestLabel = page.Newer > 0 ? "Newest entries" : null,
            CloseLabel = "Back to the records",
        };
    }

    // ---------- one document ----------

    /// <summary>One page of a document's versions, newest first.</summary>
    public static FactoryRecordsDocumentHistoryViewDto DocumentHistory(string name, FactoryDocumentHistoryPage page,
        int newest, bool firstPage, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(zone);
        return new FactoryRecordsDocumentHistoryViewDto
        {
            Name = name,
            Heading = $"Every version of {name}",
            SummaryText = $"{FactoriesScreenFold.Count(page.Total, "version")}. Every version is kept.",
            Columns = new List<string> { "Version", "State", "Written by", "When", "Size" },
            Rows = page.Versions.Select(v => new FactoryRecordsDocumentVersionRowDto
            {
                Version = v.Version,
                Title = $"Version {v.Version}",
                StateWord = v.Version == newest ? "Newest" : "Earlier",
                StateTone = v.Version == newest ? FactoryTone.Ok : FactoryTone.Grey,
                WhoText = $"Written by {Who(v.AuthorKind, v.AuthorId)}",
                WhenText = When(v.WrittenAtUtc, zone, nowUtc),
                SizeText = Size(v.TextBytes),
                OpenLabel = "Open",
            }).ToList(),
            NextBefore = page.NextBefore,
            OlderLabel = page.NextBefore is null ? null : "Older versions",
            NewestLabel = firstPage ? null : "Newest versions",
            CloseLabel = "Back to the records",
        };
    }

    /// <summary>One version of a document, with its text.</summary>
    public static FactoryRecordsDocumentViewDto Document(FactoryDocumentVersionEntity version, int newest, TimeZoneInfo zone,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(zone);
        var state = version.Version == newest
            ? "The newest version."
            : $"An earlier version; the newest is version {newest}.";
        return new FactoryRecordsDocumentViewDto
        {
            Name = version.Name,
            Version = version.Version,
            Title = $"{version.Name}, version {version.Version}",
            LineText = $"{state} Written by {Who(version.AuthorKind, version.AuthorId)} {When(version.WrittenAtUtc, zone, nowUtc)}. {Size(version.TextBytes)}.",
            Text = version.Text,
            HistoryLabel = newest > 1 ? "History" : null,
            CloseLabel = "Back to the records",
        };
    }

    // ---------- the words they share ----------

    /// <summary>
    /// Whose a journal or a file is: the seat's name and id as the registry holds it, the factory's own for no seat, and
    /// a seat the registry no longer holds by its id and that fact - its records are kept.
    /// </summary>
    internal static string SeatTitle(RegisteredFactoryDto factory, string? seat)
    {
        if (seat is null) return "The factory as a whole";
        var found = factory.Seats.FirstOrDefault(s => string.Equals(s.Id, seat, StringComparison.OrdinalIgnoreCase));
        if (found is null) return $"{seat} (no longer a seat of this factory)";
        var name = FactoryTalkSeed.IsBoss(factory, found) ? FactoriesScreenFold.BossRoleWord : found.Name;
        return $"{name} ({found.Id})";
    }

    private static int? SeatIndex(RegisteredFactoryDto factory, string seat)
    {
        for (var i = 0; i < factory.Seats.Count; i++)
            if (string.Equals(factory.Seats[i].Id, seat, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    /// <summary>Who wrote a record, as the Gateway recorded it. The identifier is never cut short.</summary>
    internal static string Who(string kind, string? id)
    {
        var known = !string.IsNullOrWhiteSpace(id);
        return kind switch
        {
            FactoryRecordAuthorKinds.Session => known ? $"session {id}" : "a session",
            FactoryRecordAuthorKinds.Person => known ? $"a person ({id})" : "a person",
            FactoryRecordAuthorKinds.Upload => known ? $"the one-time upload from {id}" : "the one-time upload",
            // The store refuses any other writer, so another kind here is a broken row - never named as someone it is not.
            _ => throw new InvalidOperationException($"a record names an author kind '{kind}', which is none of session, person or upload"),
        };
    }

    /// <summary>"1 entry", "12 entries".</summary>
    internal static string Entries(int n) => n == 1 ? "1 entry" : $"{n} entries";

    private static string When(DateTime utc, TimeZoneInfo zone, DateTime nowUtc) =>
        FactoriesScreenFold.When(utc, zone, nowUtc, capital: false);

    /// <summary>"512 bytes", "1.2 KB", "3.4 MB".</summary>
    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => FactoriesScreenFold.Count((int)bytes, "byte"),
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
    };
}
