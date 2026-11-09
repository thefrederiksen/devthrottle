using CcDirector.Core.Utilities;
using CcDirector.Gateway.Factory;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// WHAT IS A CONTRIBUTOR'S OWN IN THE WATCH VIEW, version one's rule (A) ("Watch your factory", piece 4). Decided here
/// and nowhere else; the watch route builds the fold's scope only through <see cref="For"/>.
///
/// Rule (A): the whole factory is the contributor's own only when the factory was sent to exactly ONE person and that
/// person is the caller. A factory sent to two people serves more than one customer, and version one cannot yet tell
/// whose request a row was done for - so nothing is anyone's, and every step is a plain crate with no words. The fold
/// fails safe the same way (<see cref="FactoryWatchContributorScope.IsMine"/>); this decides the one flag it reads.
/// </summary>
internal static class ContributorWatchScope
{
    /// <summary>True only when <paramref name="contributorsOfFactory"/> (every share of the factory, from
    /// <see cref="FactoryContributors.ContributorsOf"/>) holds exactly one row, and it names <paramref name="personSubject"/>.</summary>
    public static bool IsWholeFactory(IReadOnlyList<FactoryShare> contributorsOfFactory, string personSubject)
    {
        ArgumentNullException.ThrowIfNull(contributorsOfFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(personSubject);
        return contributorsOfFactory.Count == 1
               && string.Equals(contributorsOfFactory[0].ContributorSubject, personSubject, StringComparison.Ordinal);
    }

    /// <summary>The fold's contributor scope: rule (A) above, and the team account's machine names its words are checked
    /// against.</summary>
    public static FactoryWatchContributorScope For(IReadOnlyList<FactoryShare> contributorsOfFactory, string personSubject,
        IReadOnlyList<string> machineNames)
    {
        ArgumentNullException.ThrowIfNull(machineNames);
        var whole = IsWholeFactory(contributorsOfFactory, personSubject);
        FileLog.Write($"[ContributorWatchScope] For: contributors={contributorsOfFactory.Count}, wholeFactory={whole}, machines={machineNames.Count}");
        return new FactoryWatchContributorScope(whole, machineNames);
    }
}
