using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// Teams and their members (Teams, first version - devthrottle_internal#2300). A team IS a tenant: its id is the
/// tenant id the team's rows carry, so every tenant-scoped table and the tenant filter already keep one team's
/// data from another's. This registry owns the two tables that turn "one account, one tenant" into a
/// membership: many accounts per team, each with a role, and one account in several teams.
///
/// A PERSON WHO NEVER CREATES A TEAM SEES NO CHANGE. Their personal tenant lives in the <c>tenants</c> table,
/// minted and read by <see cref="TenantRegistry"/> exactly as before; nothing here reads or writes that row
/// except to show a member's display email.
///
/// EVERY TEAM HAS EXACTLY ONE OWNER. Creating a team makes the creator its Owner in the same write. Adding a
/// second Owner, promoting anyone to Owner, demoting the Owner and removing the Owner are all refused with a
/// plain-words reason. The database backs the "at most one" half with a filtered unique index.
///
/// WHO MAY DO WHAT IS NOT DECIDED HERE (that is devthrottle_internal#2302). These methods enforce the team's
/// own invariants - one role per person, exactly one Owner - and nothing about the caller. The one reading
/// rule that is here is that a team's member list is shown only to a member of that team.
///
/// Writes are serialized under one lock, like <see cref="TenantRegistry"/>. The subject and email are
/// personally identifying and are never logged; a team id is logged only in its hashed form.
/// </summary>
public sealed class TeamRegistry
{
    /// <summary>The longest team name accepted, in characters.</summary>
    public const int MaxNameLength = 100;

    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly Func<DateTime> _utcNow;
    private readonly object _writeLock = new();

    // What IsTeam has SETTLED about a tenant id, so the question every request asks costs no database read once
    // answered (review finding F4). Only a settled answer is kept: true for a team's id, false for a PERSONAL account's
    // id (a row in the tenants table). Both are final - ids are minted separately and never reused, and teams are not
    // deleted in the first version (deleting one must remove its entry here). An id that is in neither table is not
    // kept, so it is asked again next time: a team created by ANOTHER process is learned on its first request, and the
    // answer never fails open to "not a team" from memory alone.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _settled = new(StringComparer.Ordinal);

    /// <param name="db">The Gateway database. The team tables are global, so they are read through the UNSCOPED
    /// context, as the <c>tenants</c> table is.</param>
    /// <param name="tenants">The tenant registry, told when a team is created so its tenant census - the list
    /// every background sweep walks - includes the new team's tenant.</param>
    /// <param name="utcNow">The clock; the system clock when omitted.</param>
    public TeamRegistry(GatewayDatabase db, TenantRegistry tenants, Func<DateTime>? utcNow = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// CREATE A TEAM - the one place a team comes into being. The creating account becomes its Owner in the same
    /// database write, so a team can never exist without one. Starting the team's bill (devthrottle_internal#2299)
    /// attaches HERE and nowhere else.
    /// </summary>
    /// <param name="ownerSubject">The verified account subject of the person creating the team.</param>
    /// <param name="name">The team's display name, as typed. Trimmed; refused when empty, too long, or holding a
    /// control character.</param>
    public TeamCreateResult CreateTeam(string ownerSubject, string? name)
    {
        var subject = RequireSubject(ownerSubject);
        FileLog.Write("[TeamRegistry] CreateTeam: start");

        var refusal = NameRefusal(name);
        if (refusal is not null)
        {
            FileLog.Write("[TeamRegistry] CreateTeam: REFUSED - the name is not usable");
            return TeamCreateResult.Refused(refusal);
        }

        var teamName = name!.Trim();
        var teamId = Guid.NewGuid().ToString();
        var now = _utcNow();

        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            ctx.Teams.Add(new TeamEntity { Id = teamId, Name = teamName, CreatedAtUtc = now });
            ctx.TeamMembers.Add(new TeamMemberEntity
            {
                TeamId = teamId,
                AccountSubject = subject,
                Role = TeamRole.Owner,
                JoinedAtUtc = now,
            });
            CommitMembershipChange(ctx, teamId, TeamMembershipChange.TeamCreated);
        }

        _settled[teamId] = true;
        _tenants.CensusChanged();
        FileLog.Write($"[TeamRegistry] CreateTeam: created team {LogTeam(teamId)} with its creator as Owner");
        return TeamCreateResult.Created(new TeamSummary(teamId, teamName, TeamRole.Owner, MemberCount: 1));
    }

