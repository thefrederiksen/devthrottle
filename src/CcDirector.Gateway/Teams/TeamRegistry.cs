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
public sealed partial class TeamRegistry
{
    /// <summary>The longest team name accepted, in characters.</summary>
    public const int MaxNameLength = 100;

    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly Func<DateTime> _utcNow;
    private readonly TeamBillStore _bills;
    private readonly TeamGovernanceStore _governance;
    private readonly Func<string, TeamBill> _readTeamBill;
    private readonly TeamBillEndedNotice? _billEndedNotice;
    // Teams come with the pro account (piece 1.6): asked of whoever creates a team, and of a team's Owner when anyone is added.
    private readonly TeamOwnerPlan _ownerPlan;
    // "May this person do this in this team", asked of the one place that answers it (devthrottle_internal#2302).
    private readonly TeamAccess _access;
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
    /// <param name="bills">The team bills (Teams v1, the team bill without Stripe): the Owner's bill actions, and the
    /// paid-seat count recorded after a membership change. Defaults to a store over this same database and clock.</param>
    /// <param name="readTeamBill">Reads a team's bill for the invitation bill gate. Defaults to the entitlement reader
    /// over this same database - the one reader of a team's bill.</param>
    /// <param name="billEndedNotice">Tells a team's Owner, once per ended bill, that someone was refused because the
    /// bill has ended. Null on a Gateway with no team bills, and then such a refusal says in the log that nobody was
    /// told.</param>
    /// <param name="ownerPlan">Reads whether an account's own plan carries teams (piece 1.6). The hosted Gateway passes
    /// <see cref="TeamOwnerPlan.Hosted"/>; omitted, it is <see cref="TeamOwnerPlan.SelfHost"/> - a Gateway with no plans,
    /// where every owner is granted.</param>
    public TeamRegistry(GatewayDatabase db, TenantRegistry tenants, Func<DateTime>? utcNow = null,
        TeamBillStore? bills = null, Func<string, TeamBill>? readTeamBill = null, TeamBillEndedNotice? billEndedNotice = null,
        TeamGovernanceStore? governance = null, TeamOwnerPlan? ownerPlan = null)
    {
        _ownerPlan = ownerPlan ?? TeamOwnerPlan.SelfHost;
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _bills = bills ?? new TeamBillStore(db, _utcNow);
        _governance = governance ?? new TeamGovernanceStore(db, _utcNow);
        // The default reader needs no live-mode answer: ReadTeamBill reads the team's bill row and never consults the
        // live-mode requirement, which only EvaluatePaid applies to a person's own subscription.
        _readTeamBill = readTeamBill ?? new EntitlementRegistry(db, requireLivemode: false).ReadTeamBill;
        _billEndedNotice = billEndedNotice;
        _access = new TeamAccess(this);
    }

    /// <summary>
    /// CREATE A TEAM - the one place a team comes into being. The creating account becomes its Owner in the same
    /// database write, so a team can never exist without one. Starting the team's bill (devthrottle_internal#2299)
    /// attaches HERE and nowhere else.
    ///
    /// TEAMS COME WITH THE PRO ACCOUNT (piece 1.6): the creator's own plan must carry teams (<see cref="TeamOwnerPlan"/>).
    /// Refused in plain words when it does not, and refused as unavailable - try again - when it could not be read.
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

