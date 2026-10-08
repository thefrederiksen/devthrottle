using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// The Team page (screen S1, devthrottle_internal#2303): the member list with roles and seats, the waiting invitations,
/// and - per member - what the caller may do: change the role, remove them. Plus the two changes the page makes,
/// change a member's role and remove a member.
///
/// THE GATEWAY DECIDES EVERY VERDICT (product rule 7). Whether the caller may change a role, which roles the dropdown
/// offers, whether a member can be removed, the seat each member takes and the line that counts them are all worked out
/// here and carried on the model; the page renders them and never derives them from a role.
///
/// WHO MAY DO WHAT IS ASKED OF <see cref="TeamAccess"/>, THE ONE PLACE THAT ANSWERS IT (devthrottle_internal#2302):
/// changing a role is the row "make someone a Manager, change roles"; removing a member is the cell for adding or
/// removing that member's role (<see cref="TeamPermissions.ActionToAddOrRemove"/>), the same rule invitations ask. The
/// team's own invariants - exactly one Owner, who is never demoted or removed - stay in <see cref="ChangeRole"/> and
/// <see cref="RemoveMember"/>, which these call, and every change commits through <see cref="CommitMembershipChange"/>
/// so the bill follows a change in the paid-seat count.
///
/// A MEMBER IS NAMED BY AN OPAQUE ID (<see cref="TeamMemberIds"/>), never by the account subject, which is personally
/// identifying, never shown and never logged - and a member id travels in a route the access log records.
/// </summary>
public sealed partial class TeamRegistry
{
    /// <summary>The three roles a member can be given. Nobody is made Owner: a team has exactly one.</summary>
    private static readonly TeamRole[] AssignableRoles = { TeamRole.Manager, TeamRole.Developer, TeamRole.Collaborator };

    /// <summary>
    /// What the Team page shows <paramref name="callerSubject"/> for <paramref name="teamId"/>. Not found for someone who
    /// is not a member (or a team that does not exist); forbidden, with the role table's sentence, for a role that has no
    /// Team page (a Collaborator).
    /// </summary>
    public TeamPageResult DescribeTeamPage(string teamId, string callerSubject)
    {
        var caller = RequireSubject(callerSubject);
        FileLog.Write($"[TeamRegistry] DescribeTeamPage: team {LogTeam(teamId)}");

        var page = _access.Decide(teamId ?? "", caller, TeamAction.SeeTeamPage);
        if (!page.IsMember)
            return TeamPageResult.NotFound;
        if (!page.Allowed)
        {
            FileLog.Write($"[TeamRegistry] DescribeTeamPage: REFUSED - a {page.Role} has no Team page");
            return TeamPageResult.Forbidden(page.Refusal!);
        }

        var members = ListMembers(teamId!, caller);
        if (members.Outcome != TeamMembersOutcome.Found || members.Team is not { } team)
            return TeamPageResult.NotFound;

        var callerRole = page.Role!.Value;
        var mayChangeRoles = _access.Decide(team.TeamId, caller, TeamPermissions.ActionToChangeRole).Allowed;
        var mayInvite = _access.Decide(team.TeamId, caller, TeamAction.InviteOrRemoveDevelopersAndCollaborators).Allowed;

        var rows = members.Members.Select(m => DescribeMember(team.TeamId, caller, callerRole, mayChangeRoles, m)).ToList();

        // The invitations still to be answered, live or lapsed: a lapsed one can be resent or cancelled (its stored state
        // is still sent, so CancelInvitation allows it - review F1), so it stays listed until someone does one of the two.
        // Who sees them is ListInvitations' rule - the people who may invite - so a Developer's list is empty.
        var invitations = (ListInvitations(team.TeamId, caller) ?? Array.Empty<TeamInvitation>())
            .Where(i => i.State is TeamInvitationStates.Sent or TeamInvitationStates.Expired)
            .Select(i => new TeamPageInvitation(
                i.Id, i.Email, TeamRoles.Label(i.Role), i.State,
                SeatLabel(i.Role, invited: true),
                i.InvitedBy, i.SentAtUtc, i.ExpiresAtUtc,
                CanResend: TeamInvitationRules.MayInvite(callerRole, i.Role),
                CanCancel: TeamInvitationRules.MayInvite(callerRole, i.Role)))
            .ToList();

        var paid = members.Members.Count(m => IsPaid(m.Role));
        var free = members.Members.Count - paid;
        // "Waiting" counts only the live ones: a lapsed invitation is waiting on nobody (review F1).
        var waiting = invitations.Count(i => i.State == TeamInvitationStates.Sent);
        var summary = Summary(paid, free, mayInvite ? waiting : null);

        // The Billing section (Teams v1, the team bill without Stripe): for the Owner, who may change it, and a Manager, who
        // sees it read-only. A Developer gets no section at all - null, never an empty one.
        var maySeeBill = _access.Decide(team.TeamId, caller, TeamAction.SeeTeamBill).Allowed;
        var bill = maySeeBill
            ? BuildBillView(team.TeamId, _access.Decide(team.TeamId, caller, TeamAction.BillingRenameOrDeleteTeam).Allowed)
            : null;

        // The team's bill has ENDED: nobody can join, nobody is removed, and the page says so in plain words - with the
        // way back for the Owner, who is the only one who can renew the team plan (owner rulings, 7 October). Only for a
        // role that may see the team's bill: the notice tells the bill's status, which the role table withholds from a
        // Developer (review finding 3605-1; Delivery Lead ruling, 7 October).
        var billEnded = maySeeBill && IsEnded(_readTeamBill(team.TeamId));
        var billNotice = billEnded ? TeamBillNotices.Ended(callerRole) : null;

        FileLog.Write($"[TeamRegistry] DescribeTeamPage: team {LogTeam(team.TeamId)} callerRole={callerRole} members={rows.Count} paid={paid} invitations={invitations.Count} canChangeRoles={mayChangeRoles} canInvite={mayInvite} bill={bill?.State ?? "<not shown>"} canChangeBill={bill?.CanChange ?? false} billEnded={billEnded}");
        return TeamPageResult.Found(new TeamPage(team.TeamId, team.Name, TeamRoles.Label(callerRole), summary, mayInvite, rows, invitations, bill,
            billNotice));
    }

