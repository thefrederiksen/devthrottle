using System.Globalization;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// A CONTRIBUTOR'S CHOICE OF HOW THEY ARE TOLD THAT SOMETHING IS WAITING (Team implementation piece 4.4,
/// devthrottle_internal#2801; mockup.html, the contributor's "Notifications"): right away, once a day at an hour they
/// choose, or never.
///
/// <list type="bullet">
/// <item><c>GET /teams/notifications</c> - the Settings card, every word finished here (rule 7).</item>
/// <item><c>PUT /teams/notifications</c> with <c>{"mode": "now|daily|never", "dailyHour": 0-23}</c> - store the choice;
/// answers the card again.</item>
/// </list>
///
/// WHY HERE, AND WHY ONE CHOICE. The choice belongs to the PERSON, not to a team: the mockup shows one Notifications
/// setting on the contributor's own Settings, and a person on several teams gets one daily email naming every team. So
/// the route is not under <c>/teams/{teamId}</c>; it answers for the caller's own account, like <c>GET /teams</c>, and the
/// row lives in their own tenant. It is under <c>/teams</c> rather than the general account settings because it exists
/// only with Teams: it is dark until Teams is released, as every team route is, and it is person-only by its address
/// (<see cref="TeamPersonOnlyRoutes"/>) - a Director, an agent's session or the machine key cannot read or change how a
/// person is emailed. A session key is refused by the session-key guard's default deny, and in a team's tenant the team
/// gate refuses it as a route that acts for a person's own account, not inside a team.
/// </summary>
internal static class ContributorNoticeEndpoints
{
    /// <summary>The route, as the route table writes it.</summary>
    public const string Pattern = TeamEndpoints.Path + "/notifications";

    /// <summary>The body of the PUT.</summary>
    internal sealed record ChoiceRequest(string? Mode, int? DailyHour);

    /// <summary>Maps the two routes.</summary>
    public static void Map(IEndpointRouteBuilder app, ContributorNotices notices, TeamRegistry teams, HostedTenantBoundary boundary,
        TenantRegistry tenants, Func<TenantId, string> timeZoneOf)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(timeZoneOf);

        app.MapGet(Pattern, (HttpContext ctx) =>
        {
            try
            {
                var caller = TeamEndpoints.ResolveCaller(ctx, boundary, tenants);
                if (caller.Denial is not null) return caller.Denial;
                var own = boundary.ResolveRequestTenant(ctx)!.Value;
                FileLog.Write($"[ContributorNoticeEndpoints] GET /teams/notifications: tenant {own.ToLogString()}");
                return Results.Json(ContributorNoticeCard.For(notices.ChoiceOf(own), IsAContributor(teams, caller.Subject!),
                    timeZoneOf(own), savedLine: null));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ContributorNoticeEndpoints] GET /teams/notifications FAILED ({ex.GetType().Name}): {ex.Message}");
                return Fault();
            }
        });

        app.MapPut(Pattern, async (HttpContext ctx) =>
        {
            try
            {
                // Who is asking is settled before the body is read.
                var caller = TeamEndpoints.ResolveCaller(ctx, boundary, tenants);
                if (caller.Denial is not null) return caller.Denial;
                var own = boundary.ResolveRequestTenant(ctx)!.Value;

                ChoiceRequest? body;
                try
                {
                    body = await ctx.Request.ReadFromJsonAsync<ChoiceRequest>(ctx.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
                {
                    FileLog.Write($"[ContributorNoticeEndpoints] PUT /teams/notifications: rejected, the request body is not readable JSON ({ex.GetType().Name})");
                    return Results.BadRequest(new { error = "The request body is not readable JSON. Send {\"mode\": \"now\", \"dailyHour\": 8}.", code = "bad_body" });
                }

                var result = notices.SetChoice(own, body?.Mode, body?.DailyHour);
                if (result.Choice is null)
                    return Results.BadRequest(new { error = result.Refusal, code = "bad_choice" });
                return Results.Json(ContributorNoticeCard.For(result.Choice, IsAContributor(teams, caller.Subject!), timeZoneOf(own),
                    ContributorNoticeCard.Saved(result.Choice)));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ContributorNoticeEndpoints] PUT /teams/notifications FAILED ({ex.GetType().Name}): {ex.Message}");
                return Fault();
            }
        });

        FileLog.Write($"[ContributorNoticeEndpoints] mapped GET and PUT {Pattern}");
    }

    private static bool IsAContributor(TeamRegistry teams, string subject) =>
        teams.ListTeamsFor(subject).Any(t => t.Role == TeamRole.Collaborator);

    private static IResult Fault() => Results.Json(new { error = "DevThrottle could not do this just now because of a fault. Try again shortly." },
        statusCode: StatusCodes.Status500InternalServerError);
}

/// <summary>
/// THE SETTINGS CARD FOR A CONTRIBUTOR'S EMAIL CHOICE, every word and every verdict decided here (rule 7). The card in
/// <c>packages/client-core</c> lays it out on both shells and decides nothing.
/// </summary>
internal static class ContributorNoticeCard
{
    internal const string Title = "Email when something is sent to you";
    internal const string Explanation =
        "When something is sent to you in a team - a report, a question, a factory, or an answer to what you wrote - DevThrottle can tell you by email.";
    internal const string HourLabel = "Send the daily email at";
    internal const string Note = "The email only says that something is waiting, and in which team, with a link. What was sent stays on DevThrottle.";

    /// <summary>The three choices, in order, with the words the card shows.</summary>
    internal static readonly IReadOnlyList<(string Mode, string Label)> Options = new[]
    {
        (ContributorNoticeModes.Now, "Email me right away"),
        (ContributorNoticeModes.Daily, "Once a day, in one email"),
        (ContributorNoticeModes.Never, "Never - I will check myself"),
    };

    /// <summary>The card for <paramref name="choice"/>. <paramref name="applies"/> is whether the person is a contributor
    /// on any team: only then is anything ever emailed, so only then is the card shown.</summary>
    public static object For(ContributorNoticeChoice choice, bool applies, string timeZone, string? savedLine)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return new
        {
            applies,
            title = Title,
            explanation = Explanation,
            mode = choice.Mode,
            options = Options.Select(o => new { value = o.Mode, label = o.Label, selected = o.Mode == choice.Mode }).ToList(),
            showHour = choice.Mode == ContributorNoticeModes.Daily,
            hourLabel = HourLabel,
            dailyHour = choice.DailyHour,
            hours = Enumerable.Range(0, 24).Select(h => new { value = h, label = HourText(h) }).ToList(),
            timeZoneLine = $"Times are in your own time zone, {timeZone}.",
            note = Note,
            savedLine,
        };
    }

    /// <summary>The line shown after a change was stored.</summary>
    public static string Saved(ContributorNoticeChoice choice) => choice.Mode switch
    {
        ContributorNoticeModes.Now => "Saved. You get an email each time something is sent to you.",
        ContributorNoticeModes.Daily => $"Saved. You get one email a day at {HourText(choice.DailyHour)}, only when something is waiting.",
        ContributorNoticeModes.Never => "Saved. You get no email; what is sent to you waits in your Inbox.",
        _ => throw new InvalidOperationException($"A notice choice the card does not know: '{choice.Mode}'."),
    };

    internal static string HourText(int hour) => hour.ToString("00", CultureInfo.InvariantCulture) + ":00";
}