        switch (_ownerPlan.Read(subject, _utcNow()))
        {
            case TeamOwnerPlanAnswer.Granted:
                break;
            case TeamOwnerPlanAnswer.NotPro:
                FileLog.Write("[TeamRegistry] CreateTeam: REFUSED - the creator's plan does not include teams");
                return TeamCreateResult.Refused(TeamRefusals.CreateNeedsPro);
            case TeamOwnerPlanAnswer.Unknown:
                FileLog.Write("[TeamRegistry] CreateTeam: REFUSED for now - the creator's plan could not be read");
                return TeamCreateResult.Unavailable(TeamRefusals.CreatePlanUnreadable);
            default:
                throw new InvalidOperationException("TeamOwnerPlan gave an answer this code does not know.");
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
            CommitMembershipChange(ctx, teamId, TeamMembershipChange.TeamCreated, subject, TeamRole.Owner,
                TeamMembershipEvents.Created, fromRole: null, bySubject: subject);
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

        var members = ReadMembers(ctx, teamId);
        // A role the table does not let see the members (a Collaborator, devthrottle_internal#2787) is given every OTHER
        // member without an address, named by role. Every screen built on this list - a report's "from", a question's
        // "asked by", a request's trail - therefore cannot show such a caller another person's address.
        var seesMembers = TeamPermissions.Allows(callerRow.Role, TeamAction.SeeMembersAndRoles);
        if (!seesMembers)
            members = members.Select(m => string.Equals(m.AccountSubject, caller, StringComparison.Ordinal) ? m : WithoutAddress(m)).ToList();
        FileLog.Write($"[TeamRegistry] ListMembers: team {LogTeam(teamId)} has {members.Count} member(s), addresses {(seesMembers ? "shown" : "withheld")}");
        return TeamMembersResult.Found(new TeamSummary(team.Id, team.Name, callerRow.Role, members.Count), members);
    }

