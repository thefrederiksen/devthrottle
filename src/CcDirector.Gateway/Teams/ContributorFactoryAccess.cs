using CcDirector.Gateway.Contracts;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Factory.Registry;

namespace CcDirector.Gateway.Teams;

/// <summary>Why <see cref="ContributorFactoryAccess.Check"/> refused. Logged; never shown - every refusal reads the same
/// to the person (<see cref="ContributorFactoryAccess.NotSentToYou"/>).</summary>
public enum ContributorFactoryRefusal
{
    /// <summary>The team does not exist.</summary>
    NoSuchTeam,

    /// <summary>The person is not a member of the team - never one, or removed.</summary>
    NotAMember,

    /// <summary>The person is a member, but not a contributor: the Owner and the team's own people open the factory
    /// itself, never its customer page.</summary>
    NotAContributor,

    /// <summary>No factory of that name is registered in the team.</summary>
    NoSuchFactory,

    /// <summary>The factory is archived: it has left every list, the customer page included.</summary>
    Archived,

    /// <summary>The factory exists in the team, but it was not sent to this person by name.</summary>
    NotSentToThem,
}

/// <summary>The check's answer. On a yes, the factory and the team it was read from; on a no, why.</summary>
public sealed record ContributorFactoryVerdict(bool Allowed, ContributorFactoryRefusal? Refusal, RegisteredFactoryDto? Factory,
    string? TeamName, FactoryShare? Share)
{
    internal static ContributorFactoryVerdict No(ContributorFactoryRefusal why) => new(false, why, null, null, null);
}

/// <summary>
/// THE ONE CHECK: MAY THIS PERSON SEE THIS FACTORY AS A CUSTOMER (Team implementation piece 4.1, devthrottle_internal#2798;
/// security review Risk 3 and M2). Every read that shows a contributor a factory asks here and nowhere else - the
/// contributor's Factories list, the customer page, and the "Watch your factory" view for a contributor.
///
/// Yes only when ALL of these hold, read fresh on every call:
/// <list type="number">
/// <item>The team is live: it exists. (A team has no suspended or deleted state on main; when one is added, it is added
/// here and every caller follows.)</item>
/// <item>The person is a member of the team NOW, with the contributor role. Removal takes effect at once; the Owner and
/// the team's own people are refused, because the customer page is the contributor's view, not a second owner view.</item>
/// <item>The factory is registered in THAT team's tenant and is not archived. A factory is looked up only inside the
/// team the route names, never by a global id, so another team's factory of the same name is never found (M2).</item>
/// <item>The factory was sent to the person by name (<see cref="FactoryContributors"/>).</item>
/// </list>
///
/// It does not ask the team gate's role table: the gate has already decided the route (every role may read what was
/// sent to them), and this narrows to the item. Two locks, never one.
/// </summary>
public sealed class ContributorFactoryAccess
{
    /// <summary>The one sentence every refusal reads, whichever condition failed - so an answer never tells a person
    /// whether a factory of that name exists, in which team, or that they were once sent it.</summary>
    public const string NotSentToYou = "There is no factory sent to you with that name.";

    private readonly TeamRegistry _teams;
    private readonly FactoryRegistryStore _factories;
    private readonly FactoryContributors _shares;

    public ContributorFactoryAccess(TeamRegistry teams, FactoryRegistryStore factories, FactoryContributors shares)
    {
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));
        _shares = shares ?? throw new ArgumentNullException(nameof(shares));
    }

    /// <summary>The check, for one factory. <paramref name="personSubject"/> is the caller the server identified - never
    /// a value from a request body.</summary>
    public ContributorFactoryVerdict Check(string teamId, string personSubject, string factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personSubject);
        if (string.IsNullOrWhiteSpace(teamId))
            return Refuse(teamId, factory, ContributorFactoryRefusal.NoSuchTeam);

        var team = new TenantId(teamId);
        var summary = _teams.ListTeamsFor(personSubject).FirstOrDefault(t => string.Equals(t.TeamId, teamId, StringComparison.Ordinal));
        if (summary is null)
            return Refuse(teamId, factory, _teams.IsTeam(team) ? ContributorFactoryRefusal.NotAMember : ContributorFactoryRefusal.NoSuchTeam);
        if (summary.Role != TeamRole.Collaborator)
            return Refuse(teamId, factory, ContributorFactoryRefusal.NotAContributor);

        if (string.IsNullOrWhiteSpace(factory) || _factories.Find(team, factory.Trim()) is not { } registered)
            return Refuse(teamId, factory, ContributorFactoryRefusal.NoSuchFactory);
        if (registered.ArchivedAtUtc is not null)
            return Refuse(teamId, factory, ContributorFactoryRefusal.Archived);
        if (_shares.Find(team, registered.Factory, personSubject) is not { } share)
            return Refuse(teamId, factory, ContributorFactoryRefusal.NotSentToThem);

        FileLog.Write($"[ContributorFactoryAccess] Check: team {team.ToLogString()} factory={registered.Factory} - ALLOWED");
        return new ContributorFactoryVerdict(true, null, registered, summary.Name, share);
    }

    /// <summary>Every factory the person may see as a customer in the team, in the order they were sent - each one passed
    /// through <see cref="Check"/>, so the list can never hold a factory the page would refuse.</summary>
    public IReadOnlyList<ContributorFactoryVerdict> FactoriesFor(string teamId, string personSubject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(personSubject);
        var allowed = _shares.SentTo(new TenantId(teamId), personSubject)
            .Select(s => Check(teamId, personSubject, s.Factory))
            .Where(v => v.Allowed)
            .ToList();
        FileLog.Write($"[ContributorFactoryAccess] FactoriesFor: team {new TenantId(teamId).ToLogString()} count={allowed.Count}");
        return allowed;
    }

    private static ContributorFactoryVerdict Refuse(string? teamId, string? factory, ContributorFactoryRefusal why)
    {
        var where = string.IsNullOrWhiteSpace(teamId) ? "<none>" : new TenantId(teamId).ToLogString();
        FileLog.Write($"[ContributorFactoryAccess] Check: team {where} factory={factory} - REFUSED ({why})");
        return ContributorFactoryVerdict.No(why);
    }
}
