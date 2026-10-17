using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// A factory that has a definition and was never published, as the screens show it (Factory Definition, phase 6b): its
/// id, the title its newest draft gives it, and what that draft holds. It has no registry entry until its first publish
/// registers it.
/// </summary>
/// <param name="Factory">The factory's id, folded.</param>
/// <param name="Title">The title the newest draft's factory.yaml gives, or the id when that draft no longer reads.</param>
/// <param name="Version">The newest draft's version.</param>
/// <param name="FileCount">How many files the newest draft holds.</param>
/// <param name="SeatCount">How many seats the newest draft defines; 0 when it no longer reads.</param>
/// <param name="Machine">The machine the newest draft runs on, or null when it no longer reads.</param>
public sealed record UnpublishedFactory(string Factory, string Title, int Version, int FileCount, int SeatCount, string? Machine);

/// <summary>
/// NEW FACTORY ON THE FACTORIES SCREEN, FOLDED ONCE (Factory Definition mission, phase 6b; critical rule 7). The owner
/// picks a zip of a definition folder and the Gateway imports it as the first draft of the factory its factory.yaml
/// names, through the same checked import the Definition tab uses. Every label, refusal and sentence of it is composed
/// here, and so is how a factory that is not published yet appears: as its own row under the list, and as a factory page
/// whose only tab with anything in it is the Definition tab.
///
/// Pure: it reads what it is handed and returns finished display objects.
/// </summary>
public static class FactoryNewFold
{
    public const string NewFactoryLabel = "New factory";

    /// <summary>Where the zip is posted.</summary>
    public const string NewFactoryHref = "/gateway/factories/new";

    public const string DraftTitle = "Not published yet";

    /// <summary>New factory: the button and what it does.</summary>
    public static FactoryNewFactoryActionDto NewFactoryAction() => new()
    {
        Label = NewFactoryLabel,
        BusyLabel = "Importing...",
        Href = NewFactoryHref,
        NoteText = "New factory takes a zip of a definition folder. It becomes version 1, a draft, of the factory its " +
                   "factory.yaml names, checked exactly as a save is. Nothing runs until you publish it on its Definition tab.",
    };

    /// <summary>What the newest draft of a factory with nothing published says about it.</summary>
    public static UnpublishedFactory Of(FactoryDefinitionVersion newest)
    {
        ArgumentNullException.ThrowIfNull(newest);
        var read = FactoryDefinitionCheck.Read(newest.Files).Definition;
        var s = newest.Summary;
        return new UnpublishedFactory(s.Factory, string.IsNullOrWhiteSpace(read?.Title) ? s.Factory : read.Title.Trim(),
            s.Version, s.FileCount, read?.Seats.Count ?? 0, read?.Defaults.Machine);
    }