    /// <summary>
    /// CHANGE A MEMBER'S ROLE, as <paramref name="callerSubject"/>. Only a role the table lets "make someone a Manager,
    /// change roles" may (the Owner); the Owner's own role never changes and nobody is made Owner. A change between a
    /// paid role and a Collaborator moves the bill (<see cref="CommitMembershipChange"/>).
    /// </summary>
    public TeamMemberChangeResult ChangeMemberRole(string teamId, string callerSubject, string memberId, TeamRole newRole)
    {
        var caller = RequireSubject(callerSubject);
        RequireRole(newRole);
        FileLog.Write($"[TeamRegistry] ChangeMemberRole: team {LogTeam(teamId)} newRole={newRole}");

        // Decided and written under the write lock, so the roles the decision reads cannot change before the write
        // lands (review F2). Every membership write takes this lock; it is re-entrant, so ChangeRole takes it again.
        lock (_writeLock)
        {
            var decision = _access.Decide(teamId ?? "", caller, TeamPermissions.ActionToChangeRole);
            if (!decision.IsMember)
                return MemberChangeNotFound("ChangeMemberRole");
            if (!decision.Allowed)
                return MemberChangeRefused("ChangeMemberRole", TeamMemberChangeOutcome.Forbidden, "role-table", decision.Refusal!);

            var target = FindMember(teamId!, memberId);
            if (target is null)
                return MemberChangeRefused("ChangeMemberRole", TeamMemberChangeOutcome.NotFound, "no-such-member", TeamRefusals.NotAMember);

            var written = ChangeRole(teamId!, target.Value.Subject, newRole);
            return written.IsDone
                ? TeamMemberChangeResult.Done
                : MemberChangeRefused("ChangeMemberRole", TeamMemberChangeOutcome.Refused, "team-invariant", written.Refusal!);
        }
    }

