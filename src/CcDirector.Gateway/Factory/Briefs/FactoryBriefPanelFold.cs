using System.Globalization;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Talk;

namespace CcDirector.Gateway.Factory.Briefs;

/// <summary>
/// THE BRIEF PANEL OF A SEAT'S PAGE, FOLDED ONCE (Factory Seats mission, phase 2b; critical rule 7). "What this seat
/// does" on the seat page, the editor behind Edit, the history behind History, and one version behind Open: every
/// sentence, label and confirm is composed here, and the Cockpit draws it.
///
/// Why it exists. Phase 2a put a seat's brief on the Gateway and gave it routes, and nobody but a session could use
/// them: a person had no screen, and only a person publishes. This is that screen's words.
///
/// IT NEVER DECIDES WHAT THE STORE WOULD REFUSE DIFFERENTLY. Which button a version carries follows the store's own
/// rules (<see cref="FactoryBriefStore"/>): a draft above the published version can be published; a version below the
/// published one cannot, and is brought back instead; the published version carries neither. The store still
/// answers every press, and its refusal is what the screen shows when the two disagree because something changed.
///
/// Pure: it reads what it is handed and returns finished display objects, so every sentence is proven by a unit test.
/// </summary>
public static class FactoryBriefPanelFold
{
    /// <summary>How many versions one page of the history holds on the owner's screen.</summary>
    public const int HistoryPage = 20;

    public const string PanelHeading = "What this seat does";
    public const string EditLabel = "Edit";
    public const string OpenDraftLabel = "Open the newest draft";
    public const string WriteLabel = "Write the brief here";
    public const string HistoryLabel = "History";

    /// <summary>What the brief's panel says while a brief is a file on the seat's computer.</summary>
    internal static string FileNote(string computer) =>
        $"The brief is a file on {computer}. It is not held on the Gateway yet, so it cannot be read or changed here. " +
        "A talk and a scheduled run of this seat are told this path and read the file from that computer.";

    /// <summary>
    /// THE LINE A DEFINED FACTORY'S BRIEF CARRIES (Factory Definition, phase 4, design section 9): the brief is part of
    /// the factory's definition, its versions are the definition's, and a publish publishes the whole definition. Said
    /// on the seat page, in the editor, the history and one version, and on every answer of the brief routes, so nobody
    /// presses Publish believing only this brief goes live.
    /// </summary>
    public static string DefinitionLine(string factory) =>
        $"This brief is part of the definition of {factory}. Its versions are the definition's versions, and a " +
        "publish publishes the whole definition - every seat, start and file in it - not only this brief.";

    /// <summary>The seat's name as the page says it: "Boss" for the boss seat, which has no name of its own.</summary>
    internal static string SeatName(RegisteredFactoryDto factory, RegisteredFactorySeatDto seat) =>
        FactoryTalkSeed.IsBoss(factory, seat) ? FactoriesScreenFold.BossRoleWord : seat.Name;

    // ---------- the panel on the seat page ----------

    /// <summary>"What this seat does" on the seat's page.</summary>
    /// <param name="definition">For a DEFINED factory (Factory Definition, phase 4): <paramref name="brief"/> is the
    /// seat's brief in the definition's newest and published versions, and this is <see cref="DefinitionLine"/>. Null
    /// for every other factory, whose panel reads exactly as before.</param>
    public static FactorySeatBriefDto Panel(RegisteredFactoryDto factory, RegisteredFactorySeatDto seat,
        FactoryBriefStanding brief, TimeZoneInfo zone, DateTime nowUtc, string? definition = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(seat);
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(zone);
        if (definition is not null) return DefinedPanel(factory, seat, brief, zone, nowUtc, definition);

        if (brief.Published is { } published)
        {
            var newest = brief.Newest!;
            return new FactorySeatBriefDto
            {
                Heading = PanelHeading,
                State = FactorySeatBriefStates.Published,
                Text = published.Text,
                VersionText = $"Version {published.Version}, published by {Who(FactoryMemoryAuthorKinds.Person, published.PublishedBy)} " +
                              $"{When(published.PublishedAtUtc ?? published.WrittenAtUtc, zone, nowUtc)}.",
                DraftText = newest.Version > published.Version
                    ? $"A newer draft, version {newest.Version}, was written by {Who(newest)} {When(newest.WrittenAtUtc, zone, nowUtc)}. " +
                      $"Talks and runs of this seat are given version {published.Version} until you publish a newer one."
                    : null,
                WaitingText = Waiting(brief),
                EditLabel = EditLabel,
                HistoryLabel = HistoryLabel,
            };
        }

        var file = new FactorySeatBriefDto
        {
            Heading = PanelHeading,
            State = FactorySeatBriefStates.File,
            PathLabel = "Brief file",
            PathText = FactoryTalkSeed.InFolder(factory.Folder, seat.BriefFile),
            ComputerLabel = "Computer",
            ComputerText = seat.Computer,
            Note = FileNote(seat.Computer),
            EditLabel = WriteLabel,
        };
        if (brief.Newest is not { } draft) return file;

        // Drafts and nothing published: the seat is still told the path of its file, so the file is still shown.
        file.State = FactorySeatBriefStates.Drafts;
        file.DraftText =
            $"This seat has {FactoriesScreenFold.Count(draft.Version, "draft")} on the Gateway and none is published. " +
            $"The newest is version {draft.Version}, written by {Who(draft)} {When(draft.WrittenAtUtc, zone, nowUtc)}. " +
            "Until you publish one, talks and runs of this seat are told the path of its brief file.";
        file.WaitingText = Waiting(brief);
        file.EditLabel = OpenDraftLabel;
        file.HistoryLabel = HistoryLabel;
        return file;
    }

