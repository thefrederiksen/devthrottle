using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Teams.Mentor;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE MENTOR'S WEEKLY PAGE FOR A PERSON'S OWN ACCOUNT (owner, 8 Oct 2026: "the mentor should also be for my personal
/// account"): <c>GET /account/mentor?week=YYYY-Www</c>.
///
/// The same Mentor tables as a team's page, keyed by the person's OWN tenant: the request is bound to that tenant by the
/// person's own device key, so the only blocks that can ever be read here are the ones stored under it, and of those
/// only the block about the person asking. Nobody else reads this page, so it lists no readers.
///
/// The answer has the team page's shape (<see cref="TeamMentorEndpoints"/>) with <c>scope</c> <c>"personal"</c>, no
/// team id, no readers, and the Gateway's own sentence for an empty week (<c>emptyNote</c>) - the client shows it as
/// given (rule 7).
///
/// Mapped beside the team page, only when Teams is released; the weekly writer is a separate switch and this route
/// reads whatever is stored. The subject is never logged.
/// </summary>
internal static class PersonalMentorEndpoints
{
    /// <summary>The route.</summary>
    public const string Route = "/account/mentor";

    /// <summary>What an empty week says when the Mentor has never written a page for this person.</summary>
    internal const string FirstPageNote =
        "Your first Mentor page arrives after the Mentor's first weekly run. It is written from your sessions, once a week.";

    /// <summary>What an empty week says when the Mentor has written for this person before.</summary>
    internal const string NothingThisWeekNote = "The Mentor wrote nothing for you this week.";

    /// <summary>The role label a personal block carries, where a team block carries the person's role in the team.</summary>
    internal const string PersonalRole = "Personal account";

    /// <summary>Maps the route.</summary>
    public static void Map(IEndpointRouteBuilder app, TeamMentorStore store, HostedTenantBoundary boundary,
        TenantRegistry tenants, Func<TenantId, string> timeZoneOf, Func<DateTime>? now = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(timeZoneOf);
        var clock = now ?? (() => DateTime.UtcNow);

        app.MapGet(Route, (HttpContext ctx, string? week) =>
        {
            try
            {
                var caller = TeamEndpoints.ResolveCaller(ctx, boundary, tenants);
                if (caller.Denial is not null) return caller.Denial;
                // ResolveCaller has just shown the request is bound to this person's own tenant.
                var own = boundary.ResolveRequestTenant(ctx)!.Value;
                return Read(store, tenants, timeZoneOf, clock(), own, caller.Subject!, week);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[PersonalMentorEndpoints] GET {Route} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "Your Mentor page could not be read just now because of a fault in DevThrottle. Try again shortly." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        FileLog.Write($"[PersonalMentorEndpoints] mapped GET {Route}");
    }

    /// <summary>The page for one person and week, from their own tenant. Internal so every branch is tested without a
    /// host.</summary>
    internal static IResult Read(TeamMentorStore store, TenantRegistry tenants, Func<TenantId, string> timeZoneOf,
        DateTime nowUtc, TenantId ownTenant, string callerSubject, string? weekText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerSubject);
        if (!string.Equals(tenants.SubjectForTenant(ownTenant), callerSubject, StringComparison.Ordinal))
            throw new InvalidOperationException("The personal Mentor page was asked for a tenant that is not the caller's own account.");

        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneOf(ownTenant));
        MentorWeek week;
        if (string.IsNullOrWhiteSpace(weekText))
        {
            week = MentorWeek.LastClosed(nowUtc, zone);
        }
        else if (MentorWeek.TryParse(weekText) is { } asked)
        {
            week = asked;
        }
        else
        {
            return Results.BadRequest(new { error = TeamMentorEndpoints.BadWeekRefusal });
        }

        var email = tenants.EmailForTenant(ownTenant);
        var blocks = store.Blocks(ownTenant, week)
            .Where(b => string.Equals(b.PersonSubject, callerSubject, StringComparison.Ordinal))
            .Select(b => Block(b, email))
            .ToList();
        if (blocks.Count > 1)
            throw new InvalidOperationException($"The personal Mentor page for week {week} holds {blocks.Count} blocks; there is one per person per week.");

        var emptyNote = blocks.Count > 0 ? null
            : store.HasAnyBlockFor(ownTenant, callerSubject) ? NothingThisWeekNote : FirstPageNote;
        FileLog.Write($"[PersonalMentorEndpoints] GET {Route}: week={week} blocks={blocks.Count}");
        return Results.Json(new
        {
            teamId = (string?)null,
            week = week.ToString(),
            weekStart = week.Start.ToString("yyyy-MM-dd"),
            weekEnd = week.End.ToString("yyyy-MM-dd"),
            timeZone = zone.Id,
            scope = "personal",
            // A person's own week is finished when its block is there; there is no team run to wait on.
            written = blocks.Count > 0,
            writingNote = (string?)null,
            emptyNote,
            readers = Array.Empty<object>(),
            blocks,
        });
    }

    /// <summary>A personal block: the same fields, in the same order, as a team block, so one client reads both.</summary>
    internal static object Block(MentorBlock block, string? email) => new
    {
        personEmail = email,
        role = PersonalRole,
        tone = block.Tone,
        toneLabel = MentorTones.Label(block.Tone),
        workedOn = block.WorkedOn,
        howItWent = block.HowItWent,
        wentBadlyAndWhy = block.WentBadlyAndWhy,
        quotes = block.Quotes.Select(q => new { promptId = q.PromptId, at = q.AtUtc, text = q.Text }).ToList(),
        oneThingToTry = block.OneThingToTry,
        writtenAtUtc = block.WrittenAtUtc,
        isYou = true,
    };
}