    /// <summary>
    /// REMOVE A MEMBER, as <paramref name="callerSubject"/>. Allowed by the cell for removing that member's role: the
    /// Owner removes a Manager, a Developer or a Collaborator; a Manager only a Developer or a Collaborator. Nobody
    /// removes the Owner, and the Owner cannot remove themselves. Removing a paid member moves the bill.
    /// </summary>
    public TeamMemberChangeResult RemoveTeamMember(string teamId, string callerSubject, string memberId)
    {
        var caller = RequireSubject(callerSubject);
        FileLog.Write($"[TeamRegistry] RemoveTeamMember: team {LogTeam(teamId)}");

        // Decided and written under the write lock (review F2): the target's role and the caller's are read where no
        // other membership write can move them, so a Manager can never remove someone made a Manager a moment before,
        // and a Manager demoted a moment before can never complete a removal. Re-entrant: RemoveMember takes it again.
        lock (_writeLock)
        {
            var callerRole = RoleOf(teamId ?? "", caller);
            if (callerRole is null)
                return MemberChangeNotFound("RemoveTeamMember");

            var target = FindMember(teamId!, memberId);
            if (target is null)
                return MemberChangeRefused("RemoveTeamMember", TeamMemberChangeOutcome.NotFound, "no-such-member", TeamRefusals.NotAMember);
            if (target.Value.Role == TeamRole.Owner)
                return MemberChangeRefused("RemoveTeamMember", TeamMemberChangeOutcome.Refused, "owner",
                    target.Value.Subject == caller ? TeamRefusals.OwnerRemovesSelf : TeamRefusals.RemoveOwner);

            if (RemoveRefusal(teamId!, caller, target.Value.Role) is { } refusal)
                return MemberChangeRefused("RemoveTeamMember", TeamMemberChangeOutcome.Forbidden, "role-table", refusal);

            var written = RemoveMember(teamId!, target.Value.Subject);
            return written.IsDone
                ? TeamMemberChangeResult.Done
                : MemberChangeRefused("RemoveTeamMember", TeamMemberChangeOutcome.Refused, "team-invariant", written.Refusal!);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private TeamPageMember DescribeMember(string teamId, string caller, TeamRole callerRole, bool mayChangeRoles, TeamMember m)
    {
        var isYou = string.Equals(m.AccountSubject, caller, StringComparison.Ordinal);
        var isOwner = m.Role == TeamRole.Owner;
        var canChangeRole = mayChangeRoles && !isOwner;
        var canRemove = !isOwner && RemoveRefusal(teamId, caller, m.Role) is null;
        var name = DisplayName(m);
        return new TeamPageMember(
            TeamMemberIds.For(teamId, m.AccountSubject),
            name,
            m.Email,
            TeamRoles.Label(m.Role),
            SeatLabel(m.Role, invited: false),
            isYou,
            m.JoinedAtUtc,
            canChangeRole,
            canChangeRole ? AssignableRoles.Select(TeamRoles.Label).ToList() : Array.Empty<string>(),
            canRemove,
            canRemove ? RemoveWarning(name, m.Role) : null);
    }

    /// <summary>What a member is called on every team screen: their email, or a plain sentence when none is recorded -
    /// or, for a made-up showcase member, the name written on its row. One place, so the Team page and a report's "from"
    /// (devthrottle_internal#2309) name a person the same way.</summary>
    public static string DisplayName(TeamMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.Name ?? member.Email ?? "An account with no email recorded";
    }

    /// <summary>
    /// Why <paramref name="caller"/> may not remove a member holding <paramref name="role"/> (not the Owner), or null when
    /// they may: the cell for removing that role, asked through <see cref="TeamAccess"/>. Only the words are chosen here -
    /// a role that may remove Developers and Collaborators but not this one is being refused a Manager.
    /// </summary>
    private string? RemoveRefusal(string teamId, string caller, TeamRole role)
    {
        var decision = _access.Decide(teamId, caller, TeamPermissions.ActionToAddOrRemove(role));
        if (decision.Allowed)
            return null;
        return decision.Role is { } held
               && TeamPermissions.Grant(held, TeamAction.InviteOrRemoveDevelopersAndCollaborators) == TeamGrant.Yes
            ? TeamRefusals.OnlyOwnerRemovesManager
            : decision.Refusal;
    }

    /// <summary>The member whose opaque id is <paramref name="memberId"/>, or null when there is none in the team.</summary>
    private (string Subject, TeamRole Role)? FindMember(string teamId, string? memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId))
            return null;
        using var ctx = _db.CreateUnscopedContext();
        var match = ctx.TeamMembers.AsNoTracking()
            .Where(m => m.TeamId == teamId)
            .Select(m => new { m.AccountSubject, m.Role })
            .ToList()
            .FirstOrDefault(m => string.Equals(TeamMemberIds.For(teamId, m.AccountSubject), memberId.Trim(), StringComparison.Ordinal));
        return match is null ? null : (match.AccountSubject, match.Role);
    }