    /// <summary>
    /// A team's members and their roles, for the Gateway's own background work - the Mentor's weekly run
    /// (devthrottle_internal#2305) - which acts for no caller. Empty for a team that does not exist. Never serve this to
    /// a person: a person's read is <see cref="ListMembers"/>, which checks they are a member.
    /// </summary>
    public IReadOnlyList<TeamMember> MembersOf(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId))
            throw new ArgumentException("A team id is required.", nameof(teamId));
        using var ctx = _db.CreateUnscopedContext();
        var members = ReadMembers(ctx, teamId);
        FileLog.Write($"[TeamRegistry] MembersOf: team {LogTeam(teamId)} has {members.Count} member(s)");
        return members;
    }

    /// <summary>A member as a caller who may not see the members reads them: no email address, and the role's name in
    /// place of a name (<see cref="RoleName"/>). A made-up showcase member keeps the made-up name written on its row.</summary>
    internal static TeamMember WithoutAddress(TeamMember member) =>
        member with { Email = null, Name = member.Name ?? RoleName(member.Role) };

    /// <summary>
    /// How another member is named to a caller who may not see the members: by their role, because the Gateway holds no
    /// name for a real person (the account's nickname lives on the website and can be read only with that person's own
    /// token). One team has one Owner, so the Owner is named exactly. Lower case, because the name is set inside sentences
    /// ("Your words go to the team's Owner") as well as on its own.
    /// </summary>
    public static string RoleName(TeamRole role) => role switch
    {
        TeamRole.Owner => "the team's Owner",
        TeamRole.Manager => "a Manager of the team",
        TeamRole.Developer => "a Developer of the team",
        TeamRole.Collaborator => "a contributor of the team",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not one of the four team roles."),
    };

    private static List<TeamMember> ReadMembers(GatewayDbContext ctx, string teamId)
    {
        var rows = ctx.TeamMembers.AsNoTracking().Where(m => m.TeamId == teamId).ToList();
        var subjects = rows.Select(r => r.AccountSubject).ToList();
        var emails = ctx.Tenants.AsNoTracking()
            .Where(t => subjects.Contains(t.AccountSubject))
            .Select(t => new { t.AccountSubject, t.Email })
            .ToList()
            .ToDictionary(t => t.AccountSubject, t => t.Email, StringComparer.Ordinal);

        return rows
            .Select(r => new TeamMember(
                r.AccountSubject,
                // A made-up showcase member has no account, so its email is the one written on its own row.
                r.ShowcaseTag is not null ? r.DisplayEmail
                : emails.TryGetValue(r.AccountSubject, out var email) && !string.IsNullOrWhiteSpace(email) ? email : null,
                r.Role,
                r.JoinedAtUtc,
                r.ShowcaseTag is not null ? r.DisplayName : null,
                r.ShowcaseTag))
            .OrderByDescending(m => m.Role)
            .ThenBy(m => m.Email ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.JoinedAtUtc)
            .ToList();
    }

    /// <summary>
    /// Add an account to a team in a role. Refused when there is no such team, when the account is already a
    /// member (a role is changed with <see cref="ChangeRole"/>), or when the role is Owner (a team has exactly
    /// one). Who may add whom is not checked here (devthrottle_internal#2302).
    /// </summary>
    /// <param name="bySubject">Who added them, for the membership record; null when no person's request is behind the
    /// write.</param>
    public TeamWriteResult AddMember(string teamId, string accountSubject, TeamRole role, string? bySubject = null)
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
            CommitMembershipChange(ctx, teamId!, TeamMembershipChange.MemberAdded, subject, role,
                TeamMembershipEvents.Added, fromRole: null, bySubject: OptionalSubject(bySubject));
        }

        FileLog.Write("[TeamRegistry] AddMember: added");
        return TeamWriteResult.Done;
    }

    /// <summary>
    /// Change a member's role. Refused when the account is not a member, when the member is the Owner (demoting
    /// the Owner would leave the team with none), or when the new role is Owner (the team already has one).
    /// Who may change roles is not checked here (devthrottle_internal#2302).
    /// </summary>
    /// <param name="bySubject">Who changed it, for the membership record; null when no person's request is behind the
    /// write.</param>
    public TeamWriteResult ChangeRole(string teamId, string accountSubject, TeamRole newRole, string? bySubject = null)
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

            var oldRole = row.Role;
            row.Role = newRole;
            CommitMembershipChange(ctx, teamId!, TeamMembershipChange.RoleChanged, subject, newRole,
                TeamMembershipEvents.RoleChanged, oldRole, OptionalSubject(bySubject));
        }

        FileLog.Write("[TeamRegistry] ChangeRole: changed");
        return TeamWriteResult.Done;
    }

    /// <summary>
    /// Remove a member from a team. Refused when the account is not a member, or when it is the Owner - a team
    /// always has exactly one Owner. Who may remove whom is not checked here (devthrottle_internal#2302).
    ///
    /// REMOVAL CLEANS UP (security review M4): what was SENT to the person in this team - their rows in
    /// <c>dev_report_recipients</c>, which are their Reports page and their Questions page - is deleted in the same save
    /// as the member row, so a person added back later starts with nothing from before. What they WROTE - comments,
    /// answers, requests - stays as the team's record. See <see cref="CommitMembershipChange"/>.
    /// </summary>
    /// <param name="bySubject">Who removed them, for the membership record - the person themselves when they left; null
    /// when no person's request is behind the write.</param>
    public TeamWriteResult RemoveMember(string teamId, string accountSubject, string? bySubject = null)
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

            var by = OptionalSubject(bySubject);
            ctx.TeamMembers.Remove(row);
            CommitMembershipChange(ctx, teamId!, TeamMembershipChange.MemberRemoved, subject, null,
                string.Equals(by, subject, StringComparison.Ordinal) ? TeamMembershipEvents.Left : TeamMembershipEvents.Removed,
                row.Role, by);
        }

        FileLog.Write("[TeamRegistry] RemoveMember: removed");
        return TeamWriteResult.Done;
    }

    /// <summary>
    /// THE ONE PLACE A MEMBERSHIP CHANGE IS COMMITTED. Creating a team, adding a member (directly or by accepting an
    /// invitation), changing a role and removing a member all write through here and nowhere else, so what must
    /// follow every change attaches here once. Called under the write lock.
    ///
    /// THE SEAT COUNT (Teams v1, the team bill without Stripe): after the save commits, a change that MOVED THE TEAM'S
    /// PAID-SEAT COUNT records the new count on the team's bill (<see cref="TeamBillStore.RecordSeats"/>). Whether it
    /// moved is measured, not inferred from the kind of change: the paid members (Owner, Manager, Developer -
    /// <see cref="TeamSeatRoles"/>) are counted from the database before the save and again after it. So a Developer made
    /// a Manager, a Collaborator added or a Collaborator removed leaves the bill alone, and a Developer made a
    /// Collaborator, a paid member added or a paid member removed reaches it (devthrottle_internal#2303). Creating the team
    /// is left out because the team's bill does not exist yet: the Owner starts the plan from the Team page. Nothing is
    /// sent to the website: the count is the Gateway's own, on the Gateway's own bill. A Gateway that stops between the
    /// two saves is put right by <see cref="TeamSeatConvergence"/>.
    ///
    /// THE MEMBERSHIP EVENT (devthrottle_internal#2311): after the save, and after the seat count is recorded, it tells
    /// <see cref="MembershipCommitted"/>, so what must follow a person's membership - cutting their Directors off a team
    /// they left or can no longer run sessions in - hangs off this one place too. A listener that throws fails the
    /// write's caller AFTER the change has committed; that is deliberate, because a removed person whose keys could not
    /// be revoked must be seen, not hidden.
    ///
    /// THE MEMBERSHIP RECORD (security review Risk 12): one <see cref="TeamMembershipEventEntity"/> row is added to the
    /// SAME save as the change, so the change and its record commit or fail together, and a refused change - which never
    /// reaches here - writes none.
    ///
    /// A REMOVAL CLEANS UP (security review M4): the person's recipient rows in this team are deleted in that same save.
    /// Nothing they wrote is touched.
    /// </summary>
    /// <param name="accountSubject">The person whose membership changed.</param>
    /// <param name="role">Their role after the change, or null when they are no longer a member.</param>
    /// <param name="membershipEvent">The record's word for the change (<see cref="TeamMembershipEvents"/>).</param>
    /// <param name="fromRole">Their role before the change, or null when they were not a member.</param>
    /// <param name="bySubject">Who made the change, or null when no person's request is behind it.</param>
    private void CommitMembershipChange(GatewayDbContext ctx, string teamId, TeamMembershipChange change,
        string accountSubject, TeamRole? role, string membershipEvent, TeamRole? fromRole, string? bySubject)
    {
        AddMembershipRecord(ctx, teamId, accountSubject, membershipEvent, fromRole, role, bySubject, _utcNow());
        var cleared = change == TeamMembershipChange.MemberRemoved ? ClearWhatWasSentTo(ctx, teamId, accountSubject) : 0;

        // Read before the save: a query goes to the database, which does not yet hold the pending change.
        var paidBefore = TeamBillStore.PaidSeats(ctx, teamId);
        ctx.SaveChanges();
        var paidAfter = TeamBillStore.PaidSeats(ctx, teamId);
        FileLog.Write($"[TeamRegistry] CommitMembershipChange: team {LogTeam(teamId)} change={change} event={membershipEvent} committed, paid seats {paidBefore} -> {paidAfter}" +
                      (change == TeamMembershipChange.MemberRemoved ? $", {cleared} report(s) sent to them cleared" : ""));

        if (change == TeamMembershipChange.TeamCreated)
            FileLog.Write($"[TeamRegistry] CommitMembershipChange: team {LogTeam(teamId)} - a new team has no bill yet, so no seat count was recorded");
        else if (paidBefore == paidAfter)
            FileLog.Write($"[TeamRegistry] CommitMembershipChange: team {LogTeam(teamId)} - the paid-seat count did not change, so the bill was left alone");
        else
            FileLog.Write($"[TeamRegistry] CommitMembershipChange: team {LogTeam(teamId)} - seat count on the bill: {_bills.RecordSeats(teamId)}");

        // Raised for every change, whether or not the paid-seat count moved: what must follow a person's membership
        // is for the listener to decide, not the bill.
        MembershipCommitted?.Invoke(new TeamMembershipCommitted(teamId, accountSubject, change, role));
    }

    /// <summary>
    /// THE ONE PLACE A MEMBERSHIP RECORD IS WRITTEN (security review Risk 12): adds one
    /// <see cref="TeamMembershipEventEntity"/> to the caller's pending save, so it commits with the change it records or not
    /// at all. Called by <see cref="CommitMembershipChange"/> and by the showcase's member writes, and nowhere else.
    /// </summary>
    private static void AddMembershipRecord(GatewayDbContext ctx, string teamId, string accountSubject, string membershipEvent,
        TeamRole? fromRole, TeamRole? toRole, string? bySubject, DateTime atUtc)
    {
        ctx.TeamMembershipEvents.Add(new TeamMembershipEventEntity
        {
            Id = Guid.NewGuid().ToString(),
            TeamId = teamId,
            AccountSubject = accountSubject,
            Event = membershipEvent,
            FromRole = fromRole,
            ToRole = toRole,
            BySubject = bySubject,
            AtUtc = atUtc,
        });
    }

    /// <summary>
    /// ADD A SHOWCASE'S MADE-UP MEMBERS (<see cref="TeamShowcase"/>), each with its membership record, in ONE save under the
    /// write lock. Not <see cref="CommitMembershipChange"/>: a made-up member is not an account, so it holds no paid seat,
    /// has no Director to cut off and raises no <see cref="MembershipCommitted"/>; but it is a row in <c>team_members</c>,
    /// so it leaves the same record as any other member. Every row must carry a showcase tag.
    /// </summary>
    /// <param name="bySubject">Who wrote the showcase: the team's Owner.</param>
    /// <param name="atUtc">The showcase's clock.</param>
    internal void AddShowcaseMembers(string teamId, IReadOnlyList<TeamMemberEntity> rows, string bySubject, DateTime atUtc)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var by = RequireSubject(bySubject);
        if (rows.Any(r => r.ShowcaseTag is null || !string.Equals(r.TeamId, teamId, StringComparison.Ordinal)))
            throw new ArgumentException("A showcase member row carries its tag and this team's id.", nameof(rows));
        FileLog.Write($"[TeamRegistry] AddShowcaseMembers: team {LogTeam(teamId)} count={rows.Count}");

        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            foreach (var row in rows)
            {
                ctx.TeamMembers.Add(row);
                AddMembershipRecord(ctx, teamId, row.AccountSubject, TeamMembershipEvents.Added, null, row.Role, by, atUtc);
            }
            ctx.SaveChanges();
        }
        FileLog.Write($"[TeamRegistry] AddShowcaseMembers: team {LogTeam(teamId)} added {rows.Count} made-up member(s), each recorded");
    }

    /// <summary>
    /// REMOVE THE MADE-UP MEMBERS carrying <paramref name="tag"/> (<see cref="TeamShowcase.Remove"/>), each with its
    /// membership record, in ONE save under the write lock. Only rows with that tag are touched. Returns how many went.
    /// </summary>
    /// <param name="bySubject">Who removed the showcase: the team's Owner.</param>
    /// <param name="atUtc">The showcase's clock.</param>
    internal int RemoveShowcaseMembers(string teamId, string tag, string bySubject, DateTime atUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var by = RequireSubject(bySubject);
        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            var rows = ctx.TeamMembers.Where(m => m.TeamId == teamId && m.ShowcaseTag == tag).ToList();
            foreach (var row in rows)
            {
                ctx.TeamMembers.Remove(row);
                AddMembershipRecord(ctx, teamId, row.AccountSubject, TeamMembershipEvents.Removed, row.Role, null, by, atUtc);
            }
            ctx.SaveChanges();
            FileLog.Write($"[TeamRegistry] RemoveShowcaseMembers: team {LogTeam(teamId)} removed {rows.Count} made-up member(s), each recorded");
            return rows.Count;
        }
    }

    /// <summary>
    /// Marks for deletion, in the caller's pending save, every report a person was sent in a team: their rows in
    /// <c>dev_report_recipients</c>, which both their Reports page and their Questions page read (security review M4).
    /// Read through the unscoped context, so the tenant filter is lifted and the team's id is named explicitly. Returns
    /// how many rows were marked.
    /// </summary>
    private static int ClearWhatWasSentTo(GatewayDbContext ctx, string teamId, string accountSubject)
    {
        var rows = ctx.DevReportRecipients.IgnoreQueryFilters()
            .Where(r => r.TenantId == teamId && r.RecipientSubject == accountSubject)
            .ToList();
        ctx.DevReportRecipients.RemoveRange(rows);
        return rows.Count;
    }

    /// <summary>The subject of whoever made a change, trimmed, or null when no person is behind it.</summary>
    private static string? OptionalSubject(string? bySubject) =>
        string.IsNullOrWhiteSpace(bySubject) ? null : bySubject.Trim();

    /// <summary>
    /// Raised once for every committed membership change, under the write lock, after the save. The Gateway attaches
    /// <see cref="TeamMemberAccessRevoker"/> here.
    /// </summary>
    public event Action<TeamMembershipCommitted>? MembershipCommitted;

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

