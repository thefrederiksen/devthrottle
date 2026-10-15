using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Briefs;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// THE DEFINITION TAB OF A FACTORY'S PAGE, FOLDED ONCE (Factory Definition mission, phase 6, section 5.7; critical rule
/// 7). It replaces the Documents tab, whose only sentence promised exactly this: the published version, the draft, what
/// changes, Publish, History, Restore, Export and Import. Every sentence, label, tone and confirm is composed here, and
/// the Cockpit draws it.
///
/// IT NEVER DECIDES WHAT THE STORE WOULD REFUSE DIFFERENTLY. Which button a version carries follows the store's own
/// rules (<see cref="FactoryDefinitionStore"/>): a draft above the published version can be published; a version below
/// the published one cannot, and is restored instead; the published version carries neither. The publish and restore
/// routes still answer every press, and their refusal - with the check's findings, file and line - is what the tab shows
/// when the two disagree.
///
/// Pure: it reads what it is handed and returns finished display objects, so every sentence is proven by a unit test.
/// </summary>
public static class FactoryDefinitionTabFold
{
    /// <summary>How many versions one page of the history holds on the owner's screen.</summary>
    public const int HistoryPage = 20;

    public const string ExportLabel = "Export";
    public const string ExportVersionLabel = "Export this version";
    public const string ImportLabel = "Import a folder";

    /// <summary>The tab's own address on the factory page.</summary>
    public static string TabHref(string factory) => $"{FactoriesScreenFold.PageHref(factory)}/definition";

    /// <summary>Where the tab's view and its zips are read: <c>/gateway/factories/{factory}/definition</c>.</summary>
    public static string ApiPrefix(string factory) => $"/gateway/factories/{Uri.EscapeDataString(factory)}/definition";

    public static string ExportHref(string factory, int? version = null) =>
        ApiPrefix(factory) + "/export" + (version is { } v ? $"?version={v}" : "");

    public static string ImportHref(string factory) => ApiPrefix(factory) + "/import";

    // ---------- the tab ----------

    /// <summary>The Definition tab.</summary>
    /// <param name="factory">The factory's id, folded.</param>
    /// <param name="title">The factory's title, as the page says it.</param>
    /// <param name="standing">How its definition stands.</param>
    /// <param name="published">The published version with its files, or null.</param>
    /// <param name="newest">The newest version with its files, or null when it has no definition.</param>
    /// <param name="planOf">What a publish of a version would do to the live factory now (the diff route's plan).</param>
    /// <param name="exportLeavesOut">For a factory with NO definition: what an export from what it has today cannot hold,
    /// each with the file it would have gone in. Ignored for a defined factory.</param>
    public static FactoryDefinitionTabViewDto Tab(string factory, string title, FactoryDefinitionStanding standing,
        FactoryDefinitionVersion? published, FactoryDefinitionVersion? newest,
        Func<FactoryDefinitionVersion, FactoryDefinitionPlanDto> planOf,
        IReadOnlyList<(string File, string Message)> exportLeavesOut, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(standing);
        ArgumentNullException.ThrowIfNull(planOf);
        ArgumentNullException.ThrowIfNull(exportLeavesOut);
        ArgumentNullException.ThrowIfNull(zone);

        var view = new FactoryDefinitionTabViewDto
        {
            Factory = factory,
            Heading = $"The definition of {title}",
            Import = new FactoryDefinitionImportActionDto
            {
                Label = ImportLabel,
                Href = ImportHref(factory),
                BusyLabel = "Importing...",
                NoteText = $"Choose a zip of a definition folder whose factory.yaml names {factory}. It is checked exactly as a " +
                           "save is and stored as the next draft. It is never published: read what changes here, then publish it.",
            },
            FootnoteText = "Only a person publishes or restores. A session of this factory may save a draft, which waits " +
                           "here until a person publishes it. Every version is kept.",
        };

        if (newest is null)
        {
            view.StateText = $"{title} has no definition on the Gateway.";
            view.EmptyText = $"Today {title} is defined by its registry entry, its briefs, its schedules and its triggers, " +
                             "and it keeps working exactly that way. To give it a definition: Export writes a folder from what " +
                             "it has today; change it if you want, import it here as a draft, read what changes, and publish it.";
            view.Export = new FactoryDefinitionExportActionDto
            {
                Label = ExportLabel,
                Href = ExportHref(factory),
                NoteText = $"A zip of a folder written from what {title} has today: its registry entry, published briefs, " +
                           "schedules and triggers." + (exportLeavesOut.Count == 0
                               ? " Everything it has is in the folder."
                               : $" {FactoriesScreenFold.Count(exportLeavesOut.Count, "thing")} it has cannot go in the folder:"),
                LeavesOut = exportLeavesOut.Select(x => $"{x.File}: {x.Message}").ToList(),
            };
            return view;
        }

        var p = standing.Published?.Version;
        var n = newest.Summary.Version;
        view.HistoryLabel = "History";
        view.StateText = p switch
        {
            null when n == 1 => "Nothing is published yet. Version 1, a draft, is the only version.",
            null => $"Nothing is published yet. {FactoriesScreenFold.Count(n, "draft")} saved; the newest is version {n}.",
            { } live when live == n => $"Version {live} is published, and it is the newest version.",
            { } live => $"Version {live} is published. {FactoriesScreenFold.Count(n - live, "draft")} saved after it; the newest is version {n}.",
        };
        if (published is not null)
            view.Published = Card(published, $"Published: version {published.Summary.Version}", "published", FactoryTone.Ok, zone, nowUtc);

        var chosen = p ?? n;
        view.Export = new FactoryDefinitionExportActionDto
        {
            Label = ExportLabel,
            Href = ExportHref(factory, chosen),
            NoteText = $"A zip of the folder of version {chosen}, {(p is null ? "the newest draft, as nothing is published" : "the published version")}, " +
                       "exactly as stored. Two exports of one version are the same bytes. To export another version, open it in History.",
        };

        if (p is { } pub && n <= pub) return view;

        view.Draft = Card(newest, $"Draft: version {n}", "draft", FactoryTone.Amber, zone, nowUtc);
        view.Changes = ChangesOf(n, published, newest, planOf);
        view.Publish = PublishAction(title, n, p, n, view.Changes.Changes?.Summary);
        return view;
    }