    private static bool IsPaid(TeamRole role) => TeamSeatRoles.IsPaidSeat(TeamRoles.ToStored(role));

    /// <summary>The seat column: Paid or No charge (never the word "free", owner rule); for an invitation, "Paid when accepted".</summary>
    private static string SeatLabel(TeamRole role, bool invited) =>
        IsPaid(role) ? (invited ? "Paid when accepted" : "Paid") : "No charge";

    /// <summary>The line under the page title: "3 paid seats, 2 Collaborators (no charge), 1 invitation waiting". The
    /// invitations part is left out for a caller who cannot see invitations.</summary>
    internal static string Summary(int paid, int free, int? waiting)
    {
        var parts = new List<string>
        {
            paid == 1 ? "1 paid seat" : $"{paid} paid seats",
            free == 1 ? "1 Collaborator (no charge)" : $"{free} Collaborators (no charge)",
        };
        if (waiting is { } w)
            parts.Add(w == 1 ? "1 invitation waiting" : $"{w} invitations waiting");
        return string.Join(", ", parts);
    }

    /// <summary>The sentence the remove confirmation shows.</summary>
    private static string RemoveWarning(string name, TeamRole role) =>
        IsPaid(role)
            ? $"{name} will leave the team at once, and their paid seat comes off the team's bill. To bring them back, invite them again."
            : $"{name} will leave the team at once. A Collaborator seat has no charge, so the bill does not change. To bring them back, invite them again.";

    private static TeamMemberChangeResult MemberChangeNotFound(string method)
    {
        FileLog.Write($"[TeamRegistry] {method}: no such team for this caller");
        return TeamMemberChangeResult.NotFound;
    }

    /// <summary>Log the refusal by its KIND and give the sentence to the caller: a sentence can name a person.</summary>
    private static TeamMemberChangeResult MemberChangeRefused(string method, TeamMemberChangeOutcome outcome, string kind, string reason)
    {
        FileLog.Write($"[TeamRegistry] {method}: REFUSED ({outcome}) kind={kind}");
        return new TeamMemberChangeResult(outcome, reason);
    }
}

/// <summary>
/// The opaque id a member is named by on the Team page and in its routes: a hash of the team and the account subject.
/// Stable for as long as the membership lasts, different in every team, and it reveals nothing about the account -
/// so it can sit in an address the access log records, where the subject never may.
/// </summary>
public static class TeamMemberIds
{
    /// <summary>The member id of <paramref name="accountSubject"/> in <paramref name="teamId"/>.</summary>
    public static string For(string teamId, string accountSubject)
    {
        if (string.IsNullOrWhiteSpace(teamId))
            throw new ArgumentException("A team id is required.", nameof(teamId));
        if (string.IsNullOrWhiteSpace(accountSubject))
            throw new ArgumentException("An account subject is required.", nameof(accountSubject));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"team-member\n{teamId.Trim()}\n{accountSubject.Trim()}"));
        return Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
    }
}

/// <summary>What the Team page shows one caller. Every flag and sentence is the Gateway's verdict.</summary>
/// <param name="YourRole">The caller's role in the team, as the page names it.</param>
/// <param name="Summary">The counting line: paid seats, free Collaborators and, for someone who may invite, the
/// invitations waiting.</param>
/// <param name="CanInvite">Whether the page offers "Invite someone".</param>
/// <param name="Bill">The Billing section: for the Owner and a Manager; null for a role that may not see the bill.</param>
/// <param name="BillNotice">When the team's bill has ended: what that means, in plain words - nobody can join, nobody is
/// removed, and who can renew the team plan. For the Owner and a Manager only; null for a role that may not see the
/// bill, and while the bill runs (or has not started).</param>
public sealed record TeamPage(string TeamId, string TeamName, string YourRole, string Summary, bool CanInvite,
    IReadOnlyList<TeamPageMember> Members, IReadOnlyList<TeamPageInvitation> Invitations, TeamBillView? Bill,
    string? BillNotice = null);