    /// <summary>
    /// The teams an account belongs to, each with the account's role there and the team's member count - what
    /// the team switcher shows. Empty for an account that belongs to no team. Ordered by team name.
    /// </summary>
    public IReadOnlyList<TeamSummary> ListTeamsFor(string accountSubject)
    {
        var subject = RequireSubject(accountSubject);
        using var ctx = _db.CreateUnscopedContext();

        var mine = ctx.TeamMembers.AsNoTracking()
            .Where(m => m.AccountSubject == subject)
            .Join(ctx.Teams.AsNoTracking(), m => m.TeamId, t => t.Id, (m, t) => new { t.Id, t.Name, m.Role })
            .ToList();
        if (mine.Count == 0)
        {
            FileLog.Write("[TeamRegistry] ListTeamsFor: the account belongs to no team");
            return Array.Empty<TeamSummary>();
        }

        var ids = mine.Select(t => t.Id).ToList();
        var counts = ctx.TeamMembers.AsNoTracking()
            .Where(m => ids.Contains(m.TeamId))
            .GroupBy(m => m.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionary(g => g.TeamId, g => g.Count, StringComparer.Ordinal);

        var teams = mine
            .Select(t => new TeamSummary(t.Id, t.Name, t.Role, counts[t.Id]))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.TeamId, StringComparer.Ordinal)
            .ToList();
        FileLog.Write($"[TeamRegistry] ListTeamsFor: the account belongs to {teams.Count} team(s)");
        return teams;
    }

    /// <summary>
    /// Whether <paramref name="tenant"/> is a team's tenant. Asked on every hosted request by
    /// <see cref="TeamEndpointGate"/>. Answered from memory once settled; a tenant that is neither a known team nor a
    /// known personal account is read from the database every time, so a team this process did not create is still
    /// recognised (fail closed, review finding F4).
    /// </summary>
    public bool IsTeam(TenantId tenant)
    {
        if (!tenant.IsValid || tenant.IsLocal || tenant.IsSystem)
            return false;

        var id = tenant.Value;
        if (_settled.TryGetValue(id, out var known))
            return known;

        using var ctx = _db.CreateUnscopedContext();
        if (ctx.Teams.AsNoTracking().Any(t => t.Id == id))
        {
            _settled[id] = true;
            FileLog.Write($"[TeamRegistry] IsTeam: {LogTeam(id)} read from the database - a team");
            return true;
        }

        if (ctx.Tenants.AsNoTracking().Any(t => t.Id == id))
            _settled[id] = false;
        return false;
    }

    /// <summary>The account's role in a team, or null when it is not a member (or there is no such team).</summary>
    public TeamRole? RoleOf(string teamId, string accountSubject)
    {
        var subject = RequireSubject(accountSubject);
        if (string.IsNullOrWhiteSpace(teamId))
            return null;

        using var ctx = _db.CreateUnscopedContext();
        var row = ctx.TeamMembers.AsNoTracking()
            .FirstOrDefault(m => m.TeamId == teamId && m.AccountSubject == subject);
        return row?.Role;
    }

    /// <summary>
    /// A team's members and their roles, for a caller who is a member of that team. A caller who is not a member
    /// gets <see cref="TeamMembersOutcome.NotFound"/> - the same answer as for a team that does not exist, so the
    /// list cannot be used to learn which teams exist. Ordered Owner first, then by role, then by email.
    /// </summary>
    public TeamMembersResult ListMembers(string teamId, string callerSubject)
    {
        var caller = RequireSubject(callerSubject);
        if (string.IsNullOrWhiteSpace(teamId))
            return TeamMembersResult.NotFound;

        using var ctx = _db.CreateUnscopedContext();
        var team = ctx.Teams.AsNoTracking().FirstOrDefault(t => t.Id == teamId);
        var callerRow = team is null
            ? null
            : ctx.TeamMembers.AsNoTracking().FirstOrDefault(m => m.TeamId == teamId && m.AccountSubject == caller);
        if (team is null || callerRow is null)
        {
            FileLog.Write($"[TeamRegistry] ListMembers: team {LogTeam(teamId)} - no such team for this caller (absent, or not a member)");
            return TeamMembersResult.NotFound;
        }

        var rows = ctx.TeamMembers.AsNoTracking().Where(m => m.TeamId == teamId).ToList();
        var subjects = rows.Select(r => r.AccountSubject).ToList();
        var emails = ctx.Tenants.AsNoTracking()
            .Where(t => subjects.Contains(t.AccountSubject))
            .Select(t => new { t.AccountSubject, t.Email })
            .ToList()
            .ToDictionary(t => t.AccountSubject, t => t.Email, StringComparer.Ordinal);

        var members = rows
            .Select(r => new TeamMember(
                r.AccountSubject,
                emails.TryGetValue(r.AccountSubject, out var email) && !string.IsNullOrWhiteSpace(email) ? email : null,
                r.Role,
                r.JoinedAtUtc))
            .OrderByDescending(m => m.Role)
            .ThenBy(m => m.Email ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.JoinedAtUtc)
            .ToList();

        FileLog.Write($"[TeamRegistry] ListMembers: team {LogTeam(teamId)} has {members.Count} member(s)");
        return TeamMembersResult.Found(new TeamSummary(team.Id, team.Name, callerRow.Role, members.Count), members);
    }