    /// <summary>
    /// "What this seat does" for a DEFINED factory. Its brief is never a file on a computer - it is the seat's
    /// <c>brief:</c> in the published definition - so no path is ever shown, and every version is a definition version.
    /// </summary>
    private static FactorySeatBriefDto DefinedPanel(RegisteredFactoryDto factory, RegisteredFactorySeatDto seat,
        FactoryBriefStanding brief, TimeZoneInfo zone, DateTime nowUtc, string definition)
    {
        if (brief.Published is not { } published)
            return new FactorySeatBriefDto
            {
                Heading = PanelHeading,
                State = FactorySeatBriefStates.Drafts,
                DraftText = $"The published definition of {factory.Title} has no seat file for {SeatName(factory, seat)}, " +
                            "so no talk or run of this seat is given a brief.",
                WaitingText = Waiting(brief),
                Note = definition,
                EditLabel = EditLabel,
                HistoryLabel = HistoryLabel,
            };

        var newest = brief.Newest ?? published;
        return new FactorySeatBriefDto
        {
            Heading = PanelHeading,
            State = FactorySeatBriefStates.Published,
            Text = published.Text,
            VersionText = $"Definition version {published.Version}, published by {Who(FactoryMemoryAuthorKinds.Person, published.PublishedBy)} " +
                          $"{When(published.PublishedAtUtc ?? published.WrittenAtUtc, zone, nowUtc)}.",
            DraftText = newest.Version > published.Version
                ? $"A newer draft of the definition, version {newest.Version}, was written by {Who(newest)} {When(newest.WrittenAtUtc, zone, nowUtc)}. " +
                  $"Talks and runs of this seat are given the brief in definition version {published.Version} until a person publishes a newer one."
                : null,
            WaitingText = Waiting(brief),
            Note = definition,
            EditLabel = EditLabel,
            HistoryLabel = HistoryLabel,
        };
    }

    // ---------- the editor ----------

