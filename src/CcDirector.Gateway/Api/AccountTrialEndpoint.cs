using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The free-Pro-trial read: <c>GET /account/trial</c> (issue #1243). Answers whether the CALLER's trial is
/// running, when it ends, and how many days are left.
///
/// WHY IT DID NOT EXIST. The trial itself was never broken. <see cref="Tenancy.TrialRegistry"/> grants it at
/// hosted enrolment, the Gateway's own <c>account_trials</c> ledger stores it, and the entitlement decision
/// reads it - a live row sat in the production database with fourteen days on it while every screen in the
/// product stayed silent, because nothing could ask. The entire defect was the missing read path: we grant
/// something valuable and never tell the person, and a promise kept silently is indistinguishable from a
/// promise broken.
///
/// THIS ROUTE NEEDS NO OUTBOUND CREDENTIAL, which is what makes it different from its <c>/account</c>
/// siblings. The credit balance and the device list live in the cloud and are proxied with the Gateway's
/// stored account token; the trial ledger is the Gateway's OWN table, read locally. So the caller's identity
/// is all that is needed, and it comes from exactly where every other hosted identity comes from - the
/// authenticated device key, resolved once by <see cref="AccountActingCredential"/>, never from anything the
/// client claims.
///
/// EVERY ANSWER CARRIES A THREE-WAY STATE, INCLUDING THE REFUSALS. The deny and the deployment-fault paths
/// return their proper 403 and 503, and they return the SAME <see cref="AccountTrialDto"/> shape with
/// <see cref="AccountTrialDto.StateUnknown"/> rather than a bare error envelope. That is a deliberate
/// divergence from <see cref="AccountCreditsEndpoint"/>: this contract exists so that no consumer can ever
/// be handed a body without a state, because the one thing a trial surface must never do is decide for
/// itself what a missing answer means. A page that reads a failure as "no trial" tells a member with twelve
/// days left that they have nothing.
///
/// Security: nothing identifying is logged - not the subject, not the email. Inherits the host-wide Gateway
/// token middleware like the other <c>/account</c> routes.
/// </summary>
internal static class AccountTrialEndpoint
{
    /// <summary>Maps <c>GET /account/trial</c>.</summary>
    /// <param name="app">The route builder.</param>
    /// <param name="trials">The trial ledger - the Gateway's own table, read locally, never proxied.</param>
    /// <param name="tenantBoundary">
    /// The hosted tenant boundary. On hosted it resolves the CALLER's tenant from their authenticated device
    /// key. Required and non-nullable: a forgotten boundary must be a compile error, never a silent default,
    /// and on hosted a missing one fails CLOSED rather than dropping to the self-host answer.
    /// </param>
    /// <param name="tenants">The tenant registry, read on hosted to turn the caller's tenant into the
    /// account subject the trial ledger is keyed by.</param>
    /// <param name="nowUtc">The clock, injected so the day count and the expiry boundary are testable at an
    /// exact instant. Defaults to the real one.</param>
    public static void Map(IEndpointRouteBuilder app, bool hosted, Tenancy.TrialRegistry trials,
        Tenancy.HostedTenantBoundary tenantBoundary, Tenancy.TenantRegistry? tenants = null,
        Func<DateTime>? nowUtc = null)
    {
        if (trials is null) throw new ArgumentNullException(nameof(trials));
        if (tenantBoundary is null) throw new ArgumentNullException(nameof(tenantBoundary));

        var clock = nowUtc ?? (() => DateTime.UtcNow);

        app.MapGet("/account/trial", async (HttpContext ctx) =>
        {
            // Entry point: the delegate is the boundary, so the only try-catch lives here. An unexpected
            // failure is reported AS an unknown - which is not a fallback hiding a problem but this route's
            // whole contract, because "I could not find out" is the true answer and it is logged loud.
            try
            {
                var (dto, status) = await ResolveAsync(ctx, hosted, trials, tenantBoundary, tenants, clock()).ConfigureAwait(false);
                FileLog.Write($"[AccountTrialEndpoint] GET /account/trial: state={dto.State}, status={status}");
                return Results.Json(dto, statusCode: status);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AccountTrialEndpoint] GET /account/trial FAILED ({ex.GetType().Name}): {ex.Message} - answering UNKNOWN, which must never be rendered as no-trial");
                return Results.Json(Unknown(
                    "DevThrottle could not read your trial status just now. This does not mean you have no "
                    + "trial - try again shortly."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    /// <summary>
    /// The whole decision, folded once and rendered verbatim by the client (CLAUDE.md rule 7). Internal so it
    /// can be exercised directly at every state without standing a web host up.
    /// </summary>
    internal static async Task<(AccountTrialDto Dto, int StatusCode)> ResolveAsync(
        HttpContext ctx, bool hosted, Tenancy.TrialRegistry trials, Tenancy.HostedTenantBoundary boundary,
        Tenancy.TenantRegistry? tenants, DateTime now)
    {
        // SELF-HOST. The trial is a hosted-account fact: it is granted at hosted enrolment and recorded in the
        // HOSTED Gateway's ledger. This Gateway's own account_trials table is a different table belonging to a
        // different deployment, and reading it here would produce a statement that is true about THIS RECORD
        // and false about the world - the most convincing kind of wrong answer. So it does not read: it says
        // it cannot tell, which is exactly what is true.
        if (!hosted)
        {
            return (Unknown(
                "This is a self-hosted DevThrottle Gateway, so it does not hold your account's trial status. "
                + "A free Pro trial belongs to your DevThrottle account on the hosted Gateway - check it on "
                + "the DevThrottle website."),
                StatusCodes.Status200OK);
        }

        var verdict = await AccountActingCredential
            .ResolveAsync(AccountOperations.Trial, ctx, hosted, account: null, boundary, tenants, ctx.RequestAborted)
            .ConfigureAwait(false);

        // A deny (nothing bound this request to an account) and a deployment fault both keep their proper
        // status code AND carry a state, so a consumer parsing the body is never handed a shape it cannot read
        // a three-way answer out of. Neither is "you have no trial" - we do not know whose trial to look up.
        if (verdict.State is AccountActingState.HostedNoTenant or AccountActingState.HostedMiswired)
            return (Unknown(verdict.Message), verdict.StatusCode);

        // The subject is the ledger's key, resolved from the authenticated device key at the one place that
        // turns a key into an identity. Its absence is ignorance about WHO is asking, so it is an unknown -
        // never an answer about a trial we never looked for.
        if (string.IsNullOrWhiteSpace(verdict.AccountSubject))
        {
            FileLog.Write("[AccountTrialEndpoint] the caller's tenant resolved but no account subject is mapped to it - answering UNKNOWN");
            return (Unknown(
                "DevThrottle could not identify the account behind this request, so it cannot tell you "
                + "whether a free Pro trial is running. This does not mean you have no trial."),
                StatusCodes.Status200OK);
        }

        var status = trials.ReadStatus(verdict.AccountSubject, now);
        return (Describe(status, now), StatusCodes.Status200OK);
    }

    /// <summary>
    /// Turns one ledger read into the finished body. Pure: no clock of its own, no database, no request - so
    /// every state and both sides of the expiry boundary are pinned by direct tests.
    /// </summary>
    internal static AccountTrialDto Describe(Tenancy.TrialStatus status, DateTime now)
    {
        if (status is null) throw new ArgumentNullException(nameof(status));

        switch (status.Kind)
        {
            case Tenancy.TrialStatusKind.Active:
            {
                // Active without an end instant would be a day count with nothing behind it. The registry
                // always carries the row's expiry on Active, so this is a contradiction rather than a state to
                // render - and inventing a date on a page about what someone is entitled to is worse than
                // saying we do not know.
                if (status.ExpiresAtUtc is not { } expires)
                {
                    FileLog.Write("[AccountTrialEndpoint] a trial read said ACTIVE with no expiry instant - answering UNKNOWN rather than naming a date we do not have");
                    return Unknown(
                        "DevThrottle could not read when your free Pro trial ends. This does not mean you "
                        + "have no trial - try again shortly.");
                }

                var days = DaysRemaining(expires, now);
                return new AccountTrialDto
                {
                    State = AccountTrialDto.StateActive,
                    StartedAtUtc = status.StartedAtUtc,
                    EndsAtUtc = expires,
                    DaysRemaining = days,
                    Message = $"Your free Pro trial is running - {days} {(days == 1 ? "day" : "days")} left, "
                              + $"ending {OnDay(expires)}.",
                };
            }

            case Tenancy.TrialStatusKind.Expired:
                return new AccountTrialDto
                {
                    State = AccountTrialDto.StateExpired,
                    StartedAtUtc = status.StartedAtUtc,
                    EndsAtUtc = status.ExpiresAtUtc,
                    Message = status.ExpiresAtUtc is { } ended
                        ? $"Your free Pro trial ended on {OnDay(ended)}."
                        : "Your free Pro trial has ended.",
                };

            case Tenancy.TrialStatusKind.NeverGranted:
                // NOT "your trial expired" - this account never had one, and saying it ended would be false.
                // Stated as a plain fact rather than a loss, because a paying member is in this state too.
                return new AccountTrialDto
                {
                    State = AccountTrialDto.StateNone,
                    Message = "No free Pro trial is running on this account.",
                };

            case Tenancy.TrialStatusKind.Unreadable:
                return Unknown(
                    "DevThrottle could not read your trial status just now. This does not mean you have no "
                    + "trial - try again shortly.");

            default:
                // An unrecognised state is ignorance by definition. It resolves toward unknown, never toward
                // the comfortable answer - which is the direction that would quietly tell a member on day two
                // of their trial that they have nothing.
                FileLog.Write($"[AccountTrialEndpoint] unrecognised trial status '{status.Kind}' - answering UNKNOWN");
                return Unknown(
                    "DevThrottle could not read your trial status just now. This does not mean you have no "
                    + "trial - try again shortly.");
        }
    }

    /// <summary>
    /// Whole days left, counting a PART day as a day: while a trial is active there is always at least some of
    /// today left, so the smallest true answer is 1. Rounding down would print "0 days left" beside working
    /// access on the last day, which reads as an expiry that has not happened.
    /// </summary>
    private static int DaysRemaining(DateTime expires, DateTime now)
    {
        var left = expires - now;
        var days = (int)Math.Ceiling(left.TotalDays);
        return days < 1 ? 1 : days;
    }

    /// <summary>The end date as a person would say it, invariant so it never varies with server culture.</summary>
    private static string OnDay(DateTime instant)
        => instant.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// The undetermined answer, in one place. It carries no dates and no day count - an unknown that shipped
    /// a number beside it would invite exactly the reading this state exists to prevent.
    /// </summary>
    private static AccountTrialDto Unknown(string message) => new()
    {
        State = AccountTrialDto.StateUnknown,
        Message = message,
    };
}