    /// <summary>
    /// Add an account to a team in a role. Refused when there is no such team, when the account is already a
    /// member (a role is changed with <see cref="ChangeRole"/>), or when the role is Owner (a team has exactly
    /// one). Who may add whom is not checked here (devthrottle_internal#2302).
    /// </summary>
    public TeamWriteResult AddMember(string teamId, string accountSubject, TeamRole role)
    {
        var subject = RequireSubject(accountSubject);
        RequireRole(role);
        FileLog.Write($"[TeamRegistry] AddMember: team {LogTeam(teamId)} role={role}");

        if (role == TeamRole.Owner)
            return Refuse("AddMember", TeamRefusals.SecondOwner);

        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            if (!ctx.Teams.Any(t => t.Id == teamId))
                return Refuse("AddMember", TeamRefusals.NoSuchTeam);
            if (ctx.TeamMembers.Any(m => m.TeamId == teamId && m.AccountSubject == subject))
                return Refuse("AddMember", TeamRefusals.AlreadyAMember);

            ctx.TeamMembers.Add(new TeamMemberEntity
            {
                TeamId = teamId!,
                AccountSubject = subject,
                Role = role,
                JoinedAtUtc = _utcNow(),
            });
            CommitMembershipChange(ctx, teamId!, TeamMembershipChange.MemberAdded);
        }

        FileLog.Write("[TeamRegistry] AddMember: added");
        return TeamWriteResult.Done;
    }

    /// <summary>
    /// Change a member's role. Refused when the account is not a member, when the member is the Owner (demoting
    /// the Owner would leave the team with none), or when the new role is Owner (the team already has one).
    /// Who may change roles is not checked here (devthrottle_internal#2302).
    /// </summary>
    public TeamWriteResult ChangeRole(string teamId, string accountSubject, TeamRole newRole)
    {
        var subject = RequireSubject(accountSubject);
        RequireRole(newRole);
        FileLog.Write($"[TeamRegistry] ChangeRole: team {LogTeam(teamId)} newRole={newRole}");

        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            var row = ctx.TeamMembers.FirstOrDefault(m => m.TeamId == teamId && m.AccountSubject == subject);
            if (row is null)
                return Refuse("ChangeRole", TeamRefusals.NotAMember);
            if (row.Role == TeamRole.Owner)
                return Refuse("ChangeRole", TeamRefusals.DemoteOwner);
            if (newRole == TeamRole.Owner)
                return Refuse("ChangeRole", TeamRefusals.SecondOwner);
            if (row.Role == newRole)
            {
                FileLog.Write("[TeamRegistry] ChangeRole: already in that role, nothing to change");
                return TeamWriteResult.Done;
            }

            row.Role = newRole;
            CommitMembershipChange(ctx, teamId!, TeamMembershipChange.RoleChanged);
        }

        FileLog.Write("[TeamRegistry] ChangeRole: changed");
        return TeamWriteResult.Done;
    }

    /// <summary>
    /// Remove a member from a team. Refused when the account is not a member, or when it is the Owner - a team
    /// always has exactly one Owner. Who may remove whom is not checked here (devthrottle_internal#2302).
    /// </summary>
    public TeamWriteResult RemoveMember(string teamId, string accountSubject)
    {
        var subject = RequireSubject(accountSubject);
        FileLog.Write($"[TeamRegistry] RemoveMember: team {LogTeam(teamId)}");

        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            var row = ctx.TeamMembers.FirstOrDefault(m => m.TeamId == teamId && m.AccountSubject == subject);
            if (row is null)
                return Refuse("RemoveMember", TeamRefusals.NotAMember);
            if (row.Role == TeamRole.Owner)
                return Refuse("RemoveMember", TeamRefusals.RemoveOwner);

            ctx.TeamMembers.Remove(row);
            CommitMembershipChange(ctx, teamId!, TeamMembershipChange.MemberRemoved);
        }

        FileLog.Write("[TeamRegistry] RemoveMember: removed");
        return TeamWriteResult.Done;
    }

    /// <summary>
    /// THE ONE PLACE A MEMBERSHIP CHANGE IS COMMITTED. Creating a team, adding a member, changing a role and removing
    /// a member all write through here and nowhere else, so anything that must follow every change - the website's
    /// seat count (devthrottle_internal#2301, seam-team-billing.md section 4) - attaches here once. Called under the
    /// write lock; nothing is called out from here today.
    /// </summary>
    private static void CommitMembershipChange(GatewayDbContext ctx, string teamId, TeamMembershipChange change)
    {
        ctx.SaveChanges();
        FileLog.Write($"[TeamRegistry] CommitMembershipChange: team {LogTeam(teamId)} change={change} committed");
    }

    /// <summary>The plain-words refusal for a team name, or null when the name is usable.</summary>
    internal static string? NameRefusal(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
            return "A team needs a name. Type the name your team will see, for example the company or the project.";
        if (trimmed.Length > MaxNameLength)
            return $"That team name is {trimmed.Length} characters long. Keep it to {MaxNameLength} characters or fewer.";
        if (trimmed.Any(char.IsControl))
            return "A team name cannot contain line breaks, tabs or other control characters. Type it on one line.";
        return null;
    }

    private static string LogTeam(string? teamId) =>
        string.IsNullOrWhiteSpace(teamId) ? "<none>" : new TenantId(teamId).ToLogString();

    private static TeamWriteResult Refuse(string method, string reason)
    {
        FileLog.Write($"[TeamRegistry] {method}: REFUSED - {reason}");
        return TeamWriteResult.Refused(reason);
    }

    private static string RequireSubject(string accountSubject)
    {
        if (string.IsNullOrWhiteSpace(accountSubject))
            throw new ArgumentException("A verified account subject is required.", nameof(accountSubject));
        return accountSubject.Trim();
    }

    private static void RequireRole(TeamRole role)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role), role, "Not one of the four team roles.");
    }
}

