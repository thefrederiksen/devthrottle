using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// SENDING A WHOLE FACTORY TO A CONTRIBUTOR BY NAME (Team implementation piece 4.1). A contributor sees nothing until it is
/// shared with them, item by item; this is the share that puts a factory's customer page on their Factories page.
///
/// <list type="bullet">
/// <item><c>GET    /teams/{teamId}/factories/{factory}/contributors</c> - who it is sent to, and who it can still go to.</item>
/// <item><c>POST   /teams/{teamId}/factories/{factory}/contributors</c> with <c>{"memberId": "..."}</c> - send it.</item>
/// <item><c>DELETE /teams/{teamId}/factories/{factory}/contributors/{memberId}</c> - stop showing it to them.</item>
/// </list>
///
/// The team's Owner alone: the team gate asks the role table's factory row before any of these runs
/// (<see cref="TeamEndpointRules"/>), and a contributor is refused. Only a contributor of THIS team can be chosen, named by
/// their Team page id - never by an email or an account subject in a body. People only, like every route under
/// <c>/teams</c>.
/// </summary>
internal static class FactoryShareEndpoints
{
    /// <summary>The route as the route table writes it.</summary>
    public const string Pattern = TeamEndpoints.Path + "/{teamId}/factories/{factory}/contributors";

    internal const string NoSuchFactory = "There is no factory with that name in this team.";
    internal const string ArchivedFactory = "This factory is archived, so it cannot be sent to anyone. Restore it first.";
    internal const string NotAContributor = "That person is not a contributor of this team, so the factory was not sent.";
    internal const string NotSentToThem = "This factory was not sent to that person.";
    internal const string Note =
        "A contributor sees this factory as a customer page: what needs them, where the project is, today, what they have so far and what was sent to them. Never its sessions, computers or settings.";
    internal const string NobodyYet = "Not sent to anyone yet.";
    internal const string NoContributors = "Nobody on this team to send it to. Invite a contributor first.";

    private const string CallerItemKey = "cc.teams.factory-share.caller";

    /// <summary>Maps the three routes.</summary>
    public static void Map(IEndpointRouteBuilder app, FactoryShares shares, HostedTenantBoundary boundary, TenantRegistry tenants)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(tenants);

        var group = app.MapGroup(Pattern);
        group.AddEndpointFilter(async (filterCtx, next) =>
        {
            var http = filterCtx.HttpContext;
            var (teamId, callerSubject, denial) = TeamLibraryEndpoints.AdmitIntoTeam(http, boundary, tenants);
            if (denial is not null)
                return denial;
            http.Items[CallerItemKey] = callerSubject;
            using (boundary.EnterScope(new TenantId(teamId!)))
            {
                try
                {
                    return await next(filterCtx).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[FactoryShareEndpoints] {http.Request.Method} {http.Request.Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                    return Results.Json(new { error = "DevThrottle could not do this just now because of a fault. Try again shortly." },
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            }
        });

        group.MapGet("", (HttpContext ctx, string teamId, string factory) => shares.Describe(teamId, Caller(ctx), factory));
        group.MapPost("", async (HttpContext ctx, string teamId, string factory) =>
        {
            var (memberId, bad) = await ReadMemberId(ctx).ConfigureAwait(false);
            return bad ?? shares.Share(teamId, Caller(ctx), factory, memberId!);
        });
        group.MapDelete("/{memberId}", (HttpContext ctx, string teamId, string factory, string memberId) =>
            shares.Unshare(teamId, Caller(ctx), factory, memberId));

        FileLog.Write($"[FactoryShareEndpoints] mapped {Pattern} (GET, POST) and {Pattern}/{{memberId}} (DELETE)");
    }

    private static string Caller(HttpContext ctx) =>
        ctx.Items.TryGetValue(CallerItemKey, out var value) && value is string subject && subject.Length > 0
            ? subject
            : throw new InvalidOperationException("A factory share route ran without the caller its filter admits.");

    private static async Task<(string? MemberId, IResult? Bad)> ReadMemberId(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("memberId", out var v) || v.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(v.GetString()))
                return (null, TeamReportEndpoints.BadRequest("bad_request_body", "The body must be an object with a \"memberId\" string."));
            return (v.GetString()!.Trim(), null);
        }
        catch (JsonException ex)
        {
            return (null, TeamReportEndpoints.BadRequest("bad_request_body", $"The body could not be read as JavaScript Object Notation: {ex.Message}"));
        }
    }
}

/// <summary>What each share route answers, once the gate has let the Owner in. Every sentence is decided here (rule 7).</summary>
internal sealed class FactoryShares
{
    private readonly TeamRegistry _teams;
    private readonly FactoryRegistryStore _factories;
    private readonly FactoryContributors _shares;
    private readonly Func<DateTime> _utcNow;

