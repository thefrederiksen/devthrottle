namespace CcDirector.Gateway.Contracts;

// The brief panel of a seat's page (Factory Seats mission, phase 2b): the editor, the history and one version, as
// the owner's screen reads them. Every word, tone and order here is decided on the Gateway (FactoryBriefPanelFold)
// and the Cockpit renders it verbatim - critical rule 7. The writes themselves go to the brief's own routes under
// /factory-briefs/{factory}/{seat}; what is here is only what the screen shows before and after them.

/// <summary>
/// One act on a seat's brief that a person confirms first: publish, or bring an older version back. Every sentence
/// is the Gateway's. The Cockpit sends back exactly the versions the confirm named.
/// </summary>
public sealed class FactoryBriefActionDto
{
    /// <summary>The version the act is about: the one to publish, or the one to bring back.</summary>
    public int Version { get; set; }

    /// <summary>The seat's newest version when the confirm was composed. A bring back sends it, so the Gateway can
    /// refuse when something newer was written since.</summary>
    public int ExpectedVersion { get; set; }

    /// <summary>"Publish", "Bring back".</summary>
    public string Label { get; set; } = "";

    /// <summary>What the confirm's button says while the Gateway works.</summary>
    public string BusyLabel { get; set; } = "";

    /// <summary>The confirm's heading.</summary>
    public string ConfirmTitle { get; set; } = "";

    /// <summary>The confirm's sentences, in order: exactly what will happen.</summary>
    public List<string> ConfirmLines { get; set; } = new();

    /// <summary>The confirm's button.</summary>
    public string ConfirmLabel { get; set; } = "";

    /// <summary>What the page says once the Gateway has done it.</summary>
    public string DoneText { get; set; } = "";
}

/// <summary><c>GET /gateway/factories/{factory}/seats/{seat}/brief/editor</c>: what the brief's editor starts from.</summary>
public sealed class FactoryBriefEditorViewDto
{
    /// <summary>"Edit the brief of Scout", "Write the brief of Scout".</summary>
    public string Heading { get; set; } = "";

    /// <summary>Which version the text starts from, said plainly: the published brief, the newest draft, or nothing.</summary>
    public string StartText { get; set; } = "";

    /// <summary>The text the editor starts with; empty for a seat with no brief on the Gateway.</summary>
    public string Text { get; set; } = "";

    /// <summary>The seat's newest version: what a save states as the version it read. 0 for a seat with no brief here.</summary>
    public int ExpectedVersion { get; set; }

    /// <summary>True when <see cref="Text"/> is a draft nobody has published, so Publish can publish it as it is.</summary>
    public bool StartsFromDraft { get; set; }

    /// <summary>The label of the text box.</summary>
    public string TextLabel { get; set; } = "";

    /// <summary>"A brief takes at most 65,536 bytes."</summary>
    public string LimitText { get; set; } = "";

    /// <summary>Set when as many drafts wait as a session may leave. Null below that.</summary>
    public string? WaitingText { get; set; }

    /// <summary>"Save draft".</summary>
    public string SaveLabel { get; set; } = "";

    /// <summary>What Save draft says while the Gateway works.</summary>
    public string SaveBusyLabel { get; set; } = "";

    /// <summary>What the page says once a draft is saved.</summary>
    public string SavedText { get; set; } = "";

    /// <summary>Publish and the confirm it asks first. <see cref="FactoryBriefActionDto.Version"/> is the version the
    /// editor started from; a changed text is saved as a new version first and that one is published.</summary>
    public FactoryBriefActionDto Publish { get; set; } = new();

    /// <summary>"Close without saving".</summary>
    public string CancelLabel { get; set; } = "";

    /// <summary>What the editor says when a save was refused because a newer version exists: that nothing was saved
    /// and the owner's text is still there.</summary>
    public string ConflictText { get; set; } = "";