    /// <summary>What the editor starts from: the newest version, said plainly, or nothing for a first brief.</summary>
    /// <param name="definition">For a DEFINED factory, <see cref="DefinitionLine"/>: the editor then starts from the
    /// seat's brief in the newest definition version and says the save makes a new definition draft. Null otherwise.</param>
    public static FactoryBriefEditorViewDto Editor(RegisteredFactoryDto factory, RegisteredFactorySeatDto seat,
        FactoryBriefStanding brief, TimeZoneInfo zone, DateTime nowUtc, string? definition = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(seat);
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(zone);
        var name = SeatName(factory, seat);
        var newest = brief.Newest;
        var published = brief.Published;
        var fromDraft = newest is not null && newest.State == FactoryBriefStates.Draft;

        string start;
        if (newest is null && definition is not null)
            start = $"{name} has no seat file in the newest definition of {factory.Title}, so it has no brief to edit here. " +
                    "Add the seat to the definition first.";
        else if (newest is null)
            start = $"{name} has no brief on the Gateway yet. Write it here. Until one is published, talks and runs of " +
                    $"this seat are told the path of its brief file, {FactoryTalkSeed.InFolder(factory.Folder, seat.BriefFile)} on {seat.Computer}.";
        else if (!fromDraft)
            start = $"This starts from version {newest.Version}, the published brief.";
        else if (published is null)
            start = $"This starts from version {newest.Version}, the newest draft, written by {Who(newest)} " +
                    $"{When(newest.WrittenAtUtc, zone, nowUtc)}. Nothing is published yet.";
        else
            start = $"This starts from version {newest.Version}, the newest draft, written by {Who(newest)} " +
                    $"{When(newest.WrittenAtUtc, zone, nowUtc)}. It is not the published brief: talks and runs of this " +
                    $"seat are given version {published.Version}.";

        if (definition is not null) start += " " + definition;

        var view = new FactoryBriefEditorViewDto
        {
            Heading = newest is null ? $"Write the brief of {name}" : $"Edit the brief of {name}",
            StartText = start,
            Text = newest?.Text ?? "",
            ExpectedVersion = newest?.Version ?? 0,
            TextLabel = $"The brief of {name}",
            LimitText = $"A brief takes at most {FactoryBriefStore.MaxBriefBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes.",
            WaitingText = Waiting(brief),
            SaveLabel = "Save draft",
            SaveBusyLabel = "Saving...",
            SavedText = published is null
                ? "Saved as a draft. Nothing is published yet, so talks and runs of this seat are still told the path of its brief file."
                : $"Saved as a draft. Talks and runs of this seat are still given version {published.Version} until you publish.",
            Publish = new FactoryBriefActionDto
            {
                Version = newest?.Version ?? 0,
                ExpectedVersion = newest?.Version ?? 0,
                Label = "Publish",
                BusyLabel = "Publishing...",
                ConfirmTitle = $"Publish this brief for {name}?",
                ConfirmLines = new List<string>
                {
                    $"Every later talk and every later run of {name} will be given this text.",
                    "A text you have changed is saved as a new version first, and that version is published.",
                    Replaces(published),
                },
                ConfirmLabel = "Publish",
                DoneText = $"Published. Every later talk and every later run of {name} is given this text.",
            },
            CancelLabel = "Close without saving",
            ConflictText = "Nothing was saved. Your text is still in the box, and the newer version is shown beside it. " +
                           "Merge what you need into your text and save it after the newer version, or take the newer version.",
            YoursLabel = "Your text",
            TheirsLabel = "The newer version",
            SaveOverLabel = "Save my text after the newer version",
            TakeTheirsLabel = "Take the newer version and drop my text",
        };
        if (definition is null) return view;

        // A DEFINED FACTORY'S BRIEF IS SAVED INTO ITS SEAT FILE, so the file's cap is the one that holds, and the confirm
        // of a publish carries the line that it is the whole definition that goes live.
        view.LimitText = "The brief is saved into the seat's file, and a file in a definition takes at most " +
                         $"{Size(DefinitionPaths.MaxFileBytes)}.";
        view.SavedText = published is null
            ? "Saved as a new draft of the definition. Nothing in it is published for this seat yet."
            : $"Saved as a new draft of the definition. Talks and runs of this seat are still given the brief in definition version {published.Version} until a person publishes.";
        view.Publish.ConfirmLines.Add(definition);
        return view;
    }

    // ---------- the history ----------

