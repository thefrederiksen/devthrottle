using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Talk;

namespace CcDirector.Gateway.Factory.Briefs;

/// <summary>
/// A SEAT'S BRIEF, FOR A DEFINED FACTORY, IS PART OF ITS DEFINITION (Factory Definition, phase 4, design section 9;
/// mission 5.3 "Briefs (A1)"). A factory with a published definition holds each seat's brief as the <c>brief:</c> value
/// of <c>seats/&lt;seat&gt;.yaml</c>, so the brief a talk or a run is given, the brief's history and a saved brief all
/// come from and go to the definition. The brief store is not read for such a factory at all.
///
/// AND ONLY FOR A DEFINED FACTORY. A factory with no published definition - none at all, or only drafts - takes
/// exactly the paths it took before (goal 14): <see cref="GivenTo"/> hands it to <see cref="FactoryBriefStore.GivenTo"/>
/// unchanged, and every route asks <see cref="IsDefined"/> first and goes on as before when the answer is no.
///
/// A BRIEF'S VERSION IS THE DEFINITION'S VERSION. There is no separate brief number for a defined factory: version 7
/// of a seat's brief is the seat's brief in definition version 7. So every place a brief version is recorded or shown
/// also says which kind it is (<see cref="FactoryBriefVersionKinds"/>), and a reader never takes one for the other.
///
/// THE BRIEF PANEL'S WORDS ARE REUSED. A version of the definition is handed to the panel's fold as the brief version
/// it is for this seat (<see cref="AsBriefVersion"/>): the same number, state, author and dates, and the seat's brief
/// as its text. The fold then adds the one line that says the brief is part of the definition.
/// </summary>
public sealed class FactoryDefinitionBriefs
{
    private readonly FactoryDefinitionStore _definitions;
    private readonly FactoryBriefStore _briefs;

