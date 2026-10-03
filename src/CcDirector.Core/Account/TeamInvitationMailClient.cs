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
/// The Gateway's client for <c>POST /api/v1/teams/invitation-email</c> (devthrottle_internal#2301): "send the email for
/// this invitation".
///
/// THE SAFETY PROPERTY THIS TYPE IS BUILT AROUND: <b>this client cannot address anyone.</b> The body is exactly
/// <c>{ "invitation_id": "...", "token": "..." }</c>. There is no recipient parameter and no code path that could add
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
    public const string InvitationEmailPath = "/api/v1/teams/invitation-email";

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
    /// <param name="acceptToken">The link's secret, which the website checks against the row's hash. Never logged.</param>
    public async Task<TeamInvitationMailResult> SendInvitationAsync(string serviceToken, string invitationId, string acceptToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceToken))
            throw new ArgumentException("A Gateway service token is required", nameof(serviceToken));
        if (string.IsNullOrWhiteSpace(invitationId))
            throw new ArgumentException("An invitation id is required", nameof(invitationId));
        if (string.IsNullOrWhiteSpace(acceptToken))
            throw new ArgumentException("The invitation link's token is required", nameof(acceptToken));

        var endpoint = $"{_baseUrl}{InvitationEmailPath}";
        FileLog.Write($"[TeamInvitationMailClient] SendInvitationAsync: POST {endpoint}");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(BuildBody(invitationId, acceptToken), Encoding.UTF8, "application/json"),
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

    /// <summary>The request body: the invitation id and the link's token, nothing else. Internal so a test can pin the
    /// wire shape.</summary>
    internal static string BuildBody(string invitationId, string acceptToken) =>
        new JsonObject { ["invitation_id"] = invitationId, ["token"] = acceptToken }.ToJsonString();
}
