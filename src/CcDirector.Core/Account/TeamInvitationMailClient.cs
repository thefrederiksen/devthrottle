using CcDirector.Core.Network;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The outcome of asking the website to send one team invitation email.
/// </summary>
/// <param name="Sent">True only when the website answered that it sent the email. Never inferred from a status alone.</param>
/// <param name="Error">The website's own human-readable message, or a sentence saying why no call was made. Null when sent.</param>
/// <param name="StatusCode">The website's HTTP status (0 when no call was made or no response arrived).</param>
/// <param name="ErrorCode">The website's machine-readable code, for logging only.</param>
public sealed record TeamInvitationMailResult(bool Sent, string? Error, int StatusCode, string? ErrorCode);

/// <summary>
/// The Gateway's client for <c>POST /api/v1/team-invitations/email</c> (devthrottle_internal#2301): "send the email for
/// this invitation".
///
/// THE SAFETY PROPERTY THIS TYPE IS BUILT AROUND: <b>this client cannot address anyone.</b> The body is exactly
/// <c>{ "invitation_id": "...", "team_id": "...", "token": "..." }</c>; the team id is a cross-check only - the website
/// refuses an invitation of another team. There is no recipient parameter and no code path that could add
/// one: the website reads the invitee's address from the Gateway's own invitation row, server-side, and refuses a body
/// that names a recipient with a hard 400. So a fault in the Gateway can at worst send an invitation's email to the
/// address that invitation was made for - never to an address the Gateway chose.
///
/// THE TOKEN is the link's secret. The Gateway stores only its hash, so the website cannot build the link from the row
/// alone; it is handed the token here, checks that its hash matches the row, and puts it in the link. A token is not
/// an address: it can only ever open the invitation it was minted for.
///
/// Auth is the existing Gateway service credential, sent exactly as <see cref="AccountNotifyByTenantClient"/> sends it:
/// the same header, the same secret, and NO Authorization header. The secret is never logged.
/// </summary>
public sealed class TeamInvitationMailClient
{
    /// <summary>The website route that sends one invitation's email.</summary>
    public const string InvitationEmailPath = "/api/v1/team-invitations/email";


    /// <summary>The website route that tells a team's Owner the team's bill has ended.</summary>
    public const string BillEndedEmailPath = "/api/v1/team-bill/ended-email";

    private readonly HttpClient _client;
    private readonly string _baseUrl;

