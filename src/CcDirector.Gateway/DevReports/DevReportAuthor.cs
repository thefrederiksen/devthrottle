using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Teams;

namespace CcDirector.Gateway.DevReports;

/// <summary>
/// WHO WROTE A DEV REPORT, asked once, when it is published (devthrottle_internal#2309). In a personal account's tenant
/// the account is the owner and nothing is recorded. In a TEAM's tenant a session belongs to one person - the person
/// its Director's key was issued to - and that person is the report's author: the one who may send it to a member of
/// the team, and the one a member's comment on it goes to.
///
/// The answer comes from THE ONE RESOLVER (<see cref="TeamCallerOwnership.OwnerOf"/>, devthrottle_internal#2311): the
/// calling session's Director, the device key that Director registered with in this tenant, and that key's person, read
/// live. Never a second copy of that question, and never guessed: in a team's tenant a session whose person cannot be
/// named has no author, and the publish is refused.
/// </summary>
internal sealed class DevReportAuthor
{
    /// <summary>What a session in a team's tenant is told when the person behind it cannot be named.</summary>
    public const string UnknownAuthorRefusal =
        "DevThrottle cannot tell which member of the team this session belongs to, so it will not publish the report " +
        "inside the team. Nothing was published.";

    private readonly TeamRegistry _teams;
    private readonly TeamCallerOwnership _ownership;

    public DevReportAuthor(TeamRegistry teams, TeamCallerOwnership ownership)
    {
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
    }

    /// <summary>
    /// The author of a report a session of <paramref name="directorId"/> publishes in <paramref name="tenant"/>.
    /// </summary>
    public DevReportAuthorAnswer Resolve(TenantId tenant, string directorId)
    {
        if (!_teams.IsTeam(tenant))
            return DevReportAuthorAnswer.PersonalAccount;

        var subject = _ownership.OwnerOf(tenant, directorId);
        if (string.IsNullOrWhiteSpace(subject))
        {
            FileLog.Write($"[DevReportAuthor] Resolve: tenant {tenant.ToLogString()} director={directorId} - a team's session with no person behind it, REFUSED");
            return DevReportAuthorAnswer.Unknown;
        }

        FileLog.Write($"[DevReportAuthor] Resolve: tenant {tenant.ToLogString()} director={directorId} - the author is the Director's person");
        return new DevReportAuthorAnswer(true, subject);
    }

    /// <summary>
    /// THE ONE WAY a session's report is written: under the author <see cref="Resolve"/> named for it. The publish route
    /// resolves before it reads the body and writes through here after, so the person recorded is the one resolved.
    /// </summary>
    /// <exception cref="InvalidOperationException">The answer is a refusal; the route answers it before reaching here.</exception>
    public static (DevReportEntity Report, bool Created) PublishAs(DevReportStore store, DevReportAuthorAnswer author,
        TenantId tenant, string sessionId, string key, string html, string status, string title, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(author);
        if (author.Refused)
            throw new InvalidOperationException("A refused author publishes nothing; the route answers the refusal first.");
        return store.Publish(tenant, sessionId, key, html, status, title, nowUtc, author.Subject);
    }
}

/// <summary>The author of a report about to be published. <see cref="InTeam"/> false: a personal account, nothing to
/// record. <see cref="InTeam"/> true with a null <see cref="Subject"/>: a team's session whose person is unknown.</summary>
internal sealed record DevReportAuthorAnswer(bool InTeam, string? Subject)
{
    public static readonly DevReportAuthorAnswer PersonalAccount = new(false, null);
    public static readonly DevReportAuthorAnswer Unknown = new(true, null);

    /// <summary>Whether the publish must be refused.</summary>
    public bool Refused => InTeam && Subject is null;
}