    /// <summary>
    /// One page of the seat's history, newest first. <paramref name="firstPage"/> is false when the page was asked
    /// for with a starting version, so the screen can offer the way back to the newest versions.
    /// </summary>
    /// <param name="definition">For a DEFINED factory, <see cref="DefinitionLine"/>: the page then holds the definition
    /// versions in which the seat's brief changed, and <paramref name="publishedDefinition"/> is the definition version
    /// that is published. Null otherwise.</param>
    /// <param name="publishedDefinition">The published definition version, for a defined factory.</param>
    public static FactoryBriefHistoryViewDto History(RegisteredFactoryDto factory, RegisteredFactorySeatDto seat,
        FactoryBriefStanding brief, FactoryBriefHistoryPage page, bool firstPage, TimeZoneInfo zone, DateTime nowUtc,
        string? definition = null, int? publishedDefinition = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(seat);
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(zone);
        var name = SeatName(factory, seat);
        var published = brief.Published?.Version;
        var newest = brief.Newest?.Version ?? 0;

        var view = new FactoryBriefHistoryViewDto
        {
            Heading = $"History of the brief of {name}",
            SummaryText = page.Total == 0
                ? "No version yet."
                : $"{FactoriesScreenFold.Count(page.Total, "version")}. " +
                  (published is { } p ? $"Version {p} is published." : "None is published."),
            PageText = page.Versions.Count == 0
                ? null
                : page.Versions.Count == 1
                    ? $"Version {page.Versions[0].Version} of {page.Total}."
                    : $"Versions {page.Versions[0].Version} to {page.Versions[^1].Version} of {page.Total}.",
            WaitingText = Waiting(brief),
            Columns = new List<string> { "Version", "State", "Written by", "When", "Size" },
            Rows = page.Versions.Select(v => Row(name, v, published, newest, zone, nowUtc)).ToList(),
            EmptyText = page.Total == 0
                ? "This seat has no brief on the Gateway, so it has no history."
                : page.Versions.Count == 0 ? "There is no older version." : null,
            NextBefore = page.NextBefore,
            OlderLabel = page.NextBefore is null ? null : "Older versions",
            NewestLabel = firstPage ? null : "Newest versions",
            CloseLabel = "Back to the brief",
        };
        if (definition is null) return view;

        // THE VERSIONS OF A DEFINED FACTORY'S BRIEF ARE THE DEFINITION'S, and only those that changed this brief are
        // listed - so the summary says which definition version is live, and every confirm says the whole definition goes.
        view.SummaryText = page.Total == 0
            ? "No version of the definition holds a brief for this seat."
            : $"{FactoriesScreenFold.Count(page.Total, "version")} of the definition changed this brief. " +
              (publishedDefinition is { } live ? $"Definition version {live} is published." : "None is published.") +
              " " + definition;
        view.EmptyText = page.Total == 0
            ? "No version of the definition holds a brief for this seat, so it has no history."
            : view.EmptyText;
        foreach (var row in view.Rows)
        {
            row.Publish?.ConfirmLines.Add(definition);
            row.BringBack?.ConfirmLines.Add(definition);
        }
        return view;
    }

    private static FactoryBriefHistoryRowDto Row(string name, FactoryBriefVersionSummary v, int? published, int newest,
        TimeZoneInfo zone, DateTime nowUtc)
    {
        var isDraft = v.State == FactoryBriefStates.Draft;
        var (word, tone) = (isDraft, published) switch
        {
            (false, { } p) when p == v.Version => ("Published", FactoryTone.Ok),
            (false, _) => ("Published earlier", FactoryTone.Grey),
            (true, { } p) when v.Version < p => ("Draft, passed over", FactoryTone.Grey),
            _ => ("Draft", FactoryTone.Amber),
        };
        return new FactoryBriefHistoryRowDto
        {
            Version = v.Version,
            Title = $"Version {v.Version}",
            StateWord = word,
            StateTone = tone,
            WhoText = Who(v.AuthorKind, v.AuthorId),
            WhenText = When(v.WrittenAtUtc, zone, nowUtc),
            SizeText = Size(v.TextBytes),
            OpenLabel = "Open",
            // The store's own rules: a draft above the published version is published; anything below it is brought back.
            Publish = isDraft && v.Version > (published ?? 0) ? PublishAction(name, v.Version, published, newest) : null,
            BringBack = published is { } current && v.Version < current ? BringBackAction(name, v.Version, current, newest) : null,
        };
    }

    internal static FactoryBriefActionDto PublishAction(string name, int version, int? published, int newest)
    {
        var lines = new List<string>
        {
            $"Every later talk and every later run of {name} will be given the text of version {version}.",
            published is { } p
                ? $"It replaces version {p}, which is kept. History can bring it back."
                : "It is the first published brief of this seat. Until now its talks and runs were told the path of its brief file.",
        };
        if (version < newest)
            lines.Add(newest - version == 1
                ? $"Version {newest} stays a draft."
                : $"Versions {version + 1} to {newest} stay drafts.");
        return new FactoryBriefActionDto
        {
            Version = version,
            ExpectedVersion = newest,
            Label = "Publish",
            BusyLabel = "Publishing...",
            ConfirmTitle = $"Publish version {version} of the brief of {name}?",
            ConfirmLines = lines,
            ConfirmLabel = "Publish",
            DoneText = $"Version {version} is published. Every later talk and every later run of {name} is given it.",
        };
    }