    public TeamInvitationMailClient(HttpClient? client = null, string? baseUrl = null)
    {
        _client = client ?? new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(30) };
        _baseUrl = DevThrottleApi.ResolveBaseUrl(baseUrl);
    }

    /// <summary>
    /// Ask the website to send the email for <paramref name="invitationId"/>. Throws only on a transport failure; a
    /// refusal is returned with the website's own message. Neither secret nor the invitation id is logged.
    /// </summary>
    /// <param name="serviceToken">The Gateway service secret. Never logged.</param>
    /// <param name="invitationId">The invitation's id.</param>
    /// <param name="teamId">The invitation's team, as a cross-check.</param>
    /// <param name="acceptToken">The link's secret, which the website checks against the row's hash. Never logged.</param>
    public async Task<TeamInvitationMailResult> SendInvitationAsync(string serviceToken, string invitationId, string teamId,
        string acceptToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceToken))
            throw new ArgumentException("A Gateway service token is required", nameof(serviceToken));
        if (string.IsNullOrWhiteSpace(invitationId))
            throw new ArgumentException("An invitation id is required", nameof(invitationId));
        if (string.IsNullOrWhiteSpace(teamId))
            throw new ArgumentException("A team id is required", nameof(teamId));
        if (string.IsNullOrWhiteSpace(acceptToken))
            throw new ArgumentException("The invitation link's token is required", nameof(acceptToken));

        var endpoint = $"{_baseUrl}{InvitationEmailPath}";
        FileLog.Write($"[TeamInvitationMailClient] SendInvitationAsync: POST {endpoint}");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(BuildBody(invitationId, teamId, acceptToken), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(AccountNotifyByTenantClient.ServiceTokenHeader, serviceToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            var sent = AccountNotifyByTenantClient.ParseSuccess(body, status);
            FileLog.Write($"[TeamInvitationMailClient] SendInvitationAsync: status={status}, sent={sent.Sent}");
            return new TeamInvitationMailResult(sent.Sent, sent.Error, status, null);
        }

        var (message, code) = AccountNotifyByTenantClient.ParseError(body, status);
        FileLog.Write($"[TeamInvitationMailClient] SendInvitationAsync: NOT sent, status={status}, code={code ?? "<none>"}");
        return new TeamInvitationMailResult(false, message, status, code);
    }

    /// <summary>
    /// Ask the website to tell a team's Owner that the team's bill has ended, so nobody can join it (Teams v1, owner
    /// ruling of 7 October: "tell the Owner"). The same safety property as the invitation email: <b>this call cannot
    /// address anyone.</b> The body names the team, the Owner's ACCOUNT SUBJECT and the bill row's fingerprint, nothing
    /// else. A subject is not an address: the website reads the Owner's address from their own account, server-side
    /// (decision D4). The subject is sent because the website has no read of the Gateway's member table, and giving it
    /// one is a database schema change the owner rules on.
    ///
    /// The fingerprint is the idempotency key: it is <c>EntitlementRegistry.TeamBillFingerprint</c> of the ended bill
    /// row, so the website can refuse a second email for the same ended bill even after the Gateway restarts and forgets
    /// it already asked. Throws only on a transport failure; a refusal is returned with the website's own message.
    /// Neither the secret nor the team id is logged.
    /// </summary>
    /// <param name="serviceToken">The Gateway service secret. Never logged.</param>
    /// <param name="teamId">The team whose bill has ended.</param>
    /// <param name="ownerSubject">The team's Owner's account subject. Never logged.</param>
    /// <param name="billFingerprint">The ended bill row's fingerprint.</param>
    public async Task<TeamInvitationMailResult> SendBillEndedAsync(string serviceToken, string teamId, string ownerSubject,
        string billFingerprint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceToken))
            throw new ArgumentException("A Gateway service token is required", nameof(serviceToken));
        if (string.IsNullOrWhiteSpace(teamId))
            throw new ArgumentException("A team id is required", nameof(teamId));
        if (string.IsNullOrWhiteSpace(ownerSubject))
            throw new ArgumentException("The team's Owner's account subject is required", nameof(ownerSubject));
        if (string.IsNullOrWhiteSpace(billFingerprint))
            throw new ArgumentException("The ended bill's fingerprint is required", nameof(billFingerprint));

        var endpoint = $"{_baseUrl}{BillEndedEmailPath}";
        FileLog.Write($"[TeamInvitationMailClient] SendBillEndedAsync: POST {endpoint}");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(BuildBillEndedBody(teamId, ownerSubject, billFingerprint), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(AccountNotifyByTenantClient.ServiceTokenHeader, serviceToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            var sent = AccountNotifyByTenantClient.ParseSuccess(body, status);
            FileLog.Write($"[TeamInvitationMailClient] SendBillEndedAsync: status={status}, sent={sent.Sent}");
            return new TeamInvitationMailResult(sent.Sent, sent.Error, status, null);
        }

        var (message, code) = AccountNotifyByTenantClient.ParseError(body, status);
        FileLog.Write($"[TeamInvitationMailClient] SendBillEndedAsync: NOT sent, status={status}, code={code ?? "<none>"}");
        return new TeamInvitationMailResult(false, message, status, code);
    }

    /// <summary>The request body: the invitation id, its team and the link's token, nothing else. Internal so a test can
    /// pin the wire shape.</summary>
    internal static string BuildBody(string invitationId, string teamId, string acceptToken) =>
        new JsonObject { ["invitation_id"] = invitationId, ["team_id"] = teamId, ["token"] = acceptToken }.ToJsonString();

    /// <summary>The bill-ended request body: the team, the Owner's account subject and the ended bill's fingerprint,
    /// nothing else - no address. Internal so a test can pin the wire shape.</summary>
    internal static string BuildBillEndedBody(string teamId, string ownerSubject, string billFingerprint) =>
        new JsonObject { ["team_id"] = teamId, ["owner_subject"] = ownerSubject, ["bill_fingerprint"] = billFingerprint }.ToJsonString();
}
