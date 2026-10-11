using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The ONE place the Gateway rules on "can this Gateway act on the caller's DevThrottle account for this
/// operation, and if not, what is ACTUALLY wrong?" (issue #984). Per CLAUDE.md rule 7 the verdict is folded
/// here once - state, finished user-facing message, and HTTP status - and every <c>/account/*</c> route
/// renders it verbatim. No route re-derives what an absent token means.
///
/// WHY THIS EXISTS. Every account route used to open with the same three lines:
/// <code>
///   var token = account?.GetAccessTokenForForwarding();
///   if (string.IsNullOrEmpty(token))
///       -> "not signed in to DevThrottle ... Sign in from the Gateway tray, then retry."
/// </code>
/// That conditional asks whether THIS GATEWAY holds a signed-in credential, and then reports the answer as
/// a statement about THE CALLER. On the hosted Gateway those are different questions with different
/// answers: a hosted Gateway holds NO account credential at all, by design - it is one shared multi-tenant
/// Gateway, identity arrives per device and is bound to a tenant at enrollment, and
/// <see cref="HostedEnrollmentEndpoint"/> stores neither the account access token nor the refresh token. So
/// on hosted that token is empty for EVERY caller, ALWAYS, and a correctly signed-in paying subscriber was
/// told "not signed in - sign in from the Gateway tray, then retry": the one action they had already
/// performed, and the one action that could never change the outcome.
///
/// It cost real time twice in a single day, and it is not merely a bad string. A product that misreports
/// its own state teaches the user that either their sign-in is broken or the software does not know what it
/// is doing, and both readings are worse than the feature being honestly unavailable.
///
/// THE RULE THAT MATTERS: "this Gateway cannot act on your account" and "you are not signed in" are
/// DIFFERENT STATEMENTS and must never share a code path. An absent forwarding token is never, on its own,
/// evidence of a signed-out user.
///
/// The states this distinguishes, each with its own message and status:
/// <list type="bullet">
/// <item><see cref="AccountActingState.Ready"/> - a forwardable account token is in hand; act.</item>
/// <item><see cref="AccountActingState.HostedNoGatewayCredential"/> - hosted, the caller's tenant IS
///   resolved: they are signed in, and this Gateway simply holds no credential of theirs to act with.</item>
/// <item><see cref="AccountActingState.HostedNoTenant"/> - hosted, nothing bound the request to a tenant:
///   a deny, not a sign-out.</item>
/// <item><see cref="AccountActingState.HostedMiswired"/> - hosted mode with no tenant boundary wired: a
///   deployment fault, refused loudly rather than answered wrongly.</item>
/// <item><see cref="AccountActingState.NoCredentialStore"/> - self-host on a machine with no operating
///   system credential store, so no account can be held here at all.</item>
/// <item><see cref="AccountActingState.SignedOut"/> - self-host with nothing stored: the ONLY state where
///   "sign in, then retry" is true, and the only one that answers 401.</item>
/// <item><see cref="AccountActingState.CredentialUnusable"/> - self-host, a credential IS stored but could
///   not be made forwardable even after a refresh attempt: an internal inconsistency, reported as one.</item>
/// </list>
///
/// Security (carries DT-05): <see cref="AccountActingVerdict.Token"/> is for attaching to an outbound
/// request only. No token is ever logged here, and no token field reaches any response contract.
/// </summary>
internal static class AccountActingCredential
{
    /// <summary>
    /// Resolves the acting verdict for one request. On HOSTED this never touches
    /// <paramref name="account"/> - the hosted Gateway's own credential is meaningless to the caller, so
    /// asking about it can only produce a wrong answer. Gated on the REQUIRED <paramref name="hosted"/> value
    /// (read once at startup into <c>GatewayHostOptions</c>), NOT on whether a boundary was passed in: selecting
    /// the hosted path from an optional argument would FAIL OPEN, letting a one-word wiring mistake drop a
    /// hosted Gateway back onto the self-host answer -
    /// the exact defect this type exists to stop. A hosted Gateway with no boundary FAILS CLOSED instead.
    /// </summary>
    /// <param name="operation">What the calling route is trying to do, for the finished message.</param>
    /// <param name="ctx">The request, used on hosted to resolve the caller's own tenant.</param>
    /// <param name="account">The Gateway-hosted credential service; null on a host with no credential store.</param>
    /// <param name="boundary">The hosted tenant boundary. Required on hosted; ignored off hosted.</param>
    /// <param name="tenants">The tenant registry, read on hosted for the caller's display email.</param>
    /// <param name="ct">Cancellation for the refresh attempt.</param>
    public static async Task<AccountActingVerdict> ResolveAsync(
        AccountOperation operation,
        HttpContext ctx,
        bool hosted,
        DevThrottleAccountService? account,
        Tenancy.HostedTenantBoundary? boundary,
        Tenancy.TenantRegistry? tenants,
        CancellationToken ct = default)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));

        if (hosted)
            return Hosted(operation, ctx, boundary, tenants);

        return await SelfHostAsync(operation, account, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The HOSTED verdict, folded from the CALLER'S OWN authenticated device-key binding and from nothing
    /// else. signedIn is never false here for an authenticated tenant-bound caller: the two failure answers
    /// are "I cannot resolve who you are" (a deny) and "I cannot act on your account" (a Gateway limit),
    /// and neither of them is "you are signed out".
    /// </summary>
    private static AccountActingVerdict Hosted(
        AccountOperation operation, HttpContext ctx,
        Tenancy.HostedTenantBoundary? boundary, Tenancy.TenantRegistry? tenants)
    {
        // A hosted Gateway that cannot tell who is asking has no honest answer to give, and it certainly
        // does not have a signed-out one. Say the server cannot answer, and say it LOUD - this is a
        // deployment fault that will not recover on its own.
        if (boundary is not { IsHosted: true })
        {
            FileLog.Write($"[AccountActingCredential] {operation.RouteLabel} (hosted): MISWIRED - hosted mode with no hosted tenant boundary, so the caller's tenant cannot be resolved. Refusing rather than reporting a false signed-out state.");
            return new AccountActingVerdict(
                AccountActingState.HostedMiswired, null, null,
                "This hosted DevThrottle Gateway cannot resolve which account is calling, so it will not answer. "
                + "This is a fault in the Gateway deployment, not a problem with your account or your sign-in.",
                StatusCodes.Status503ServiceUnavailable);
        }

        var resolved = boundary.ResolveRequestTenant(ctx);
        if (resolved is not { } tenant)
        {
            FileLog.Write($"[AccountActingCredential] {operation.RouteLabel} (hosted): DENIED - no tenant is bound to this request");
            return new AccountActingVerdict(
                AccountActingState.HostedNoTenant, null, null,
                "No DevThrottle account is bound to this request, so this hosted Gateway will not act on one. "
                + "The device making this call is not enrolled to an account on this Gateway.",
                StatusCodes.Status403Forbidden);
        }

        // The caller IS signed in. The hosted Gateway simply holds nothing it can act WITH on their behalf,
        // because it never keeps the account token - enrollment validates it, mints a tenant and a device
        // key, and discards it. Naming the signed-in identity in the message is the point: it makes the
        // sentence impossible to misread as a sign-out.
        var email = tenants?.EmailForTenant(tenant);
        // Carried on the verdict so a route that CAN act for a tenant without a user token has what it needs
        // already resolved, from the authenticated device key, at the one place that resolves it. See
        // AccountActingVerdict.Tenant / AccountSubject for why this is on the verdict rather than re-derived.
        var subject = tenants?.SubjectForTenant(tenant);
        var who = string.IsNullOrWhiteSpace(email) ? "your DevThrottle account" : email;
        var message =
            $"You are signed in to DevThrottle as {who}. This hosted DevThrottle Gateway does not hold a "
            + $"DevThrottle account credential for your account, so it cannot {operation.Description}. "
            + "This is a limitation of the shared hosted Gateway - it is not a sign-in problem, and signing "
            + "in again will not change it.";
        if (!string.IsNullOrWhiteSpace(operation.HostedReassurance))
            message += " " + operation.HostedReassurance;

        // The email is user identity and is never written to the log; only whether it resolved.
        FileLog.Write($"[AccountActingCredential] {operation.RouteLabel} (hosted): caller IS signed in (identity {(email is null ? "unavailable" : "resolved")}); this Gateway holds no account credential to {operation.Description} with -> reporting the Gateway limit, NOT a sign-out");
        return new AccountActingVerdict(
            AccountActingState.HostedNoGatewayCredential, null, email, message,
            StatusCodes.Status503ServiceUnavailable,
            Tenant: tenant.Value, AccountSubject: subject);   // TenantId.Value is the tenant row id string
    }

    /// <summary>
    /// The SELF-HOST verdict, where one Gateway holds one account and the question "does this Gateway hold
    /// a credential?" really is a question about the user. Renews the credential BEFORE acting (issue #984
    /// acceptance 3 - see the comment at the refresh call for why "before failing" would have been a no-op),
    /// and separates "nothing is stored" from "something is stored but is not usable".
    /// </summary>
    private static async Task<AccountActingVerdict> SelfHostAsync(
        AccountOperation operation, DevThrottleAccountService? account, CancellationToken ct)
    {
        if (account is null)
        {
            FileLog.Write($"[AccountActingCredential] {operation.RouteLabel}: no credential service on this host -> this Gateway can hold no DevThrottle account");
            return new AccountActingVerdict(
                AccountActingState.NoCredentialStore, null, null,
                "This Gateway host has no operating system credential store, so it cannot hold a DevThrottle "
                + $"account and cannot {operation.Description}.",
                StatusCodes.Status503ServiceUnavailable);
        }

        // Issue #984 acceptance 3, renewing BEFORE acting rather than after failing. Refreshing only on the
        // way out of a failure would be theatre here: the credential service deliberately declines to spend a
        // refresh on an access token that does not verify (it is not ours to renew), which is the ONLY state
        // in which the read below comes back empty - so a refresh at that point could never change the
        // outcome. Renewing first, however, does real work: it stops the Gateway forwarding an access token
        // it could have renewed, and it turns an expired-and-unrenewable credential into something this
        // method can NAME rather than something the cloud rejects with a message about a different problem.
        //
        // Cheap by construction: a no-op with no network call when the token has comfortable life left, and
        // single-flight, so concurrent account calls share one exchange rather than racing rotation.
        await account.RefreshIfNeededAsync(ct).ConfigureAwait(false);

        // BOTH conditions, and they are not the same question. The token read says "is there something to
        // attach"; IsLoggedIn says "is it genuinely usable" - it is false for an expired token whose refresh
        // is persistently failing (issue #911), which the token read still hands back happily. Forwarding
        // that is how a dead credential produces a cloud rejection naming some other cause.
        var token = account.GetAccessTokenForForwarding();
        if (!string.IsNullOrEmpty(token) && account.IsLoggedIn())
            return new AccountActingVerdict(AccountActingState.Ready, token, account.GetIdentity()?.Email, "", StatusCodes.Status200OK);

        // Nothing stored at all is the ONE state where telling the user to sign in is true and useful.
        if (!account.HasStoredCredential())
        {
            FileLog.Write($"[AccountActingCredential] {operation.RouteLabel}: no credential is stored on this Gateway -> genuinely signed out");
            return new AccountActingVerdict(
                AccountActingState.SignedOut, null, null,
                "This Gateway is not signed in to DevThrottle - it holds no account credential, so there is no "
                + $"account to {operation.Description} with. Sign in from the Gateway tray, then retry.",
                StatusCodes.Status401Unauthorized);
        }

        // A credential IS stored and still is not usable. That is an internal inconsistency, and dressing it
        // up as a sign-out is what sent people to re-run a sign-in that was already done.
        FileLog.Write($"[AccountActingCredential] {operation.RouteLabel}: a credential IS stored but is not usable after a refresh attempt (persistentRefreshFailure={account.HasPersistentRefreshFailure}) -> reporting an unusable credential, NOT a sign-out");
        var detail = account.HasPersistentRefreshFailure
            ? "Renewing it is failing persistently, so retrying alone will not repair it."
            : "It could not be verified, and renewing it did not produce a usable token.";
        return new AccountActingVerdict(
            AccountActingState.CredentialUnusable, null, account.GetIdentity()?.Email,
            "This Gateway holds a DevThrottle credential for your account, but it is not in a usable state, so "
            + $"it cannot {operation.Description}. {detail} Signing out and signing in again from the Gateway "
            + "tray will replace it.",
            StatusCodes.Status503ServiceUnavailable);
    }
}

