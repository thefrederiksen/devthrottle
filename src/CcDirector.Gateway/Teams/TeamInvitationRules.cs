using System.Net.Mail;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// The rules for team invitations (devthrottle_internal#2301), each a small pure function so it is tested directly.
/// WHO MAY INVITE WHOM is not decided here: it is the role table's cell for adding that role
/// (<see cref="TeamPermissions.ActionToAddOrRemove"/>, devthrottle_internal#2302), read by <see cref="MayInvite"/> and
/// asked through <see cref="TeamAccess"/> by every write. This file only chooses the words.
/// </summary>
public static class TeamInvitationRules
{
    /// <summary>How long an invitation can be accepted after it was last sent (owner decision, 3 Oct 2026).</summary>
    public static readonly TimeSpan ValidFor = TimeSpan.FromDays(7);

    /// <summary>The longest address accepted, in characters (the limit for an email address path).</summary>
    public const int MaxEmailLength = 254;

    /// <summary>
    /// Whether <paramref name="inviter"/> may invite <paramref name="invited"/>: the role table's cell for adding that
    /// role (<see cref="TeamPermissions.ActionToAddOrRemove"/>) - today the Owner invites a Manager, a Developer or a
    /// Collaborator, a Manager a Developer or a Collaborator, and a Developer or Collaborator nobody. Nobody is ever
    /// invited as Owner - a team has exactly one. The same answer decides who may resend or cancel an invitation: the
    /// people who could have sent it.
    /// </summary>
    public static bool MayInvite(TeamRole inviter, TeamRole invited)
    {
        RequireRole(inviter, nameof(inviter));
        RequireRole(invited, nameof(invited));
        if (invited == TeamRole.Owner)
            return false;
        return TeamPermissions.Grant(inviter, TeamPermissions.ActionToAddOrRemove(invited)) == TeamGrant.Yes;
    }

    /// <summary>
    /// Why <paramref name="inviter"/> may not invite <paramref name="invited"/>, in plain words, or null when they may.
    /// The one place these sentences are chosen, so the form (S2) and the refusal say the same thing.
    /// </summary>
    public static string? InviteRefusal(TeamRole inviter, TeamRole invited)
    {
        if (MayInvite(inviter, invited))
            return null;
        if (invited == TeamRole.Owner)
            return TeamInvitationRefusals.InviteOwner;
        // A role that may not invite even a Collaborator invites nobody; one that may, but not this role, is being
        // refused a Manager.
        if (TeamPermissions.Grant(inviter, TeamAction.InviteOrRemoveDevelopersAndCollaborators) != TeamGrant.Yes)
            return TeamInvitationRefusals.NotAllowedToInvite;
        return TeamInvitationRefusals.OnlyOwnerInvitesManager;
    }

    /// <summary>
    /// What the invitation is NOW: the stored state, except that a <c>sent</c> invitation whose time has run out is
    /// <c>expired</c>. Expired the moment <paramref name="nowUtc"/> reaches <paramref name="expiresAtUtc"/>: an
    /// invitation sent at 10:00 on day 1 can be accepted until 09:59:59 on day 8, so anywhere on day 7 works and day 8
    /// is refused.
    /// </summary>
    public static string EffectiveState(string storedState, DateTime expiresAtUtc, DateTime nowUtc)
    {
        if (storedState == TeamInvitationStates.Sent && nowUtc >= expiresAtUtc)
            return TeamInvitationStates.Expired;
        return storedState;
    }

    /// <summary>
    /// The address as stored - trimmed and lower-cased - or null when it is not a usable email address. ANY domain is
    /// accepted (owner decision, 18 Sep 2026: not only a company domain); what is refused is something that is not an
    /// address at all.
    /// </summary>
    public static string? NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > MaxEmailLength)
            return null;
        if (trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || trimmed.Count(c => c == '@') != 1)
            return null;

        var at = trimmed.IndexOf('@');
        var domain = trimmed[(at + 1)..];
        if (at == 0 || domain.Length == 0 || !domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.'))
            return null;

        // The framework parser is the last word: it refuses a display name, angle brackets and the other shapes
        // that are not a bare address. The address it reads back must be exactly what was typed.
        if (!MailAddress.TryCreate(trimmed, out var parsed) || !string.Equals(parsed.Address, trimmed, StringComparison.Ordinal))
            return null;
        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// The stored form of a link's secret: SHA-256 of its UTF-8 bytes, lower-case hexadecimal. The website computes the
    /// same thing to check the token the Gateway hands it before it builds the link, so this is a contract with
    /// another codebase and must not change.
    /// </summary>
    public static string HashAcceptToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            throw new ArgumentException("A token is required.", nameof(token));
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void RequireRole(TeamRole role, string name)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(name, role, "Not one of the four team roles.");
    }
}

/// <summary>The states an invitation can be in. All but <see cref="Expired"/> are stored.</summary>
public static class TeamInvitationStates
{
    public const string Sent = "sent";
    public const string Accepted = "accepted";
    public const string Declined = "declined";
    public const string Cancelled = "cancelled";

    /// <summary>Never stored: a <see cref="Sent"/> invitation past its expiry reads as this.</summary>
    public const string Expired = "expired";
}

/// <summary>The refusals an invitation can give, in the words a person reads.</summary>
public static class TeamInvitationRefusals
{
    public const string NotAllowedToInvite = "Only the team's Owner and Managers can invite people.";

    public const string OnlyOwnerInvitesManager = "Only the Owner can invite a Manager.";

    public const string InviteOwner = "A team has exactly one Owner, so nobody can be invited as Owner. Choose Manager, Developer or Collaborator.";

    public const string BadEmail = "That is not an email address. Type the full address, for example anna@example.com.";

    public const string BillNotStarted = "The team's bill has not started - the Owner finishes billing first.";

    public const string BillCancelled = "The team's bill has been cancelled, so nobody can be invited. The Owner restarts billing first.";

    public const string BillUnreadable = "DevThrottle could not check the team's bill just now, so the invitation was not sent. Try again shortly.";

    /// <summary>Accepting, when the team's bill is no longer running (Tech Lead ruling on review F5).</summary>
    public const string BillStopped = "The team's bill has stopped, so nobody can join the team right now. Ask the team's Owner.";

    public const string BillUnreadableOnAccept = "DevThrottle could not check the team's bill just now, so you have not joined yet. Try again shortly.";

    public const string AlreadyInvited = "That address already has an invitation waiting. Resend it from the team's invitations instead of sending a second one.";

    public const string AlreadyAMember = "That person is already a member of this team.";

    public const string NoSuchInvitation = "There is no such invitation. Check that you opened the whole link from the email.";

    public const string NoLongerWaiting = "This invitation is no longer waiting, so it cannot be resent or cancelled.";

    public const string AlreadyAccepted = "This invitation has already been accepted. Each invitation can be used once.";

    public const string CallerAlreadyMember = "You are already a member of this team, so this invitation cannot be used.";

    public static string Expired(DateTime sentAtUtc, string inviter) =>
        $"This invitation has expired. It was sent on {Day(sentAtUtc)} and was good for 7 days. Ask {inviter} to send a new one.";

    public static string Cancelled(string inviter) =>
        $"This invitation was cancelled by the team, so it can no longer be accepted. Ask {inviter} if you still want to join.";

    public static string Declined(string inviter) =>
        $"This invitation was declined, so it can no longer be accepted. Ask {inviter} to send a new one if you want to join.";

    private static string Day(DateTime utc) => utc.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
}
