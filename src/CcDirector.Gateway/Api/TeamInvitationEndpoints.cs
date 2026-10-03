using CcDirector.Core.Utilities;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Team invitations by email that expire (devthrottle_internal#2301): the invite form (screen S2), the team's waiting
/// invitations, resend and cancel, and the accept page (screen S3).
///
/// <list type="bullet">
/// <item><c>GET /teams/{teamId}/invitations/options</c> - what the invite form offers the caller.</item>
/// <item><c>GET /teams/{teamId}/invitations</c> - the team's invitations, for its Owner and Managers.</item>
/// <item><c>POST /teams/{teamId}/invitations</c> with <c>{"email": "...", "role": "Developer"}</c> - invite.</item>
/// <item><c>POST /teams/{teamId}/invitations/{invitationId}/resend</c> - send again; a new 7 days.</item>
/// <item><c>POST /teams/{teamId}/invitations/{invitationId}/cancel</c> - cancel a waiting invitation.</item>
/// <item><c>POST /team-invitations/open</c>, <c>/accept</c>, <c>/decline</c> with <c>{"token": "..."}</c> - the accept
/// page's three calls, for the person holding the link.</item>
/// </list>
///
/// THE LINK'S SECRET TRAVELS IN A REQUEST BODY, never in an API path or query, because the Gateway's access log
/// records every path and query. (The accept PAGE's own address carries it; that one line is redacted in the access
/// log - see <see cref="RedactForLog"/>.)
///
/// WHO IS ASKING comes from the caller's device key, exactly as on the other team routes
/// (<see cref="TeamEndpoints.ResolveCaller"/>). DARK until the owner releases Teams: these routes are mapped only when
/// <c>CC_GATEWAY_TEAMS=1</c>, beside the team routes. A session key is refused by the session-key guard's default
/// deny, so an agent can neither invite nor accept.
/// </summary>
internal static class TeamInvitationEndpoints
{
    /// <summary>The accept page's API root.</summary>
    public const string AcceptPath = "/team-invitations";

    /// <summary>The Cockpit page the email's link opens: <c>/invite/{token}</c>.</summary>
    public const string CockpitAcceptPagePrefix = "/invite/";

    internal sealed record CreateInvitationRequest(string? Email, string? Role);

    internal sealed record TokenRequest(string? Token);

    /// <summary>Maps the eight routes.</summary>
    public static void Map(IEndpointRouteBuilder app, TeamRegistry teams, HostedTenantBoundary boundary, TenantRegistry tenants,
        ITeamInvitationMailer mailer)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(mailer);

        var root = TeamEndpoints.Path + "/{teamId}/invitations";

        app.MapGet(root + "/options", (HttpContext ctx, string teamId) => Guarded("GET invitations/options", () =>
        {
            var caller = TeamEndpoints.ResolveCaller(ctx, boundary, tenants);
            return Task.FromResult(caller.Denial ?? Options(teams, caller.Subject!, teamId));
        }));

        app.MapGet(root, (HttpContext ctx, string teamId) => Guarded("GET invitations", () =>
        {
            var caller = TeamEndpoints.ResolveCaller(ctx, boundary, tenants);
            return Task.FromResult(caller.Denial ?? List(teams, caller.Subject!, teamId));
        }));

        app.MapPost(root, (HttpContext ctx, string teamId) => Guarded("POST invitations", async () =>
        {
            var caller = TeamEndpoints.ResolveCaller(ctx, boundary, tenants);
            if (caller.Denial is not null) return caller.Denial;
            var body = await ReadBody<CreateInvitationRequest>(ctx, "{\"email\": \"<address>\", \"role\": \"Developer\"}").ConfigureAwait(false);
            if (body.Denial is not null) return body.Denial;
            return await CreateAsync(teams, mailer, caller.Subject!, teamId, body.Value, ctx.RequestAborted).ConfigureAwait(false);
        }));

        app.MapPost(root + "/{invitationId}/resend", (HttpContext ctx, string teamId, string invitationId) => Guarded("POST invitations/resend", async () =>
        {
            var caller = TeamEndpoints.ResolveCaller(ctx, boundary, tenants);
            if (caller.Denial is not null) return caller.Denial;
            return await ResendAsync(teams, mailer, caller.Subject!, teamId, invitationId, ctx.RequestAborted).ConfigureAwait(false);
        }));

        app.MapPost(root + "/{invitationId}/cancel", (HttpContext ctx, string teamId, string invitationId) => Guarded("POST invitations/cancel", () =>
        {
            var caller = TeamEndpoints.ResolveCaller(ctx, boundary, tenants);
            return Task.FromResult(caller.Denial ?? Answer(teams.CancelInvitation(teamId, invitationId, caller.Subject!), "cancel"));
        }));

        app.MapPost(AcceptPath + "/open", (Func<HttpContext, Task<IResult>>)(ctx => Guarded("POST team-invitations/open", () =>
            WithToken(ctx, boundary, tenants, (subject, token) => Answer(teams.OpenInvitation(token, subject), "open")))));

        app.MapPost(AcceptPath + "/accept", (Func<HttpContext, Task<IResult>>)(ctx => Guarded("POST team-invitations/accept", () =>
            WithToken(ctx, boundary, tenants, (subject, token) => Answer(teams.AcceptInvitation(token, subject), "accept")))));

        app.MapPost(AcceptPath + "/decline", (Func<HttpContext, Task<IResult>>)(ctx => Guarded("POST team-invitations/decline", () =>
            WithToken(ctx, boundary, tenants, (subject, token) => Answer(teams.DeclineInvitation(token, subject), "decline")))));

        FileLog.Write($"[TeamInvitationEndpoints] mapped {root}(/options, /{{invitationId}}/resend, /{{invitationId}}/cancel) and {AcceptPath}/open|accept|decline");
    }

    /// <summary>The invite form's model (S2): the team, the caller's role, every role with whether it may be chosen and
    /// its sentence, and the reason nothing can be sent now (or null).</summary>
    internal static IResult Options(TeamRegistry teams, string callerSubject, string teamId)
    {
        var options = teams.InviteOptions(teamId, callerSubject);
        if (options is null)
            return Results.NotFound(new { error = TeamEndpoints.NoSuchTeamRefusal });
        return Results.Json(new
        {
            teamId = options.TeamId,
            teamName = options.TeamName,
            yourRole = TeamRoles.Label(options.CallerRole),
            roles = options.Roles.Select(r => new { role = TeamRoles.Label(r.Role), allowed = r.Allowed, hint = r.Hint }).ToList(),
            blocked = options.Blocked,
            expiryNote = "The invitation expires in 7 days.",
        });
    }

    /// <summary>The team's invitations for its Owner and Managers; 404 for anyone not in the team.</summary>
    internal static IResult List(TeamRegistry teams, string callerSubject, string teamId)
    {
        var list = teams.ListInvitations(teamId, callerSubject);
        if (list is null)
            return Results.NotFound(new { error = TeamEndpoints.NoSuchTeamRefusal });
        return Results.Json(new { count = list.Count, invitations = list.Select(Describe).ToList() });
    }

    /// <summary>Invite, then ask the website to send the email. The invitation is stored before the email is asked
    /// for, so an email that could not be sent is REPORTED with its reason (201 with <c>email.sent=false</c>) and the
    /// invitation can be resent - it is never reported as sent, and never lost.</summary>
    internal static async Task<IResult> CreateAsync(TeamRegistry teams, ITeamInvitationMailer mailer, string callerSubject,
        string teamId, CreateInvitationRequest? body, CancellationToken ct)
    {
        if (!TryParseRole(body?.Role, out var role))
            return Results.BadRequest(new { error = "Choose the role to invite them as: Manager, Developer or Collaborator." });

        var result = teams.CreateInvitation(teamId, callerSubject, body?.Email, role);
        if (result.Outcome != TeamInvitationOutcome.Done)
            return Answer(result, "create");

        var mail = await mailer.SendAsync(result.Invitation!.Id, result.Invitation.TeamId, result.AcceptToken!, ct).ConfigureAwait(false);
        FileLog.Write($"[TeamInvitationEndpoints] POST invitations: stored, email sent={mail.Sent}");
        return Results.Json(new { invitation = Describe(result.Invitation), email = DescribeMail(mail) }, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>Resend: a new 7 days and a new link, then the email again.</summary>
    internal static async Task<IResult> ResendAsync(TeamRegistry teams, ITeamInvitationMailer mailer, string callerSubject,
        string teamId, string invitationId, CancellationToken ct)
    {
        var result = teams.ResendInvitation(teamId, invitationId, callerSubject);
        if (result.Outcome != TeamInvitationOutcome.Done)
            return Answer(result, "resend");

        var mail = await mailer.SendAsync(result.Invitation!.Id, result.Invitation.TeamId, result.AcceptToken!, ct).ConfigureAwait(false);
        FileLog.Write($"[TeamInvitationEndpoints] POST invitations/resend: renewed, email sent={mail.Sent}");
        return Results.Json(new { invitation = Describe(result.Invitation), email = DescribeMail(mail) });
    }

    /// <summary>One result as HTTP: 200 with the invitation, or the refusal with its status.</summary>
    internal static IResult Answer(TeamInvitationResult result, string action)
    {
        FileLog.Write($"[TeamInvitationEndpoints] {action}: outcome={result.Outcome}");
        return result.Outcome switch
        {
            TeamInvitationOutcome.Done => Results.Json(new { invitation = Describe(result.Invitation!) }),
            TeamInvitationOutcome.NotFound => Results.NotFound(new { error = result.Refusal }),
            TeamInvitationOutcome.Forbidden => Results.Json(new { error = result.Refusal }, statusCode: StatusCodes.Status403Forbidden),
            TeamInvitationOutcome.Refused => Results.Json(new { error = result.Refusal }, statusCode: StatusCodes.Status409Conflict),
            TeamInvitationOutcome.Unavailable => Results.Json(new { error = result.Refusal }, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => throw new InvalidOperationException($"Unknown invitation outcome {result.Outcome}."),
        };
    }

    /// <summary>
    /// The accept page's address with its secret cut out, for the access log: <c>/invite/{token}</c> and the same
    /// address carried in a sign-in redirect's <c>next=</c> (URL-encoded) both lose the token. Everything else passes
    /// through unchanged.
    /// </summary>
    internal static string RedactForLog(string pathOrQuery)
    {
        if (string.IsNullOrEmpty(pathOrQuery))
            return pathOrQuery;
        var redacted = System.Text.RegularExpressions.Regex.Replace(pathOrQuery,
            "(/invite/)[^/?&#%]+", "$1[redacted]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(redacted,
            "(%2Finvite%2F)[^/?&#]+?(?=$|&|%3F|%23|#)", "$1[redacted]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static object Describe(TeamInvitation i) => new
    {
        id = i.Id,
        teamId = i.TeamId,
        teamName = i.TeamName,
        email = i.Email,
        role = TeamRoles.Label(i.Role),
        state = i.State,
        invitedBy = i.InvitedBy,
        acceptedBy = i.AcceptedBy,
        paidBy = i.PaidBy,
        sentAtUtc = i.SentAtUtc,
        expiresAtUtc = i.ExpiresAtUtc,
        signedInAs = i.SignedInAs,
        canRespond = i.CanRespond,
        refusal = i.Refusal,
    };

    private static object DescribeMail(CcDirector.Core.Account.TeamInvitationMailResult mail) => new
    {
        sent = mail.Sent,
        message = mail.Sent
            ? "The invitation email is on its way."
            : $"The invitation is saved, but its email was not sent: {mail.Error} Resend it from the team's invitations.",
    };

    private static bool TryParseRole(string? value, out TeamRole role)
    {
        role = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        foreach (var candidate in Enum.GetValues<TeamRole>())
        {
            if (string.Equals(TeamRoles.Label(candidate), value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }
        return false;
    }

    private static async Task<IResult> WithToken(HttpContext ctx, HostedTenantBoundary boundary, TenantRegistry tenants,
        Func<string, string, IResult> handle)
    {
        var caller = TeamEndpoints.ResolveCaller(ctx, boundary, tenants);
        if (caller.Denial is not null) return caller.Denial;
        var body = await ReadBody<TokenRequest>(ctx, "{\"token\": \"<the code from the invitation link>\"}").ConfigureAwait(false);
        if (body.Denial is not null) return body.Denial;
        if (string.IsNullOrWhiteSpace(body.Value?.Token))
            return Results.NotFound(new { error = TeamInvitationRefusals.NoSuchInvitation });
        return handle(caller.Subject!, body.Value!.Token!);
    }

    private static async Task<(T? Value, IResult? Denial)> ReadBody<T>(HttpContext ctx, string shape) where T : class
    {
        try
        {
            return (await ctx.Request.ReadFromJsonAsync<T>(ctx.RequestAborted).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or BadHttpRequestException)
        {
            FileLog.Write($"[TeamInvitationEndpoints] rejected, the request body is not readable JSON ({ex.GetType().Name})");
            return (null, Results.BadRequest(new { error = $"The request body is not readable JSON. Send {shape}." }));
        }
    }

    private static async Task<IResult> Guarded(string route, Func<Task<IResult>> handle)
    {
        try
        {
            return await handle().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TeamInvitationEndpoints] {route} FAILED ({ex.GetType().Name}): {ex.Message}");
            return Results.Json(new { error = "DevThrottle could not do that with the invitation just now because of a fault. Try again shortly." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
