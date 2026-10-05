using CcDirector.Core.Network;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The outcome of one seat-sync call.
/// </summary>
/// <param name="Synced">True only when the website answered with a success status.</param>
/// <param name="StatusCode">The website's HTTP status (0 when no call was made or no response arrived).</param>
/// <param name="Error">The website's own human-readable message on a refusal, or a sentence saying why no call
/// was made. Null on success.</param>
/// <param name="ErrorCode">The website's machine-readable code on a refusal. NOT for logging only: it decides
/// <see cref="SubscriptionEnded"/>, which stops a team's seat convergence, so a change to how it is read
/// (<c>AccountNotifyByTenantClient.ParseError</c>) can bring back the never-ending retry.</param>
public sealed record TeamSeatSyncResult(bool Synced, int StatusCode, string? Error, string? ErrorCode)
{
    /// <summary>
    /// True only for the website's one refusal that means "this team has no running bill": HTTP 409 with the code
    /// <see cref="TeamSeatSyncClient.NoActiveSubscriptionCode"/>. The website answers it when its own row for the
    /// team is not billed, and when the payment provider says the subscription has ended - either way no call can
    /// succeed until the team's bill row changes. Every other failure (no response, a 5xx, a timeout, a missing
    /// credential, any other refusal) is false, and is retried as before.
    /// </summary>
    public bool SubscriptionEnded =>
        !Synced
        && StatusCode == 409
        && string.Equals(ErrorCode, TeamSeatSyncClient.NoActiveSubscriptionCode, StringComparison.Ordinal);
}

/// <summary>
/// The Gateway's client for <c>POST /api/v1/teams/sync-seats</c> (devthrottle_internal #2299): "this team's
/// membership changed - recount its paid seats and bill that".
///
/// THE SAFETY PROPERTY THIS TYPE IS BUILT AROUND: <b>the caller names a team, never a number.</b> The body is
/// exactly <c>{ "team_id": "..." }</c> and there is no parameter, field or code path that could add a seat count.
/// The website reads the paid-seat count from the Gateway's membership table itself and sets the payment
/// provider's quantity to it, so the call is idempotent and a buggy Gateway cannot bill a wrong count.
///
/// Auth is the existing Gateway service credential, sent exactly as <see cref="AccountNotifyByTenantClient"/>
/// sends it: the same header, the same secret, and NO Authorization header. The secret is never logged.
/// </summary>
public sealed class TeamSeatSyncClient
{
    /// <summary>The website route that recounts and re-bills one team's seats.</summary>
    public const string SyncSeatsPath = "/api/v1/teams/sync-seats";

    /// <summary>
    /// The website's machine-readable code, on a 409, for "this team has no running bill, so there is no seat count
    /// to change" (devthrottle_internal #2299, <c>syncTeamSeats</c>). See <see cref="TeamSeatSyncResult.SubscriptionEnded"/>.
    /// </summary>
    public const string NoActiveSubscriptionCode = "no_active_team_subscription";

    private readonly HttpClient _client;
    private readonly string _baseUrl;

    public TeamSeatSyncClient(HttpClient? client = null, string? baseUrl = null)
    {
        _client = client ?? new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(30) };
        _baseUrl = DevThrottleApi.ResolveBaseUrl(baseUrl);
    }

    /// <summary>
    /// Ask the website to recount and re-bill <paramref name="teamId"/>'s seats. Throws only on a transport
    /// failure; a refusal is returned as a result with the website's own message. The team id and the token are
    /// not logged.
    /// </summary>
    /// <param name="serviceToken">The Gateway service secret. Never logged.</param>
    /// <param name="teamId">The team id, which is the tenant id.</param>
    public async Task<TeamSeatSyncResult> SyncSeatsAsync(string serviceToken, string teamId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceToken))
            throw new ArgumentException("A Gateway service token is required", nameof(serviceToken));
        if (string.IsNullOrWhiteSpace(teamId))
            throw new ArgumentException("A team id is required", nameof(teamId));

        var endpoint = $"{_baseUrl}{SyncSeatsPath}";
        FileLog.Write($"[TeamSeatSyncClient] SyncSeatsAsync: POST {endpoint}");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(BuildBody(teamId), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(AccountNotifyByTenantClient.ServiceTokenHeader, serviceToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            FileLog.Write($"[TeamSeatSyncClient] SyncSeatsAsync: status={status}, synced");
            return new TeamSeatSyncResult(true, status, null, null);
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var (message, code) = AccountNotifyByTenantClient.ParseError(body, status);
        FileLog.Write($"[TeamSeatSyncClient] SyncSeatsAsync: NOT synced, status={status}, code={code ?? "<none>"}");
        return new TeamSeatSyncResult(false, status, message, code);
    }

    /// <summary>The request body: the team id and nothing else. Internal so a test can pin the wire shape.</summary>
    internal static string BuildBody(string teamId) => new JsonObject { ["team_id"] = teamId }.ToJsonString();
}
