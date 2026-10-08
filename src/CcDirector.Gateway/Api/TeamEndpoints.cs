using CcDirector.Core.Utilities;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Teams, first version (devthrottle_internal#2300): create a team, list the caller's teams with their role in
/// each (the team switcher, screen S11), and list one team's members with their roles (screen S1).
///
/// <list type="bullet">
/// <item><c>GET /teams</c> - the caller's teams. An account in no team gets an empty list.</item>
/// <item><c>POST /teams</c> with <c>{"name": "..."}</c> - create a team; the caller becomes its Owner.</item>
/// <item><c>GET /teams/{teamId}/members</c> - the members, only for a member of that team.</item>
/// <item><c>GET /teams/{teamId}/page</c> - the Team page (screen S1, devthrottle_internal#2303): members, seats, waiting
/// invitations and what the caller may do to each, decided here.</item>
/// <item><c>PUT /teams/{teamId}/members/{memberId}/role</c> with <c>{"role": "..."}</c> - change a member's role.</item>
/// <item><c>DELETE /teams/{teamId}/members/{memberId}</c> - remove a member.</item>
/// <item><c>PUT /teams/{teamId}/name</c> with <c>{"name": "..."}</c> - rename the team (the Owner).</item>
/// <item><c>POST /teams/{teamId}/leave</c> - leave the team (any member but the Owner).</item>
/// <item><c>DELETE /teams/{teamId}</c> with <c>{"name": "..."}</c>, the team's name typed exactly - delete the team (the
/// Owner, once they are its last member). Nothing is erased; see <see cref="TeamRegistry.DeleteTeam"/>.</item>
/// <item><c>GET /teams/{teamId}/bill</c> - the Billing section (Teams v1, the team bill without Stripe), for the Owner and
/// a Manager. <c>POST /teams/{teamId}/bill/start</c>, <c>POST .../bill/renew</c>, <c>PUT .../bill/auto-renew</c> with
/// <c>{"on": true}</c> and <c>POST .../bill/cancel</c> - the Owner's four actions. Nothing is charged.</item>
/// <item><c>GET /teams/{teamId}/fleet-map</c> - the team's Fleet Map by role (devthrottle_internal#2312): every Director
/// on the team by person for the Owner and a Manager, only their own for a Developer, none for a Collaborator. Names and
/// status only - see <see cref="TeamFleetMap"/>.</item>
/// </list>
///
/// WHO IS ASKING comes from the caller's authenticated device key and nothing else: the key's tenant is the
/// caller's personal tenant, and that tenant's row names the account subject. Nothing the client sends names
/// the caller. A device key that does not resolve to a personal account is refused - it cannot say which
/// person is asking.
///
/// TEAMS ARE A HOSTED FEATURE. A self-hosted Gateway holds one account and no account subject, so every route
/// here answers that teams are not available on it, rather than inventing a team-less answer.
///
/// Inherits the host-wide device-key middleware; a session key is refused by the session-key guard's default
/// deny, so an agent cannot create teams or read a member list. The subject and email are never logged.
/// </summary>
internal static class TeamEndpoints
{
    /// <summary>The route root.</summary>
    public const string Path = "/teams";

    /// <summary>What a self-hosted Gateway says on every team route.</summary>
    internal const string SelfHostedRefusal =
        "Teams are part of the hosted DevThrottle service. This is a self-hosted Gateway with a single account, so it has no teams.";

    /// <summary>The answer for a team that does not exist or that the caller is not a member of - deliberately one
    /// answer, so the route cannot be used to learn which teams exist.</summary>
    internal const string NoSuchTeamRefusal = "There is no team with that id that you are a member of.";

    /// <summary>The team's bill (Teams v1, the team bill without Stripe), and the root of the Owner's actions on it.</summary>
    public const string BillPath = "/teams/{teamId}/bill";

    /// <summary>The team's own address: delete it (Teams v1, rename, delete and leave).</summary>
    public const string TeamPath = "/teams/{teamId}";

    /// <summary>Rename the team.</summary>
    public const string NamePath = "/teams/{teamId}/name";

    /// <summary>Leave the team.</summary>
    public const string LeavePath = "/teams/{teamId}/leave";

    /// <summary>The body of <c>PUT /teams/{teamId}/name</c> and of <c>DELETE /teams/{teamId}</c>: the team's new name, or
    /// for a delete the name typed to confirm it.</summary>
    internal sealed record TeamNameRequest(string? Name);

    /// <summary>The body of <c>PUT /teams/{teamId}/bill/auto-renew</c>.</summary>
    internal sealed record AutoRenewRequest(bool? On);

    /// <summary>The body of <c>POST /teams</c>.</summary>
    internal sealed record CreateTeamRequest(string? Name);

    /// <summary>Maps every route listed in the class comment.</summary>
    public static void Map(IEndpointRouteBuilder app, TeamRegistry teams, HostedTenantBoundary boundary, TenantRegistry tenants,
        TeamFleetMap fleetMap, Pairing.DeviceRegistry devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(fleetMap);

        app.MapGet(Path, (HttpContext ctx) => Guarded("GET /teams", () =>
        {
            var caller = ResolveCaller(ctx, boundary, tenants);
            if (caller.Denial is not null) return caller.Denial;
            // ResolveCaller has just shown the request is bound to this person's own tenant.
            var own = boundary.ResolveRequestTenant(ctx)!.Value;
            return ListTeams(teams, caller.Subject!, devices.HasADirectorOnRecord(own));
        }));

        app.MapPost(Path, async (HttpContext ctx) =>
        {
            try
            {
                // Who is asking is settled before the body is read: an unidentified request gets the refusal,
                // whatever it sent.
                var caller = ResolveCaller(ctx, boundary, tenants);
                if (caller.Denial is not null) return caller.Denial;

                CreateTeamRequest? body;
                try
                {
                    body = await ctx.Request.ReadFromJsonAsync<CreateTeamRequest>(ctx.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[TeamEndpoints] POST /teams: rejected, the request body is not readable JSON ({ex.GetType().Name})");
                    return Results.BadRequest(new { error = "The request body is not readable JSON. Send {\"name\": \"<team name>\"}." });
                }

                return CreateTeam(teams, caller.Subject!, body);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TeamEndpoints] POST /teams FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "The team could not be created just now because of a fault in DevThrottle. Try again shortly." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet(Path + "/{teamId}/members", (HttpContext ctx, string teamId) => Guarded("GET /teams/{teamId}/members", () =>
        {
            var caller = ResolveCaller(ctx, boundary, tenants);
            return caller.Denial ?? ListMembers(teams, caller.Subject!, teamId);
        }));

        // The Team page (screen S1, devthrottle_internal#2303): the page's model, change a member's role, remove a member.
        app.MapGet(Path + "/{teamId}/page", (HttpContext ctx, string teamId) => Guarded("GET /teams/{teamId}/page", () =>
        {
            var caller = ResolveCaller(ctx, boundary, tenants);
            return caller.Denial ?? TeamPage(teams, caller.Subject!, teamId);
        }));

        app.MapPut(Path + "/{teamId}/members/{memberId}/role", async (HttpContext ctx, string teamId, string memberId) =>
        {
            try
            {
                var caller = ResolveCaller(ctx, boundary, tenants);
                if (caller.Denial is not null) return caller.Denial;

                ChangeRoleRequest? body;
                try
                {
                    body = await ctx.Request.ReadFromJsonAsync<ChangeRoleRequest>(ctx.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or BadHttpRequestException)
                {
                    FileLog.Write($"[TeamEndpoints] PUT /teams/{{teamId}}/members/{{memberId}}/role: rejected, the request body is not readable JSON ({ex.GetType().Name})");
                    return Results.BadRequest(new { error = "The request body is not readable JSON. Send {\"role\": \"Developer\"}." });
                }

                return ChangeRole(teams, caller.Subject!, teamId, memberId, body);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TeamEndpoints] PUT /teams/{{teamId}}/members/{{memberId}}/role FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "The role could not be changed just now because of a fault in DevThrottle. Try again shortly." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapDelete(Path + "/{teamId}/members/{memberId}", (HttpContext ctx, string teamId, string memberId) => Guarded("DELETE /teams/{teamId}/members/{memberId}", () =>
        {
            var caller = ResolveCaller(ctx, boundary, tenants);
            return caller.Denial ?? RemoveMember(teams, caller.Subject!, teamId, memberId);
        }));

        app.MapGet(TeamFleetMap.RoutePattern, (HttpContext ctx, string teamId) => Guarded("GET /teams/{teamId}/fleet-map", () =>
        {
            var caller = ResolveCaller(ctx, boundary, tenants);
            return caller.Denial ?? ReadFleetMap(fleetMap, caller.Subject!, teamId);
        }));

        // The team's bill (Teams v1, the team bill without Stripe): read by the Owner and a Manager, changed by the Owner.
        app.MapGet(BillPath, (HttpContext ctx, string teamId) => Guarded("GET " + BillPath, () =>
        {
            var caller = ResolveCaller(ctx, boundary, tenants);
            return caller.Denial ?? ReadBill(teams, caller.Subject!, teamId);
        }));
        app.MapPost(BillPath + "/start", (HttpContext ctx, string teamId) => Guarded("POST " + BillPath + "/start", () =>
        {
            var caller = ResolveCaller(ctx, boundary, tenants);
            return caller.Denial ?? AnswerBillChange(teams.StartTeamPlan(teamId, caller.Subject!), "start the team plan");
        }));
        app.MapPost(BillPath + "/renew", (HttpContext ctx, string teamId) => Guarded("POST " + BillPath + "/renew", () =>
        {
            var caller = ResolveCaller(ctx, boundary, tenants);
            return caller.Denial ?? AnswerBillChange(teams.RenewTeamPlan(teamId, caller.Subject!), "renew the team plan");
        }));
        app.MapPost(BillPath + "/cancel", (HttpContext ctx, string teamId) => Guarded("POST " + BillPath + "/cancel", () =>
        {
            var caller = ResolveCaller(ctx, boundary, tenants);
            return caller.Denial ?? AnswerBillChange(teams.CancelTeamPlan(teamId, caller.Subject!), "cancel the team plan");
        }));
        app.MapPut(BillPath + "/auto-renew", async (HttpContext ctx, string teamId) =>
        {
            try
            {
                var caller = ResolveCaller(ctx, boundary, tenants);
                if (caller.Denial is not null) return caller.Denial;

                AutoRenewRequest? body;
                try
                {
                    body = await ctx.Request.ReadFromJsonAsync<AutoRenewRequest>(ctx.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or BadHttpRequestException)
                {
                    FileLog.Write($"[TeamEndpoints] PUT {BillPath}/auto-renew: rejected, the request body is not readable JSON ({ex.GetType().Name})");
                    return Results.BadRequest(new { error = "The request body is not readable JSON. Send {\"on\": true} or {\"on\": false}." });
                }
                if (body?.On is not { } on)
                    return Results.BadRequest(new { error = "Say whether auto-renew is on: send {\"on\": true} or {\"on\": false}." });

                return AnswerBillChange(teams.SetTeamPlanAutoRenew(teamId, caller.Subject!, on), on ? "switch auto-renew on" : "switch auto-renew off");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TeamEndpoints] PUT {BillPath}/auto-renew FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "Auto-renew could not be changed just now because of a fault in DevThrottle. Try again shortly." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // Rename, delete and leave the team (Teams v1).
        app.MapPut(NamePath, async (HttpContext ctx, string teamId) =>
        {
            try
            {
                var caller = ResolveCaller(ctx, boundary, tenants);
                if (caller.Denial is not null) return caller.Denial;
                var body = await ReadNameBody(ctx, "PUT " + NamePath).ConfigureAwait(false);
                if (body.Denial is not null) return body.Denial;
                return RenameTeam(teams, caller.Subject!, teamId, body.Name);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TeamEndpoints] PUT {NamePath} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "The team could not be renamed just now because of a fault in DevThrottle. Try again shortly." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        app.MapPost(LeavePath, (HttpContext ctx, string teamId) => Guarded("POST " + LeavePath, () =>
        {
            var caller = ResolveCaller(ctx, boundary, tenants);
            return caller.Denial ?? AnswerChange(teams.LeaveTeam(teamId ?? "", caller.Subject!), "leave team");
        }));
        app.MapDelete(TeamPath, async (HttpContext ctx, string teamId) =>
        {
            try
            {
                var caller = ResolveCaller(ctx, boundary, tenants);
                if (caller.Denial is not null) return caller.Denial;
                var body = await ReadNameBody(ctx, "DELETE " + TeamPath).ConfigureAwait(false);
                if (body.Denial is not null) return body.Denial;
                return AnswerChange(teams.DeleteTeam(teamId ?? "", caller.Subject!, body.Name), "delete team");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TeamEndpoints] DELETE {TeamPath} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "The team could not be deleted just now because of a fault in DevThrottle. Nothing was changed if this happened before the delete; try again shortly." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        FileLog.Write($"[TeamEndpoints] mapped GET {Path}, POST {Path}, GET {Path}/{{teamId}}/members, GET {Path}/{{teamId}}/page, PUT {Path}/{{teamId}}/members/{{memberId}}/role, DELETE {Path}/{{teamId}}/members/{{memberId}}, GET {TeamFleetMap.RoutePattern}, GET {BillPath}, POST {BillPath}/start, POST {BillPath}/renew, POST {BillPath}/cancel, PUT {BillPath}/auto-renew, PUT {NamePath}, POST {LeavePath}, DELETE {TeamPath}");
    }

    /// <summary>The body of <c>PUT /teams/{teamId}/members/{memberId}/role</c>.</summary>
    internal sealed record ChangeRoleRequest(string? Role);

    /// <summary>Rename the team. 400 for a name the create rule refuses, otherwise the change's own answer.</summary>
    internal static IResult RenameTeam(TeamRegistry teams, string callerSubject, string? teamId, string? name)
    {
        if (TeamRegistry.NameRefusal(name) is { } refusal)
        {
            FileLog.Write($"[TeamEndpoints] PUT {NamePath}: rejected, the name is not usable");
            return Results.BadRequest(new { error = refusal });
        }
        return AnswerChange(teams.RenameTeam(teamId ?? "", callerSubject, name), "rename team");
    }

    /// <summary>Reads <c>{"name": "..."}</c>, or the 400 that says what to send.</summary>
    private static async Task<(string? Name, IResult? Denial)> ReadNameBody(HttpContext ctx, string route)
    {
        try
        {
            var body = await ctx.Request.ReadFromJsonAsync<TeamNameRequest>(ctx.RequestAborted).ConfigureAwait(false);
            return (body?.Name, null);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or BadHttpRequestException)
        {
            FileLog.Write($"[TeamEndpoints] {route}: rejected, the request body is not readable JSON ({ex.GetType().Name})");
            return (null, Results.BadRequest(new { error = "The request body is not readable JSON. Send {\"name\": \"<team name>\"}." }));
        }
    }

    /// <summary>The Team page's model for the caller: 200, 404 for anyone not in the team, 403 with the role table's
    /// sentence for a role that has no Team page.</summary>
    internal static IResult TeamPage(TeamRegistry teams, string callerSubject, string? teamId)
    {
        var result = teams.DescribeTeamPage(teamId ?? "", callerSubject);
        switch (result.Outcome)
        {
            case TeamPageOutcome.NotFound:
                FileLog.Write("[TeamEndpoints] GET /teams/{teamId}/page: no such team for this caller");
                return Results.NotFound(new { error = NoSuchTeamRefusal });
            case TeamPageOutcome.Forbidden:
                FileLog.Write("[TeamEndpoints] GET /teams/{teamId}/page: refused for this role");
                return Results.Json(new { error = result.Refusal }, statusCode: StatusCodes.Status403Forbidden);
        }

        var page = result.Page!;
        FileLog.Write($"[TeamEndpoints] GET /teams/{{teamId}}/page: {page.Members.Count} member(s), {page.Invitations.Count} invitation(s)");
        return Results.Json(new
        {
            teamId = page.TeamId,
            teamName = page.TeamName,
            yourRole = page.YourRole,
            summary = page.Summary,
            canInvite = page.CanInvite,
            billNotice = page.BillNotice,
            members = page.Members.Select(m => new
            {
                memberId = m.MemberId,
                name = m.Name,
                email = m.Email,
                role = m.Role,
                seat = m.Seat,
                isYou = m.IsYou,
                joinedAtUtc = m.JoinedAtUtc,
                canChangeRole = m.CanChangeRole,
                roleChoices = m.RoleChoices,
                canRemove = m.CanRemove,
                removeWarning = m.RemoveWarning,
            }).ToList(),
            invitations = page.Invitations.Select(i => new
            {
                id = i.Id,
                email = i.Email,
                role = i.Role,
                state = i.State,
                seat = i.Seat,
                invitedBy = i.InvitedBy,
                sentAtUtc = i.SentAtUtc,
                expiresAtUtc = i.ExpiresAtUtc,
                canResend = i.CanResend,
                canCancel = i.CanCancel,
            }).ToList(),
            bill = BillJson(page.Bill),
            manage = new
            {
                canRename = page.Manage.CanRename,
                canDelete = page.Manage.CanDelete,
                deleteBlocked = page.Manage.DeleteBlocked,
                deleteWarning = page.Manage.DeleteWarning,
                canLeave = page.Manage.CanLeave,
                leaveWarning = page.Manage.LeaveWarning,
            },
        });
    }

    /// <summary>The Billing section on its own: 200, 404 for anyone not in the team, 403 with the role table's sentence
    /// for a role that may not see the bill.</summary>
    internal static IResult ReadBill(TeamRegistry teams, string callerSubject, string? teamId)
    {
        var result = teams.DescribeTeamBill(teamId ?? "", callerSubject);
        switch (result.Outcome)
        {
            case TeamBillViewOutcome.NotFound:
                FileLog.Write($"[TeamEndpoints] GET {BillPath}: no such team for this caller");
                return Results.NotFound(new { error = NoSuchTeamRefusal });
            case TeamBillViewOutcome.Forbidden:
                FileLog.Write($"[TeamEndpoints] GET {BillPath}: refused for this role");
                return Results.Json(new { error = result.Refusal }, statusCode: StatusCodes.Status403Forbidden);
        }
        FileLog.Write($"[TeamEndpoints] GET {BillPath}: state={result.View!.State}");
        return Results.Json(BillJson(result.View));
    }

    /// <summary>One change to the bill as HTTP: 200, 404, 403 (the caller's role) or 409 (the bill's own rules).</summary>
    internal static IResult AnswerBillChange(TeamBillChangeResult result, string action)
    {
        FileLog.Write($"[TeamEndpoints] {action}: outcome={result.Outcome}");
        return result.Outcome switch
        {
            TeamBillChangeOutcome.Done => Results.Json(new { done = true }),
            TeamBillChangeOutcome.NotFound => Results.NotFound(new { error = NoSuchTeamRefusal }),
            TeamBillChangeOutcome.Forbidden => Results.Json(new { error = result.Refusal }, statusCode: StatusCodes.Status403Forbidden),
            TeamBillChangeOutcome.Refused => Results.Json(new { error = result.Refusal }, statusCode: StatusCodes.Status409Conflict),
            _ => throw new InvalidOperationException($"Unknown bill change outcome {result.Outcome}."),
        };
    }

    /// <summary>The Billing section as the wire carries it, or null when the caller may not see it.</summary>
    private static object? BillJson(TeamBillView? bill) => bill is null ? null : new
    {
        state = bill.State,
        statusLabel = bill.StatusLabel,
        statusLine = bill.StatusLine,
        seats = bill.Seats,
        seatsLine = bill.SeatsLine,
        priceLine = bill.PriceLine,
        amountLine = bill.AmountLine,
        chargeLine = bill.ChargeLine,
        periodEndUtc = bill.PeriodEndUtc,
        periodEnd = bill.PeriodEnd,
        autoRenew = bill.AutoRenew,
        canChange = bill.CanChange,
        canStart = bill.CanStart,
        canRenew = bill.CanRenew,
        canSetAutoRenew = bill.CanSetAutoRenew,
        canCancel = bill.CanCancel,
        checkout = bill.Checkout is not { } c ? null : new
        {
            title = c.Title,
            seatsLine = c.SeatsLine,
            priceLine = c.PriceLine,
            totalLine = c.TotalLine,
            chargeLine = c.ChargeLine,
            periodLine = c.PeriodLine,
            confirmLabel = c.ConfirmLabel,
        },
        note = bill.Note,
        cancelWarning = bill.CancelWarning,
        history = bill.History.Select(h => new
        {
            id = h.Id,
            period = h.Period,
            seats = h.Seats,
            amount = h.Amount,
            charged = h.Charged,
            reason = h.Reason,
        }).ToList(),
    };

    /// <summary>Change a member's role. 200, or the refusal with its status.</summary>
    internal static IResult ChangeRole(TeamRegistry teams, string callerSubject, string? teamId, string? memberId, ChangeRoleRequest? body)
    {
        if (!TryParseRole(body?.Role, out var role))
            return Results.BadRequest(new { error = "Choose the new role: Manager, Developer or Collaborator." });
        return AnswerChange(teams.ChangeMemberRole(teamId ?? "", callerSubject, memberId ?? "", role), "change role");
    }

    /// <summary>Remove a member. 200, or the refusal with its status.</summary>
    internal static IResult RemoveMember(TeamRegistry teams, string callerSubject, string? teamId, string? memberId) =>
        AnswerChange(teams.RemoveTeamMember(teamId ?? "", callerSubject, memberId ?? ""), "remove member");

    /// <summary>One change as HTTP: 200, 404, 403 (the caller's role) or 409 (the team's own rules).</summary>
    internal static IResult AnswerChange(TeamMemberChangeResult result, string action)
    {
        FileLog.Write($"[TeamEndpoints] {action}: outcome={result.Outcome}");
        return result.Outcome switch
        {
            TeamMemberChangeOutcome.Done => Results.Json(new { done = true }),
            TeamMemberChangeOutcome.NotFound => Results.NotFound(new { error = result.Refusal }),
            TeamMemberChangeOutcome.Forbidden => Results.Json(new { error = result.Refusal }, statusCode: StatusCodes.Status403Forbidden),
            TeamMemberChangeOutcome.Refused => Results.Json(new { error = result.Refusal }, statusCode: StatusCodes.Status409Conflict),
            _ => throw new InvalidOperationException($"Unknown member change outcome {result.Outcome}."),
        };
    }

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

    /// <summary>A key whose tenant is not a person's own account - a team's own key, Director or session - is told this
    /// by every team route that admits through ResolveCaller (devthrottle_internal#2309, review F4).</summary>
    internal const string NotAPersonalAccountRefusal =
        "DevThrottle cannot tell which person is making this request: the device is not signed in to a personal account. Teams are managed from a device signed in to your own account.";

    /// <summary>
    /// Who is asking. On hosted: the account subject behind the caller's device key, or a refusal. Off hosted: the
    /// self-hosted refusal. Internal so every branch is tested.
    /// </summary>
    internal static (string? Subject, IResult? Denial) ResolveCaller(HttpContext ctx, HostedTenantBoundary boundary, TenantRegistry tenants)
    {
        if (!GatewayHostedMode.IsHosted)
        {
            FileLog.Write("[TeamEndpoints] ResolveCaller: self-hosted Gateway - teams are not available here");
            return (null, Results.NotFound(new { error = SelfHostedRefusal }));
        }

        if (!boundary.IsHosted)
        {
            FileLog.Write("[TeamEndpoints] ResolveCaller: MISWIRED - hosted mode with no hosted tenant boundary; refusing");
            return (null, Results.Json(new { error = "This hosted DevThrottle Gateway cannot tell which account is calling, so it will not answer. This is a fault in the Gateway deployment, not a problem with your account." },
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        if (boundary.ResolveRequestTenant(ctx) is not { } tenant)
        {
            FileLog.Write("[TeamEndpoints] ResolveCaller: DENIED - no account is bound to this request");
            return (null, Results.Json(new { error = "No DevThrottle account is bound to this request. The device making this call is not signed in to an account on this Gateway." },
                statusCode: StatusCodes.Status403Forbidden));
        }

        var subject = tenants.SubjectForTenant(tenant);
        if (string.IsNullOrWhiteSpace(subject))
        {
            FileLog.Write($"[TeamEndpoints] ResolveCaller: DENIED - tenant {tenant.ToLogString()} is not a personal account, so the person asking is unknown");
            return (null, Results.Json(new { error = NotAPersonalAccountRefusal },
                statusCode: StatusCodes.Status403Forbidden));
        }

        return (subject, null);
    }

    /// <summary>The caller's teams, each with the caller's role and the member count, and where a fresh browser of
    /// theirs starts (<see cref="TeamStart"/>).</summary>
    internal static IResult ListTeams(TeamRegistry teams, string callerSubject, bool ownAccountHasADirector)
    {
        var list = teams.ListTeamsFor(callerSubject);
        var start = TeamStart.For(ownAccountHasADirector, list);
        FileLog.Write($"[TeamEndpoints] GET /teams: {list.Count} team(s), a fresh browser starts at {TeamStart.Wire(start.Place)}");
        return Results.Json(new
        {
            count = list.Count,
            teams = list.Select(Describe).ToList(),
            start = new { where = TeamStart.Wire(start.Place), teamId = start.TeamId },
        });
    }

    /// <summary>Create a team with the caller as its Owner. 201 with the team, or 400 with the reason.</summary>
    internal static IResult CreateTeam(TeamRegistry teams, string callerSubject, CreateTeamRequest? body)
    {
        var result = teams.CreateTeam(callerSubject, body?.Name);
        if (result.Team is not { } team)
        {
            FileLog.Write("[TeamEndpoints] POST /teams: refused");
            return Results.BadRequest(new { error = result.Refusal });
        }

        FileLog.Write($"[TeamEndpoints] POST /teams: created {team.Tenant.ToLogString()}");
        return Results.Json(new { team = Describe(team) }, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>A team's members and roles, for a member of that team; 404 for anyone else.</summary>
    internal static IResult ListMembers(TeamRegistry teams, string callerSubject, string? teamId)
    {
        var result = teams.ListMembers(teamId ?? "", callerSubject);
        if (result.Outcome != TeamMembersOutcome.Found || result.Team is not { } team)
        {
            FileLog.Write("[TeamEndpoints] GET /teams/{teamId}/members: no such team for this caller");
            return Results.NotFound(new { error = NoSuchTeamRefusal });
        }

        FileLog.Write($"[TeamEndpoints] GET /teams/{{teamId}}/members: {result.Members.Count} member(s)");
        return Results.Json(new
        {
            team = Describe(team),
            count = result.Members.Count,
            members = result.Members.Select(m => new
            {
                name = MemberName(m),
                email = m.Email,
                role = TeamRoles.Label(m.Role),
                isYou = string.Equals(m.AccountSubject, callerSubject.Trim(), StringComparison.Ordinal),
                joinedAtUtc = m.JoinedAtUtc,
            }).ToList(),
        });
    }

    /// <summary>A member's name as every team screen shows it - the member list here and "changed by" on the Skills and
    /// workflows page (devthrottle_internal#2304). One rule, so the two never disagree. The Gateway holds no display name
    /// for a person today, so it is the email they signed up with.</summary>
    internal static string MemberName(TeamMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.Email ?? "An account with no email recorded";
    }

    /// <summary>
    /// The team's Fleet Map for the caller: 200 with the map, 403 with the role table's sentence for a role that has no
    /// Fleet Map (a Collaborator), 404 for a team that does not exist or that the caller is not a member of.
    /// </summary>
    internal static IResult ReadFleetMap(TeamFleetMap fleetMap, string callerSubject, string? teamId)
    {
        var result = fleetMap.Read(teamId ?? "", callerSubject);
        switch (result.Outcome)
        {
            case TeamFleetMapOutcome.Found:
                FileLog.Write("[TeamEndpoints] GET /teams/{teamId}/fleet-map: served");
                return Results.Json(result.Map);
            case TeamFleetMapOutcome.Refused:
                FileLog.Write("[TeamEndpoints] GET /teams/{teamId}/fleet-map: refused for this role");
                return Results.Json(new { error = result.Refusal, code = TeamEndpointGate.RefusalCode },
                    statusCode: StatusCodes.Status403Forbidden);
            default:
                FileLog.Write("[TeamEndpoints] GET /teams/{teamId}/fleet-map: no such team for this caller");
                return Results.NotFound(new { error = NoSuchTeamRefusal });
        }
    }

    /// <summary>One team as the caller sees it. <c>role</c> is the CALLER's role in the team, and <c>app</c> is what
    /// the caller's Cockpit is in it (<see cref="TeamApp"/>, devthrottle_internal#2306).</summary>
    private static object Describe(TeamSummary team) => new
    {
        id = team.TeamId,
        name = team.Name,
        role = TeamRoles.Label(team.Role),
        memberCount = team.MemberCount,
        people = team.MemberCount == 1 ? "1 person" : $"{team.MemberCount} people",
        app = DescribeApp(TeamApp.For(team.Role), team.TeamId),
    };

    /// <summary>The page verdict on the wire. A page's <c>countPath</c> is where the Cockpit reads the number waiting on
    /// the person for that page, in this team (null for a page with no count) - the Gateway names it; the Cockpit never
    /// builds it.</summary>
    internal static object DescribeApp(TeamAppVerdict app, string teamId) => new
    {
        full = app.FullApp,
        pages = app.Pages.Select(p => new
        {
            id = p.Id,
            label = p.Label,
            path = p.Path,
            countPath = p.CountRoute?.Replace("{teamId}", Uri.EscapeDataString(teamId), StringComparison.Ordinal),
        }).ToList(),
        landing = app.Landing,
        elsewhere = app.Elsewhere,
    };

    private static IResult Guarded(string route, Func<IResult> handle)
    {
        try
        {
            return handle();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TeamEndpoints] {route} FAILED ({ex.GetType().Name}): {ex.Message}");
            return Results.Json(new { error = "DevThrottle could not read your teams just now because of a fault. Try again shortly." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
