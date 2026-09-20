using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// The New Session dialog's repository list, decided as pure functions so the screen itself holds no
/// rules (the one-repository-list mission, phase 6).
///
/// THE ORDER IS THE GATEWAY'S, AND THIS CLASS RENDERS IT RATHER THAN RE-DERIVING IT. Critical Rule 7:
/// the Gateway serves the one list already ordered - most recently used first, never-opened beneath, by
/// name and then by path within a tie - and the desktop is one of three screens showing it. There is
/// deliberately NO comparison on a last-used date anywhere in this file for a list that came from the
/// Gateway. A second opinion about recency, computed here, is the mission's own defect wearing a
/// feature's clothes: it is how three screens on one machine came to show three different lists.
///
/// The one thing the screen still decides for itself is which COLUMN the user asked to sort by, because
/// that is a person pressing a heading, not a client ruling on what the data means. Even then the
/// last-used heading never computes an order: the Gateway's list read top-down is already that order, and
/// read bottom-up is its exact inverse.
/// </summary>
internal static class NewSessionRepositoryList
{
    /// <summary>The heading tags the dialog's sortable column headers carry.</summary>
    internal const string NameColumn = "Name";

    internal const string PathColumn = "Path";

    internal const string LastUsedColumn = "LastUsed";

    /// <summary>
    /// The Gateway's rows, as the dialog's row model, IN THE ORDER THEY ARRIVED. Nothing is sorted,
    /// filtered, de-duplicated or re-named here.
    ///
    /// Two verdicts ride across unchanged rather than being recomputed:
    /// <see cref="KnownRepositoryDto.NeverOpened"/> becomes <see cref="RepositoryConfig.IsDiscovered"/>
    /// (the Gateway stamped it from the last-used time in one place, so the two can never disagree), and
    /// every row is marked <see cref="RepositoryConfig.IsFromTheGatewayList"/> so the Remove button - which
    /// only ever removed from this Director's own registry - is not offered for a catalogue it cannot
    /// change.
    ///
    /// A row whose name the Gateway does not hold keeps an empty name. The folder name is NOT computed
    /// here: the name is a fact about the repository and the Gateway owns it, so a blank one is a gap in
    /// the catalogue to be closed there, not papered over on one of the three screens that would then
    /// disagree with the other two.
    /// </summary>
    /// <param name="served">The rows exactly as the Gateway served them.</param>
    public static List<RepositoryConfig> FromGateway(IReadOnlyList<KnownRepositoryDto> served)
    {
        ArgumentNullException.ThrowIfNull(served);

        var rows = new List<RepositoryConfig>(served.Count);
        foreach (var repository in served)
        {
            rows.Add(new RepositoryConfig
            {
                Name = repository.Name,
                Path = repository.Path,
                LastUsed = repository.LastUsed,
                IsDiscovered = repository.NeverOpened,
                IsFromTheGatewayList = true,
            });
        }

        return rows;
    }

    /// <summary>
    /// What to call a repository the root-folder scan found, on the machine's own fallback list: the name
    /// the scan computed, or the folder's own name when it computed none.
    ///
    /// The folder name comes from <see cref="RepositoryPaths.FolderName"/> and NOT from
    /// <c>Path.GetFileName</c>, which honours only the separator of the host it runs on - handed
    /// <c>D:\ReposFred\devthrottle</c> on macOS it finds no separator and puts the whole path in the Name
    /// column. This is the fifth-sighting defect this mission keeps meeting: code deciding a path's shape
    /// from the machine running it instead of from the path.
    /// </summary>
    /// <param name="scannedName">The name the repository scan computed, if it computed one.</param>
    /// <param name="path">The repository's path, in whatever spelling it was written.</param>
    public static string NameForScannedRepository(string? scannedName, string path) =>
        string.IsNullOrWhiteSpace(scannedName) ? RepositoryPaths.FolderName(path) : scannedName;

    /// <summary>
    /// The list in the order the screen shows it, built from the source list every time so the source's
    /// own order survives being sorted away and back again.
    ///
    /// <paramref name="sourceIsTheGatewaysOrder"/> is what makes the last-used heading honest. When the
    /// rows came from the Gateway they are ALREADY in the last-used order, so that heading hands them
    /// back untouched - and reversed for the ascending press, which is that same ruling read bottom-up.
    /// When the rows are this machine's own local list there is no served order to read, so the local
    /// last-used order is computed here exactly as it always was.
    /// </summary>
    /// <param name="source">The list as it was built or served, never re-ordered in place.</param>
    /// <param name="sourceIsTheGatewaysOrder">True when <paramref name="source"/> is the Gateway's list.</param>
    /// <param name="column">Which heading the user last pressed.</param>
    /// <param name="ascending">Whether that heading is showing its ascending arrow.</param>
    public static List<RepositoryConfig> Order(
        IReadOnlyList<RepositoryConfig> source, bool sourceIsTheGatewaysOrder, string column, bool ascending)
    {
        ArgumentNullException.ThrowIfNull(source);

        switch (column)
        {
            case NameColumn:
                return ascending
                    ? source.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList()
                    : source.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

            case PathColumn:
                return ascending
                    ? source.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList()
                    : source.OrderByDescending(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();

            default:
                if (sourceIsTheGatewaysOrder)
                    return ascending ? source.Reverse().ToList() : source.ToList();

                return ascending
                    ? source.OrderBy(r => r.LastUsed ?? DateTime.MinValue).ToList()
                    : source.OrderByDescending(r => r.LastUsed ?? DateTime.MinValue).ToList();
        }
    }

    /// <summary>
    /// What the screen says while it is waiting for the Gateway. The dialog shows the machine's own list
    /// immediately so it is never blank, and this line says that answer is not in yet - the alternative,
    /// showing one list and silently swapping it for another, is the failure the owner named.
    /// </summary>
    public const string CheckingNotice = "Checking the Gateway for the one repository list...";

    /// <summary>
    /// The sentence shown when the screen is on its local fallback, or null when the Gateway served the
    /// list and there is nothing to say.
    ///
    /// EVERY ONE OF THESE IS A TERMINATED SENTENCE, and any words the Gateway supplied are terminated
    /// before the advice that follows them. A reason run together with advice reads as one broken
    /// half-sentence - the Cockpit shipped "Director not connected Try again." doing exactly that.
    /// </summary>
    /// <param name="outcome">What happened when the Gateway was asked.</param>
    /// <param name="reason">The Gateway's own words, or the transport's, when there are any.</param>
    public static string? FallbackNotice(KnownRepositoryListOutcome outcome, string? reason)
    {
        const string local = "This is this machine's own list, and its order may differ from the Cockpit and the phone.";

        return outcome switch
        {
            KnownRepositoryListOutcome.Served => null,
            KnownRepositoryListOutcome.NotConfigured => "No Gateway is connected. " + local,
            KnownRepositoryListOutcome.Unreachable => "The Gateway could not be reached. " + local,
            KnownRepositoryListOutcome.Refused => string.IsNullOrWhiteSpace(reason)
                ? "The Gateway could not give the repository list. " + local
                : "The Gateway could not give the repository list: " + Terminated(reason) + " " + local,
            _ => "The Gateway could not give the repository list. " + local,
        };
    }

    /// <summary>
    /// One supplied phrase, ended. Words that arrive from somewhere else rarely end themselves, and a
    /// phrase glued to the next sentence is the wording defect this mission already shipped once.
    /// </summary>
    private static string Terminated(string words)
    {
        var trimmed = words.Trim();
        if (trimmed.Length == 0) return "";
        return trimmed[^1] is '.' or '!' or '?' ? trimmed : trimmed + ".";
    }
}
