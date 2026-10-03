using System.Security.Cryptography;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// Invitations by email that expire (devthrottle_internal#2301). The Owner and Managers invite any email address to a
/// role; the person invited opens the link from the email, signs up if they need to, and accepts or declines. An
/// invitation is good for <see cref="TeamInvitationRules.ValidFor"/> after it was last sent, and resending is one
/// action that starts a new period.
///
/// SENDING IS NOT A MEMBERSHIP CHANGE. Creating, resending, cancelling and declining an invitation never touch
/// team_members and never reach <see cref="CommitMembershipChange"/>, so none of them moves the team's bill. ACCEPTING
/// is a membership change: the member row and the invitation's new state are written in one save through
/// <see cref="CommitMembershipChange"/>, which is where the seat count follows (seam-team-billing.md section 4).
///
/// NO INVITATION BEFORE THE BILL (seam section 3, step 5): until the team's bill has started - a team_entitlements row
/// that is active or past_due - an invitation is refused, so a team's first seat is always the Owner's.
///
/// The address, the subjects and the accept token are never logged.
/// </summary>
public sealed partial class TeamRegistry
{
    /// <summary>
    /// What the invite form (screen S2) offers the caller for this team: every role that can be invited, whether the
    /// caller may invite it, and the sentence to show beside it. Null when the caller is not a member of the team (or
    /// there is no such team). The Gateway decides; the form only lays it out.
    /// </summary>
    public TeamInviteOptions? InviteOptions(string teamId, string callerSubject)
    {
        var caller = RequireSubject(callerSubject);
        using var ctx = _db.CreateUnscopedContext();
        var team = string.IsNullOrWhiteSpace(teamId) ? null : ctx.Teams.AsNoTracking().FirstOrDefault(t => t.Id == teamId);
        var callerRole = team is null ? null : MemberRole(ctx, team.Id, caller);
        if (team is null || callerRole is null)
        {
            FileLog.Write($"[TeamRegistry] InviteOptions: team {LogTeam(teamId)} - no such team for this caller");
            return null;
        }

        var payer = DisplayFor(ctx, OwnerSubject(ctx, team.Id), TeamRole.Owner);
        var roles = new[] { TeamRole.Manager, TeamRole.Developer, TeamRole.Collaborator }
            .Select(r => new TeamInviteRoleOption(
                r,
                TeamInvitationRules.MayInvite(callerRole.Value, r),
                TeamInvitationRules.InviteRefusal(callerRole.Value, r) ?? RoleHint(r, payer)))
            .ToList();
        var canInvite = roles.Any(r => r.Allowed);
        var bill = canInvite ? BillRefusal(team.Id) : null;

        FileLog.Write($"[TeamRegistry] InviteOptions: team {LogTeam(team.Id)} callerRole={callerRole} canInvite={canInvite} billReady={bill is null}");
        return new TeamInviteOptions(team.Id, team.Name, callerRole.Value, roles,
            canInvite ? bill : TeamInvitationRefusals.NotAllowedToInvite);
    }