    public FactoryDefinitionBriefs(FactoryDefinitionStore definitions, FactoryBriefStore briefs)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _briefs = briefs ?? throw new ArgumentNullException(nameof(briefs));
    }

    /// <summary>The store a defined factory's briefs live in.</summary>
    public FactoryDefinitionStore Definitions => _definitions;

    /// <summary>The path of a seat's file in a definition, which is where its brief is.</summary>
    public static string SeatPath(string seat) => $"seats/{seat}.yaml";

    /// <summary>
    /// IS THIS FACTORY DEFINED: does it have a PUBLISHED definition version (design section 1). A factory with only
    /// drafts is not. The one place this module asks it.
    /// </summary>
    public bool IsDefined(TenantId tenant, string factory) => _definitions.Standing(tenant, factory).Published is not null;

    /// <summary>
    /// The brief a talk or a scheduled run of this seat is given. For a defined factory, the seat's <c>brief:</c> in
    /// the PUBLISHED definition, recorded as a definition version; for any other factory, exactly what
    /// <see cref="FactoryBriefStore.GivenTo"/> gives. A defined factory whose published definition has no file for the
    /// seat cannot give it a brief, and a talk or a run of it is never told the path of a file instead: that is
    /// refused here, in words, so the run records why it did not start.
    /// </summary>
    public FactoryGivenBrief? GivenTo(TenantId tenant, string factory, string seat)
    {
        var standing = _definitions.Standing(tenant, factory);
        if (standing.Published is not { } published)
            return _briefs.GivenTo(tenant, factory, seat);

        var brief = InVersion(tenant, factory, seat, published.Version);
        if (brief is null)
        {
            FileLog.Write($"[FactoryDefinitionBriefs] GivenTo {factory}/{seat}: REFUSED - definition version {published.Version} holds no {SeatPath(seat)}");
            throw new InvalidOperationException(NotInVersion(factory, seat, published.Version, "published"));
        }
        FileLog.Write($"[FactoryDefinitionBriefs] GivenTo {factory}/{seat}: definition version {published.Version}");
        return new FactoryGivenBrief(published.Version, brief.Text, FactoryBriefVersionKinds.Definition);
    }

    /// <summary>
    /// The seat's brief in one definition version, or null when that version does not exist or holds no file for the
    /// seat, or the file's <c>brief:</c> cannot be read.
    /// </summary>
    public DefinedSeatBrief? InVersion(TenantId tenant, string factory, string seat, int version)
    {
        if (!FactoryNames.TrySeat(seat, out var folded, out _)) return null;
        if (_definitions.Find(tenant, factory, version) is not { } found) return null;
        if (!found.Files.TryGetValue(SeatPath(folded), out var file)) return null;
        return SeatBriefText.Read(SeatPath(folded), file) is { } text ? new DefinedSeatBrief(found.Summary, folded, text) : null;
    }

    /// <summary>
    /// How the seat's brief stands, for the brief panel: the seat's brief in the NEWEST definition version and in the
    /// PUBLISHED one, each as the brief version it is (<see cref="AsBriefVersion"/>). A version that holds no file for
    /// the seat stands as none.
    /// </summary>
    public FactoryBriefStanding Standing(TenantId tenant, string factory, string seat)
    {
        var standing = _definitions.Standing(tenant, factory);
        var newest = standing.Newest is { } n ? InVersion(tenant, factory, seat, n.Version) : null;
        var published = standing.Published is { } p ? InVersion(tenant, factory, seat, p.Version) : null;
        return new FactoryBriefStanding(newest?.AsBriefVersion(), published?.AsBriefVersion());
    }

    /// <summary>
    /// ONE PAGE of the seat's brief history, newest first: the definition versions in which this seat's brief text
    /// CHANGED - the first version that holds it, and every version whose brief differs from the version before. A
    /// version that changed only other files is not a version of this brief. Paged exactly as the brief store's
    /// history is: <paramref name="before"/> is the version the page starts below, and the page says where the next one
    /// starts.
    /// </summary>
    public FactoryBriefHistoryPage History(TenantId tenant, string factory, string seat, int? before, int limit)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), limit, "a page of the history holds at least one version");
        if (before is < 1) throw new ArgumentOutOfRangeException(nameof(before), before, "versions count from 1");
        if (!FactoryNames.TrySeat(seat, out var folded, out _)) return FactoryBriefHistoryPage.Empty;

        var path = SeatPath(folded);
        var changed = new List<FactoryBriefVersionSummary>();
        string? previous = null;
        foreach (var v in _definitions.FileInEveryVersion(tenant, factory, path))
        {
            var text = v.Text is null ? null : SeatBriefText.Read(path, v.Text);
            if (text is not null && !string.Equals(text, previous, StringComparison.Ordinal))
                changed.Add(SummaryOf(v.Summary, folded, text));
            previous = text;
        }
        changed.Reverse();

        var below = before is { } b ? changed.Where(v => v.Version < b).ToList() : changed;
        var page = below.Take(limit).ToList();
        var next = below.Count > limit ? page[^1].Version : (int?)null;
        FileLog.Write($"[FactoryDefinitionBriefs] History {factory}/{folded}: {changed.Count} versions changed the brief, page of {page.Count}");
        return new FactoryBriefHistoryPage(page, changed.Count, next);
    }

    /// <summary>
    /// The newest definition version with this seat's brief replaced by <paramref name="text"/> as a text edit
    /// (<see cref="SeatBriefText.Replace"/>), every other byte of every file kept. Not saved: the caller runs the
    /// whole-definition check and then saves it as a draft, stating the version its writer read.
    /// </summary>
    public DefinedBriefDraft DraftWith(TenantId tenant, string factory, string seat, string text)
    {
        if (!FactoryNames.TrySeat(seat, out var folded, out var seatRefusal))
            return DefinedBriefDraft.Refused(FactoryBriefOutcome.BadSeat, seatRefusal!, null);
        var standing = _definitions.Standing(tenant, factory);
        var newest = _definitions.Find(tenant, factory, standing.Newest!.Version)!;
        var path = SeatPath(folded);
        if (!newest.Files.TryGetValue(path, out var file))
            return DefinedBriefDraft.Refused(FactoryBriefOutcome.SeatNotRegistered,
                NotInVersion(factory, folded, newest.Summary.Version, "newest"), newest.Summary);

        var edit = SeatBriefText.Replace(path, file, text);
        if (!edit.Ok)
        {
            FileLog.Write($"[FactoryDefinitionBriefs] DraftWith {factory}/{folded}: REFUSED - {edit.Refusal}");
            return DefinedBriefDraft.Refused(string.IsNullOrWhiteSpace(text) ? FactoryBriefOutcome.Empty : FactoryBriefOutcome.NotEditable,
                edit.Refusal!, newest.Summary);
        }
        var content = new Dictionary<string, string>(newest.Files, StringComparer.Ordinal) { [path] = edit.Text! };
        return new DefinedBriefDraft(content, newest.Summary, folded, null, null);
    }

    /// <summary>What a definition version holds no file for this seat says.</summary>
    public static string NotInVersion(string factory, string seat, int version, string which) =>
        $"'{seat}' has no seat file in the {which} definition of {factory} (version {version}), so it has no brief there. " +
        $"A seat of a defined factory is added to its definition as {SeatPath(seat)}.";

    private static FactoryBriefVersionSummary SummaryOf(FactoryDefinitionVersionSummary v, string seat, string text) =>
        new(v.Factory, seat, v.Version, v.State, v.AuthorKind, v.AuthorId, v.WrittenAtUtc, v.PublishedAtUtc,
            v.PublishedBy, System.Text.Encoding.UTF8.GetByteCount(text));
}

/// <summary>A seat's brief in one definition version: the version's summary, the seat, and the <c>brief:</c> text.</summary>
public sealed record DefinedSeatBrief(FactoryDefinitionVersionSummary Version, string Seat, string Text)
{
    /// <summary>
    /// This definition version as the brief version it is for the seat: the same number, state, author and dates, and
    /// the seat's brief as its text. Never stored - the brief store holds nothing for a defined factory - only handed
    /// to the brief panel's fold and to the brief routes' answer.
    /// </summary>
    public FactoryBriefVersionEntity AsBriefVersion() => new()
    {
        Factory = Version.Factory,
        Seat = Seat,
        Version = Version.Version,
        Text = Text,
        TextBytes = System.Text.Encoding.UTF8.GetByteCount(Text),
        State = Version.State,
        AuthorKind = Version.AuthorKind,
        AuthorId = Version.AuthorId,
        WrittenAtUtc = Version.WrittenAtUtc,
        PublishedAtUtc = Version.PublishedAtUtc,
        PublishedBy = Version.PublishedBy,
    };
}

/// <summary>
/// The newest definition with one seat's brief replaced, ready for the check and the save, or why it could not be
/// made. <see cref="Newest"/> is the version it was made from, also on a refusal where there is one.
/// </summary>
public sealed record DefinedBriefDraft(IReadOnlyDictionary<string, string>? Content, FactoryDefinitionVersionSummary? Newest,
    string? Seat, FactoryBriefOutcome? Outcome, string? Refusal)
{
    public bool Ok => Content is not null;

    public static DefinedBriefDraft Refused(FactoryBriefOutcome outcome, string refusal, FactoryDefinitionVersionSummary? newest)
        => new(null, newest, null, outcome, refusal);
}