    /// <summary>The heading over the owner's own text when a newer version is shown beside it.</summary>
    public string YoursLabel { get; set; } = "";

    /// <summary>The heading over the newer version's text.</summary>
    public string TheirsLabel { get; set; } = "";

    /// <summary>The button that saves the owner's text as the version after the newer one.</summary>
    public string SaveOverLabel { get; set; } = "";

    /// <summary>The button that takes the newer version's text and drops the owner's.</summary>
    public string TakeTheirsLabel { get; set; } = "";
}

/// <summary>One version in a page of a seat's history, as the owner's screen shows it.</summary>
public sealed class FactoryBriefHistoryRowDto
{
    public int Version { get; set; }

    /// <summary>"Version 5".</summary>
    public string Title { get; set; } = "";

    /// <summary>"Published", "Published earlier", "Draft", "Draft, passed over".</summary>
    public string StateWord { get; set; } = "";
    public string StateTone { get; set; } = FactoryTone.Grey;

    /// <summary>"Written by session 1f3a...", "Written by a person (device browser abc)".</summary>
    public string WhoText { get; set; } = "";

    /// <summary>"today 20:14", "3 Oct 06:20", in the account's time zone.</summary>
    public string WhenText { get; set; } = "";

    /// <summary>"1.2 KB", "512 bytes".</summary>
    public string SizeText { get; set; } = "";

    /// <summary>"Open": reads this version's text.</summary>
    public string OpenLabel { get; set; } = "";

    /// <summary>Publish, on a draft that can be published; else null.</summary>
    public FactoryBriefActionDto? Publish { get; set; }

    /// <summary>Bring back, on a version older than the published one; else null.</summary>
    public FactoryBriefActionDto? BringBack { get; set; }
}

/// <summary><c>GET /gateway/factories/{factory}/seats/{seat}/brief/history</c>: one page of a seat's history, newest first.</summary>
public sealed class FactoryBriefHistoryViewDto
{
    /// <summary>"History of the brief of Scout".</summary>
    public string Heading { get; set; } = "";

    /// <summary>"43 versions. Version 41 is published."</summary>
    public string SummaryText { get; set; } = "";

    /// <summary>"Versions 43 to 24 of 43."; null when the seat has no version.</summary>
    public string? PageText { get; set; }

    /// <summary>Set when as many drafts wait as a session may leave. Null below that.</summary>
    public string? WaitingText { get; set; }

    /// <summary>The column headings, in order: version, state, written by, when, size.</summary>
    public List<string> Columns { get; set; } = new();

    /// <summary>This page's versions, newest first.</summary>
    public List<FactoryBriefHistoryRowDto> Rows { get; set; } = new();

    /// <summary>"This seat has no brief on the Gateway, so it has no history." when there is no version, else null.</summary>
    public string? EmptyText { get; set; }

    /// <summary>What to send as <c>before</c> for the next, older page; null when this page reaches the oldest version.</summary>
    public int? NextBefore { get; set; }

    /// <summary>"Older versions"; null when this page reaches the oldest version.</summary>
    public string? OlderLabel { get; set; }

    /// <summary>"Newest versions": back to the first page; null on the first page.</summary>
    public string? NewestLabel { get; set; }

    /// <summary>"Back to the brief".</summary>
    public string CloseLabel { get; set; } = "";
}

/// <summary><c>GET /gateway/factories/{factory}/seats/{seat}/brief/versions/{version}</c>: one version, with its text.</summary>
public sealed class FactoryBriefVersionViewDto
{
    public int Version { get; set; }

    /// <summary>"Version 5".</summary>
    public string Title { get; set; } = "";

    /// <summary>"Draft. Written by session 1f3a... today 20:14. 1.2 KB."</summary>
    public string LineText { get; set; } = "";

    /// <summary>The version's whole text.</summary>
    public string Text { get; set; } = "";

    /// <summary>"Close".</summary>
    public string CloseLabel { get; set; } = "";
}