    /// <summary>
    /// INVITE someone by email. Refused, with the reason in plain words, when the caller is not a member (not found),
    /// may not invite that role, the address is not an address, the team's bill has not started, the address already
    /// has a waiting invitation, or it belongs to someone already in the team. The invitation is stored; sending its
    /// email is the caller's next step (<see cref="ITeamInvitationMailer"/>).
    /// </summary>
    public TeamInvitationResult CreateInvitation(string teamId, string inviterSubject, string? email, TeamRole role)
    {
        var inviter = RequireSubject(inviterSubject);
        RequireRole(role);
        FileLog.Write($"[TeamRegistry] CreateInvitation: team {LogTeam(teamId)} role={role}");

        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            var team = string.IsNullOrWhiteSpace(teamId) ? null : ctx.Teams.AsNoTracking().FirstOrDefault(t => t.Id == teamId);
            var inviterRole = team is null ? null : MemberRole(ctx, team.Id, inviter);
            if (team is null || inviterRole is null)
                return InvitationNotFound("CreateInvitation");

            if (TeamInvitationRules.InviteRefusal(inviterRole.Value, role) is { } notAllowed)
                return InvitationRefused("CreateInvitation", TeamInvitationOutcome.Forbidden, notAllowed);

            var address = TeamInvitationRules.NormalizeEmail(email);
            if (address is null)
                return InvitationRefused("CreateInvitation", TeamInvitationOutcome.Refused, TeamInvitationRefusals.BadEmail);

            if (BillRefusal(team.Id) is { } bill)
                return InvitationRefused("CreateInvitation",
                    bill == TeamInvitationRefusals.BillUnreadable ? TeamInvitationOutcome.Unavailable : TeamInvitationOutcome.Refused, bill);

            var now = _utcNow();
            var waiting = ctx.TeamInvitations.AsNoTracking()
                .Where(i => i.TeamId == team.Id && i.Email == address && i.State == TeamInvitationStates.Sent)
                .Select(i => i.ExpiresAtUtc)
                .ToList();
            if (waiting.Any(expires => now < expires))
                return InvitationRefused("CreateInvitation", TeamInvitationOutcome.Refused, TeamInvitationRefusals.AlreadyInvited);

            if (MemberEmails(ctx, team.Id).Contains(address))
                return InvitationRefused("CreateInvitation", TeamInvitationOutcome.Refused, TeamInvitationRefusals.AlreadyAMember);

            var row = new TeamInvitationEntity
            {
                Id = Guid.NewGuid().ToString(),
                TeamId = team.Id,
                Email = address,
                Role = role,
                State = TeamInvitationStates.Sent,
                InvitedBySubject = inviter,
                CreatedAtUtc = now,
                SentAtUtc = now,
                ExpiresAtUtc = now + TeamInvitationRules.ValidFor,
                AcceptToken = NewAcceptToken(),
            };
            ctx.TeamInvitations.Add(row);
            // Deliberately NOT CommitMembershipChange: an invitation is not a member, and sending one never moves the bill.
            ctx.SaveChanges();

            FileLog.Write($"[TeamRegistry] CreateInvitation: invitation {row.Id} stored for team {LogTeam(team.Id)}, expires {row.ExpiresAtUtc:o}");
            return TeamInvitationResult.Done(Describe(ctx, team, row, now));
        }
    }

    /// <summary>
    /// The team's invitations, newest first, for a caller who may invite (the Owner and Managers) - what the members
    /// page lists under its members. Null when there is no such team or the caller is not a member; an empty list for
    /// a member who may not invite, who has nothing here to act on.
    /// </summary>
    public IReadOnlyList<TeamInvitation>? ListInvitations(string teamId, string callerSubject)
    {
        var caller = RequireSubject(callerSubject);
        using var ctx = _db.CreateUnscopedContext();
        var team = string.IsNullOrWhiteSpace(teamId) ? null : ctx.Teams.AsNoTracking().FirstOrDefault(t => t.Id == teamId);
        var callerRole = team is null ? null : MemberRole(ctx, team.Id, caller);
        if (team is null || callerRole is null)
        {
            FileLog.Write($"[TeamRegistry] ListInvitations: team {LogTeam(teamId)} - no such team for this caller");
            return null;
        }
        if (!TeamRoles.IsAtLeast(callerRole.Value, TeamRole.Manager))
        {
            FileLog.Write($"[TeamRegistry] ListInvitations: team {LogTeam(team.Id)} - a {callerRole} invites nobody, so has no invitations to see");
            return Array.Empty<TeamInvitation>();
        }

        var now = _utcNow();
        var rows = ctx.TeamInvitations.AsNoTracking().Where(i => i.TeamId == team.Id).ToList();
        var list = rows
            .OrderByDescending(r => r.SentAtUtc)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => Describe(ctx, team, r, now))
            .ToList();
        FileLog.Write($"[TeamRegistry] ListInvitations: team {LogTeam(team.Id)} has {list.Count} invitation(s)");
        return list;
    }

    /// <summary>
    /// RESEND - one action that sends the invitation again and starts a new <see cref="TeamInvitationRules.ValidFor"/>.
    /// It also replaces the link's secret, so only the newest email's link works. Allowed for a waiting or expired
    /// invitation, by anyone who could have sent it.
    /// </summary>
    public TeamInvitationResult ResendInvitation(string teamId, string invitationId, string callerSubject)
    {
        var caller = RequireSubject(callerSubject);
        FileLog.Write($"[TeamRegistry] ResendInvitation: team {LogTeam(teamId)} invitation {invitationId}");

        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            var (team, row, denial) = InvitationForManager(ctx, teamId, invitationId, caller, "ResendInvitation");
            if (denial is not null) return denial;

            if (row!.State != TeamInvitationStates.Sent)
                return InvitationRefused("ResendInvitation", TeamInvitationOutcome.Refused, TeamInvitationRefusals.NoLongerWaiting);
            if (BillRefusal(team!.Id) is { } bill)
                return InvitationRefused("ResendInvitation",
                    bill == TeamInvitationRefusals.BillUnreadable ? TeamInvitationOutcome.Unavailable : TeamInvitationOutcome.Refused, bill);

            var now = _utcNow();
            row.SentAtUtc = now;
            row.ExpiresAtUtc = now + TeamInvitationRules.ValidFor;
            row.AcceptToken = NewAcceptToken();
            ctx.SaveChanges();

            FileLog.Write($"[TeamRegistry] ResendInvitation: invitation {row.Id} resent, now expires {row.ExpiresAtUtc:o}");
            return TeamInvitationResult.Done(Describe(ctx, team, row, now));
        }
    }

    /// <summary>
    /// CANCEL a waiting invitation, by anyone who could have sent it. Its link stops working at once. An invitation
    /// that was already accepted, declined or cancelled is refused.
    /// </summary>
    public TeamInvitationResult CancelInvitation(string teamId, string invitationId, string callerSubject)
    {
        var caller = RequireSubject(callerSubject);
        FileLog.Write($"[TeamRegistry] CancelInvitation: team {LogTeam(teamId)} invitation {invitationId}");

        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            var (team, row, denial) = InvitationForManager(ctx, teamId, invitationId, caller, "CancelInvitation");
            if (denial is not null) return denial;

            if (row!.State != TeamInvitationStates.Sent)
                return InvitationRefused("CancelInvitation", TeamInvitationOutcome.Refused, TeamInvitationRefusals.NoLongerWaiting);

            var now = _utcNow();
            row.State = TeamInvitationStates.Cancelled;
            row.RespondedAtUtc = now;
            ctx.SaveChanges();

            FileLog.Write($"[TeamRegistry] CancelInvitation: invitation {row.Id} cancelled");
            return TeamInvitationResult.Done(Describe(ctx, team!, row, now));
        }
    }

    /// <summary>
    /// What the accept page (screen S3) shows the signed-in person holding the link: who invited them to which team,
    /// in what role, who pays, which account they are signed in as - or, when it can no longer be accepted, why.
    /// Not found when no invitation carries this token.
    /// </summary>
    public TeamInvitationResult OpenInvitation(string acceptToken, string callerSubject)
    {
        var caller = RequireSubject(callerSubject);
        using var ctx = _db.CreateUnscopedContext();
        var row = FindByToken(ctx, acceptToken);
        var team = row is null ? null : ctx.Teams.AsNoTracking().FirstOrDefault(t => t.Id == row.TeamId);
        if (row is null || team is null)
            return InvitationNotFound("OpenInvitation");

        var now = _utcNow();
        var view = Describe(ctx, team, row, now);
        var refusal = AcceptRefusal(ctx, row, view, caller, now);
        FileLog.Write($"[TeamRegistry] OpenInvitation: invitation {row.Id} state={view.State} canRespond={refusal is null}");
        return TeamInvitationResult.Done(view with
        {
            SignedInAs = DisplayFor(ctx, caller, role: null),
            CanRespond = refusal is null,
            Refusal = refusal,
        });
    }

    /// <summary>
    /// ACCEPT - joins the caller to the team in the invited role, once. The member row and the invitation's accepted
    /// state are written in ONE save through <see cref="CommitMembershipChange"/>, so the seat count follows. A second
    /// accept, and an expired, cancelled or declined invitation, are refused in plain words.
    /// </summary>
    public TeamInvitationResult AcceptInvitation(string acceptToken, string callerSubject)
    {
        var caller = RequireSubject(callerSubject);
        FileLog.Write("[TeamRegistry] AcceptInvitation: start");

        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            var row = FindByToken(ctx, acceptToken, tracked: true);
            var team = row is null ? null : ctx.Teams.AsNoTracking().FirstOrDefault(t => t.Id == row.TeamId);
            if (row is null || team is null)
                return InvitationNotFound("AcceptInvitation");

            var now = _utcNow();
            var refusal = AcceptRefusal(ctx, row, Describe(ctx, team, row, now), caller, now);
            if (refusal is not null)
                return InvitationRefused("AcceptInvitation", TeamInvitationOutcome.Refused, refusal);

            row.State = TeamInvitationStates.Accepted;
            row.RespondedAtUtc = now;
            row.AcceptedBySubject = caller;
            ctx.TeamMembers.Add(new TeamMemberEntity
            {
                TeamId = team.Id,
                AccountSubject = caller,
                Role = row.Role,
                JoinedAtUtc = now,
            });
            CommitMembershipChange(ctx, team.Id, TeamMembershipChange.MemberAdded);

            FileLog.Write($"[TeamRegistry] AcceptInvitation: invitation {row.Id} accepted - joined team {LogTeam(team.Id)} as {row.Role}");
            return TeamInvitationResult.Done(Describe(ctx, team, row, now) with { SignedInAs = DisplayFor(ctx, caller, role: null) });
        }
    }

    /// <summary>
    /// DECLINE - the person invited says no. The invitation can no longer be accepted. Refused, with the reason, for
    /// one that was already accepted, declined or cancelled, or has expired.
    /// </summary>
    public TeamInvitationResult DeclineInvitation(string acceptToken, string callerSubject)
    {
        var caller = RequireSubject(callerSubject);
        FileLog.Write("[TeamRegistry] DeclineInvitation: start");

        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            var row = FindByToken(ctx, acceptToken, tracked: true);
            var team = row is null ? null : ctx.Teams.AsNoTracking().FirstOrDefault(t => t.Id == row.TeamId);
            if (row is null || team is null)
                return InvitationNotFound("DeclineInvitation");

            var now = _utcNow();
            var view = Describe(ctx, team, row, now);
            if (StateRefusal(view) is { } refusal)
                return InvitationRefused("DeclineInvitation", TeamInvitationOutcome.Refused, refusal);

            row.State = TeamInvitationStates.Declined;
            row.RespondedAtUtc = now;
            ctx.SaveChanges();

            FileLog.Write($"[TeamRegistry] DeclineInvitation: invitation {row.Id} declined");
            return TeamInvitationResult.Done(Describe(ctx, team, row, now) with { SignedInAs = DisplayFor(ctx, caller, role: null) });
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>Why this invitation cannot be accepted by this caller now, or null when it can.</summary>
    private static string? AcceptRefusal(GatewayDbContext ctx, TeamInvitationEntity row, TeamInvitation view, string caller, DateTime now)
    {
        if (StateRefusal(view) is { } refusal)
            return refusal;
        if (ctx.TeamMembers.AsNoTracking().Any(m => m.TeamId == row.TeamId && m.AccountSubject == caller))
            return TeamInvitationRefusals.CallerAlreadyMember;
        return null;
    }

    /// <summary>The plain-words reason an invitation in this state cannot be answered, or null while it is waiting.</summary>
    private static string? StateRefusal(TeamInvitation view) => view.State switch
    {
        TeamInvitationStates.Sent => null,
        TeamInvitationStates.Accepted => TeamInvitationRefusals.AlreadyAccepted,
        TeamInvitationStates.Declined => TeamInvitationRefusals.Declined(view.InvitedBy),
        TeamInvitationStates.Cancelled => TeamInvitationRefusals.Cancelled(view.InvitedBy),
        TeamInvitationStates.Expired => TeamInvitationRefusals.Expired(view.SentAtUtc, view.InvitedBy),
        _ => throw new InvalidOperationException($"team_invitations.state holds '{view.State}', which is not one of sent, accepted, declined or cancelled."),
    };

    /// <summary>
    /// The bill gate (seam section 3, step 5): null when the team's bill has started (active or past_due - a failed
    /// payment stops nothing), otherwise the plain-words refusal. A read that failed refuses too: it is not evidence
    /// that the bill has started.
    /// </summary>
    private string? BillRefusal(string teamId)
    {
        var bill = _readTeamBill(teamId);
        if (!bill.Known)
            return TeamInvitationRefusals.BillUnreadable;
        if (!bill.HasBill)
            return TeamInvitationRefusals.BillNotStarted;
        if (string.Equals(bill.Status, EntitlementRegistry.StatusActive, StringComparison.Ordinal)
            || string.Equals(bill.Status, EntitlementRegistry.StatusPastDue, StringComparison.Ordinal))
            return null;
        if (string.Equals(bill.Status, EntitlementRegistry.StatusCanceled, StringComparison.Ordinal))
            return TeamInvitationRefusals.BillCancelled;
        FileLog.Write($"[TeamRegistry] BillRefusal: team {LogTeam(teamId)} bill status is not one this Gateway knows - treated as not started");
        return TeamInvitationRefusals.BillNotStarted;
    }

    /// <summary>The team, the invitation and no denial - or the denial - for a caller acting on an invitation as the
    /// team's Owner or a Manager. Anyone who could not have sent this invitation may not resend or cancel it.</summary>
    private (TeamEntity? Team, TeamInvitationEntity? Row, TeamInvitationResult? Denial) InvitationForManager(
        GatewayDbContext ctx, string teamId, string invitationId, string caller, string method)
    {
        var team = string.IsNullOrWhiteSpace(teamId) ? null : ctx.Teams.AsNoTracking().FirstOrDefault(t => t.Id == teamId);
        var callerRole = team is null ? null : MemberRole(ctx, team.Id, caller);
        var row = team is null || string.IsNullOrWhiteSpace(invitationId)
            ? null
            : ctx.TeamInvitations.FirstOrDefault(i => i.Id == invitationId && i.TeamId == team.Id);
        if (team is null || callerRole is null || row is null)
            return (null, null, InvitationNotFound(method));
        if (TeamInvitationRules.InviteRefusal(callerRole.Value, row.Role) is { } notAllowed)
            return (null, null, InvitationRefused(method, TeamInvitationOutcome.Forbidden, notAllowed));
        return (team, row, null);
    }

    private static TeamInvitationEntity? FindByToken(GatewayDbContext ctx, string? token, bool tracked = false)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        var query = tracked ? ctx.TeamInvitations : ctx.TeamInvitations.AsNoTracking();
        return query.FirstOrDefault(i => i.AcceptToken == token.Trim());
    }

    private static TeamRole? MemberRole(GatewayDbContext ctx, string teamId, string subject) =>
        ctx.TeamMembers.AsNoTracking().FirstOrDefault(m => m.TeamId == teamId && m.AccountSubject == subject)?.Role;

    private static string? OwnerSubject(GatewayDbContext ctx, string teamId) =>
        ctx.TeamMembers.AsNoTracking().Where(m => m.TeamId == teamId && m.Role == TeamRole.Owner)
            .Select(m => m.AccountSubject).FirstOrDefault();

    /// <summary>The lower-cased emails of a team's members, for the "already a member" check.</summary>
    private static HashSet<string> MemberEmails(GatewayDbContext ctx, string teamId)
    {
        var subjects = ctx.TeamMembers.AsNoTracking().Where(m => m.TeamId == teamId).Select(m => m.AccountSubject).ToList();
        return ctx.Tenants.AsNoTracking()
            .Where(t => subjects.Contains(t.AccountSubject) && t.Email != null)
            .Select(t => t.Email!)
            .ToList()
            .Select(e => e.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// How a person is named on screen: the email on their own account. An account with no email recorded is named by
    /// its role on the team ("the team's Owner"), never guessed; with no role to go on, "an account with no email
    /// recorded".
    /// </summary>
    private static string DisplayFor(GatewayDbContext ctx, string? subject, TeamRole? role)
    {
        var email = string.IsNullOrWhiteSpace(subject)
            ? null
            : ctx.Tenants.AsNoTracking().Where(t => t.AccountSubject == subject).Select(t => t.Email).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(email))
            return email!;
        return role switch
        {
            TeamRole.Owner => "the team's Owner",
            TeamRole.Manager => "a Manager of the team",
            null => "an account with no email recorded",
            _ => $"a {TeamRoles.Label(role.Value)} of the team",
        };
    }

    private static string RoleHint(TeamRole role, string payer) => role switch
    {
        TeamRole.Manager => $"Invites and removes Developers and Collaborators. A paid seat on {payer}'s bill.",
        TeamRole.Developer => $"Runs sessions on their own computer. A paid seat on {payer}'s bill.",
        TeamRole.Collaborator => "Answers questions, sends requests, reads reports. Free.",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Only Manager, Developer and Collaborator can be invited."),
    };

    private static TeamInvitation Describe(GatewayDbContext ctx, TeamEntity team, TeamInvitationEntity row, DateTime now)
    {
        var inviterRole = MemberRole(ctx, team.Id, row.InvitedBySubject);
        var paidSeat = TeamSeatRoles.IsPaidSeat(TeamRoles.ToStored(row.Role));
        return new TeamInvitation(
            Id: row.Id,
            TeamId: team.Id,
            TeamName: team.Name,
            Email: row.Email,
            Role: row.Role,
            State: TeamInvitationRules.EffectiveState(row.State, row.ExpiresAtUtc, now),
            InvitedBy: DisplayFor(ctx, row.InvitedBySubject, inviterRole),
            PaidBy: paidSeat ? DisplayFor(ctx, OwnerSubject(ctx, team.Id), TeamRole.Owner) : null,
            SentAtUtc: row.SentAtUtc,
            ExpiresAtUtc: row.ExpiresAtUtc,
            SignedInAs: null,
            CanRespond: false,
            Refusal: null);
    }

    /// <summary>A link secret: 32 random bytes, URL-safe. Unguessable, and carried only by the email.</summary>
    private static string NewAcceptToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static TeamInvitationResult InvitationNotFound(string method)
    {
        FileLog.Write($"[TeamRegistry] {method}: no such invitation for this caller");
        return TeamInvitationResult.NotFound;
    }

    private static TeamInvitationResult InvitationRefused(string method, TeamInvitationOutcome outcome, string reason)
    {
        FileLog.Write($"[TeamRegistry] {method}: REFUSED ({outcome}) - {reason}");
        return new TeamInvitationResult(outcome, reason, null);
    }
}

