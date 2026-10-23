namespace CcDirector.Gateway.Contracts;

// The Definition tab of a factory's page (Factory Definition mission, phase 6; mission section 5.7). It replaces the
// Documents tab: the published version, the draft, what changes, Publish, History, Restore, Export and Import. Every
// word, tone and order here is decided on the Gateway (FactoryDefinitionTabFold) and the Cockpit renders it verbatim -
// critical rule 7. Publish and restore go to the definition's own routes under /factory-definitions/{factory}, which
// check who is asking and word every refusal; export and import of a zip go to the tab's own routes.

/// <summary>
/// One act on a factory's definition that a person confirms first: publish a draft, or restore an older version. Every
/// sentence is the Gateway's. The Cockpit sends back exactly the versions named here.
/// </summary>
public sealed class FactoryDefinitionActionDto
{
    /// <summary>The version the act is about: the one to publish, or the one to restore.</summary>
    public int Version { get; set; }

    /// <summary>The newest version when the confirm was composed. A restore sends it, so the Gateway refuses it when
    /// something newer was written since.</summary>
    public int ExpectedVersion { get; set; }

    /// <summary>The published version the confirm was composed against, or null when none is published. Sent as
    /// <c>replaces</c>, so the Gateway refuses the act when another publish got there first.</summary>
    public int? Replaces { get; set; }

    /// <summary>"Publish version 4", "Restore".</summary>
    public string Label { get; set; } = "";

    /// <summary>What the confirm's button says while the Gateway works.</summary>
    public string BusyLabel { get; set; } = "";

    /// <summary>The confirm's heading.</summary>
    public string ConfirmTitle { get; set; } = "";

    /// <summary>The confirm's sentences, in order: exactly what will happen.</summary>
    public List<string> ConfirmLines { get; set; } = new();

    /// <summary>The confirm's button.</summary>
    public string ConfirmLabel { get; set; } = "";

    /// <summary>What the tab says once the Gateway has done it, above what the Gateway applied.</summary>
    public string DoneText { get; set; } = "";
}

/// <summary>One version of the definition as a card on the tab: the published one, or the newest draft.</summary>
public sealed class FactoryDefinitionVersionCardDto
{
    public int Version { get; set; }

    /// <summary>"Published: version 3", "Draft: version 4".</summary>
    public string Title { get; set; } = "";

    /// <summary>"Published by a person (device browser abc) today 10:53. Written by ... 6 files, 1.2 KB."</summary>
    public string LineText { get; set; } = "";

    /// <summary>The change note the writer left, or null.</summary>
    public string? NoteText { get; set; }

    /// <summary>"published", "draft".</summary>
    public string StateWord { get; set; } = "";
    public string StateTone { get; set; } = FactoryTone.Grey;

    /// <summary>"Open": reads this version's files.</summary>
    public string OpenLabel { get; set; } = "";
}

/// <summary>What publishing the draft changes, against the published version, and what it would do to the live factory.</summary>
public sealed class FactoryDefinitionTabChangesDto
{
    /// <summary>"What changes when version 4 is published".</summary>
    public string Heading { get; set; } = "";

    /// <summary>What changes in the definition, composed by the same comparison the command line prints.</summary>
    public FactoryDefinitionChangesDto? Changes { get; set; }

    /// <summary>What a publish would do to the registry, the schedules and the triggers now.</summary>
    public FactoryDefinitionPlanDto? Plan { get; set; }

    /// <summary>Set, with no changes and no plan, when a version no longer passes the format check, so nothing can be
    /// compared: which version, and where.</summary>
    public string? CannotCompareText { get; set; }
}

/// <summary>Export: a zip of the definition's folder, or of the folder written from what an undefined factory has today.</summary>
public sealed class FactoryDefinitionExportActionDto
{
    /// <summary>"Export".</summary>
    public string Label { get; set; } = "";

    /// <summary>"Exporting...", while the zip is read.</summary>
    public string BusyLabel { get; set; } = "";

    /// <summary>Where the zip is read: <c>/gateway/factories/{factory}/definition/export</c>, with <c>?version=N</c> for one version.</summary>
    public string Href { get; set; } = "";

    /// <summary>What the zip will hold, said before it is downloaded.</summary>
    public string NoteText { get; set; } = "";

    /// <summary>For a factory with no definition: what it has today that the folder cannot hold, each with the file it
    /// would have gone in ("factory.yaml: the goal is held in a file ..."). Empty otherwise.</summary>
    public List<string> LeavesOut { get; set; } = new();
}

/// <summary>Import: a zip of a definition folder, checked as a save is and stored as a draft. Never published.</summary>
public sealed class FactoryDefinitionImportActionDto
{
    /// <summary>"Import a folder".</summary>
    public string Label { get; set; } = "";

    /// <summary>What an import does, said before a file is chosen.</summary>
    public string NoteText { get; set; } = "";

    /// <summary>Where the zip is sent: <c>/gateway/factories/{factory}/definition/import</c>.</summary>
    public string Href { get; set; } = "";

    /// <summary>What the button says while the Gateway reads the zip.</summary>
    public string BusyLabel { get; set; } = "";
}

/// <summary><c>GET /gateway/factories/{factory}/definition</c>: the Definition tab.</summary>
public sealed class FactoryDefinitionTabViewDto
{
    public string Factory { get; set; } = "";

    /// <summary>"The definition of QA Outreach".</summary>
    public string Heading { get; set; } = "";

    /// <summary>How the definition stands, in one sentence: which version is published and whether a draft waits.</summary>
    public string StateText { get; set; } = "";

    /// <summary>For a factory with no definition on the Gateway: what that means and how to give it one. Null otherwise.</summary>
    public string? EmptyText { get; set; }

