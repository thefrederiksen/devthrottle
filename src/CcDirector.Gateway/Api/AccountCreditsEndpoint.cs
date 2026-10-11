using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The account credit-balance proxy (issue #884): <c>GET /account/credits</c>. The Settings account
/// section shows the signed-in account's balance and refreshes it after a hosted AI action, but the
/// Cockpit must NEVER hold the account token or call the cloud directly - the token lives on the
/// Gateway. So the Gateway proxies: it reads its own stored account token
/// (<see cref="DevThrottleAccountService.GetAccessTokenForForwarding"/>, the SAME egress credential it
/// already uses for account and device operations), calls the cloud credits endpoint
/// (<see cref="AccountCreditsClient"/>, JWT-authed), and returns a local, token-free DTO.
///
/// THIS IS A BILLING SURFACE, and that is why issue #984 treats it as at least as serious as the owner-email
/// route it was found alongside. On the hosted Gateway it used to answer a paying subscriber with a bare
/// <c>{"signedIn":false,"balanceMicros":null}</c> - because it asked whether THIS GATEWAY held a credential
/// and then reported the answer as a fact about THE CALLER. A hosted Gateway holds no account credential by
/// design, so that false was served to every hosted customer, always. A customer reads "not signed in, no
/// balance" on a billing page as "my account is gone" or "my money is gone". Both are false, both are
/// alarming, and neither was recoverable by anything the customer could do.
///
/// The verdict is now folded once by <see cref="AccountActingCredential"/> and rendered verbatim (CLAUDE.md
/// rule 7): <c>signedIn</c> carries whether THE CALLER is signed in - which on hosted is true for any
/// enrolled tenant - and <see cref="AccountCreditsDto.BalanceAvailable"/> plus
/// <see cref="AccountCreditsDto.Message"/> carry, separately and explicitly, whether a balance could be read
/// and why not. The old shape had no way to say "you ARE signed in AND I cannot read your balance", so it
/// said the wrong one of the two.
///
/// Behaviour at the edges (no fabricated data):
/// <list type="bullet">
/// <item>Genuinely signed out (self-host, nothing stored) -> <c>signedIn:false</c> with a message, never a
/// fabricated zero balance.</item>
/// <item>Hosted (this Gateway cannot read the caller's balance) -> <c>signedIn:true</c>,
/// <c>balanceAvailable:false</c>, and a message saying the account and balance are unaffected.</item>
/// <item>Cloud unreachable / erroring -> a clear 502 error (logged), never a fabricated balance.</item>
/// </list>
///
/// Security (DT-05): the raw account token NEVER appears in the response (no token field) and is never
/// logged. Inherits the host-wide Gateway token middleware like the other <c>/account</c> endpoints.
/// </summary>
internal static class AccountCreditsEndpoint
{
    /// <summary>Maps <c>GET /account/credits</c>.</summary>
    /// <param name="app">The route builder.</param>
    /// <param name="account">The Gateway-hosted credential service. Null on a host with no credential service.</param>
    /// <param name="credits">The cloud credits client (the injectable cloud egress seam).</param>
    /// <param name="tenantBoundary">
    /// The hosted tenant boundary (issue #984). On hosted it resolves the CALLER's own tenant so this route
    /// reports the truth about them. Omitting it on a hosted Gateway does NOT fall back to the self-host
    /// answer - the hosted path fails closed. Ignored off hosted mode.
    /// </param>
    /// <param name="tenants">The tenant registry, read on hosted for the caller's display email.</param>
    public static void Map(IEndpointRouteBuilder app, bool hosted, DevThrottleAccountService? account, AccountCreditsClient credits,
        // REQUIRED AND NON-NULLABLE (finding I1-01): a forgotten boundary must be a compile error, never a
        // silent default. Self-host callers construct it over the SingleTenantContext.
        Tenancy.HostedTenantBoundary tenantBoundary, Tenancy.TenantRegistry? tenants = null)
    {
        if (credits is null) throw new ArgumentNullException(nameof(credits));

        app.MapGet("/account/credits", async (HttpContext ctx) =>
        {
            // Entry point: the delegate is the boundary, so the only try-catch lives here. Which account
            // state this request is in is ruled on once, in the fold, and rendered verbatim - this route
            // does not decide for itself what an absent token means (issue #984).
            var verdict = await AccountActingCredential
                .ResolveAsync(AccountOperations.Credits, ctx, hosted, account, tenantBoundary, tenants, ctx.RequestAborted)
                .ConfigureAwait(false);

            if (!verdict.IsReady)
            {
                // A deny or a deployment fault is an error, not a balance answer. Every other state is a
                // truthful 200 that says, in one body, both whether the caller is signed in AND whether a
                // balance could be read - because on hosted the honest answer is "signed in, no balance here".
                if (verdict.State is AccountActingState.HostedNoTenant or AccountActingState.HostedMiswired)
                {
                    FileLog.Write($"[AccountCreditsEndpoint] GET /account/credits: {verdict.State} -> {verdict.StatusCode}");
                    return Results.Json(new { error = verdict.Message }, statusCode: verdict.StatusCode);
                }

                FileLog.Write($"[AccountCreditsEndpoint] GET /account/credits: no balance readable ({verdict.State}); callerSignedIn={verdict.CallerIsSignedIn}");
                return Results.Json(new AccountCreditsDto
                {
                    SignedIn = verdict.CallerIsSignedIn,
                    BalanceAvailable = false,
                    Message = verdict.Message,
                });
            }

            try
            {
                var result = await credits.GetCreditsAsync(verdict.Token!, ctx.RequestAborted).ConfigureAwait(false);

                // The most recent debit's magnitude is the last hosted action's cost (a debit's
                // amount is negative on the ledger); a top-up (credit) is not an action cost.
                long? lastDebit = null;
                foreach (var tx in result.Recent)
                {
                    if (tx.AmountMicros < 0)
                    {
                        lastDebit = -tx.AmountMicros;
                        break;
                    }
                }

                FileLog.Write($"[AccountCreditsEndpoint] GET /account/credits: signedIn=true, balanceMicros={result.BalanceMicros}");
                return Results.Json(new AccountCreditsDto
                {
                    SignedIn = true,
                    BalanceAvailable = true,
                    BalanceMicros = result.BalanceMicros,
                    LastDebitMicros = lastDebit,
                });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AccountCreditsEndpoint] GET /account/credits FAILED: {ex.Message}");
                return Results.Json(
                    new { error = "Could not reach the DevThrottle account service to read your balance. Try again shortly." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}
