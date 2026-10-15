namespace CcDirector.Gateway.Contracts;

// The Records tab of a factory's page (Factory Definition mission, phase 6; mission sections 5.6 and 5.7): what the
// factory produced, held on the Gateway - journals by seat, working documents with their versions, and attachments.
// Every word here is composed on the Gateway (FactoryRecordsTabFold) and the Cockpit renders it verbatim - critical
// rule 7. The tab only reads: records are written by the factory's sessions through `cc-devthrottle factory journal`,
// `factory doc` and `factory attach`, and by the one-time upload.

/// <summary>One seat's journal, as a row of the tab: whose it is, how many entries, and the newest day.</summary>
public sealed class FactoryRecordsJournalGroupDto
{
    /// <summary>The seat id, or null for the journal the factory keeps as a whole.</summary>
    public string? Seat { get; set; }

    /// <summary>"Writer (writer)", "The factory as a whole", "scout (no longer a seat of this factory)".</summary>
    public string Title { get; set; } = "";

    /// <summary>"12 entries, the newest of 2026-10-14".</summary>
    public string SummaryText { get; set; } = "";

    /// <summary>"Read".</summary>
    public string OpenLabel { get; set; } = "";
}

/// <summary>One working document as it stands.</summary>
public sealed class FactoryRecordsDocumentRowDto
{
    /// <summary>The document's name, itself a path: <c>outreach/ledger.json</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>"Version 4".</summary>
    public string VersionText { get; set; } = "";

    /// <summary>"Written by session 1f3a...", "Written by the one-time upload from SOREN_NORTH".</summary>
    public string WhoText { get; set; } = "";

    public string WhenText { get; set; } = "";

    public string SizeText { get; set; } = "";

    /// <summary>"Open".</summary>
    public string OpenLabel { get; set; } = "";

    /// <summary>"History"; null when the document has one version only.</summary>
    public string? HistoryLabel { get; set; }
}

/// <summary>One attachment.</summary>
public sealed class FactoryRecordsAttachmentRowDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>"Writer (writer)", "The factory as a whole".</summary>
    public string SeatText { get; set; } = "";

    public string ContentType { get; set; } = "";

    public string SizeText { get; set; } = "";

    public string WhoText { get; set; } = "";

    public string WhenText { get; set; } = "";

    /// <summary>"Download".</summary>
    public string DownloadLabel { get; set; } = "";

    /// <summary>Where the file is read: <c>/gateway/factories/{factory}/records/attachments/{id}</c>.</summary>
    public string DownloadHref { get; set; } = "";
}

/// <summary>One section of the tab: its heading, the line under it, and what it says when it is empty.</summary>
public sealed class FactoryRecordsSectionDto
{
    public string Heading { get; set; } = "";

    /// <summary>"3 documents." - how many there are and, where the Gateway caps the list, how many are shown.</summary>
    public string SummaryText { get; set; } = "";

    /// <summary>Set when the section holds nothing, else null.</summary>
    public string? EmptyText { get; set; }
}

/// <summary><c>GET /gateway/factories/{factory}/records</c>: the Records tab.</summary>
public sealed class FactoryRecordsTabViewDto
{
    public string Factory { get; set; } = "";

    /// <summary>"The records of QA Outreach".</summary>
    public string Heading { get; set; } = "";

    /// <summary>What the records are and how they are written.</summary>
    public string IntroText { get; set; } = "";

    public FactoryRecordsSectionDto Journals { get; set; } = new();
    public List<FactoryRecordsJournalGroupDto> JournalGroups { get; set; } = new();

    public FactoryRecordsSectionDto Documents { get; set; } = new();
    public List<string> DocumentColumns { get; set; } = new();
    public List<FactoryRecordsDocumentRowDto> DocumentRows { get; set; } = new();

    public FactoryRecordsSectionDto Attachments { get; set; } = new();
    public List<string> AttachmentColumns { get; set; } = new();
    public List<FactoryRecordsAttachmentRowDto> AttachmentRows { get; set; } = new();

    /// <summary>The cursor of the next page of attachments, read from <c>/records/attachments?before=</c>; null when
    /// every attachment is listed.</summary>
    public Guid? AttachmentsNextBefore { get; set; }

    /// <summary>"Older files"; null when <see cref="AttachmentsNextBefore"/> is.</summary>
    public string? AttachmentsOlderLabel { get; set; }
}

