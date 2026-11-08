using System.Text.Json.Serialization;
using CcDirector.Core.Account;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// IN-MAIL BY DEFAULT, EMAIL ONLY WHEN CHOSEN (the owner's decision, 8 November 2026): "I guess we have email and
/// in-mail, and in-mail should be the default. Email, you should have to select it specially. ... I would want all of
/// mine as in-mail, but we still need to support email." In-mail is Your inbox. So this route, by DEFAULT, writes ONE
/// Your inbox message to the caller's own account, with the same subject, and sends no email. It is done HERE, on the
/// route, and not in the callers, because about 36 schedule prompts and several briefs nobody here may edit still say
/// <c>cc-devthrottle email owner</c>, and older command lines on other machines call this same route: every one of them
/// now lands in Your inbox with no edit. No push goes to the phone for these messages (the owner, same day: the phone
/// notifications are noisy and do not work; the message shows in the app).
///
///   - WHO SENT IT. A session is named exactly as <c>POST /sessions/{sid}/inbox</c> names it (number, name, factory,
///     seat), from the Gateway's own records. A session key is refused on this route by <see cref="SessionKeyGuard"/>,
///     so the command line authenticates with the account's key as before and ALSO hands its session key in
///     <see cref="SessionKeyHeader"/>; the route verifies that key and names the session only when it is live and in
///     the caller's account. Any other caller - an older command line, a script on a Director's key, the machine
///     token - is shown from "Owner email - no session named", and the message's first line says which key sent it.
///   - AN HTML BODY becomes plain text with every link's address kept (<see cref="OwnerEmailText"/>); a plain-text body
///     is used as it is.
///   - EMAIL WHEN CHOSEN: <c>"deliver": "email"</c> sends the real email exactly as before and writes nothing to Your
///     inbox.
///   - WHAT CANNOT BE IN-MAIL is sent as an email anyway, never dropped: a request with attachments, a body over Your
///     inbox's limit, or a subject Your inbox cannot hold. When email was not chosen, a short Your inbox note ALSO says
///     an email was sent and why, so nothing goes silent.
///   - THE ANSWER keeps the shape old clients check - <c>{"sent": true, "providerId": ...}</c> on success - and adds
///     <c>deliveredTo</c> ("inbox" or "email"), <c>inboxMessageId</c>, and <c>message</c>: the sentence, decided here,
///     saying where it went. On Your inbox, <c>providerId</c> is <c>your-inbox:&lt;message id&gt;</c>.
///
/// The Gateway relay for "DevThrottle emails me" (issue #1318 consumer), used when email is chosen or needed. A session or scheduled run on any
/// Director calls <c>POST /account/email</c> with a subject + body (+ optional attachments); the Gateway
/// injects the account access token it already holds (<see cref="DevThrottleAccountService.GetAccessTokenForForwarding"/>,
/// the SAME egress credential it uses for other account operations) and forwards to the cloud primitive
/// (<c>POST /api/v1/account/notify-owner</c>, devthrottle_internal #338) via <see cref="AccountNotifyClient"/>.
///
/// The Gateway holds NO Resend key and runs NO email code - the send happens entirely in the cloud, which
/// resolves the recipient from the token. The request has no recipient field, so it is single-recipient by
/// construction end to end. Wire contract:
///   POST /account/email  { "subject": string, "bodyText"?: string, "bodyHtml"?: string, "deliver"?: "inbox" | "email",
///                          "attachments"?: [ { "filename": string, "content": base64, "contentType"?: string } ] }
///        200 -> { "sent": true, "providerId": string, "deliveredTo": "inbox", "inboxMessageId": string, "message": string }
///        200 -> { "sent": true, "providerId"?: string, "deliveredTo": "email", "inboxMessageId"?: string (the note), "message": string }
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
        AccountNotifyByTenantClient? byTenant = null,
        InMail? inMail = null)
    {
        if (notify is null) throw new ArgumentNullException(nameof(notify));

        app.MapPost("/account/email", async (AccountEmailRequest? request, HttpContext http) =>
        {
            // Entry point: the delegate is the boundary, so the only try-catch lives here.
            if (request is null || string.IsNullOrWhiteSpace(request.Subject))
                return Results.BadRequest(new AccountEmailResponse(false, "subject is required.", null));

            var deliver = (request.Deliver ?? "").Trim().ToLowerInvariant();
            if (deliver.Length > 0 && deliver is not (DeliverInbox or DeliverEmail))
                return Results.BadRequest(new AccountEmailResponse(false,
                    $"\"deliver\" is \"{request.Deliver}\"; it must be \"{DeliverInbox}\" (the default: Your inbox) or \"{DeliverEmail}\" (a real email). Nothing was sent.", null));
            var emailChosen = deliver == DeliverEmail;
            FileLog.Write($"[AccountEmailEndpoint] POST /account/email: identity={AuthMiddleware.IdentityKind(http)} deliver={(emailChosen ? DeliverEmail : DeliverInbox)} attachments={request.Attachments?.Count ?? 0}");

            // ---------------------------------------------------------------- in-mail: the default (8 November 2026)
            InMailNote? note = null;
            if (!emailChosen)
            {
                if (inMail is null)
                {
                    FileLog.Write("[AccountEmailEndpoint] POST /account/email: Your inbox is not wired on this Gateway -> 503");
                    return Results.Json(new AccountEmailResponse(false,
                        "This Gateway is not set up to write to Your inbox, which is where this route delivers unless email is "
                        + "chosen. Nothing was sent. To send a real email, ask for it: \"deliver\": \"email\" "
                        + "(cc-devthrottle email owner --email).", null), statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                if (inMail.ResolveTenant(http) is not { } inboxTenant)
                {
                    FileLog.Write("[AccountEmailEndpoint] POST /account/email: no account bound to this request -> 403");
                    return Results.Json(new AccountEmailResponse(false,
                        "No DevThrottle account is bound to this request, so there is no Your inbox to write to. Nothing was sent.", null),
                        statusCode: StatusCodes.Status403Forbidden);
                }

                var caller = Caller(inMail, inboxTenant, http);
                var plain = !string.IsNullOrWhiteSpace(request.BodyText) ? request.BodyText! : OwnerEmailText.FromHtml(request.BodyHtml);
                var attachmentCount = request.Attachments?.Count ?? 0;
                if (string.IsNullOrWhiteSpace(plain) && attachmentCount == 0)
                    return Results.BadRequest(new AccountEmailResponse(false,
                        "The body is empty. Write what the owner needs to know in bodyText or bodyHtml. Nothing was sent.", null));

                var subject = request.Subject!.Trim();
                var body = caller.Session is null ? caller.WhoLine + "\n\n" + plain : plain;
                var why = WhyNotInMail(request, subject, body);
                if (why is null)
                {
                    try
                    {
                        var written = Write(inMail, inboxTenant, caller, subject, body);
                        FileLog.Write($"[AccountEmailEndpoint] POST /account/email: delivered to Your inbox {written.Id:D} from={written.FromKind} session={written.SessionId ?? "(none)"}; no email sent");
                        return Results.Json(new AccountEmailResponse(true, null, YourInboxProviderPrefix + written.Id.ToString("D"),
                            DeliverInbox, written.Id.ToString("D"), InboxWords));
                    }
                    catch (YourInboxRefusedException ex)
                    {
                        FileLog.Write($"[AccountEmailEndpoint] POST /account/email: Your inbox refused ({ex.Code}) -> {ex.Status}");
                        return Results.Json(new AccountEmailResponse(false, ex.Message + " Nothing was sent.", null), statusCode: ex.Status);
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"[AccountEmailEndpoint] POST /account/email FAILED writing to Your inbox: {ex.Message}");
                        return Results.Json(new AccountEmailResponse(false,
                            $"Could not write to Your inbox: {ex.Message}. Nothing was sent, and no email was sent in its place.", null),
                            statusCode: StatusCodes.Status500InternalServerError);
                    }
                }

                // It cannot be in-mail, so it goes as the email it was always going to be - and a note in Your inbox says so.
                FileLog.Write($"[AccountEmailEndpoint] POST /account/email: cannot be in-mail ({why}) -> sending as email with a Your inbox note");
                note = new InMailNote(inMail, inboxTenant, caller, subject, why);
            }

            IResult Sent(string? providerId) => SentAnswer(note, providerId, emailChosen);

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
                var result = await SendForTenantAsync(byTenant, tenant, verdict, request, http, Sent).ConfigureAwait(false);
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
                return Sent(result.ProviderId);
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
        AccountEmailRequest request, HttpContext http, Func<string?, IResult> sent)
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
                return sent(result.ProviderId);
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

        /// <summary>Where it goes: "inbox" (the default, also when absent) or "email" - a real email, chosen on purpose
        /// (8 November 2026). Anything else is refused.</summary>
        [JsonPropertyName("deliver")] public string? Deliver { get; set; }
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
        string? ProviderId,
        [property: JsonPropertyName("deliveredTo")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? DeliveredTo = null,
        [property: JsonPropertyName("inboxMessageId")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? InboxMessageId = null,
        [property: JsonPropertyName("message")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Message = null);

    // ---------------------------------------------------------------- in-mail (8 November 2026)

    /// <summary>The header the command line hands its own session key in, so the message is named for that session.
    /// The request still authenticates with the account's key: a session key is refused on this route.</summary>
    public const string SessionKeyHeader = "X-CC-Session-Key";

    /// <summary>The default: Your inbox.</summary>
    public const string DeliverInbox = "inbox";

    /// <summary>A real email, chosen on purpose.</summary>
    public const string DeliverEmail = "email";

    /// <summary>What <c>providerId</c> starts with when the message went to Your inbox, so a reader of an old client's
    /// record can tell it from an email provider's id.</summary>
    public const string YourInboxProviderPrefix = "your-inbox:";

    /// <summary>The sentence a delivery to Your inbox is answered with.</summary>
    internal const string InboxWords =
        "Delivered to Your inbox, not sent as an email: since 8 November 2026 the owner email route writes to Your inbox unless "
        + "email is chosen. To send a real email, ask for it: cc-devthrottle email owner --email (\"deliver\": \"email\").";

    /// <summary>The sentence an email that was asked for is answered with.</summary>
    internal const string EmailChosenWords = "Sent as an email to the account owner, as asked. Nothing was written to Your inbox.";

    /// <summary>What the in-mail path reads. Every function is required, so a wiring line that forgot one fails to compile.</summary>
    /// <param name="Store">The account's Your inbox.</param>
    /// <param name="ResolveTenant">The caller's account, or null when no account is bound to the request.</param>
    /// <param name="ResolveSessionKey">The live session a session key belongs to, or null when the key is unknown, ended,
    /// or cannot be checked.</param>
    /// <param name="Naming">A session's number and name, as <c>POST /sessions/{sid}/inbox</c> reads them.</param>
    /// <param name="FactorySeatOf">The factory and seat a session was started as, or null.</param>
    /// <param name="DeviceOf">The machine name, platform and type of an enrolled device, or null.</param>
    /// <param name="NowUtc">The clock.</param>
    internal sealed record InMail(
        YourInboxStore Store,
        Func<HttpContext, TenantId?> ResolveTenant,
        Func<string, SessionCredentialIdentity?> ResolveSessionKey,
        Func<TenantId, string, DevReportSessionNaming> Naming,
        Func<TenantId, string, DevReportFactorySeatIdentity?> FactorySeatOf,
        Func<string, DeviceDisplay?> DeviceOf,
        Func<DateTime> NowUtc);

    /// <summary>Who called: a named session, or none - and then the line that says which key sent it.</summary>
    private sealed record CallerIdentity(YourInboxSender? Session, string WhoLine);

    /// <summary>A note to write in Your inbox once the email it describes has been sent.</summary>
    private sealed record InMailNote(InMail Parts, TenantId Tenant, CallerIdentity Caller, string Subject, string Why);

    /// <summary>
    /// WHO SENT IT, decided once from what the Gateway verified - never from the body. A session is named when the request
    /// was authenticated as one, or when the session key in <see cref="SessionKeyHeader"/> is live and in the caller's own
    /// account. Otherwise the key that authenticated the request is named in words.
    /// </summary>
    private static CallerIdentity Caller(InMail parts, TenantId tenant, HttpContext http)
    {
        var verified = AuthMiddleware.CallingSession(http);
        var handed = http.Request.Headers[SessionKeyHeader].ToString().Trim();
        var unverified = false;
        if (verified is null && handed.Length > 0)
        {
            var resolved = parts.ResolveSessionKey(handed);
            if (resolved is not null && resolved.Tenant == tenant)
                verified = resolved;
            else
                unverified = true;
        }
        if (verified is not null)
        {
            var sessionId = verified.SessionId.ToString("D");
            var naming = parts.Naming(tenant, sessionId);
            var seat = parts.FactorySeatOf(tenant, sessionId);
            FileLog.Write($"[AccountEmailEndpoint] Caller: session {sessionId} factory={seat?.Factory ?? "(none)"}");
            return new CallerIdentity(new YourInboxSender(YourInboxWords.Senders.Session, sessionId, naming.Number, naming.Name, seat?.Factory, seat?.Seat), "");
        }

        string who;
        if (AuthMiddleware.AuthenticatedDevice(http) is { } device)
        {
            var display = parts.DeviceOf(device.DeviceId);
            who = display is null
                ? $"Sent with the key of a device on this account ({device.DeviceType}, id {device.DeviceId}), by a caller that named no session."
                : $"Sent with the key of the {display.DeviceType} \"{display.MachineName}\" ({display.Platform}), by a caller that named no session.";
        }
        else if (AuthMiddleware.IdentityKind(http) == Core.Sessions.SubmissionIdentityKinds.MachineToken)
            who = "Sent with this Gateway's machine token, which names no machine and no session.";
        else
            who = "Sent with no key checked: this Gateway's sign-in check is off, so who sent it is not known.";
        if (unverified)
            who += " It carried a session key the Gateway could not verify, so no session is named.";
        FileLog.Write($"[AccountEmailEndpoint] Caller: no session named (identity={AuthMiddleware.IdentityKind(http)}, unverifiedSessionKey={unverified})");
        return new CallerIdentity(null, who);
    }

    /// <summary>Why a request cannot be a Your inbox message, in words that finish "because ...", or null when it can.</summary>
    private static string? WhyNotInMail(AccountEmailRequest request, string subject, string body)
    {
        if (request.Attachments is { Count: > 0 } files)
        {
            var names = string.Join(", ", files.Take(10).Select(a => Cut(string.IsNullOrWhiteSpace(a.Filename) ? "(no name)" : a.Filename!, 100)));
            if (files.Count > 10) names += $", and {files.Count - 10} more";
            return $"it carries {files.Count} attachment{(files.Count == 1 ? "" : "s")} ({names}), and Your inbox holds no files";
        }
        if (subject.Contains('\n') || subject.Contains('\r'))
            return "its subject is more than one line, and Your inbox holds a one-line subject";
        if (subject.Length > YourInboxStore.MaxSubjectLength)
            return $"its subject is {subject.Length} characters, and Your inbox holds a subject of at most {YourInboxStore.MaxSubjectLength}";
        if (body.Length > YourInboxStore.MaxBodyLength)
            return $"its body is {body.Length} characters, and Your inbox holds at most {YourInboxStore.MaxBodyLength}";
        return null;
    }

    /// <summary>Write one message to Your inbox from the caller: as the named session, or from the owner email seat.</summary>
    private static YourInboxMessageEntity Write(InMail parts, TenantId tenant, CallerIdentity caller, string subject, string body)
        => caller.Session is { } sender
            ? parts.Store.Send(tenant, new YourInboxSendInput(subject, body, sender, parts.NowUtc()))
            : parts.Store.Add(tenant, YourInboxStore.ForOwnerEmail(tenant, subject, body, parts.NowUtc()));

    /// <summary>
    /// The answer to an email that was sent. When it went as an email only because it could not be in-mail, the note is
    /// written first, so the answer can say whether it was. A note that cannot be written does not undo the email, which
    /// has gone: the answer says the note is missing.
    /// </summary>
    private static IResult SentAnswer(InMailNote? note, string? providerId, bool emailChosen)
    {
        if (note is null)
            return Results.Json(new AccountEmailResponse(true, null, providerId, DeliverEmail, null,
                emailChosen ? EmailChosenWords : "Sent as an email to the account owner."));

        var subject = Cut("Sent to you as an email: " + note.Subject.Replace('\r', ' ').Replace('\n', ' '), YourInboxStore.MaxSubjectLength);
        var body = $"An email with the subject \"{Cut(note.Subject, YourInboxStore.MaxSubjectLength)}\" was sent to your email address, "
            + $"not to Your inbox, because {note.Why}. Read it in your email.";
        if (note.Caller.Session is null) body = note.Caller.WhoLine + "\n\n" + body;
        try
        {
            var written = Write(note.Parts, note.Tenant, note.Caller, subject, body);
            FileLog.Write($"[AccountEmailEndpoint] POST /account/email: sent as email; Your inbox note {written.Id:D}");
            return Results.Json(new AccountEmailResponse(true, null, providerId, DeliverEmail, written.Id.ToString("D"),
                $"Sent as an email to the account owner, not to Your inbox, because {note.Why}. A short note in Your inbox says so."));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountEmailEndpoint] POST /account/email: sent as email, but the Your inbox note FAILED: {ex.Message}");
            return Results.Json(new AccountEmailResponse(true, null, providerId, DeliverEmail, null,
                $"Sent as an email to the account owner, not to Your inbox, because {note.Why}. The note in Your inbox that should "
                + $"say so could not be written: {ex.Message}"));
        }
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..(max - 3)].TrimEnd() + "...";
}