    /// <summary>What publishing <paramref name="newest"/> changes against <paramref name="published"/>, and the plan.</summary>
    internal static FactoryDefinitionTabChangesDto ChangesOf(int version, FactoryDefinitionVersion? published,
        FactoryDefinitionVersion newest, Func<FactoryDefinitionVersion, FactoryDefinitionPlanDto> planOf)
    {
        var heading = $"What changes when version {version} is published";
        // A stored version that no longer passes the format check cannot be compared: say which and where.
        if (FactoryPublishApply.FirstFormatError(newest.Files) is { } badNew)
            return new FactoryDefinitionTabChangesDto
            {
                Heading = heading,
                CannotCompareText = $"Version {version} no longer passes the format check at {FactoryPublishApply.Where(badNew)}, " +
                                    "so what changes cannot be shown, and a publish of it would be refused. Import or save a corrected draft.",
            };
        if (published is not null && FactoryPublishApply.FirstFormatError(published.Files) is { } badOld)
            return new FactoryDefinitionTabChangesDto
            {
                Heading = heading,
                CannotCompareText = $"The published version {published.Summary.Version} no longer passes the format check at " +
                                    $"{FactoryPublishApply.Where(badOld)}, so what changes cannot be shown.",
            };
        return new FactoryDefinitionTabChangesDto
        {
            Heading = heading,
            Changes = FactoryDefinitionChanges.Compare(published?.Files, newest.Files),
            Plan = planOf(newest),
        };
    }

    private static FactoryDefinitionVersionCardDto Card(FactoryDefinitionVersion v, string title, string word, string tone,
        TimeZoneInfo zone, DateTime nowUtc) => new()
    {
        Version = v.Summary.Version,
        Title = title,
        LineText = LineOf(v.Summary, zone, nowUtc),
        NoteText = NoteOf(v.Summary),
        StateWord = word,
        StateTone = tone,
        OpenLabel = "Open",
    };

    /// <summary>"Published by a person (abc) today 10:53. Written by session 1f3a... today 10:50. 6 files, 1.2 KB."</summary>
    internal static string LineOf(FactoryDefinitionVersionSummary v, TimeZoneInfo zone, DateTime nowUtc)
    {
        var published = v.State == FactoryDefinitionStates.Published && v.PublishedAtUtc is { } at
            ? $"Published by {FactoryBriefPanelFold.Who(FactoryMemoryAuthorKinds.Person, v.PublishedBy)} {When(at, zone, nowUtc)}. "
            : "";
        return $"{published}Written by {FactoryBriefPanelFold.Who(v.AuthorKind, v.AuthorId)} {When(v.WrittenAtUtc, zone, nowUtc)}. {SizeOf(v)}.";
    }

