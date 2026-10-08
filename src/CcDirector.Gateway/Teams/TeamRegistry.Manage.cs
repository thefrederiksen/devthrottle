using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// RENAME, DELETE AND LEAVE A TEAM (Teams v1 - the owner's 8 October ruling: "finish the first version before adding
/// anything"). The role table already had the row "change the billing, or rename or delete the team" for the Owner alone;
/// these are its first two endpoints, and leaving is its own row (<see cref="TeamAction.LeaveTheTeam"/>), held by every
/// member but the Owner.
///
/// <list type="bullet">
/// <item><b>Rename</b> - the Owner. The same name rules as creating a team (<see cref="NameRefusal"/>).</item>
/// <item><b>Leave</b> - any member but the Owner. Their membership row goes through <see cref="CommitMembershipChange"/>
/// like a removal, so the bill follows the paid-seat count and their Directors on the team take the "removed from the
/// team" path (<see cref="TeamMemberAccessRevoker"/>). Their own sessions and Personal work are not touched.</item>
/// <item><b>Delete</b> - the Owner, and ONLY when the Owner is the last member (Architect decision, 8 October): the Owner
/// removes everyone else first, so nobody is ever cut off by surprise. Deleting ends the team's bill, cancels its waiting
/// invitations, takes the Owner's own membership through the same removal path - so the Owner's Directors on the team
/// are signed out of it - and marks the team deleted. NOTHING IS ERASED: the deleted team is left out of every read by
/// the context's query filter, and its skills, workflows, requests, reports and Mentor rows stay in the database.</item>
/// </list>
///
/// Every decision and write happens under the registry's write lock, as the Team page's changes do, so a role read for
/// the decision cannot change before the write lands. The verdicts the Cockpit renders - whether to offer Rename, Delete
/// and Leave, and the sentences each confirmation shows - are made here (<see cref="DescribeManage"/>), never in the
/// client (rule 7).
/// </summary>
public sealed partial class TeamRegistry
{
    /// <summary>
    /// RENAME THE TEAM, as <paramref name="callerSubject"/>: the Owner alone. The name is trimmed and checked by the same
    /// rule as creating a team. Renaming to the name it already has succeeds and changes nothing.
    /// </summary>
    public TeamMemberChangeResult RenameTeam(string teamId, string callerSubject, string? name)
    {
        var caller = RequireSubject(callerSubject);
        FileLog.Write($"[TeamRegistry] RenameTeam: team {LogTeam(teamId)}");

        lock (_writeLock)
        {
            var decision = _access.Decide(teamId ?? "", caller, TeamAction.BillingRenameOrDeleteTeam);
            if (!decision.IsMember)
                return MemberChangeNotFound("RenameTeam");
            if (!decision.Allowed)
                return MemberChangeRefused("RenameTeam", TeamMemberChangeOutcome.Forbidden, "role-table", decision.Refusal!);
            if (NameRefusal(name) is { } refusal)
                return MemberChangeRefused("RenameTeam", TeamMemberChangeOutcome.Refused, "name", refusal);

            var newName = name!.Trim();
            using var ctx = _db.CreateUnscopedContext();
            var team = ctx.Teams.FirstOrDefault(t => t.Id == teamId);
            if (team is null)
                return MemberChangeNotFound("RenameTeam");
            if (string.Equals(team.Name, newName, StringComparison.Ordinal))
            {
                FileLog.Write($"[TeamRegistry] RenameTeam: team {LogTeam(teamId)} already has that name - nothing changed");
                return TeamMemberChangeResult.Done;
            }

            team.Name = newName;
            ctx.SaveChanges();
        }

        FileLog.Write($"[TeamRegistry] RenameTeam: team {LogTeam(teamId)} renamed");
        return TeamMemberChangeResult.Done;
    }

