using System.Net.Http;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Teams;

/// <summary>Sends the email for a stored invitation. The Gateway names the invitation, never an address.</summary>
public interface ITeamInvitationMailer
{
    /// <summary>Send the email for <paramref name="invitationId"/> of <paramref name="teamId"/>, whose link carries
    /// <paramref name="acceptToken"/>.
    /// Never throws for a refusal or an unreachable website: the invitation is already stored, so the result says the
    /// email was not sent, and why.</summary>
    Task<TeamInvitationMailResult> SendAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default);

    /// <summary>Send the "you were added to the team" email for <paramref name="invitationId"/> of
    /// <paramref name="teamId"/> - an invitation stored already accepted by the one account that carries its address.
    /// <paramref name="acceptToken"/> only proves to the website that the Gateway minted the row; it is not put in the
    /// email, which carries no link to accept, only one to sign in. Never throws for a refusal or an unreachable
    /// website: the person is already a member, so the result says the email was not sent, and why.</summary>
    Task<TeamInvitationMailResult> SendAddedAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default);
}

/// <summary>Tells a team's Owner that the team's bill has ended. The Gateway names the team, never an address.</summary>
public interface ITeamBillEndedMailer
{
    /// <summary>Ask the website to email the Owner of <paramref name="teamId"/> - the account named by
    /// <paramref name="ownerSubject"/>, whose address the website reads itself - that its bill has ended, keyed by the
    /// ended bill row's <paramref name="billFingerprint"/>. Never throws for a refusal or an unreachable website: the
    /// result says the email was not sent, and why.</summary>
    Task<TeamInvitationMailResult> TellOwnerBillEndedAsync(string teamId, string ownerSubject, string billFingerprint,
        CancellationToken ct = default);
}

/// <summary>
/// The website-backed mailer (devthrottle_internal#2301, Delivery Lead decision D4): the website reads the invitee's
/// address from the invitation row itself and sends through its email module, logging the email like every other.
/// Fails closed when the Gateway service credential is not set - it never calls the website unauthenticated - and
/// says so, so "the email was not sent" always carries its real reason.
/// </summary>
public sealed class TeamInvitationMailer : ITeamInvitationMailer, ITeamBillEndedMailer
{
    private readonly TeamInvitationMailClient _client;
    private readonly Func<string?> _serviceToken;

    /// <param name="client">The website client.</param>
    /// <param name="serviceToken">Resolves the Gateway service secret; the owner-email setting when omitted.</param>
    public TeamInvitationMailer(TeamInvitationMailClient client, Func<string?>? serviceToken = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _serviceToken = serviceToken ?? AccountNotifyByTenantClient.ResolveServiceToken;
    }

    public Task<TeamInvitationMailResult> SendAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default) =>
        SendOneAsync("SendAsync", TeamInvitationMailClient.InvitationEmailPath, invitationId, teamId, acceptToken, ct);

    public Task<TeamInvitationMailResult> SendAddedAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default) =>
        SendOneAsync("SendAddedAsync", TeamInvitationMailClient.AddedEmailPath, invitationId, teamId, acceptToken, ct);

    /// <summary>
    /// The one send both emails share. Its refusal sentences are THE SAME for both, on purpose: they reach the person
    /// who added someone, and a different sentence would tell them whether the address already had an account (no
    /// account discovery). Which email was asked for is in the log line, never in the answer.
    /// </summary>
    private async Task<TeamInvitationMailResult> SendOneAsync(string method, string path, string invitationId, string teamId,
        string acceptToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(invitationId))
            throw new ArgumentException("An invitation id is required", nameof(invitationId));
        if (string.IsNullOrWhiteSpace(acceptToken))
            throw new ArgumentException("The invitation link's token is required", nameof(acceptToken));

        var token = _serviceToken();
        if (token is null)
        {
            FileLog.Write($"[TeamInvitationMailer] {method}: {AccountNotifyByTenantClient.ServiceTokenEnvVar} is not set on this Gateway - the email was NOT sent");
            return new TeamInvitationMailResult(false, NotSetUp, 0, null);
        }

        try
        {
            return await _client.SendForInvitationAsync(path, token, invitationId, teamId, acceptToken, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            FileLog.Write($"[TeamInvitationMailer] {method}: the website could not be reached ({ex.GetType().Name}) - the email was NOT sent");
            return new TeamInvitationMailResult(false, Unreachable, 0, null);
        }
    }

    /// <summary>Why no email was sent when this Gateway has no service credential. The same for both emails.</summary>
    internal const string NotSetUp = "This DevThrottle Gateway is not set up to send email.";

    /// <summary>Why no email was sent when the website could not be reached. The same for both emails.</summary>
    internal const string Unreachable = "The DevThrottle email service could not be reached.";

    public async Task<TeamInvitationMailResult> TellOwnerBillEndedAsync(string teamId, string ownerSubject, string billFingerprint,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(teamId))
            throw new ArgumentException("A team id is required", nameof(teamId));
        if (string.IsNullOrWhiteSpace(ownerSubject))
            throw new ArgumentException("The team's Owner's account subject is required", nameof(ownerSubject));
        if (string.IsNullOrWhiteSpace(billFingerprint))
            throw new ArgumentException("The ended bill's fingerprint is required", nameof(billFingerprint));

        var token = _serviceToken();
        if (token is null)
        {
            FileLog.Write($"[TeamInvitationMailer] TellOwnerBillEndedAsync: {AccountNotifyByTenantClient.ServiceTokenEnvVar} is not set on this Gateway - the Owner was NOT told the team's bill has ended");
            return new TeamInvitationMailResult(false,
                "This DevThrottle Gateway is not set up to send email, so the team's Owner was not told the bill has ended.",
                0, null);
        }

        try
        {
            return await _client.SendBillEndedAsync(token, teamId, ownerSubject, billFingerprint, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            FileLog.Write($"[TeamInvitationMailer] TellOwnerBillEndedAsync: the website could not be reached ({ex.GetType().Name}) - the Owner was NOT told the team's bill has ended");
            return new TeamInvitationMailResult(false,
                "The DevThrottle email service could not be reached, so the team's Owner was not told the bill has ended.",
                0, null);
        }
    }
}