    /// <summary>The writer's change note, or where a restore came from, or null.</summary>
    internal static string? NoteOf(FactoryDefinitionVersionSummary v) =>
        !string.IsNullOrWhiteSpace(v.ChangeNote) ? v.ChangeNote
        : v.RestoredFrom is { } from ? $"Restored from version {from}."
        : null;

    /// <summary>"6 files, 1.2 KB".</summary>
    internal static string SizeOf(FactoryDefinitionVersionSummary v) =>
        $"{FactoriesScreenFold.Count(v.FileCount, "file")}, {FactoryBriefPanelFold.Size(v.ContentBytes)}";

    // ---------- the acts ----------

    /// <summary>
    /// Publish a draft. The confirm says that the WHOLE version goes live - every change saved since the published one,
    /// not only the newest edit - and what it does to the registry, the schedules and the triggers.
    /// </summary>
    /// <param name="changesSummary">The one-line summary of what changes, when it is known.</param>
    internal static FactoryDefinitionActionDto PublishAction(string title, int version, int? published, int newest,
        string? changesSummary)
    {
        var lines = new List<string>
        {
            $"Version {version} becomes the definition of {title}. Its registry entry, schedules and triggers are changed " +
            "to match in one step, or nothing changes.",
        };
        if (!string.IsNullOrWhiteSpace(changesSummary)) lines.Add($"What changes: {changesSummary}");
        var since = version - (published ?? 0);
        if (since > 1)
            lines.Add(published is { } p
                ? $"Versions {p + 1} to {version} were all saved since version {p} was published, and version {version} holds " +
                  "every change in them. They all go live with it, not only the newest edit."
                : $"Versions 1 to {version} were all saved before anything was published, and version {version} holds every " +
                  "change in them. They all go live with it, not only the newest edit.");
        lines.Add(published is { } old
            ? $"Version {old}, published now, is kept. History can restore it."
            : "It is the first published version. From then on the definition owns this factory's registry entry, schedules " +
              "and triggers, and a direct edit of them is refused.");
        if (version < newest)
            lines.Add(newest - version == 1
                ? $"Version {newest} stays a draft."
                : $"Versions {version + 1} to {newest} stay drafts.");
        return new FactoryDefinitionActionDto
        {
            Version = version,
            ExpectedVersion = newest,
            Replaces = published,
            Label = $"Publish version {version}",
            BusyLabel = "Publishing...",
            ConfirmTitle = $"Publish version {version} of the definition of {title}?",
            ConfirmLines = lines,
            ConfirmLabel = "Publish",
            DoneText = $"Version {version} is published. It is the definition of {title} now.",
        };
    }

    /// <summary>Restore an older version: a copy of it is published as the newest version.</summary>
    internal static FactoryDefinitionActionDto RestoreAction(string title, int version, int published, int newest) =>
        new()
        {
            Version = version,
            ExpectedVersion = newest,
            Replaces = published,
            Label = "Restore",
            BusyLabel = "Restoring...",
            ConfirmTitle = $"Restore version {version} of the definition of {title}?",
            ConfirmLines = RestoreLines(version, published, newest),
            ConfirmLabel = "Restore",
            DoneText = $"Version {version} was restored as version {newest + 1}, and it is published. It is the definition of {title} now.",
        };

    private static List<string> RestoreLines(int version, int published, int newest)
    {
        var lines = new List<string>
        {
            $"A copy of version {version} becomes version {newest + 1}, and it is published. The registry entry, schedules " +
            "and triggers are changed to match in one step, or nothing changes.",
            $"Nothing is removed. Version {published}, published now, and every other version are kept.",
        };
        var waiting = newest - published;
        if (waiting > 0)
            lines.Add(waiting == 1
                ? $"Version {newest}, a draft, is then older than the published version. It is kept and can be restored."
                : $"The {waiting} drafts above version {published} are then older than the published version. They are kept and each can be restored.");
        return lines;
    }

    // ---------- the history ----------

