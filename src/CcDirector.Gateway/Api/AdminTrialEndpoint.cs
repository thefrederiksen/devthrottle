using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The administrator trial-extension surface:
///
///   POST /gateway/admin/trials/extend  ->  { outcome, started_at_utc, previous_expires_at_utc, expires_at_utc, max_expiry_utc }
///
/// The website's admin screen calls this to honour a promise we made about somebody's trial. It is the ONLY
/// way that date moves, and it moves it LATER or not at all.
///
/// WHY THIS ROUTE EXISTS RATHER THAN A DATABASE GRANT. The trial is a row in this Gateway's own table, owned
/// by this Gateway's database role. The website runs as a different role with SELECT and nothing more, so the
/// alternative was granting that role UPDATE on a table it does not own. That would have put the capability
/// where the data is not, split one rule across two codebases, and left the permission itself outside either
/// system's migrations - so rebuilding this schema would silently remove it and the admin screen would start
/// answering "could not confirm" with nothing failing loudly. The capability belongs with the data. This is
/// that, and it is why no database privilege has to change for the button to work.
///
/// AUTHORIZATION IS ITS OWN, AND IT IS NOT THE REPORT TOKEN. The caller is a SERVER (the website's admin API),
/// which holds no device credential, so this route is exempt from the host-wide token gate
/// (<c>AuthMiddleware.PublicPaths</c>) and carries its own: a bearer service token supplied out of band as the
/// <c>ADMIN_SERVICE_TOKEN</c> environment variable. It is a SEPARATE secret from
/// <see cref="MorningReportEndpoint.ServiceTokenEnvVar"/> on purpose - that one guards a read-only report, this
/// one hands out paid product, and a credential that leaked from a reporting cron must not also be able to
/// give a year of Pro away - and because two variable NAMES are not two secrets, an equal pair is refused
/// rather than trusted. Exempt from the host gate does NOT mean open; every path denies by default, and they
/// are listed IN THE ORDER THEY ARE CHECKED, because that order is itself a property:
///   - the variable unset or blank     -> 503. The endpoint refuses to serve rather than serve unguarded.
///   - both service tokens set EQUAL   -> 503. The separation is checked, not merely documented.
///   - no / malformed / wrong bearer   -> 401.
///   - ...and only THEN is the body read at all. A malformed body from an AUTHORIZED caller -> 400.
/// So an anonymous caller never gets this Gateway to parse anything it sent, and never learns anything about
/// its own input from a route whose only correct answer to a stranger is "who are you?".
/// The token comparison is fixed-time, so a wrong token cannot be discovered a byte at a time.
///
/// A VALID TOKEN IS AUTHORITY TO ASK, NOT AUTHORITY OVER EVERY ACCOUNT AT ONCE. The only account input is one
/// subject, matched for equality against the trial table's primary key: there is no wildcard, no pattern, and
/// no value meaning "all", so a single call can move exactly one account's date.
///
/// SELF-HOST REFUSES. A trial belongs to an account on the HOSTED Gateway; a self-hosted install's
/// <c>account_trials</c> table is a different table belonging to a different deployment, and writing there
/// would produce a change that is real in that database and meaningless to the member.
///
/// Nothing identifying is logged - not the subject, not the email, not the actor.
/// </summary>
internal static class AdminTrialEndpoint
{
    /// <summary>The route. Exact-match public in <c>AuthMiddleware</c>; this endpoint carries its own gate.</summary>
    public const string Path = "/gateway/admin/trials/extend";

    /// <summary>The environment variable holding the bearer service token the website's admin API presents.
    /// Deliberately distinct from the report token: different authority, different secret.</summary>
    public const string ServiceTokenEnvVar = "ADMIN_SERVICE_TOKEN";