    internal static FactoryBriefActionDto BringBackAction(string name, int version, int published, int newest)
    {
        var lines = new List<string>
        {
            $"A copy of version {version} becomes version {newest + 1}, and it is published. Every later talk and every later run of {name} will be given that text.",
            $"Nothing is removed. Version {published}, which is published now, and every other version are kept.",
        };
        var waiting = newest - published;
        if (waiting > 0)
            lines.Add(waiting == 1
                ? $"Version {newest}, a draft, is then older than the published version. It is kept and can be brought back."
                : $"The {waiting} drafts above version {published} are then older than the published version. They are kept and each can be brought back.");
        return new FactoryBriefActionDto
        {
            Version = version,
            ExpectedVersion = newest,
            Label = "Bring back",
            BusyLabel = "Bringing it back...",
            ConfirmTitle = $"Bring version {version} of the brief of {name} back?",
            ConfirmLines = lines,
            ConfirmLabel = "Bring it back",
            DoneText = $"Version {version} was brought back as the newest version, and it is published. Every later talk and every later run of {name} is given it.",
        };
    }

    // ---------- one version ----------

    /// <summary>One version, with its text: what Open on a history row shows.</summary>
    /// <param name="definition">For a DEFINED factory, <see cref="DefinitionLine"/>, said after the version's line.
    /// Null otherwise.</param>
    public static FactoryBriefVersionViewDto Version(FactoryBriefVersionEntity version, FactoryBriefStanding brief,
        TimeZoneInfo zone, DateTime nowUtc, string? definition = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(zone);
        var isDraft = version.State == FactoryBriefStates.Draft;
        var state = isDraft
            ? "Draft"
            : brief.Published?.Version == version.Version ? "Published" : "Published earlier";
        var publishedBy = isDraft || version.PublishedAtUtc is not { } at
            ? ""
            : $" Published by {Who(FactoryMemoryAuthorKinds.Person, version.PublishedBy)} {When(at, zone, nowUtc)}.";
        return new FactoryBriefVersionViewDto
        {
            Version = version.Version,
            Title = definition is null ? $"Version {version.Version}" : $"Definition version {version.Version}",
            LineText = $"{state}. Written by {Who(version)} {When(version.WrittenAtUtc, zone, nowUtc)}.{publishedBy} {Size(version.TextBytes)}."
                       + (definition is null ? "" : " " + definition),
            Text = version.Text,
            CloseLabel = "Close",
        };
    }

    // ---------- the words they share ----------

    /// <summary>
    /// How many drafts wait above the published version: with nothing published, every version. Versions are never
    /// removed and each save adds the next number, so this is the count <see cref="FactoryBriefStore"/> refuses a
    /// session's draft on.
    /// </summary>
    internal static int WaitingDrafts(FactoryBriefStanding brief) =>
        (brief.Newest?.Version ?? 0) - (brief.Published?.Version ?? 0);

    /// <summary>
    /// The sentence the owner reads when as many drafts wait as a session may leave, or null below that. Sessions
    /// are being refused, and only a person's publish makes room, so the page that can publish says so.
    /// </summary>
    internal static string? Waiting(FactoryBriefStanding brief)
    {
        var waiting = WaitingDrafts(brief);
        if (waiting < FactoryBriefStore.MaxWaitingDrafts) return null;
        var published = brief.Published?.Version ?? 0;
        return $"{waiting} drafts are waiting that nobody has published (versions {published + 1} to {brief.Newest!.Version}). " +
               $"A session of this factory is refused another draft while {FactoryBriefStore.MaxWaitingDrafts} or more are waiting. " +
               "Publish one to make room. Every version is kept.";
    }

    private static string Replaces(FactoryBriefVersionEntity? published) => published is null
        ? "It is the first published brief of this seat. Until now its talks and runs were told the path of its brief file."
        : $"It replaces version {published.Version}, which is kept. History can bring it back.";

    private static string Who(FactoryBriefVersionEntity v) => Who(v.AuthorKind, v.AuthorId);

    /// <summary>Who wrote or published a version, as the Gateway recorded it. The identifier is never cut short.</summary>
    internal static string Who(string kind, string? id)
    {
        var known = !string.IsNullOrWhiteSpace(id);
        if (kind == FactoryMemoryAuthorKinds.Session) return known ? $"session {id}" : "a session";
        return known ? $"a person ({id})" : "a person";
    }

    private static string When(DateTime utc, TimeZoneInfo zone, DateTime nowUtc) =>
        FactoriesScreenFold.When(utc, zone, nowUtc, capital: false);

    /// <summary>"512 bytes", "1.2 KB".</summary>
    internal static string Size(int bytes) => bytes < 1024
        ? FactoriesScreenFold.Count(bytes, "byte")
        : (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
}