    /// <summary>The published version, or null when none is published.</summary>
    public FactoryDefinitionVersionCardDto? Published { get; set; }

    /// <summary>The newest version when it is newer than the published one (or nothing is published), else null.</summary>
    public FactoryDefinitionVersionCardDto? Draft { get; set; }

    /// <summary>What publishing the draft changes; null when there is no draft.</summary>
    public FactoryDefinitionTabChangesDto? Changes { get; set; }

    /// <summary>Publish the draft, and the confirm it asks first; null when there is no draft.</summary>
    public FactoryDefinitionActionDto? Publish { get; set; }

    /// <summary>"History"; null when the factory has no definition, so no history.</summary>
    public string? HistoryLabel { get; set; }

    public FactoryDefinitionExportActionDto Export { get; set; } = new();

    public FactoryDefinitionImportActionDto Import { get; set; } = new();

    /// <summary>The line under the tab: only a person publishes, and where a session's drafts come from.</summary>
    public string FootnoteText { get; set; } = "";
}

/// <summary>One version in a page of the definition's history.</summary>
public sealed class FactoryDefinitionHistoryRowDto
{
    public int Version { get; set; }

    /// <summary>"Version 5".</summary>
    public string Title { get; set; } = "";

    /// <summary>"Published", "Published earlier", "Draft", "Draft, passed over".</summary>
    public string StateWord { get; set; } = "";
    public string StateTone { get; set; } = FactoryTone.Grey;

    /// <summary>"Written by a person (device browser abc)", "Written by session 1f3a...".</summary>
    public string WhoText { get; set; } = "";

    /// <summary>"today 20:14", in the account's time zone.</summary>
    public string WhenText { get; set; } = "";

    /// <summary>The change note, "restored from version 1.", or null.</summary>
    public string? NoteText { get; set; }

    /// <summary>"6 files, 1.2 KB".</summary>
    public string SizeText { get; set; } = "";

    /// <summary>"Open".</summary>
    public string OpenLabel { get; set; } = "";

    /// <summary>"Export this version".</summary>
    public string ExportLabel { get; set; } = "";

    /// <summary>The zip of this version.</summary>
    public string ExportHref { get; set; } = "";

    /// <summary>Publish, on a draft above the published version; else null.</summary>
    public FactoryDefinitionActionDto? Publish { get; set; }

    /// <summary>Restore, on a version older than the published one; else null.</summary>
    public FactoryDefinitionActionDto? Restore { get; set; }
}

/// <summary><c>GET /gateway/factories/{factory}/definition/history</c>: one page of the history, newest first.</summary>
public sealed class FactoryDefinitionHistoryViewDto
{
    /// <summary>"History of the definition of QA Outreach".</summary>
    public string Heading { get; set; } = "";

    /// <summary>"4 versions. Version 3 is published."</summary>
    public string SummaryText { get; set; } = "";

    /// <summary>"Versions 4 to 1 of 4."; null when there is no version.</summary>
    public string? PageText { get; set; }

    /// <summary>The column headings, in order.</summary>
    public List<string> Columns { get; set; } = new();

    public List<FactoryDefinitionHistoryRowDto> Rows { get; set; } = new();

    /// <summary>Set when there is no version on this page, else null.</summary>
    public string? EmptyText { get; set; }

    /// <summary>What to send as <c>before</c> for the next, older page; null on the oldest page.</summary>
    public int? NextBefore { get; set; }

    /// <summary>"Older versions"; null on the oldest page.</summary>
    public string? OlderLabel { get; set; }

    /// <summary>"Newest versions"; null on the first page.</summary>
    public string? NewestLabel { get; set; }

    /// <summary>"Back to the definition".</summary>
    public string CloseLabel { get; set; } = "";
}

/// <summary>One file of a version, with its text.</summary>
public sealed class FactoryDefinitionFileViewDto
{
    /// <summary>The path in the definition: <c>seats/writer.yaml</c>.</summary>
    public string Path { get; set; } = "";

    /// <summary>"512 bytes", "1.2 KB".</summary>
    public string SizeText { get; set; } = "";

    public string Text { get; set; } = "";
}

/// <summary><c>GET /gateway/factories/{factory}/definition/versions/{version}</c>: one version, with every file.</summary>
public sealed class FactoryDefinitionVersionViewDto
{
    public int Version { get; set; }

    /// <summary>"Version 3".</summary>
    public string Title { get; set; } = "";

    /// <summary>"Published. Written by ... Published by ... 6 files, 1.2 KB."</summary>
    public string LineText { get; set; } = "";

    /// <summary>The change note, or null.</summary>
    public string? NoteText { get; set; }

    /// <summary>Every file, in path order.</summary>
    public List<FactoryDefinitionFileViewDto> Files { get; set; } = new();

    /// <summary>"Export this version".</summary>
    public string ExportLabel { get; set; } = "";

    public string ExportHref { get; set; } = "";

    /// <summary>"Close".</summary>
    public string CloseLabel { get; set; } = "";
}

/// <summary>The answer to an import from the tab: the draft it made, in the Gateway's words.</summary>
public sealed class FactoryDefinitionImportedDto
{
    public string Factory { get; set; } = "";

    /// <summary>The draft version the import made.</summary>
    public int Version { get; set; }

    /// <summary>"Imported 6 files as version 5, a draft. Nothing is published: ..."</summary>
    public string DoneText { get; set; } = "";

    /// <summary>Notices the check found, each "file:line: message". They never refuse.</summary>
    public List<FactoryDefinitionFindingDto> Findings { get; set; } = new();

    /// <summary>Set by New factory: the new factory's page, on its Definition tab, which the Cockpit opens.</summary>
    public string? OpenHref { get; set; }
}
