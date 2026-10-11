using System.Text.Json.Serialization;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway relay for "DevThrottle emails me" (issue #1318 consumer). A session or scheduled run on any
/// Director calls <c>POST /account/email</c> with a subject + body (+ optional attachments); the Gateway
/// injects the account access token it already holds (<see cref="DevThrottleAccountService.GetAccessTokenForForwarding"/>,
/// the SAME egress credential it uses for other account operations) and forwards to the cloud primitive
/// (<c>POST /api/v1/account/notify-owner</c>, devthrottle_internal #338) via <see cref="AccountNotifyClient"/>.
///
/// The Gateway holds NO Resend key and runs NO email code - the send happens entirely in the cloud, which
/// resolves the recipient from the token. The request has no recipient field, so it is single-recipient by
/// construction end to end. Wire contract:
///   POST /account/email  { "subject": string, "bodyText"?: string, "bodyHtml"?: string,
///                          "attachments"?: [ { "filename": string, "content": base64, "contentType"?: string } ] }
///        200 -> { "sent": true, "providerId"?: string }
///        400 -> { "sent": false, "error": string }   (bad input, forwarded from the cloud)
///        401 -> { "sent": false, "error": string }   (GENUINELY signed out - nothing stored on this Gateway)
///        403 -> { "sent": false, "error": string }   (hosted: no account is bound to this request)
///        503 -> { "sent": false, "error": string }   (this Gateway cannot act on the caller's account)
///        502 -> { "sent": false, "error": string }   (cloud unreachable / send failure - never a fake success)
///
/// WHICH of those non-2xx answers applies is NOT decided here. It is folded once by
/// <see cref="AccountActingCredential"/> and rendered verbatim (CLAUDE.md rule 7). This route used to make
/// that call itself, with a single <c>if (string.IsNullOrEmpty(token))</c> that answered 401 "not signed in
/// to DevThrottle - sign in from the Gateway tray, then retry". On the HOSTED Gateway that token is empty
/// for every caller always - hosted holds no account credential by design - so a signed-in paying user was
/// told to perform the one action they had already performed and that could not possibly help (issue #984).
/// The states are now distinct, and 401 is reserved for the one case where it is true.
///
/// HOSTED LIMIT, stated plainly: the cloud primitive resolves the recipient from an account access token,
/// and the hosted Gateway holds none for its tenants. So on hosted this route reports the truth and does NOT
/// send. Making it send is devthrottle_internal #986 - a tenant-addressed cloud sender authenticated by a
/// Gateway service credential, with the recipient resolved SERVER-side from the tenant (naming an address is
/// refused outright, so this Gateway can never address anyone). Its swap point is marked below.
///
/// Inherits the host-wide Gateway token middleware like the other <c>/account/*</c> endpoints (not on the
/// public-paths allow-list). The account token is never returned or logged (DT-05).
/// </summary>
internal static class AccountEmailEndpoint
{
    /// <param name="app">The route builder.</param>
    /// <param name="account">The Gateway-hosted credential service; null on a host with no credential store.</param>
    /// <param name="notify">The cloud owner-email client (the injectable egress seam).</param>
    /// <param name="tenantBoundary">
    /// The hosted tenant boundary (issue #984). On hosted it resolves the CALLER's own tenant so this route
    /// can report the truth about them. Omitting it on a hosted Gateway does NOT fall back to the self-host
    /// answer - the hosted path fails closed. Ignored off hosted mode.
    /// </param>
    /// <param name="tenants">The tenant registry, read on hosted for the caller's display email.</param>
    /// <param name="byTenant">
    /// The TENANT-ADDRESSED cloud sender (devthrottle_internal #986), used on the hosted path where no
    /// account token exists. Null disables the hosted send, and the route then reports the truthful refusal
    /// - never a fabricated success and never a fabricated sign-out.
    /// </param>
    public static void Map(IEndpointRouteBuilder app, bool hosted, DevThrottleAccountService? account, AccountNotifyClient notify,
        // REQUIRED AND NON-NULLABLE (finding I1-01): a forgotten boundary must be a compile error, never a
        // silent default. Self-host callers construct it over the SingleTenantContext.
        Tenancy.HostedTenantBoundary tenantBoundary, Tenancy.TenantRegistry? tenants = null,
        AccountNotifyByTenantClient? byTenant = null)
    {
        if (notify is null) throw new ArgumentNullException(nameof(notify));

        app.MapPost("/account/email", async (AccountEmailRequest? request, HttpContext http) =>
        {
            // Entry point: the delegate is the boundary, so the only try-catch lives here.
            if (request is null || string.IsNullOrWhiteSpace(request.Subject))
                return Results.BadRequest(new AccountEmailResponse(false, "subject is required.", null));

            // Issue #984: the Gateway rules ONCE on what the caller's account situation actually is, and this
            // route renders the verdict verbatim. It never re-derives what an empty token means - that
            // conditional is what turned "this hosted Gateway holds no credential of yours" into "you are not
            // signed in - sign in from the Gateway tray", told to a user who plainly was signed in.
            var verdict = await AccountActingCredential
                .ResolveAsync(AccountOperations.Email, http, hosted, account, tenantBoundary, tenants, http.RequestAborted)
                .ConfigureAwait(false);
            // HOSTED SEND (devthrottle_internal #986). The caller IS signed in and this Gateway holds no
            // account token for them - and now does not need one. It names their TENANT, resolved from their
            // own authenticated device key, and the cloud resolves the recipient. Note what is NOT happening:
            // no user account token is stored, read, or forwarded on this path, so the property that hosted
            // enrollment keeps neither token stays true.
            if (verdict.State == AccountActingState.HostedNoGatewayCredential && byTenant is not null && verdict.Tenant is { } tenant)
            {
                var result = await SendForTenantAsync(byTenant, tenant, verdict, request, http).ConfigureAwait(false);
                if (result is not null)
                    return result;
                // Null means this Gateway is not configured to make that call. Fall through to the verdict,
                // which reports the truth for the state we are actually in.
            }

            if (!verdict.IsReady)
            {
                // Every remaining state is a refusal, because none of them is a Gateway that could act:
                // genuinely signed out, no credential store, an unusable credential, a caller bound to no
                // tenant, or a miswired hosted deployment.
                FileLog.Write($"[AccountEmailEndpoint] POST /account/email: cannot send ({verdict.State}) -> {verdict.StatusCode}");
                return Results.Json(new AccountEmailResponse(false, verdict.Message, null), statusCode: verdict.StatusCode);
            }

            var token = verdict.Token!;

            try
            {
                var attachments = request.Attachments?
                    .Select(a => new NotifyAttachment(a.Filename ?? "", a.Content ?? "", a.ContentType))
                    .ToList();

                var result = await notify
                    .SendOwnerAsync(token, request.Subject!, request.BodyText, request.BodyHtml, attachments, http.RequestAborted)
                    .ConfigureAwait(false);

                if (!result.Sent)
                {
                    // A 4xx from the cloud is a caller input problem (forward it as a 400); anything else is
                    // an upstream/send failure (502). Never a fabricated success.
                    var status = result.StatusCode is >= 400 and < 500
                        ? StatusCodes.Status400BadRequest
                        : StatusCodes.Status502BadGateway;
                    FileLog.Write($"[AccountEmailEndpoint] POST /account/email: not sent (cloud status {result.StatusCode})");
                    return Results.Json(new AccountEmailResponse(false, result.Error, null), statusCode: status);
                }

                FileLog.Write("[AccountEmailEndpoint] POST /account/email: sent to owner.");
                return Results.Json(new AccountEmailResponse(true, null, result.ProviderId));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AccountEmailEndpoint] POST /account/email FAILED: {ex.Message}");
                return Results.Json(
                    new AccountEmailResponse(false,
                        "Could not reach the DevThrottle account service to send the email. Try again shortly.", null),
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    /// <summary>
    /// The HOSTED send (devthrottle_internal #986): name the caller's own tenant and let the cloud resolve
    /// the recipient. Returns null when this Gateway holds no service secret, so the caller falls through to
    /// the truthful refusal - it does NOT call unauthenticated.
    ///
    /// FAILING CLOSED ON A MISSING SECRET IS LOAD-BEARING, not tidiness. If an unconfigured Gateway called
    /// anyway, the cloud's 401 would mean either "your secret is wrong" or "you have no secret", and nobody
    /// could tell which from either end. Refusing here keeps that 401 diagnostic: it can only ever mean
    /// WRONG. The message says which of the two configuration steps is outstanding, because "unauthorized"
    /// on a route nobody has configured yet is exactly the sort of true-but-useless answer this whole issue
    /// is about.
    ///
    /// This is the one try-catch for this path (the delegate boundary rule): a transport failure is caught
    /// and reported as unreachable, never as a send.
    /// </summary>
    private static async Task<IResult?> SendForTenantAsync(
        AccountNotifyByTenantClient byTenant, string tenant, AccountActingVerdict verdict,
        AccountEmailRequest request, HttpContext http)
    {
        var serviceToken = AccountNotifyByTenantClient.ResolveServiceToken();
        if (serviceToken is null)
        {
            FileLog.Write($"[AccountEmailEndpoint] POST /account/email (hosted): {AccountNotifyByTenantClient.ServiceTokenEnvVar} is not set on this Gateway -> refusing to call the owner-email service unauthenticated");
            // NAME THE SIGNED-IN IDENTITY HERE TOO. This message replaces the fold's refusal on the hosted
            // path, and the fold names the identity for a reason: it is what makes the sentence impossible to
            // read as a sign-out. A configuration gap that forgot to say who you are would quietly reintroduce
            // the ambiguity issue #984 exists to remove - which is exactly how this was caught, by that issue's
            // own test going red when this path was added.
            var who = string.IsNullOrWhiteSpace(verdict.Email) ? "your DevThrottle account" : verdict.Email;
            return Results.Json(
                new AccountEmailResponse(false,
                    $"You are signed in to DevThrottle as {who}. This hosted Gateway is not yet configured to "
                    + $"send owner email - its {AccountNotifyByTenantClient.ServiceTokenEnvVar} setting is "
                    + "missing. This is a Gateway configuration step, not a sign-in problem, and signing in "
                    + "again will not change it. No email was sent.",
                    null),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var attachments = request.Attachments?
                .Select(a => new NotifyAttachment(a.Filename ?? "", a.Content ?? "", a.ContentType))
                .ToList();

            var result = await byTenant
                .SendOwnerForTenantAsync(serviceToken, tenant, verdict.AccountSubject, request.Subject!,
                    request.BodyText, request.BodyHtml, attachments, http.RequestAborted)
                .ConfigureAwait(false);

            if (result.Sent)
            {
                FileLog.Write("[AccountEmailEndpoint] POST /account/email (hosted): sent to the tenant's owner.");
                return Results.Json(new AccountEmailResponse(true, null, result.ProviderId));
            }

            // The cloud's message is written to be read by a human, so it is surfaced VERBATIM rather than
            // reworded here. A 4xx is a caller/config problem forwarded as-is; 429 keeps its own status so a
            // caller can honour Retry-After; anything else is an upstream failure. Never a fabricated success.
            var status = result.StatusCode switch
            {
                StatusCodes.Status429TooManyRequests => StatusCodes.Status429TooManyRequests,
                >= 400 and < 500 => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status502BadGateway,
            };
            FileLog.Write($"[AccountEmailEndpoint] POST /account/email (hosted): not sent (cloud status {result.StatusCode}, code {result.ErrorCode ?? "<none>"})");

            var response = Results.Json(new AccountEmailResponse(false, result.Error, null), statusCode: status);
            if (result.RetryAfterSeconds is { } retryAfter)
            {
                // Pass the cloud's own wait through untouched. It is computed from when the oldest hit in the
                // window actually expires, so honouring it is sufficient and a backoff of our own would only
                // fight it.
                http.Response.Headers["Retry-After"] = retryAfter.ToString();
            }
            return response;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountEmailEndpoint] POST /account/email (hosted) FAILED: {ex.Message}");
            return Results.Json(
                new AccountEmailResponse(false,
                    "Could not reach the DevThrottle account service to send the email. Try again shortly.", null),
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// The relay request body. Subject + body + optional attachments - deliberately NO recipient field, so
    /// the send can only ever reach the account owner (single-recipient by construction, issue #1318).
    /// </summary>
    internal sealed class AccountEmailRequest
    {
        [JsonPropertyName("subject")] public string? Subject { get; set; }
        [JsonPropertyName("bodyText")] public string? BodyText { get; set; }
        [JsonPropertyName("bodyHtml")] public string? BodyHtml { get; set; }
        [JsonPropertyName("attachments")] public List<AccountEmailAttachment>? Attachments { get; set; }
    }

    /// <summary>One attachment: a file name plus its base64-encoded bytes and an optional MIME type.</summary>
    internal sealed class AccountEmailAttachment
    {
        [JsonPropertyName("filename")] public string? Filename { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("contentType")] public string? ContentType { get; set; }
    }

    private sealed record AccountEmailResponse(
        [property: JsonPropertyName("sent")] bool Sent,
        [property: JsonPropertyName("error")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Error,
        [property: JsonPropertyName("providerId")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ProviderId);
}