/// <summary>
/// What an <c>/account/*</c> route is trying to do, so the folded message names the real operation instead
/// of a generic one. Defined once per route in <see cref="AccountOperations"/>, so adding a route is one
/// edit here and never a new conditional in an endpoint.
/// </summary>
/// <param name="RouteLabel">The route, for the log line only (never shown to a user).</param>
/// <param name="Description">
/// The operation as a verb phrase that completes "it cannot ..." - for example "send an email to the
/// account owner". Lower case, no trailing punctuation.
/// </param>
/// <param name="HostedReassurance">
/// An optional extra sentence appended on the hosted path, for surfaces where the user's first fear needs
/// answering directly (a billing surface, above all).
/// </param>
internal sealed record AccountOperation(string RouteLabel, string Description, string? HostedReassurance = null);

/// <summary>The operations the account routes perform, one per route.</summary>
internal static class AccountOperations
{
    public static readonly AccountOperation Email = new(
        "POST /account/email",
        "send an email to the account owner",
        "No email was sent.");

    /// <summary>
    /// The credit balance. This is a BILLING surface, and the old answer here - a bare
    /// <c>{"signedIn":false,"balanceMicros":null}</c> served to a paying subscriber - reads as "my account is
    /// gone" or "my money is gone". The reassurance answers exactly that fear, in the response itself.
    /// </summary>
    public static readonly AccountOperation Credits = new(
        "GET /account/credits",
        "read your credit balance",
        "Your account is active and your credit balance is unaffected - it simply cannot be read through "
        + "this hosted Gateway. Check it on the DevThrottle website.");

