using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The administrator read of the corrections people made to the Wingman's verdicts (the Wingman-on-every-turn
/// mission, slice G):
///
///   GET /gateway/admin/turn-verdict-feedback?account=&lt;id&gt;&amp;since=2026-09-15T00:00:00Z&amp;max=500
///     [&amp;after=&lt;moment&gt;&amp;after_verdict=&lt;id&gt;]
///     -> { rows: [ { account, session_id, verdict_id, corrected_verdict, note, turn_end_observed_at_utc,
///                    reported_at_utc } ], truncated, cursor }
///
/// IT PAGES, and the caller is expected to follow the pages. One request answers at most <c>max</c> rows, in
/// (reported moment, verdict id) order; <c>truncated</c> is true only when a row BEYOND that page exists, and
/// then <c>cursor</c> carries the last served row's moment and identifier to hand back as <c>after</c> and
/// <c>after_verdict</c>. A busy day is therefore complete in several requests rather than incomplete in one.
/// Before this, a page that merely FILLED the cap was called truncated and carried no way to continue, so an
/// account with exactly one page of corrections was reported incomplete for ever, and one with more than a page
/// could not be read past the first: the daily pull stops on truncation and wrote nothing in either case.
///
/// WHY IT EXISTS. A correction is only worth making if it reaches the labelled corpus, and the corpus lives in
/// the internal repository, pulled down once a day by a job with no account credential on this Gateway. The
/// verdict rows themselves reach it through the turn log, which is written to the Gateway's DISK and pulled as
/// files; the corrections are rows in the database and have no such path, so this is it. The daily pull asks for
/// what is new since it last asked and joins each row into the turn log by (account, session, observed moment) -
/// which is why the observed moment is on the row and is the field to keep if anything here is ever trimmed.
///
/// WHAT IT SERVES AND WHAT IT CANNOT. One closed verdict word, the two moments, two identifiers, and the note the
/// person typed. It never returns a screen, a reply, a prompt, a label or a summary - so nothing here can be used
/// to dredge a terminal out of an account, which is the line <see cref="AdminTurnLogEndpoint"/> draws and this
/// keeps. The note is free text and is the one field carrying words somebody wrote; it is served because the
/// corpus label carries the reviewer's reasoning beside the word, and a correction with no reason is the weakest
/// kind of label.
///
/// AUTHORIZATION IS THE ADMIN SERVICE TOKEN, through <see cref="AdminTrialEndpoint.ServiceTokenDenial"/> - called
/// rather than copied, so there is one definition of who may act as an administrator here and the separation from
/// the read-only report token cannot drift.
///
/// A BLANK ACCOUNT IS NOT A WILDCARD, the rule the turn-log switch surface already sets: this read names ONE
/// account, and a caller that forgot to send one is refused rather than served the fleet.
/// </summary>
internal static class AdminTurnVerdictFeedbackEndpoint
{
    /// <summary>The route. Exact-match public in <c>AuthMiddleware</c>; the endpoint carries its own gate.</summary>
    public const string Path = "/gateway/admin/turn-verdict-feedback";

    /// <summary>The answer when this Gateway cannot say. Never a refusal - a caller told "denied" would go
    /// looking for a permission problem that is not there.</summary>
    private const string OutcomeUnknown = "unknown";

    public static void Map(IEndpointRouteBuilder app, TurnVerdictStore? verdicts, TenantRegistry tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);