    /// <summary>
    /// What the caller sends.
    ///
    /// EVERY NAME IS SPELLED OUT. The host's web defaults bind property names case-INSENSITIVELY, which is not
    /// the same as punctuation-insensitively: <c>ends_at_utc</c> does NOT bind to <c>EndsAtUtc</c> by that
    /// rule, it binds to nothing and arrives as null. A required field that silently arrives null would be
    /// reported to an administrator as a malformed request they did not make, so the wire names are stated
    /// rather than inferred.
    /// </summary>
    internal sealed record ExtendRequest(
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("ends_at_utc")] DateTime? EndsAtUtc,
        [property: JsonPropertyName("actor")] string? Actor,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("member_email")] string? MemberEmail);

    public static void Map(IEndpointRouteBuilder app, bool hosted, TrialRegistry trials, Func<DateTime>? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(trials);
        var clock = nowUtc ?? (() => DateTime.UtcNow);

        app.MapPost(Path, async (HttpContext ctx) =>
        {
            // Entry point: the delegate is the boundary, so the only catch-all lives here. An unexpected
            // failure is reported as UNKNOWN rather than as a refusal, because we genuinely do not know
            // whether the change landed - see the outcome's own notes.
            try
            {
                // THE GATE COMES FIRST - BEFORE THE BODY IS EVEN READ. This route is exempt from the
                // host-wide token middleware, so until this line runs the request is simply an anonymous
                // one off the internet. Parsing first meant an unauthenticated caller could make this
                // Gateway deserialize whatever it sent, and that a tokenless request with malformed JSON
                // was answered 400 - a caller learning something about its INPUT from a route that should
                // only ever have told it "who are you?". Handle() checks again, so the gate is still
                // enforced when a test calls it directly; this is the copy that faces the network.
                if (ServiceTokenDenial(ctx) is { } gate) return gate;

                ExtendRequest? body;
                try
                {
                    body = await ctx.Request.ReadFromJsonAsync<ExtendRequest>(ctx.RequestAborted)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[AdminTrialEndpoint] rejected: the request body is not readable JSON ({ex.GetType().Name})");
                    return Results.BadRequest(new { error = "the request body is not readable JSON" });
                }

                return Handle(ctx, body, trials, clock(), hosted);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AdminTrialEndpoint] POST {Path} FAILED ({ex.GetType().Name}): {ex.Message} - answering UNKNOWN, which must never be rendered as a refusal");
                return Results.Json(
                    new { outcome = OutcomeUnknown },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        FileLog.Write($"[AdminTrialEndpoint] mapped {Path} (service-token authorized, hosted only)");
    }

    // The wire vocabulary, in one place. These strings are a CONTRACT with the website's admin screen, which
    // says a different sentence for each - so they are named constants rather than literals scattered through
    // the switch below, and renaming one is a change to an interface rather than a tidy-up.
    /// <summary>The ONE body every unavailable administrator-auth state answers with. Deliberately says
    /// nothing about which state it is: an unauthenticated caller learning whether a token is configured,
    /// or whether two tokens collide, is a configuration oracle. The detail lives in the log.</summary>
    private static readonly object Unavailable = new { error = "the administrator surface is unavailable" };

    internal const string OutcomeExtended = "extended";
    internal const string OutcomeNoTrial = "no_trial";
    internal const string OutcomeNotLater = "not_later";
    internal const string OutcomeTooFar = "too_far";
    internal const string OutcomeUnknown = "unknown";

    /// <summary>Internal (not private) so the authorization and every refusal can be unit-tested directly
    /// through InternalsVisibleTo, without standing a Kestrel host up per case.</summary>
    internal static IResult Handle(HttpContext ctx, ExtendRequest? body, TrialRegistry trials, DateTime now, bool hosted)
    {
        if (ServiceTokenDenial(ctx) is { } denial) return denial;

        // SELF-HOST REFUSES, and it refuses AFTER the token check so an unauthenticated caller cannot use the
        // difference in replies to learn which mode an install is running in.
        if (!hosted)
        {
            FileLog.Write("[AdminTrialEndpoint] DENIED: this is a self-hosted Gateway - a trial belongs to an account on the hosted Gateway");
            return Results.Json(
                new { error = "this Gateway is self-hosted, so it holds no account trials to extend" },
                statusCode: StatusCodes.Status409Conflict);
        }

        if (body is null)
            return Results.BadRequest(new { error = "a request body is required" });

        // CALLER ERRORS ARE 400s AND THEY ARE NOT OUTCOMES. Returning a tidy outcome for a blank subject
        // would let a broken caller read "no_trial" and tell an administrator this member has none.
        if (string.IsNullOrWhiteSpace(body.Subject))
            return Results.BadRequest(new { error = "a subject is required: the account whose trial should move" });
        if (body.EndsAtUtc is not { } endsAt)
            return Results.BadRequest(new { error = "ends_at_utc is required: the instant the trial should now end" });
        if (string.IsNullOrWhiteSpace(body.Actor))
            return Results.BadRequest(new { error = "an actor is required: a trial extension must record who made it" });
        if (string.IsNullOrWhiteSpace(body.Reason))
            return Results.BadRequest(new { error = "a reason is required: a trial extension must record why it was made" });

        var result = trials.ExtendIfLater(
            body.Subject, endsAt, body.Actor, body.Reason, body.MemberEmail, now);

        return result.Outcome switch
        {
            TrialExtensionOutcome.Extended => Results.Json(new
            {
                outcome = OutcomeExtended,
                started_at_utc = result.StartedAtUtc,
                previous_expires_at_utc = result.PreviousExpiresAtUtc,
                expires_at_utc = result.ExpiresAtUtc,
            }),

            TrialExtensionOutcome.NoTrial => Results.Json(new { outcome = OutcomeNoTrial }),

            TrialExtensionOutcome.NotLater => Results.Json(new
            {
                outcome = OutcomeNotLater,
                started_at_utc = result.StartedAtUtc,
                expires_at_utc = result.ExpiresAtUtc,
            }),

            TrialExtensionOutcome.TooFar => Results.Json(new
            {
                outcome = OutcomeTooFar,
                started_at_utc = result.StartedAtUtc,
                expires_at_utc = result.ExpiresAtUtc,
                max_expiry_utc = result.MaxExpiryUtc,
            }),

            // UNKNOWN CARRIES A 503, not a 200. The status code is what a caller's transport layer reads
            // before anything parses the body, and this state must reach it as "I could not tell you",
            // never as a completed request whose answer happens to be a refusal.
            _ => Results.Json(new { outcome = OutcomeUnknown }, statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }

    /// <summary>
    /// The service-token gate, or null when the caller is authorized. Its own copy rather than a call into
    /// <see cref="MorningReportEndpoint"/>: sharing the helper would invite sharing the VARIABLE, and the
    /// whole point is that a read-only report token cannot hand out paid product.
    /// </summary>
    internal static IResult? ServiceTokenDenial(HttpContext ctx)
    {
        var configured = Environment.GetEnvironmentVariable(ServiceTokenEnvVar);
        if (string.IsNullOrWhiteSpace(configured))
        {
            // Fail loud and CLOSED. An unconfigured token is a deployment error, not a reason to serve to
            // anyone who asks, and not a reason to invent an allow-anything mode either.
            FileLog.Write($"[AdminTrialEndpoint] DENIED: {ServiceTokenEnvVar} is not set on this Gateway - the administrator surface refuses to serve unguarded");
            // ONE EXTERNAL ANSWER FOR EVERY UNAVAILABLE STATE. Saying WHICH misconfiguration it is told an
            // unauthenticated caller - anybody on the internet - whether this Gateway has an admin token
            // configured and whether it collides with the report token. That is a configuration oracle, and
            // the person who needs the detail is reading the log, not the response.
            return Results.Json(Unavailable, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // TWO VARIABLE NAMES ARE NOT TWO SECRETS. The separation from the read-only report token is the
        // reason this endpoint has its own variable at all - a credential that leaked from a reporting cron
        // must not also be able to hand a member a year of paid product. Nothing about naming two settings
        // stops a deployment pasting the same value into both, and if it did, the separation would be
        // documentation rather than a property. So it is CHECKED, and an equal pair refuses to serve: a
        // misconfiguration that quietly grants the report credential write authority is exactly the failure
        // this design exists to prevent, and it must be loud rather than silent.
        var report = Environment.GetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar);
        if (!string.IsNullOrWhiteSpace(report) && SameSecret(configured, report))
        {
            FileLog.Write($"[AdminTrialEndpoint] DENIED: {ServiceTokenEnvVar} and {MorningReportEndpoint.ServiceTokenEnvVar} hold the SAME value - the read-only report credential would gain administrator authority. Refusing to serve until they differ.");
            return Results.Json(Unavailable, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!PresentedTokenMatches(ctx, configured))
        {
            FileLog.Write("[AdminTrialEndpoint] DENIED: missing or incorrect service token");
            return Results.Json(new { error = "a valid admin service token is required" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return null;
    }

    /// <summary>
    /// Whether two configured secrets are the same value. Fixed-time over digests like the bearer check
    /// below - both operands here are server-side settings rather than attacker input, so the timing
    /// exposure is theoretical, but comparing secrets two different ways in one file invites the weaker way
    /// being copied to where it is not theoretical.
    /// </summary>
    private static bool SameSecret(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a.Trim())),
            SHA256.HashData(Encoding.UTF8.GetBytes(b.Trim())));

    /// <summary>
    /// Whether the request presents the configured bearer token. Compared in FIXED TIME over fixed-size
    /// digests: an ordinary string comparison returns as soon as two bytes differ, which leaks the correct
    /// prefix length to anyone who can time the response, and hashing first means even the length does not
    /// leak through the comparison.
    /// </summary>
    private static bool PresentedTokenMatches(HttpContext ctx, string configured)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
            return false;

        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var presented = header[scheme.Length..].Trim();
        if (presented.Length == 0)
            return false;

        var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(configured.Trim()));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