/// <summary><c>GET /gateway/factories/{factory}/records/attachments?before=</c>: one later page of the attachments, drawn in
/// the tab's attachment table (its columns are the tab's).</summary>
public sealed class FactoryRecordsAttachmentPageViewDto
{
    /// <summary>"143 files, 2.1 MB in all. Files 101 to 143 are listed, newest first."</summary>
    public string SummaryText { get; set; } = "";

    public List<FactoryRecordsAttachmentRowDto> Rows { get; set; } = new();

    public Guid? NextBefore { get; set; }

    /// <summary>"Older files"; null when this is the last page.</summary>
    public string? OlderLabel { get; set; }

    /// <summary>"Newest files", back to the tab's first page.</summary>
    public string NewestLabel { get; set; } = "";
}

/// <summary>One journal entry with its text.</summary>
public sealed class FactoryRecordsJournalEntryDto
{
    public Guid Id { get; set; }

    /// <summary>The day the entry is about, as written: "2026-10-14".</summary>
    public string DateText { get; set; } = "";

    /// <summary>"Written by session 1f3a... today 10:53".</summary>
    public string LineText { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>The files the entry names, each downloadable.</summary>
    public List<FactoryRecordsAttachmentRowDto> Attachments { get; set; } = new();
}

/// <summary><c>GET /gateway/factories/{factory}/records/journal?seat=</c>: one seat's journal, newest day first.</summary>
public sealed class FactoryRecordsJournalViewDto
{
    /// <summary>"The journal of Writer (writer)".</summary>
    public string Heading { get; set; } = "";

    /// <summary>"57 entries. Entries 21 to 40 are shown, newest first." / "3 entries."</summary>
    public string SummaryText { get; set; } = "";

    public List<FactoryRecordsJournalEntryDto> Entries { get; set; } = new();

    /// <summary>Set when there is no entry, else null.</summary>
    public string? EmptyText { get; set; }

    /// <summary>The cursor of the next, older page (the oldest entry shown), sent back as <c>before=</c>; null on the
    /// last page.</summary>
    public Guid? NextBefore { get; set; }

    /// <summary>"Older entries"; null when <see cref="NextBefore"/> is.</summary>
    public string? OlderLabel { get; set; }

    /// <summary>"Newest entries" on any page but the first, else null.</summary>
    public string? NewestLabel { get; set; }

    /// <summary>"Back to the records".</summary>
    public string CloseLabel { get; set; } = "";
}

/// <summary>One version of a document in its history.</summary>
public sealed class FactoryRecordsDocumentVersionRowDto
{
    public int Version { get; set; }

    /// <summary>"Version 3".</summary>
    public string Title { get; set; } = "";

    /// <summary>"Newest" for the version that stands, else "Earlier".</summary>
    public string StateWord { get; set; } = "";
    public string StateTone { get; set; } = FactoryTone.Grey;

    public string WhoText { get; set; } = "";
    public string WhenText { get; set; } = "";
    public string SizeText { get; set; } = "";

    /// <summary>"Open".</summary>
    public string OpenLabel { get; set; } = "";
}

/// <summary><c>GET /gateway/factories/{factory}/records/documents/history?name=</c>: one page of a document's versions.</summary>
public sealed class FactoryRecordsDocumentHistoryViewDto
{
    public string Name { get; set; } = "";

    /// <summary>"Every version of outreach/ledger.json".</summary>
    public string Heading { get; set; } = "";

    /// <summary>"4 versions. Every version is kept."</summary>
    public string SummaryText { get; set; } = "";

    public List<string> Columns { get; set; } = new();
    public List<FactoryRecordsDocumentVersionRowDto> Rows { get; set; } = new();

    public int? NextBefore { get; set; }
    public string? OlderLabel { get; set; }
    public string? NewestLabel { get; set; }

    /// <summary>"Back to the records".</summary>
    public string CloseLabel { get; set; } = "";
}

/// <summary><c>GET /gateway/factories/{factory}/records/documents/read?name=&amp;version=</c>: one version with its text.</summary>
public sealed class FactoryRecordsDocumentViewDto
{
    public string Name { get; set; } = "";
    public int Version { get; set; }

    /// <summary>"outreach/ledger.json, version 3".</summary>
    public string Title { get; set; } = "";

    /// <summary>"The newest version. Written by ... 1.2 KB." / "An earlier version; the newest is version 4. ..."</summary>
    public string LineText { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>"History"; null when the document has one version only.</summary>
    public string? HistoryLabel { get; set; }

    /// <summary>"Back to the records".</summary>
    public string CloseLabel { get; set; } = "";
}