    /// <summary>One page of the history, newest first. <paramref name="firstPage"/> is false when the page was asked for
    /// with a starting version, so the screen can offer the way back to the newest.</summary>
    public static FactoryDefinitionHistoryViewDto History(string factory, string title, FactoryDefinitionStanding standing,
        FactoryDefinitionHistoryPage page, bool firstPage, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(standing);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(zone);
        var published = standing.Published?.Version;
        var newest = standing.Newest?.Version ?? 0;
        return new FactoryDefinitionHistoryViewDto
        {
            Heading = $"History of the definition of {title}",
            SummaryText = page.Total == 0
                ? "No version yet."
                : $"{FactoriesScreenFold.Count(page.Total, "version")}. " +
                  (published is { } p ? $"Version {p} is published." : "None is published."),
            PageText = page.Versions.Count == 0
                ? null
                : page.Versions.Count == 1
                    ? $"Version {page.Versions[0].Version} of {page.Total}."
                    : $"Versions {page.Versions[0].Version} to {page.Versions[^1].Version} of {page.Total}.",
            Columns = new List<string> { "Version", "State", "Written by", "When", "Note", "Size" },
            Rows = page.Versions.Select(v => Row(factory, title, v, published, newest, zone, nowUtc)).ToList(),
            EmptyText = page.Total == 0
                ? $"{title} has no definition on the Gateway, so it has no history."
                : page.Versions.Count == 0 ? "There is no older version." : null,
            NextBefore = page.NextBefore,
            OlderLabel = page.NextBefore is null ? null : "Older versions",
            NewestLabel = firstPage ? null : "Newest versions",
            CloseLabel = "Back to the definition",
        };
    }

    private static FactoryDefinitionHistoryRowDto Row(string factory, string title, FactoryDefinitionVersionSummary v,
        int? published, int newest, TimeZoneInfo zone, DateTime nowUtc)
    {
        var isDraft = v.State == FactoryDefinitionStates.Draft;
        var (word, tone) = (isDraft, published) switch
        {
            (false, { } p) when p == v.Version => ("Published", FactoryTone.Ok),
            (false, _) => ("Published earlier", FactoryTone.Grey),
            (true, { } p) when v.Version < p => ("Draft, passed over", FactoryTone.Grey),
            _ => ("Draft", FactoryTone.Amber),
        };
        return new FactoryDefinitionHistoryRowDto
        {
            Version = v.Version,
            Title = $"Version {v.Version}",
            StateWord = word,
            StateTone = tone,
            WhoText = $"Written by {FactoryBriefPanelFold.Who(v.AuthorKind, v.AuthorId)}",
            WhenText = When(v.WrittenAtUtc, zone, nowUtc),
            NoteText = NoteOf(v),
            SizeText = SizeOf(v),
            OpenLabel = "Open",
            ExportLabel = ExportVersionLabel,
            ExportHref = ExportHref(factory, v.Version),
            // The store's own rules: a draft above the published version is published; anything below it is restored.
            Publish = isDraft && v.Version > (published ?? 0) ? PublishAction(title, v.Version, published, newest, null) : null,
            Restore = published is { } current && v.Version < current ? RestoreAction(title, v.Version, current, newest) : null,
        };
    }

    // ---------- one version ----------

    /// <summary>One version, with every file: what Open shows.</summary>
    public static FactoryDefinitionVersionViewDto Version(string factory, FactoryDefinitionVersion version,
        FactoryDefinitionStanding standing, TimeZoneInfo zone, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(standing);
        ArgumentNullException.ThrowIfNull(zone);
        var v = version.Summary;
        var state = v.State == FactoryDefinitionStates.Draft
            ? standing.Published is { } p && v.Version < p.Version ? "Draft, passed over" : "Draft"
            : standing.Published?.Version == v.Version ? "Published" : "Published earlier";
        return new FactoryDefinitionVersionViewDto
        {
            Version = v.Version,
            Title = $"Version {v.Version}",
            LineText = $"{state}. {LineOf(v, zone, nowUtc)}",
            NoteText = NoteOf(v),
            Files = version.Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new FactoryDefinitionFileViewDto
            {
                Path = f.Key,
                SizeText = FactoryBriefPanelFold.Size(System.Text.Encoding.UTF8.GetByteCount(f.Value)),
                Text = f.Value,
            }).ToList(),
            ExportLabel = ExportVersionLabel,
            ExportHref = ExportHref(factory, v.Version),
            CloseLabel = "Close",
        };
    }

    // ---------- an import ----------

    /// <summary>What the tab says after an import stored a draft.</summary>
    public static string ImportedText(string factory, int version, int files, bool created) =>
        $"Imported {FactoriesScreenFold.Count(files, "file")} as version {version}, a draft." +
        (created ? $" It is the first version of the definition of {factory}." : "") +
        " Nothing is published: read what changes, then publish it.";

    private static string When(DateTime utc, TimeZoneInfo zone, DateTime nowUtc) =>
        FactoriesScreenFold.When(utc, zone, nowUtc, capital: false);
}