/// <summary>One invitation as the routes describe it. <see cref="State"/> is what it is NOW (expired included).
/// The accept token is never part of it.</summary>
/// <param name="InvitedBy">How the person who sent it is named on screen.</param>
/// <param name="PaidBy">Who pays for the seat - the Owner - or null for a Collaborator, who is free.</param>
/// <param name="SignedInAs">On the accept page: how the signed-in account is named. Null elsewhere.</param>
/// <param name="CanRespond">On the accept page: whether this account can accept or decline it now.</param>
/// <param name="Refusal">On the accept page: why it cannot, in plain words. Null when it can.</param>
public sealed record TeamInvitation(
    string Id, string TeamId, string TeamName, string Email, TeamRole Role, string State, string InvitedBy, string? PaidBy,
    DateTime SentAtUtc, DateTime ExpiresAtUtc, string? SignedInAs, bool CanRespond, string? Refusal);

/// <summary>How an invitation request ended.</summary>
public enum TeamInvitationOutcome
{
    /// <summary>Done; the invitation is carried.</summary>
    Done,

    /// <summary>No such invitation or team for this caller - deliberately one answer.</summary>
    NotFound,

    /// <summary>The caller's role does not allow it.</summary>
    Forbidden,

    /// <summary>Refused for a reason about the invitation, the address or the team's bill.</summary>
    Refused,

    /// <summary>Something needed to decide could not be read just now; nothing was changed.</summary>
    Unavailable,
}

/// <summary>The outcome of an invitation request: the invitation, or the plain-words reason it was refused.</summary>
public sealed record TeamInvitationResult(TeamInvitationOutcome Outcome, string? Refusal, TeamInvitation? Invitation)
{
    public static readonly TeamInvitationResult NotFound = new(TeamInvitationOutcome.NotFound, TeamInvitationRefusals.NoSuchInvitation, null);

    public static TeamInvitationResult Done(TeamInvitation invitation) => new(TeamInvitationOutcome.Done, null, invitation);
}

/// <summary>One role the invite form offers, whether the caller may choose it, and the sentence shown beside it.</summary>
public sealed record TeamInviteRoleOption(TeamRole Role, bool Allowed, string Hint);

/// <summary>What the invite form (S2) shows. <see cref="Blocked"/> is the plain-words reason nobody can be invited
/// now (the caller's role, or the team's bill), or null when the form can be sent.</summary>
public sealed record TeamInviteOptions(string TeamId, string TeamName, TeamRole CallerRole,
    IReadOnlyList<TeamInviteRoleOption> Roles, string? Blocked);