    /// <summary>
    /// The free Pro trial (issue #1243). Only the DENY and MISWIRED verdicts are ever rendered from this one:
    /// the trial ledger is the Gateway's OWN table, so a resolved caller needs no forwardable credential and
    /// the HostedNoGatewayCredential message is never reached. The reassurance still matters on the paths that
    /// ARE reached, because "we cannot tell you about your trial" is the sentence most likely to be misread as
    /// "you do not have one".
    /// </summary>
    public static readonly AccountOperation Trial = new(
        "GET /account/trial",
        "tell you whether a free Pro trial is running",
        "This does not mean you have no trial - it means this Gateway could not find out.");

    public static readonly AccountOperation Logout = new(
        "POST /account/logout",
        "sign your account out",
        "Nothing was signed out and nothing was changed. On the hosted Gateway your access comes from this "
        + "device's enrollment, so to end it, remove this device under your account's devices.");
}

/// <summary>The distinct account states an <c>/account/*</c> route can be in. See <see cref="AccountActingCredential"/>.</summary>
internal enum AccountActingState
{
    /// <summary>A forwardable account access token is in hand.</summary>
    Ready,

    /// <summary>Hosted, caller's tenant resolved: they ARE signed in; this Gateway holds no credential of theirs.</summary>
    HostedNoGatewayCredential,

