using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// WHETHER A PERSON'S TEAMS GIVE THEM THE COCKPIT AND THE PHONE APP (Pricing Change mission, B2; Tech Lead ruling of
/// 13 October 2026). A person signs in to the Cockpit and the phone app with a key bound to THEIR OWN account - never
/// to a team's tenant - and reaches a team's pages from there. So a team seat can only give remote access through the
/// person's own account, and this is the read that says whether it does:
///
/// A PERSON HAS IT WHEN THEY ARE A MEMBER, IN ANY ROLE, OF AT LEAST ONE TEAM WHOSE BILL IS RUNNING. A Collaborator
/// counts: a Collaborator works in the team and has no Director there, so the Cockpit is their only way in. "Running"
/// is the bill's own test (<see cref="EntitlementRegistry.EvaluateTeam"/>: active, or past due), the same one that
/// gives a paid seat its paid features.
///
/// A TEAM WITH NO RUNNING BILL GIVES ITS MEMBERS NOTHING HERE. Otherwise creating a team and never paying for it would
/// be a free way into the Cockpit, which is the thing the mission closes.
///
/// IT GRANTS REMOTE ACCESS AND NOTHING ELSE: never an artificial-intelligence scope, and it does not change the tier
/// the person's own account is on.
///
/// THREE ANSWERS, like every other plan read: a membership read or a bill read that FAILED is not known - never "no
/// team", which is a verdict, and never a grant.
///
/// Built only where Teams is released. Where it is not, there are no teams and the remote access rule has no team
/// half at all.
/// </summary>
public sealed class TeamRemoteAccess
{
    private readonly TeamRegistry _teams;
    private readonly EntitlementRegistry _entitlements;

    public TeamRemoteAccess(TeamRegistry teams, EntitlementRegistry entitlements)
    {
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _entitlements = entitlements ?? throw new ArgumentNullException(nameof(entitlements));
    }

    /// <summary>
    /// Is <paramref name="accountSubject"/> a member of at least one team whose bill is running at
    /// <paramref name="nowUtc"/>? True or false when every read needed succeeded; NULL when the membership read, or
    /// the bill read of a team that would have decided it, failed.
    /// </summary>
    public bool? MemberOfATeamWithARunningBill(string accountSubject, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(accountSubject))
            throw new ArgumentException("An account subject is required.", nameof(accountSubject));

        IReadOnlyList<TeamSummary> mine;
        try
        {
            mine = _teams.ListTeamsFor(accountSubject);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            // IGNORANCE, NOT ABSENCE - the same rule, and the same loud log, as the entitlement read.
            FileLog.Write($"[TeamRemoteAccess] MemberOfATeamWithARunningBill: the membership read FAILED ({ex.GetType().Name}: {ex.Message}) - not known");
            return null;
        }

        var aBillCouldNotBeRead = false;
        foreach (var team in mine)
        {
            var bill = _entitlements.EvaluateTeam(team.TeamId, nowUtc);
            if (bill.Outcome == EntitlementOutcome.Entitled)
            {
                FileLog.Write($"[TeamRemoteAccess] MemberOfATeamWithARunningBill: yes - a member of team {team.Tenant.ToLogString()}, whose bill is running");
                return true;
            }
            if (bill.Outcome == EntitlementOutcome.Unknown)
                aBillCouldNotBeRead = true;
        }

        if (aBillCouldNotBeRead)
        {
            // No team answered yes, and at least one could not be read: that one might have. Not known.
            FileLog.Write("[TeamRemoteAccess] MemberOfATeamWithARunningBill: not known - a team's bill could not be read and no other team's bill is running");
            return null;
        }

        FileLog.Write($"[TeamRemoteAccess] MemberOfATeamWithARunningBill: no - a member of {mine.Count} team(s), none with a running bill");
        return false;
    }
}