    /// <summary>The rows under the list: one per factory with a draft and nothing published, by title.</summary>
    public static List<FactoryDraftRowDto> DraftRows(IReadOnlyList<UnpublishedFactory> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        return drafts
            .OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Factory, StringComparer.Ordinal)
            .Select(d => new FactoryDraftRowDto
            {
                Id = d.Factory,
                Title = d.Title,
                Text = $"{d.Title} - draft, not published yet",
                LineText = $"{(d.Version == 1 ? "Version 1, a draft" : $"{FactoriesScreenFold.Count(d.Version, "draft")}, the newest version {d.Version}")}, " +
                           $"{FactoriesScreenFold.Count(d.FileCount, "file")}. Open it to read what changes and publish it.",
                Href = FactoryDefinitionTabFold.TabHref(d.Factory),
            })
            .ToList();
    }

    /// <summary>The sentence a tab of a factory that is not published yet shows in place of its content.</summary>
    public static string NotYetText(string title) =>
        $"This starts when {title} is published. Until then nothing runs; its draft is on the Definition tab.";

    /// <summary>
    /// THE PAGE OF A FACTORY THAT IS NOT PUBLISHED YET: the same header and tabs as any factory's page, every tab but
    /// Definition carrying <see cref="NotYetText"/>, and no action - no Talk, no Archive - because nothing of it runs.
    /// </summary>
    public static FactoryPageViewDto DraftPage(UnpublishedFactory d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var notYet = NotYetText(d.Title);
        var tabs = FactoriesScreenFold.PageTabs(d.SeatCount);
        foreach (var tab in tabs)
            if (tab.Key != "definition") tab.NotYetText = notYet;
        return new FactoryPageViewDto
        {
            Id = d.Factory,
            Title = d.Title,
            Crumb = $"Factories / {d.Title}",
            CrumbHref = FactoriesScreenFold.ListHref,
            StatusWord = "DRAFT",
            StatusTone = FactoryTone.Amber,
            StatusReason = "Not published yet",
            StatusLine = $"Its definition is a draft and was never published, so nothing runs. Read it on the Definition tab, " +
                         "then publish it there: the first publish registers the factory.",
            StatusHref = FactoryDefinitionTabFold.TabHref(d.Factory),
            BossText = "no boss yet",
            SeatCountText = $"{FactoriesScreenFold.Count(d.SeatCount, "seat")} in the draft",
            ComputerText = d.Machine is null ? "no machine yet" : $"will run on {d.Machine}",
            Tabs = tabs,
            Goal = new FactoryGoalCardDto { Heading = "Goal", EmptyText = notYet },
            GoalNumber = new FactoryGoalNumberCardDto { Heading = "Goal number", EmptyText = notYet },
            Waiting = new FactoryPageWaitingDto { Heading = "Waiting on you", EmptyText = "Nothing is waiting on you." },
            BossLatest = new FactoryBossLatestDto { Heading = "Latest from the boss", EmptyText = notYet },
            LastTalk = new FactoryLastTalkDto { Heading = "Last talk", Text = notYet },
        };
    }

    // ---------- the refusals ----------

    /// <summary>The id is a factory that is registered and live.</summary>
    public static string AlreadyRegistered(RegisteredFactoryDto f) =>
        $"This folder's factory.yaml names '{f.Factory}', and that factory already exists: {f.Title}. Open it and import " +
        "there, on its Definition tab, to make the folder its next draft. Nothing was imported.";

    /// <summary>The id is a factory that is archived.</summary>
    public static string Archived(RegisteredFactoryDto f) =>
        $"This folder's factory.yaml names '{f.Factory}', and {f.Title} is archived under that id. A new factory needs a " +
        "new id: change factory: in factory.yaml. Or Restore it under Show archived and import on its page. Nothing was imported.";

    /// <summary>The id already has a draft definition and was never published.</summary>
    public static string AlreadyDrafted(UnpublishedFactory d) =>
        $"This folder's factory.yaml names '{d.Factory}', and {d.Title} already has a draft that is not published yet. " +
        $"Open it under {DraftTitle} and import there, on its Definition tab. Nothing was imported.";

    // ---------- the same refusals, for a factory created from the command line (phase 6c) ----------
    // The same three cases as New factory on the Factories screen, said to a session that has no screen: what to run.

    /// <summary>The id is a factory that is registered and live.</summary>
    public static string CreateAlreadyRegistered(RegisteredFactoryDto f) =>
        $"The folder's factory.yaml names '{f.Factory}', and that factory already exists: {f.Title}. Nothing was created. " +
        $"To make the folder its next draft, run: cc-devthrottle factory import <folder>";

    /// <summary>The id is a factory that is archived.</summary>
    public static string CreateArchived(RegisteredFactoryDto f) =>
        $"The folder's factory.yaml names '{f.Factory}', and {f.Title} is archived under that id. Nothing was created. " +
        "A new factory needs a new id: change factory: in factory.yaml. Or the owner restores it under Show archived on " +
        "the Factories screen, and the folder is imported then.";

    /// <summary>The id already has a draft definition and was never published.</summary>
    public static string CreateAlreadyDrafted(UnpublishedFactory d) =>
        $"The folder's factory.yaml names '{d.Factory}', and {d.Title} already has a draft that is not published yet " +
        $"(version {d.Version}). Nothing was created. To make the folder its next draft, run: cc-devthrottle factory " +
        $"import <folder>. To publish the draft it has, run: cc-devthrottle factory definition publish {d.Factory} " +
        $"--version {d.Version}";
}
