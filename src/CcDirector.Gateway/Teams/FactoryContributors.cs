using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams;

/// <summary>One factory sent to one contributor, as the store reads it back.</summary>
/// <param name="Factory">The registered factory id.</param>
/// <param name="ContributorSubject">The contributor's account subject. Never logged.</param>
/// <param name="SharedBySubject">Who sent it. Never logged.</param>
/// <param name="SharedAtUtc">When it was sent.</param>
public sealed record FactoryShare(string Factory, string ContributorSubject, string SharedBySubject, DateTime SharedAtUtc);

/// <summary>
/// THE RECORD OF WHICH FACTORY WAS SENT BY NAME TO WHICH CONTRIBUTOR (<c>factory_contributors</c>, Team implementation
/// piece 4.1). Plain reads and writes; it decides nothing. Who may send a factory is the team gate's
/// (<see cref="TeamEndpointRules"/>), whether the person is a contributor is <see cref="FactoryShareEndpoints"/>', and
/// whether a person may SEE a factory as a customer is <see cref="ContributorFactoryAccess"/>'s alone - a row here is one
/// of its conditions, never the whole answer.
/// </summary>
public sealed class FactoryContributors
{
    private readonly GatewayDatabase _db;

    public FactoryContributors(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Send <paramref name="factory"/> to the contributor. Sending it again keeps the first row: true when a row
    /// was added, false when it was already sent.</summary>
    public bool Share(TenantId team, string factory, string contributorSubject, string sharedBySubject, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(contributorSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedBySubject);

        using var ctx = _db.CreateContext(team);
        if (ctx.FactoryContributors.Any(r => r.Factory == factory && r.ContributorSubject == contributorSubject))
        {
            FileLog.Write($"[FactoryContributors] Share: team {team.ToLogString()} factory={factory} - already sent to this contributor");
            return false;
        }

        ctx.FactoryContributors.Add(new FactoryContributorEntity
        {
            TenantId = team.Value,
            Factory = factory,
            ContributorSubject = contributorSubject,
            SharedBySubject = sharedBySubject,
            SharedAtUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
        });
        try
        {
            ctx.SaveChanges();
        }
        catch (DbUpdateException ex)
        {
            // Two sends at once collide on the unique index: the other one recorded it, which is the same outcome.
            FileLog.Write($"[FactoryContributors] Share: team {team.ToLogString()} factory={factory} - lost a race with another send ({ex.InnerException?.Message ?? ex.Message}); it is recorded");
            return false;
        }
        FileLog.Write($"[FactoryContributors] Share: team {team.ToLogString()} factory={factory} - sent");
        return true;
    }

    /// <summary>Stop showing <paramref name="factory"/> to the contributor. True when a row was removed.</summary>
    public bool Unshare(TenantId team, string factory, string contributorSubject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(contributorSubject);
        using var ctx = _db.CreateContext(team);
        var removed = ctx.FactoryContributors
            .Where(r => r.Factory == factory && r.ContributorSubject == contributorSubject)
            .ExecuteDelete();
        FileLog.Write($"[FactoryContributors] Unshare: team {team.ToLogString()} factory={factory} removed={removed}");
        return removed > 0;
    }

    /// <summary>Every factory sent to the contributor in the team, oldest send first.</summary>
    public IReadOnlyList<FactoryShare> SentTo(TenantId team, string contributorSubject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contributorSubject);
        using var ctx = _db.CreateContext(team);
        var rows = ctx.FactoryContributors.AsNoTracking()
            .Where(r => r.ContributorSubject == contributorSubject)
            .ToList()
            .OrderBy(r => r.SharedAtUtc)
            .ThenBy(r => r.Factory, StringComparer.Ordinal)
            .Select(Share)
            .ToList();
        FileLog.Write($"[FactoryContributors] SentTo: team {team.ToLogString()} count={rows.Count}");
        return rows;
    }

    /// <summary>The row for one factory and one contributor, or null when it was not sent to them.</summary>
    public FactoryShare? Find(TenantId team, string factory, string contributorSubject)
    {
        if (string.IsNullOrWhiteSpace(factory) || string.IsNullOrWhiteSpace(contributorSubject))
            return null;
        using var ctx = _db.CreateContext(team);
        var row = ctx.FactoryContributors.AsNoTracking()
            .FirstOrDefault(r => r.Factory == factory && r.ContributorSubject == contributorSubject);
        return row is null ? null : Share(row);
    }

    /// <summary>Every contributor a factory was sent to, oldest send first - the Owner's Share control.</summary>
    public IReadOnlyList<FactoryShare> ContributorsOf(TenantId team, string factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factory);
        using var ctx = _db.CreateContext(team);
        return ctx.FactoryContributors.AsNoTracking()
            .Where(r => r.Factory == factory)
            .ToList()
            .OrderBy(r => r.SharedAtUtc)
            .Select(Share)
            .ToList();
    }

    /// <summary>Remove every factory sent to the person in the team - what removing them from the team clears. The number
    /// of rows removed.</summary>
    public int ClearFor(TenantId team, string contributorSubject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contributorSubject);
        using var ctx = _db.CreateContext(team);
        var removed = ctx.FactoryContributors.Where(r => r.ContributorSubject == contributorSubject).ExecuteDelete();
        FileLog.Write($"[FactoryContributors] ClearFor: team {team.ToLogString()} removed={removed}");
        return removed;
    }

    private static FactoryShare Share(FactoryContributorEntity r) =>
        new(r.Factory, r.ContributorSubject, r.SharedBySubject, DateTime.SpecifyKind(r.SharedAtUtc, DateTimeKind.Utc));
}