/// <summary>
/// The fixed words <c>team_membership_events.event</c> holds (security review Risk 12). Stored as written, so they are a
/// contract with any later reader: never rename one.
/// </summary>
public static class TeamMembershipEvents
{
    /// <summary>The team was created; its creator became the Owner.</summary>
    public const string Created = "created";

    /// <summary>The person accepted an invitation and joined.</summary>
    public const string JoinedByInvitation = "joined-by-invitation";

    /// <summary>An invitation's address had exactly one account, so that account was added at once by the inviter.</summary>
    public const string AddedAtOnce = "added-at-once";

    /// <summary>The person was added directly, without an invitation.</summary>
    public const string Added = "added";

    /// <summary>The person's role changed; the record holds the role before and after.</summary>
    public const string RoleChanged = "role-changed";

    /// <summary>Someone else removed the person from the team.</summary>
    public const string Removed = "removed";

    /// <summary>The person removed themselves.</summary>
    public const string Left = "left";
}

/// <summary>
/// One committed membership change: which team, whose membership, what kind of change, and the person's role after it
/// (null when they are no longer a member). <see cref="AccountSubject"/> is personally identifying: never log it.
/// </summary>
public sealed record TeamMembershipCommitted(string TeamId, string AccountSubject, TeamMembershipChange Change, TeamRole? Role);

