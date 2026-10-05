using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Teams.Mentor;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE MENTOR'S WEEKLY PAGE, READ (devthrottle_internal#2305): <c>GET /teams/{teamId}/mentor?week=YYYY-Www</c>. The
/// contract is <c>docs/proof/teams-2305/contract.md</c>.
///
/// WHO GETS WHAT is the role table, asked through <see cref="TeamAccess.Decide"/> and nowhere else. An Owner or Manager
/// may read the Mentor's page about each person and the prompts quoted on it, so they get every block of the week. A
/// Developer may read only the page about themselves, so they get their own block or none. A Collaborator, and anyone
/// who is not a member, never reaches here: <see cref="TeamEndpointGate"/> refuses them first (the route's rule is
/// <see cref="TeamAction.ReadOwnMentorPage"/>), and this handler refuses them again rather than trust that it ran.
///
/// ONE STORED ROW, ONE SERIALIZATION: a block is turned into its answer by <see cref="Block"/> alone, so the person and
/// their Manager are served the same object, byte for byte. Nothing here returns a prompt beyond the ones quoted.
///
/// Mapped only when Teams is released (<c>CC_GATEWAY_TEAMS=1</c>). The subject is never logged.
/// </summary>
internal static class TeamMentorEndpoints
{
    /// <summary>The route pattern.</summary>
    public const string Route = TeamEndpoints.Path + "/{teamId}/mentor";

    /// <summary>What a week that is not an ISO week is told.</summary>
    internal const string BadWeekRefusal = "The week must be an ISO week such as 2026-W40.";

    /// <summary>The line a page shows above the blocks of a week the Mentor has started and not finished (review H1):
    /// some blocks are stored, and the run is still waiting on the model for someone else.</summary>
    internal const string StillWritingNote = "The Mentor is still writing this week. More blocks may follow.";