/// <summary>
/// The words the Team page shows when a team's bill has ENDED (owner ruling, 7 October: "Refuse new invitations and
/// accepts, keep existing members, tell the Owner - nobody is cut off, and nobody joins a team that does not pay").
/// The team's bill is the Gateway's own in Teams v1 (owner ruling, 7 October: no outside payment provider), so the way
/// back is renewing the team plan on this same Team page - never a page somewhere else. One place, so the page and its
/// tests say the same thing.
/// </summary>
public static class TeamBillNotices
{
    /// <summary>What the bill having ended means, for the Owner and a Manager.</summary>
    public const string EndedWhatItMeans =
        "The team's bill has ended. Nobody can join the team: new invitations cannot be sent and waiting ones cannot be accepted. " +
        "Everyone already in the team stays in it - nobody is removed - but the paid features are off until the team plan is renewed.";

    /// <summary>The way back, for the Owner.</summary>
    public const string OwnerRestarts = "To let people join again, renew the team plan on the Team page.";

    /// <summary>The way back, for a Manager.</summary>
    public const string AskTheOwner = "Only the team's Owner can renew the team plan.";

    /// <summary>The notice for a caller holding <paramref name="role"/>.</summary>
    public static string Ended(TeamRole role) =>
        EndedWhatItMeans + " " + (role == TeamRole.Owner ? OwnerRestarts : AskTheOwner);
}

/// <summary>One member row of the Team page.</summary>
/// <param name="MemberId">The opaque id the change routes take (<see cref="TeamMemberIds"/>).</param>
/// <param name="Seat">Paid or No charge.</param>
/// <param name="CanChangeRole">Whether the role shows as a dropdown for this caller.</param>
/// <param name="RoleChoices">The roles the dropdown offers; empty when it is not a dropdown.</param>
/// <param name="CanRemove">Whether the row offers Remove.</param>
/// <param name="RemoveWarning">The sentence the remove confirmation shows; null when the row offers no Remove.</param>
public sealed record TeamPageMember(string MemberId, string Name, string? Email, string Role, string Seat, bool IsYou,
    DateTime JoinedAtUtc, bool CanChangeRole, IReadOnlyList<string> RoleChoices, bool CanRemove, string? RemoveWarning);

/// <summary>One waiting invitation on the Team page.</summary>
/// <param name="State">sent, or expired.</param>
/// <param name="Seat">"Paid when accepted" or No charge.</param>
public sealed record TeamPageInvitation(string Id, string Email, string Role, string State, string Seat, string InvitedBy,
    DateTime SentAtUtc, DateTime ExpiresAtUtc, bool CanResend, bool CanCancel);

/// <summary>How a Team page request ended.</summary>
public enum TeamPageOutcome
{
    /// <summary>The page is carried.</summary>
    Found,

    /// <summary>No such team, or the caller is not a member - deliberately one answer.</summary>
    NotFound,

    /// <summary>The caller is a member whose role has no Team page.</summary>
    Forbidden,
}

/// <summary>The outcome of <see cref="TeamRegistry.DescribeTeamPage"/>.</summary>
public sealed record TeamPageResult(TeamPageOutcome Outcome, TeamPage? Page, string? Refusal)
{
    public static readonly TeamPageResult NotFound = new(TeamPageOutcome.NotFound, null, null);

    public static TeamPageResult Found(TeamPage page) => new(TeamPageOutcome.Found, page, null);

    public static TeamPageResult Forbidden(string refusal) => new(TeamPageOutcome.Forbidden, null, refusal);
}

/// <summary>How a change to a member ended.</summary>
public enum TeamMemberChangeOutcome
{
    /// <summary>The change was made.</summary>
    Done,

    /// <summary>No such team or member for this caller.</summary>
    NotFound,

    /// <summary>The caller's role does not allow it.</summary>
    Forbidden,

    /// <summary>Refused by the team's own rules: the Owner is never demoted or removed, and nobody is made Owner.</summary>
    Refused,
}

/// <summary>The outcome of a change to a member: done, or the plain-words reason it was not.</summary>
public sealed record TeamMemberChangeResult(TeamMemberChangeOutcome Outcome, string? Refusal)
{
    public static readonly TeamMemberChangeResult Done = new(TeamMemberChangeOutcome.Done, null);

    public static readonly TeamMemberChangeResult NotFound = new(TeamMemberChangeOutcome.NotFound, TeamRefusals.NoSuchTeam);
}