/// <summary>The refusals a team write can give, in the words a person reads.</summary>
public static class TeamRefusals
{
    public const string NoSuchTeam = "There is no such team.";

    /// <summary>Creating a team on an account whose plan does not include teams (piece 1.6).</summary>
    public const string CreateNeedsPro =
        "A team comes with a Pro account, and your account is not on Pro, so the team was not created. Choose Pro at devthrottle.com/pricing, then create the team.";

    /// <summary>Creating a team when the creator's plan could not be read: not a no, so try again.</summary>
    public const string CreatePlanUnreadable = "DevThrottle could not check your plan just now, so the team was not created. Try again shortly.";

    public const string NotAMember = "That person is not a member of this team.";

    public const string AlreadyAMember = "That person is already a member of this team. To give them a different role, change their role instead.";

    public const string SecondOwner = "A team has exactly one Owner, and this team already has one. Choose Manager, Developer or Contributor.";

    public const string DemoteOwner = "The Owner's role cannot be changed: a team always has exactly one Owner, and this would leave the team without one.";

    public const string RemoveOwner = "The Owner cannot be removed from the team: a team always has exactly one Owner, and this would leave the team without one.";

    public const string OwnerRemovesSelf = "You are the team's Owner, and the Owner cannot leave the team: a team always has exactly one Owner.";