    /// <summary>Maps the route.</summary>
    public static void Map(IEndpointRouteBuilder app, TeamRegistry teams, TeamAccess access, TeamMentorStore store,
        HostedTenantBoundary boundary, TenantRegistry tenants, Func<TenantId, string> timeZoneOf, Func<DateTime>? now = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(timeZoneOf);
        var clock = now ?? (() => DateTime.UtcNow);

        app.MapGet(Route, (HttpContext ctx, string teamId, string? week) =>
        {
            try
            {
                var caller = TeamEndpoints.ResolveCaller(ctx, boundary, tenants);
                return caller.Denial ?? Read(teams, access, store, timeZoneOf, clock(), caller.Subject!, teamId, week);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TeamMentorEndpoints] GET {Route} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "The Mentor's page could not be read just now because of a fault in DevThrottle. Try again shortly." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        FileLog.Write($"[TeamMentorEndpoints] mapped GET {Route}");
    }

    /// <summary>The page for one caller and week. Internal so every branch is tested without a host.</summary>
    internal static IResult Read(TeamRegistry teams, TeamAccess access, TeamMentorStore store, Func<TenantId, string> timeZoneOf,
        DateTime nowUtc, string callerSubject, string? teamId, string? weekText)
    {
        if (string.IsNullOrWhiteSpace(teamId))
            return Results.NotFound(new { error = TeamEndpoints.NoSuchTeamRefusal });

        var everyone = access.Decide(teamId, callerSubject, TeamAction.ReadMentorPageAboutEachPerson);
        if (!everyone.IsMember)
        {
            FileLog.Write($"[TeamMentorEndpoints] GET {Route}: no such team for this caller");
            return Results.NotFound(new { error = TeamEndpoints.NoSuchTeamRefusal });
        }

        var quotes = access.Decide(teamId, callerSubject, TeamAction.ReadPromptsQuotedOnMentorPage);
        var own = access.Decide(teamId, callerSubject, TeamAction.ReadOwnMentorPage);
        if (everyone.Allowed != quotes.Allowed)
            throw new InvalidOperationException(
                "The role table lets this role read the Mentor's page about each person but not the prompts quoted on it, or the reverse; the page cannot be served by half.");
        if (!everyone.Allowed && !own.Allowed)
        {
            FileLog.Write($"[TeamMentorEndpoints] GET {Route}: REFUSED role={own.Role}");
            return Results.Json(new { error = own.Refusal, code = TeamEndpointGate.RefusalCode }, statusCode: StatusCodes.Status403Forbidden);
        }

        var team = new TenantId(teamId);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneOf(team));
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
            return Results.BadRequest(new { error = BadWeekRefusal });
        }

        var members = teams.MembersOf(teamId).ToDictionary(m => m.AccountSubject, StringComparer.Ordinal);
        var blocks = store.Blocks(team, week)
            // The block of someone who has since left the team is not shown: role and email are read from the
            // membership as it is now.
            .Where(b => members.ContainsKey(b.PersonSubject))
            .Where(b => everyone.Allowed || string.Equals(b.PersonSubject, callerSubject, StringComparison.Ordinal))
            .Select(b => (Block: b, Member: members[b.PersonSubject]))
            .OrderBy(x => x.Member.Email ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Block.PersonSubject, StringComparer.Ordinal)
            .Select(x => Block(x.Block, x.Member, isYou: string.Equals(x.Block.PersonSubject, callerSubject, StringComparison.Ordinal)))
            .ToList();

        var scope = everyone.Allowed ? "everyone" : "own";
        // A week the writer has started but not marked run - it saved some blocks and is still waiting on the model
        // for someone else - is served as it stands: the blocks so far, and the Gateway's line saying so.
        var written = store.HasRun(team, week);
        var writingNote = !written && blocks.Count > 0 ? StillWritingNote : null;
        FileLog.Write($"[TeamMentorEndpoints] GET {Route}: week={week} scope={scope} blocks={blocks.Count}");
        return Results.Json(new
        {
            teamId,
            week = week.ToString(),
            weekStart = week.Start.ToString("yyyy-MM-dd"),
            weekEnd = week.End.ToString("yyyy-MM-dd"),
            timeZone = zone.Id,
            scope,
            written,
            writingNote,
            readers = Readers(members.Values),
            blocks,
        });
    }

    /// <summary>
    /// THE ONE SERIALIZATION OF A BLOCK. Every reader of a block is served what this returns for that stored row - so
    /// the person's copy and their Manager's copy cannot differ in any word. The one field that depends on the reader
    /// is <c>isYou</c>, last: whether this block is about the person reading it. The person's account subject is NOT
    /// given out (review G6): the members route does not give it out either, and a page needs only to know which
    /// block is the reader's own.
    /// </summary>
    internal static object Block(MentorBlock block, TeamMember member, bool isYou) => new
    {
        personEmail = member.Email,
        role = TeamRoles.Label(member.Role),
        tone = block.Tone,
        toneLabel = MentorTones.Label(block.Tone),
        workedOn = block.WorkedOn,
        howItWent = block.HowItWent,
        wentBadlyAndWhy = block.WentBadlyAndWhy,
        quotes = block.Quotes.Select(q => new { promptId = q.PromptId, at = q.AtUtc, text = q.Text }).ToList(),
        oneThingToTry = block.OneThingToTry,
        writtenAtUtc = block.WrittenAtUtc,
        isYou,
    };

    /// <summary>Who reads every block: each member whose role the table lets read the Mentor's page about each person,
    /// by role then email. The same list for every caller.</summary>
    internal static IReadOnlyList<object> Readers(IEnumerable<TeamMember> members) => members
        .Where(m => TeamPermissions.Grant(m.Role, TeamAction.ReadMentorPageAboutEachPerson) != TeamGrant.No)
        .OrderByDescending(m => m.Role)
        .ThenBy(m => m.Email ?? "", StringComparer.OrdinalIgnoreCase)
        .Select(m => (object)new { email = m.Email, role = TeamRoles.Label(m.Role) })
        .ToList();
}