    /// <summary>Hosted, no tenant bound to the request: a deny.</summary>
    HostedNoTenant,

    /// <summary>Hosted mode with no tenant boundary wired: a deployment fault.</summary>
    HostedMiswired,

    /// <summary>Self-host on a machine with no operating system credential store.</summary>
    NoCredentialStore,

    /// <summary>Self-host, nothing stored: the only state where "sign in, then retry" is true.</summary>
    SignedOut,

    /// <summary>Self-host, a credential is stored but is not forwardable even after a refresh attempt.</summary>
    CredentialUnusable,
}

/// <summary>
/// The folded verdict a route renders verbatim. Carries no token field on any response contract - the
/// <see cref="Token"/> is for attaching to an outbound request only and is never serialized or logged (DT-05).
/// </summary>
/// <param name="State">Which of the distinct account states this request is in.</param>
/// <param name="Token">The forwardable access token; non-null only when <see cref="State"/> is Ready.</param>
/// <param name="Email">The caller's identity when it resolved, else null. Never fabricated.</param>
/// <param name="Message">The finished, user-facing sentence. Empty when Ready.</param>
/// <param name="StatusCode">The HTTP status a route returning an error envelope should use.</param>
/// <param name="Tenant">
/// The CALLER's tenant id on the hosted path, resolved from their authenticated device key; null off hosted
/// and on the deny paths. This is the identifier a cloud route that acts for a tenant WITHOUT a user token
/// takes (devthrottle_internal #986: the recipient is resolved cloud-side from the tenant, and naming an
/// address is refused outright). It is carried here rather than re-resolved at each route so there stays
/// exactly one place that turns a device key into an identity - the property that keeps a caller from ever
/// naming a tenant that is not its own.
/// </param>
/// <param name="AccountSubject">
/// The verified account subject behind <see cref="Tenant"/>, for use as a CROSS-CHECK on such a call - it
/// turns a wrong tenant id into a refusal instead of a correctly-delivered message to the wrong person. It
/// must never be used to RESOLVE a recipient: a subject that selects the target is a subject that can select
/// somebody else's. Null when unresolvable. Personally identifying - never logged.
/// </param>
internal sealed record AccountActingVerdict(
    AccountActingState State,
    string? Token,
    string? Email,
    string Message,
    int StatusCode,
    string? Tenant = null,
    string? AccountSubject = null)
{
    /// <summary>True when the route may go ahead and act on the account.</summary>
    public bool IsReady => State == AccountActingState.Ready;

    /// <summary>
    /// Whether THE CALLER is signed in to DevThrottle - which is NOT the same question as whether this
    /// Gateway can act for them. True on the hosted path whenever a tenant resolved, because an enrolled
    /// tenant is signed in whatever this Gateway can or cannot do on their behalf. This is the value a
    /// <c>signedIn</c> field must carry; the old code put "could this Gateway act?" there and that is the
    /// whole defect (issue #984).
    /// </summary>
    public bool CallerIsSignedIn => State is AccountActingState.Ready or AccountActingState.HostedNoGatewayCredential;
}