        app.MapGet(Path, (HttpContext ctx) =>
        {
            try
            {
                // THE GATE COMES FIRST, before a single query parameter is read: until this line runs the
                // request is an anonymous one off the internet.
                if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;
                return Handle(ctx, verdicts, tenants);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AdminTurnVerdictFeedbackEndpoint] GET {Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { outcome = OutcomeUnknown }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        FileLog.Write($"[AdminTurnVerdictFeedbackEndpoint] mapped {Path} (service-token authorized)");
    }

    /// <summary>Internal so every refusal can be tested directly, without standing a host up per case. It
    /// re-checks the gate for the reason <see cref="AdminTurnLogEndpoint.Handle"/> does: a handler that is
    /// reachable from a test must not be reachable ungated from anywhere else either.</summary>
    internal static IResult Handle(HttpContext ctx, TurnVerdictStore? verdicts, TenantRegistry tenants)
    {
        if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } denial) return denial;

        if (verdicts is null)
            return Results.Json(new { outcome = OutcomeUnknown }, statusCode: StatusCodes.Status503ServiceUnavailable);

        var account = ctx.Request.Query["account"].ToString();
        if (string.IsNullOrWhiteSpace(account))
            return Results.BadRequest(new { error = "an account is required: this read names one account, and a blank one is not a wildcard" });

        var known = tenants.ListAll()
            .Any(t => string.Equals(t.TenantId, account, StringComparison.OrdinalIgnoreCase));
        if (!known)
            return Results.BadRequest(new { error = $"no account on this Gateway has the identifier \"{account}\". Find it with GET {AdminAccountLookupEndpoint.Path}?email=..." });

        var sinceText = ctx.Request.Query["since"].ToString();
        DateTime since;
        if (string.IsNullOrWhiteSpace(sinceText))
        {
            // The store keeps seven days, so asking for eight is asking for everything it holds. A default that
            // meant "today" would make a pull that missed a day lose that day silently.
            since = DateTime.UtcNow.AddDays(-8);
        }
        else if (!DateTime.TryParse(sinceText, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out since))
        {
            return Results.BadRequest(new { error = "since must be a moment in time, for example 2026-09-15T00:00:00Z" });
        }

        var max = TurnVerdictStore.MaxFeedbackPage;
        var maxText = ctx.Request.Query["max"].ToString();
        if (!string.IsNullOrWhiteSpace(maxText))
        {
            if (!int.TryParse(maxText, out var asked) || asked <= 0)
                return Results.BadRequest(new { error = "max must be a whole number greater than zero" });
            max = Math.Min(asked, TurnVerdictStore.MaxFeedbackPage);
        }

        // THE CURSOR, and it is both halves or neither. A page continues from the last row of the page before
        // it, named by its reported moment AND its verdict id, because the moment alone is not unique and a
        // caller that moved only the moment forward would either repeat a row stamped with it or lose one. A
        // caller that sends one half has a cursor it thinks it is using and is not, so it is refused rather
        // than quietly served page one again.
        var afterText = ctx.Request.Query["after"].ToString();
        var afterVerdict = ctx.Request.Query["after_verdict"].ToString();
        if (string.IsNullOrWhiteSpace(afterText) != string.IsNullOrWhiteSpace(afterVerdict))
            return Results.BadRequest(new { error = "a cursor is both halves: send after and after_verdict together, exactly as the cursor on the previous page carried them" });

        DateTime? after = null;
        if (!string.IsNullOrWhiteSpace(afterText))
        {
            if (!DateTime.TryParse(afterText, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                return Results.BadRequest(new { error = "after must be a moment in time, for example 2026-09-15T00:00:00Z" });
            after = parsed;
        }

        var tenant = new TenantId(account);

        // ONE ROW MORE THAN THE PAGE. Whether there is another row is a question about the row AFTER the page,
        // and asking it any other way guesses: a full page was read as truncated before this, so an account
        // with exactly one page of corrections was reported incomplete for ever and the pull, which stops on
        // truncation, wrote nothing at all. The probe row is read and dropped; it is never served.
        var page = verdicts.FeedbackSince(tenant, since, max + 1, after, string.IsNullOrWhiteSpace(afterVerdict) ? null : afterVerdict);
        var truncated = page.Count > max;
        var rows = truncated ? page.Take(max).ToList() : page;

        // THE CURSOR IS THE LAST ROW OF THIS PAGE, and it is present only when there is another page - a
        // cursor beside a complete answer is an invitation to ask again for nothing.
        var last = truncated ? rows[^1] : null;

        FileLog.Write(
            $"[AdminTurnVerdictFeedbackEndpoint] served account={account} since={since:O} after={(after is null ? "none" : after.Value.ToString("O"))} rows={rows.Count} truncated={truncated}");

        return Results.Json(new
        {
            account,
            since_utc = since,
            truncated,
            cursor = last is null ? null : new
            {
                reported_at_utc = last.ReportedAtUtc,
                verdict_id = last.VerdictId,
            },
            rows = rows.Select(r => new
            {
                account = r.TenantId,
                session_id = r.SessionId,
                verdict_id = r.VerdictId,
                corrected_verdict = r.CorrectedVerdict,
                note = r.Note,
                turn_end_observed_at_utc = r.TurnEndObservedAtUtc,
                reported_at_utc = r.ReportedAtUtc,
            }),
        });
    }
}