/// <summary>What kind of membership change <see cref="TeamRegistry"/> committed.</summary>
public enum TeamMembershipChange
{
    /// <summary>A team was created, with its creator as Owner.</summary>
    TeamCreated,

    /// <summary>An account joined a team.</summary>
    MemberAdded,

    /// <summary>A member's role changed.</summary>
    RoleChanged,

    /// <summary>A member left or was removed.</summary>
    MemberRemoved,
}

/// <summary>The refusals a team write can give, in the words a person reads.</summary>
public static class TeamRefusals
{
    public const string NoSuchTeam = "There is no such team.";

    public const string NotAMember = "That person is not a member of this team.";

    public const string AlreadyAMember = "That person is already a member of this team. To give them a different role, change their role instead.";

    public const string SecondOwner = "A team has exactly one Owner, and this team already has one. Choose Manager, Developer or Collaborator.";

    public const string DemoteOwner = "The Owner's role cannot be changed: a team always has exactly one Owner, and this would leave the team without one.";

    public const string RemoveOwner = "The Owner cannot be removed from the team: a team always has exactly one Owner, and this would leave the team without one.";
}

/// <summary>One team as one account sees it: the team, that account's role in it, and how many members it has.</summary>
public sealed record TeamSummary(string TeamId, string Name, TeamRole Role, int MemberCount)
{
    /// <summary>The team's tenant - the partition every row the team owns lives in.</summary>
    public TenantId Tenant => new(TeamId);
}

/// <summary>One member of a team. <see cref="Email"/> is display metadata read from the member's personal tenant,
/// null when none is recorded; <see cref="AccountSubject"/> is the key and is never shown or logged.</summary>
public sealed record TeamMember(string AccountSubject, string? Email, TeamRole Role, DateTime JoinedAtUtc);

/// <summary>The outcome of <see cref="TeamRegistry.CreateTeam"/>: the new team, or the reason it was refused.</summary>
public sealed record TeamCreateResult(TeamSummary? Team, string? Refusal)
{
    public static TeamCreateResult Created(TeamSummary team) => new(team, null);

    public static TeamCreateResult Refused(string reason) => new(null, reason);
}

/// <summary>The outcome of a membership write: done, or the reason it was refused.</summary>
public sealed record TeamWriteResult(bool IsDone, string? Refusal)
{
    public static readonly TeamWriteResult Done = new(true, null);

    public static TeamWriteResult Refused(string reason) => new(false, reason);
}

/// <summary>Whether a member list was found for the caller.</summary>
public enum TeamMembersOutcome
{
    /// <summary>The caller is a member; the list is carried.</summary>
    Found,

    /// <summary>No such team, or the caller is not a member of it. The two are deliberately one answer.</summary>
    NotFound,
}

/// <summary>The outcome of <see cref="TeamRegistry.ListMembers"/>.</summary>
public sealed record TeamMembersResult(TeamMembersOutcome Outcome, TeamSummary? Team, IReadOnlyList<TeamMember> Members)
{
    public static readonly TeamMembersResult NotFound = new(TeamMembersOutcome.NotFound, null, Array.Empty<TeamMember>());

    public static TeamMembersResult Found(TeamSummary team, IReadOnlyList<TeamMember> members) =>
        new(TeamMembersOutcome.Found, team, members);
}