    public const string OnlyOwnerRemovesManager = "Only the Owner can remove a Manager.";
}

/// <summary>One team as one account sees it: the team, that account's role in it, and how many members it has.</summary>
public sealed record TeamSummary(string TeamId, string Name, TeamRole Role, int MemberCount)
{
    /// <summary>The team's tenant - the partition every row the team owns lives in.</summary>
    public TenantId Tenant => new(TeamId);
}

/// <summary>One member of a team. <see cref="Email"/> is display metadata read from the member's personal tenant,
/// null when none is recorded; <see cref="AccountSubject"/> is the key and is never shown or logged.
/// <see cref="Name"/> and <see cref="ShowcaseTag"/> are set only on a made-up showcase member (<see cref="TeamShowcase"/>):
/// its display name, and the tag it was written under. A showcase member is not an account: it holds no paid seat, runs
/// no Director, is sent no mail, and the Mentor's writer never visits it.</summary>
public sealed record TeamMember(string AccountSubject, string? Email, TeamRole Role, DateTime JoinedAtUtc,
    string? Name = null, string? ShowcaseTag = null)
{
    /// <summary>True for a made-up showcase member.</summary>
    public bool IsShowcase => ShowcaseTag is not null;
}

/// <summary>The outcome of <see cref="TeamRegistry.CreateTeam"/>: the new team, or the reason it was refused.</summary>
public sealed record TeamCreateResult(TeamSummary? Team, string? Refusal, bool IsUnavailable = false)
{
    public static TeamCreateResult Created(TeamSummary team) => new(team, null);

    public static TeamCreateResult Refused(string reason) => new(null, reason);

    /// <summary>Not created because something needed could not be read just now; the caller says to try again.</summary>
    public static TeamCreateResult Unavailable(string reason) => new(null, reason, IsUnavailable: true);
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