    public FactoryShares(TeamRegistry teams, FactoryRegistryStore factories, FactoryContributors shares, Func<DateTime>? utcNow = null)
    {
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));
        _shares = shares ?? throw new ArgumentNullException(nameof(shares));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Who the factory is sent to, and which contributors it can still go to.</summary>
    public IResult Describe(string teamId, string caller, string factory)
    {
        if (Registered(teamId, factory) is not { } f)
            return NotFound(FactoryShareEndpoints.NoSuchFactory);
        return Results.Json(Describe(teamId, caller, f.Factory, f.Title));
    }

    /// <summary>Send the factory to one contributor of the team, named by their Team page id.</summary>
    public IResult Share(string teamId, string caller, string factory, string memberId)
    {
        if (Registered(teamId, factory) is not { } f)
            return NotFound(FactoryShareEndpoints.NoSuchFactory);
        if (f.ArchivedAtUtc is not null)
            return Results.Json(new { error = FactoryShareEndpoints.ArchivedFactory, code = "factory_archived" }, statusCode: StatusCodes.Status409Conflict);
        if (Contributor(teamId, caller, memberId) is not { } member)
        {
            FileLog.Write($"[FactoryShares] Share: team {new TenantId(teamId).ToLogString()} factory={f.Factory} - the person chosen is not a contributor, REFUSED");
            return TeamReportEndpoints.BadRequest("not_a_contributor", FactoryShareEndpoints.NotAContributor);
        }
        _shares.Share(new TenantId(teamId), f.Factory, member.AccountSubject, caller, _utcNow());
        return Results.Json(Describe(teamId, caller, f.Factory, f.Title));
    }

    /// <summary>Stop showing the factory to one person. Allowed whatever their role is now, so a share never outlives a
    /// change of role.</summary>
    public IResult Unshare(string teamId, string caller, string factory, string memberId)
    {
        if (Registered(teamId, factory) is not { } f)
            return NotFound(FactoryShareEndpoints.NoSuchFactory);
        var team = new TenantId(teamId);
        var share = _shares.ContributorsOf(team, f.Factory)
            .FirstOrDefault(s => string.Equals(TeamMemberIds.For(teamId, s.ContributorSubject), memberId, StringComparison.Ordinal));
        if (share is null || !_shares.Unshare(team, f.Factory, share.ContributorSubject))
            return NotFound(FactoryShareEndpoints.NotSentToThem);
        return Results.Json(Describe(teamId, caller, f.Factory, f.Title));
    }

    private object Describe(string teamId, string caller, string factory, string title)
    {
        var members = Members(teamId, caller);
        var names = members.ToDictionary(m => m.AccountSubject, TeamRegistry.DisplayName, StringComparer.Ordinal);
        var sent = _shares.ContributorsOf(new TenantId(teamId), factory);
        var holding = sent.Select(s => s.ContributorSubject).ToHashSet(StringComparer.Ordinal);
        return new
        {
            factory,
            name = title,
            note = FactoryShareEndpoints.Note,
            sentTo = sent.Select(s => new
            {
                memberId = TeamMemberIds.For(teamId, s.ContributorSubject),
                name = TeamReports.NameOf(s.ContributorSubject, names),
                sharedAtUtc = s.SharedAtUtc,
            }).ToList(),
            sentToEmptyText = FactoryShareEndpoints.NobodyYet,
            choices = members
                .Where(m => m.Role == TeamRole.Collaborator && !holding.Contains(m.AccountSubject))
                .Select(m => new { memberId = TeamMemberIds.For(teamId, m.AccountSubject), name = TeamRegistry.DisplayName(m) })
                .ToList(),
            choicesEmptyText = FactoryShareEndpoints.NoContributors,
        };
    }

    private Contracts.RegisteredFactoryDto? Registered(string teamId, string factory) =>
        _factories.Find(new TenantId(teamId), factory);

    private TeamMember? Contributor(string teamId, string caller, string memberId) =>
        Members(teamId, caller).FirstOrDefault(m => m.Role == TeamRole.Collaborator
                                                    && string.Equals(TeamMemberIds.For(teamId, m.AccountSubject), memberId, StringComparison.Ordinal));

    private IReadOnlyList<TeamMember> Members(string teamId, string caller)
    {
        var result = _teams.ListMembers(teamId, caller);
        if (result.Outcome != TeamMembersOutcome.Found)
            throw new InvalidOperationException("A factory share route ran for a caller who is not a member - the team gate was not run.");
        return result.Members;
    }

    private static IResult NotFound(string sentence) =>
        Results.Json(new { error = sentence, code = "factory_not_found" }, statusCode: StatusCodes.Status404NotFound);
}