    /// <summary>
    /// LEAVE THE TEAM, as <paramref name="callerSubject"/>: any member but the Owner. Their membership is removed through
    /// <see cref="RemoveMember"/>, so the bill and their Directors follow exactly as when they are removed.
    /// </summary>
    public TeamMemberChangeResult LeaveTeam(string teamId, string callerSubject)
    {
        var caller = RequireSubject(callerSubject);
        FileLog.Write($"[TeamRegistry] LeaveTeam: team {LogTeam(teamId)}");

        lock (_writeLock)
        {
            var decision = _access.Decide(teamId ?? "", caller, TeamAction.LeaveTheTeam);
            if (!decision.IsMember)
                return MemberChangeNotFound("LeaveTeam");
            if (decision.Role == TeamRole.Owner)
                return MemberChangeRefused("LeaveTeam", TeamMemberChangeOutcome.Refused, "owner", TeamManageRefusals.OwnerCannotLeave);
            if (!decision.Allowed)
                return MemberChangeRefused("LeaveTeam", TeamMemberChangeOutcome.Forbidden, "role-table", decision.Refusal!);

            var written = RemoveMember(teamId!, caller);
            if (!written.IsDone)
                return MemberChangeRefused("LeaveTeam", TeamMemberChangeOutcome.Refused, "team-invariant", written.Refusal!);
        }

        FileLog.Write($"[TeamRegistry] LeaveTeam: team {LogTeam(teamId)} - the member left");
        return TeamMemberChangeResult.Done;
    }

    /// <summary>
    /// DELETE THE TEAM, as <paramref name="callerSubject"/>: the Owner alone, only when they are the last member, and only
    /// with the team's name typed exactly as <paramref name="confirmName"/> - the same check the Cockpit's confirmation
    /// makes, held here so no client can skip it. Ends the bill, cancels waiting invitations, removes the Owner's
    /// membership (their Directors on the team are signed out of it) and marks the team deleted. Nothing is erased.
    /// </summary>
    public TeamMemberChangeResult DeleteTeam(string teamId, string callerSubject, string? confirmName)
    {
        var caller = RequireSubject(callerSubject);
        FileLog.Write($"[TeamRegistry] DeleteTeam: team {LogTeam(teamId)}");

        lock (_writeLock)
        {
            var decision = _access.Decide(teamId ?? "", caller, TeamAction.BillingRenameOrDeleteTeam);
            if (!decision.IsMember)
                return MemberChangeNotFound("DeleteTeam");
            if (!decision.Allowed)
                return MemberChangeRefused("DeleteTeam", TeamMemberChangeOutcome.Forbidden, "role-table", decision.Refusal!);

            using (var read = _db.CreateUnscopedContext())
            {
                var team = read.Teams.AsNoTracking().FirstOrDefault(t => t.Id == teamId);
                if (team is null)
                    return MemberChangeNotFound("DeleteTeam");
                if (!string.Equals(confirmName?.Trim(), team.Name, StringComparison.Ordinal))
                    return MemberChangeRefused("DeleteTeam", TeamMemberChangeOutcome.Refused, "confirmation",
                        TeamManageRefusals.TypeTheName(team.Name));
                var others = read.TeamMembers.Count(m => m.TeamId == teamId && m.AccountSubject != caller);
                if (others > 0)
                    return MemberChangeRefused("DeleteTeam", TeamMemberChangeOutcome.Refused, "members-remain",
                        TeamManageRefusals.OthersRemain(others));
            }

            // The bill first, in its own store: a bill another writer changed at the same moment stops the delete here,
            // before anything else has moved, and the Owner is told to try again.
            var bill = _bills.EndForDeletedTeam(teamId!);
            if (bill == TeamBillEndOutcome.ChangedElsewhere)
                return MemberChangeRefused("DeleteTeam", TeamMemberChangeOutcome.Refused, "bill-changed", TeamBillRefusals.ChangedAtTheSameMoment);
            FileLog.Write($"[TeamRegistry] DeleteTeam: team {LogTeam(teamId)} bill={bill}");

            // Then, in ONE save: the waiting invitations cancelled, the team marked deleted, and the Owner's membership
            // removed - committed through the one membership path, so the Owner's Directors on the team take the removed
            // path. A crash cannot leave a deleted team that still has its Owner, or an Owner-less team still live.
            using var ctx = _db.CreateUnscopedContext();
            var now = _utcNow();
            var waiting = ctx.TeamInvitations.Where(i => i.TeamId == teamId && i.State == TeamInvitationStates.Sent).ToList();
            foreach (var invitation in waiting)
            {
                invitation.State = TeamInvitationStates.Cancelled;
                invitation.RespondedAtUtc = now;
            }

            var row = ctx.Teams.First(t => t.Id == teamId);
            row.DeletedAtUtc = now;
            row.DeletedByAccountSubject = caller;
            var owner = ctx.TeamMembers.First(m => m.TeamId == teamId && m.AccountSubject == caller);
            ctx.TeamMembers.Remove(owner);
            CommitMembershipChange(ctx, teamId!, TeamMembershipChange.MemberRemoved, caller, null);
            FileLog.Write($"[TeamRegistry] DeleteTeam: team {LogTeam(teamId)} deleted - {waiting.Count} waiting invitation(s) cancelled, the Owner's membership removed, nothing erased");
        }

        _tenants.CensusChanged();
        return TeamMemberChangeResult.Done;
    }

    /// <summary>
    /// What the Team page offers <paramref name="caller"/> for the team itself: Rename and Delete for the Owner (Delete
    /// only once the Owner is the last member, with the sentence saying why not until then), and Leave for everyone else -
    /// with the sentence each confirmation shows. Every verdict here; the Cockpit only renders it.
    /// </summary>
    private TeamPageManage DescribeManage(string teamId, string teamName, string caller, TeamRole callerRole, int memberCount)
    {
        var mayRenameOrDelete = _access.Decide(teamId, caller, TeamAction.BillingRenameOrDeleteTeam).Allowed;
        var mayLeave = callerRole != TeamRole.Owner && _access.Decide(teamId, caller, TeamAction.LeaveTheTeam).Allowed;
        var others = memberCount - 1;
        var canDelete = mayRenameOrDelete && others == 0;
        return new TeamPageManage(
            CanRename: mayRenameOrDelete,
            CanDelete: canDelete,
            DeleteBlocked: mayRenameOrDelete && others > 0 ? TeamManageRefusals.OthersRemain(others) : null,
            DeleteWarning: mayRenameOrDelete ? TeamManageRefusals.DeleteWarning(teamName) : null,
            CanLeave: mayLeave,
            LeaveWarning: mayLeave ? TeamManageRefusals.LeaveWarning(teamName, IsPaid(callerRole)) : null);
    }
}

/// <summary>
/// The Team page's verdicts about the team itself (Teams v1, rename, delete and leave). Rendered verbatim by the Cockpit.
/// </summary>
/// <param name="CanRename">Whether the page offers Rename: the Owner.</param>
/// <param name="CanDelete">Whether Delete can be used now: the Owner, once they are the last member.</param>
/// <param name="DeleteBlocked">For the Owner while others remain: why Delete is not available yet. Null otherwise.</param>
/// <param name="DeleteWarning">For the Owner: what deleting does, for the confirmation. Null for anyone else.</param>
/// <param name="CanLeave">Whether the page offers Leave this team: every member but the Owner.</param>
/// <param name="LeaveWarning">What leaving does, for the confirmation. Null when the page offers no Leave.</param>
public sealed record TeamPageManage(bool CanRename, bool CanDelete, string? DeleteBlocked, string? DeleteWarning, bool CanLeave,
    string? LeaveWarning);

/// <summary>The sentences rename, delete and leave give, in the words a person reads. One place, so the page, the
/// refusals and their tests say the same thing.</summary>
public static class TeamManageRefusals
{
    /// <summary>The Owner asked to leave.</summary>
    public const string OwnerCannotLeave =
        "You are the team's Owner, and the Owner cannot leave the team: a team always has exactly one Owner. To close the team, remove everyone else and then delete it.";

    /// <summary>Delete was asked while other members remain.</summary>
    public static string OthersRemain(int others) =>
        (others == 1 ? "1 other person is still in the team." : $"{others} other people are still in the team.") +
        " Remove everyone else first, so nobody is cut off by surprise; then you can delete the team.";

    /// <summary>Delete was asked without the team's name typed exactly.</summary>
    public static string TypeTheName(string teamName) =>
        $"To delete the team, type its name exactly as it is: {teamName}";

    /// <summary>What deleting the team does, for the Owner's confirmation.</summary>
    public static string DeleteWarning(string teamName) =>
        $"Deleting {teamName} ends the team plan, cancels the team's waiting invitations and signs your Directors out of the team. " +
        "The team disappears from your menu. Nothing is erased: its skills, workflows, requests and reports are kept, and DevThrottle can restore the team if you ask.";

    /// <summary>What leaving the team does, for the member's confirmation.</summary>
    public static string LeaveWarning(string teamName, bool paidSeat) =>
        $"You will leave {teamName} at once, and your Directors on the team are signed out of it. " +
        (paidSeat ? "Your paid seat comes off the team's bill. " : "") +
        "Your own sessions and your Personal work are not touched. To come back, ask the team's Owner or a Manager to invite you again.";
}
