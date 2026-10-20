using System.Net;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Boxes;

/// <summary>
/// Factory Boxes (issue #4258), phase 1: how a browser reaches a box's desktop.
///
/// <para><b>On the Gateway's own host</b> there is ONE desktop route, <c>POST /boxes/{boxId}/desktop-ticket</c>, behind
/// the normal sign-in: the account must have Boxes on, and the box must be hosted by a connected Director OF THAT
/// ACCOUNT (404 otherwise - another account's box is answered exactly like one that does not exist). It answers the
/// whole address to open, on the desktop host, with a single-use ticket in it. The desktop itself is never served
/// here: <c>/boxes/{id}/desktop/...</c> on this host is 404.</para>
///
/// <para><b>On the desktop host</b> (<see cref="BoxDesktopOrigin"/>) the desktop is ALL that is served, plus
/// <c>/healthz</c> for the deploy workflows. <c>/boxes/{id}/desktop/</c> and everything under it - the page, its
/// scripts, <c>api/status</c>, <c>api/upload</c>, <c>api/files/*</c>, the <c>api/websockets</c> WebSocket - is relayed
/// to the box with the prefix stripped (the spike, docs/missions/factory-boxes-2026-10-19/phase1-spike-path-prefix.md).
/// It never uses the Gateway's sign-in: a ticket is exchanged once for a box-only cookie, and every request is checked
/// against that cookie (<see cref="BoxDesktopTickets"/>). Every other path on the desktop host is 404: no Cockpit, no
/// phone app, no account or device route, no API. That 404 says in plain words that this address serves only box
/// desktops and names the Gateway's public address, and each one is logged with its method, path and user agent, so a
/// stray Director or phone configured with this address is easy to spot.</para>
/// </summary>
internal static class BoxDesktopEndpoints
{
    /// <summary>The query parameter that carries a ticket to the desktop host.</summary>
    public const string TicketParameter = "ticket";

    /// <summary>The route prefix for one box's desktop, without the trailing slash.</summary>
    public static string PrefixFor(string boxId) => $"/boxes/{boxId}/desktop";

    /// <summary>True for <c>/boxes/{id}/desktop</c> and everything under it.</summary>
    public static bool IsDesktopPath(PathString path) => DesktopBoxIdOf(path) is not null;

    // The box id segment of a desktop path, or null when the path is not one.
    private static string? DesktopBoxIdOf(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value) || !value.StartsWith("/boxes/", StringComparison.OrdinalIgnoreCase)) return null;
        var afterBoxes = value["/boxes/".Length..];
        var slash = afterBoxes.IndexOf('/');
        if (slash <= 0) return null;
        var rest = afterBoxes[(slash + 1)..];
        return rest.Equals("desktop", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("desktop/", StringComparison.OrdinalIgnoreCase)
            ? afterBoxes[..slash]
            : null;
    }

    // ------------------------------------------------------------------------------ the main host ----

    /// <summary>
    /// Map the ticket route on the Gateway's own host. Pass the Boxes gate's group (<see cref="BoxesGate.Group"/>), so an
    /// account Boxes is off for meets the same 404 as every other Boxes route.
    /// </summary>
    /// <param name="locateDirector">Which connected Director of the account hosts the box, or null
    /// (<c>PushedHostedDesktopStore.TryLocate</c>).</param>
    /// <param name="boxesOn">Whether Boxes is on for the account (<c>BoxesSwitch.IsOn</c>).</param>
    public static void MapTicket(IEndpointRouteBuilder app, Uri? desktopOrigin, Func<TenantId, string, string?> locateDirector, Func<TenantId, bool> boxesOn,
        BoxDesktopTickets tickets, Tenancy.HostedTenantBoundary tenantBoundary)
    {
        ArgumentNullException.ThrowIfNull(locateDirector);
        ArgumentNullException.ThrowIfNull(boxesOn);
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(tenantBoundary);

        app.MapPost("/boxes/{boxId}/desktop-ticket", async (string boxId, HttpContext ctx) =>
        {
            FileLog.Write($"[BoxDesktopEndpoints] desktop-ticket: box={boxId}");
            if (desktopOrigin is not null && BoxDesktopOrigin.IsDesktopHost(desktopOrigin, ctx.Request))
            {
                // The desktop host never mints: a box's page could otherwise ask for tickets to other boxes.
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (tenant is not { } t)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = "no tenant is bound to this request" });
                return;
            }
            if (!boxesOn(t))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (!BoxIdRule.IsValid(boxId))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { error = "That is not a box id." });
                return;
            }
            if (desktopOrigin is null)
            {
                FileLog.Write($"[BoxDesktopEndpoints] desktop-ticket: box={boxId}: no desktop host is set ({BoxDesktopOrigin.EnvVar}) -> 503");
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsJsonAsync(new { error = "This Gateway has no desktop host set, so it cannot open a box's desktop." });
                return;
            }
            if (locateDirector(t, boxId) is null)
            {
                FileLog.Write($"[BoxDesktopEndpoints] desktop-ticket: box={boxId}: no connected Director in tenant {t.ToLogString()} hosts it -> 404");
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync(new { error = "No connected Director in your account hosts a desktop for that box." });
                return;
            }
            var (ticket, expiresAt) = tickets.Mint(t, boxId);
            ctx.Response.Headers.CacheControl = "no-store";
            await ctx.Response.WriteAsJsonAsync(new { url = BoxDesktopOrigin.OpenAddress(desktopOrigin, boxId, ticket), expiresAtUtc = expiresAt.UtcDateTime });
        });
    }

    // ---------------------------------------------------------------------------- the desktop host ----

    /// <summary>
    /// Put the desktop host in the pipeline. The Boxes gate cannot guard it - that gate reads the account from the
    /// Gateway's sign-in, which never reaches the desktop host - so the same switch is asked here, of the account the
    /// box session belongs to. Call it BEFORE the Gateway's sign-in middleware: a request on the desktop
    /// host never meets the Gateway's sign-in, because it never carries the Gateway's credentials. With no desktop
    /// host set, the desktop route is refused on every host and nothing else changes.
    /// </summary>
    /// <param name="tenantBoundary">Enters the box session's account as the tenant scope before anything is relayed. The
    /// desktop host runs before the sign-in and the tenant-scope middleware, and on a hosted Gateway a command sent
    /// with no scope is dropped, so without this every desktop request there would answer 503.</param>
    /// <param name="gatewayAddress">The Gateway's public address (<see cref="GatewayPublicUrl.ResolveBase"/>), named in the
    /// refusal of every other route on the desktop host. Null when it cannot be named.</param>
    public static void UseDesktopHost(IApplicationBuilder app, Uri? desktopOrigin, Func<TenantId, string, string?> locateDirector, Func<TenantId, bool> boxesOn,
        BoxDesktopTickets tickets, DesktopRelayLegs legs, Tenancy.HostedTenantBoundary tenantBoundary, string? gatewayAddress)
    {
        ArgumentNullException.ThrowIfNull(tenantBoundary);
        ArgumentNullException.ThrowIfNull(locateDirector);
        ArgumentNullException.ThrowIfNull(boxesOn);
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(legs);

        if (desktopOrigin is not null)
        {
            // /healthz stays with the Gateway on every host: the deploy and rollback workflows read the live commit
            // from it on the Azure name, which is the production desktop host.
            app.UseWhen(ctx => BoxDesktopOrigin.IsDesktopHost(desktopOrigin, ctx.Request) && !IsHealthCheck(ctx.Request), desktop =>
            {
                desktop.UseWebSockets();
                desktop.Run(ctx => ServeDesktopHostAsync(ctx, locateDirector, boxesOn, tickets, legs, tenantBoundary, gatewayAddress));
            });
        }

        // Anywhere else, the desktop route does not exist.
        app.Use(async (ctx, next) =>
        {
            if (IsDesktopPath(ctx.Request.Path))
            {
                FileLog.Write($"[BoxDesktopEndpoints] desktop path on {ctx.Request.Host} (not the desktop host) -> 404");
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
    }

    private static async Task ServeDesktopHostAsync(HttpContext ctx, Func<TenantId, string, string?> locateDirector, Func<TenantId, bool> boxesOn,
        BoxDesktopTickets tickets, DesktopRelayLegs legs, Tenancy.HostedTenantBoundary tenantBoundary, string? gatewayAddress)
    {
        var path = ctx.Request.Path;
        var boxId = DesktopBoxIdOf(path);
        if (boxId is null)
        {
            await RefuseOtherRouteAsync(ctx, gatewayAddress);
            return;
        }
        if (!BoxIdRule.IsValid(boxId))
        {
            FileLog.Write($"[BoxDesktopEndpoints] desktop host: box={BoxIdRule.ForLog(boxId)}: not a box id -> 400");
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "That is not a box id." });
            return;
        }

        var prefix = PrefixFor(boxId);
        if (!path.StartsWithSegments(prefix, StringComparison.Ordinal, out var remaining))
        {
            await RefuseOtherRouteAsync(ctx, gatewayAddress);
            return;
        }
        if (!remaining.HasValue)
        {
            // The page builds its addresses from its own folder, so it must be served at the slash form. The target is
            // a path on this host - never the box's own address.
            ctx.Response.Redirect(ctx.Request.PathBase + prefix + "/" + ctx.Request.QueryString, permanent: false);
            return;
        }

        if (ctx.Request.Query.ContainsKey(TicketParameter))
        {
            await RedeemTicketAsync(ctx, boxId, prefix, tickets);
            return;
        }

        var session = tickets.Authenticate(CookieValues(ctx), boxId);
        if (session is null)
        {
            FileLog.Write($"[BoxDesktopEndpoints] box={boxId}: no desktop session for this box on the request -> 401");
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Open this desktop from the Cockpit." });
            return;
        }

        // From here on, everything runs as the box session's account: the sign-in and the tenant-scope middleware never
        // ran for this request, and a hosted Gateway sends no command to a Director without a scope.
        using var scope = tenantBoundary.EnterScope(session.Tenant);
        if (!boxesOn(session.Tenant))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var directorId = locateDirector(session.Tenant, boxId);
        if (directorId is null)
        {
            FileLog.Write($"[BoxDesktopEndpoints] box={boxId}: no connected Director in tenant {session.Tenant.ToLogString()} hosts it -> 404");
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = "No connected Director in your account hosts a desktop for that box." });
            return;
        }

        await legs.ServeAsync(ctx, session.Tenant, directorId, boxId, remaining.ToUriComponent() + ctx.Request.QueryString.Value);
    }

    /// <summary>
    /// The desktop host serves the desktop and nothing else. Anything else is 404 with a plain sentence naming where the
    /// Gateway is, and a log line with the method, path and user agent: after the desktop host is set in production, a
    /// Director or phone still configured with this address shows up here and nowhere else.
    /// </summary>
    private static async Task RefuseOtherRouteAsync(HttpContext ctx, string? gatewayAddress)
    {
        var userAgent = ForLog(ctx.Request.Headers.UserAgent.ToString());
        FileLog.Write($"[BoxDesktopEndpoints] desktop host REFUSED {ForLog(ctx.Request.Method)} {PathForLog(ctx.Request.Path)} " +
                      $"user-agent=\"{userAgent}\" -> 404 (this address serves only box desktops)");
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.WriteAsync(RefusalText(gatewayAddress));
    }

    /// <summary>The body of the desktop host's refusal of every route that is not a box desktop.</summary>
    public static string RefusalText(string? gatewayAddress) =>
        "This address serves only box desktops. " + (string.IsNullOrWhiteSpace(gatewayAddress)
            ? "The DevThrottle Gateway is at its configured public address; use that address instead."
            : $"The DevThrottle Gateway is at {gatewayAddress}; use that address instead.");

    // The refused path for the log. A path under /boxes/ carries a box id the caller chose, so only the cleaned box id
    // is written, never the path as received.
    private static string PathForLog(PathString path)
    {
        var value = path.Value ?? "";
        if (!value.StartsWith("/boxes/", StringComparison.OrdinalIgnoreCase)) return ForLog(value);
        var afterBoxes = value["/boxes/".Length..];
        var slash = afterBoxes.IndexOf('/');
        return $"/boxes/ (box={BoxIdRule.ForLog(slash < 0 ? afterBoxes : afterBoxes[..slash])})";
    }

    // A request's own text, made safe for one log line: control characters out, and no longer than a line should be.
    private static string ForLog(string value)
    {
        var clean = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return clean.Length <= 300 ? clean : clean[..300] + "...";
    }

    /// <summary>
    /// Exchange a ticket for the box's desktop cookie, then move on to the address without the ticket. The move is a
    /// tiny page on THIS site rather than a redirect: a SameSite=Strict cookie is not sent on any request of a
    /// redirect chain that began on another site (the Cockpit), but it is sent on a navigation this site starts.
    ///
    /// The exchange also sends <c>Clear-Site-Data: "storage"</c>. The production desktop host is the Gateway's Azure
    /// name, and anyone who ever opened the Cockpit there has device keys in that origin's storage, which a box's page
    /// would otherwise be able to read. The storage is wiped before any box page loads. Not "cookies": that would wipe
    /// the box cookie set on this same response.
    /// </summary>
    private static async Task RedeemTicketAsync(HttpContext ctx, string boxId, string prefix, BoxDesktopTickets tickets)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        var session = tickets.Redeem(ctx.Request.Query[TicketParameter].ToString(), boxId);
        if (session is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync("This desktop link has expired or was already used. Open the box again from the Cockpit.");
            return;
        }

        ctx.Response.Cookies.Append(BoxDesktopTickets.CookieName, session.Token, new CookieOptions
        {
            Path = prefix + "/",
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = session.ExpiresAt,
            IsEssential = true,
        });

        var rest = ctx.Request.Query.Where(q => q.Key != TicketParameter)
            .SelectMany(q => q.Value.Select(v => Uri.EscapeDataString(q.Key) + "=" + Uri.EscapeDataString(v ?? "")))
            .ToList();
        var target = prefix + "/" + (rest.Count > 0 ? "?" + string.Join("&", rest) : "");
        var encoded = WebUtility.HtmlEncode(target);
        ctx.Response.Headers["Clear-Site-Data"] = "\"storage\"";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'";
        await ctx.Response.WriteAsync(
            $"<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0; url={encoded}\">" +
            $"<title>Opening the desktop</title></head><body><a href=\"{encoded}\">Open the desktop</a></body></html>");
    }

    private static bool IsHealthCheck(HttpRequest request) =>
        request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
        && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method));

    // Every value of the desktop cookie the browser sent: one per box path it holds, so the right one is found.
    private static IEnumerable<string> CookieValues(HttpContext ctx)
    {
        foreach (var header in ctx.Request.Headers.Cookie)
        {
            if (string.IsNullOrEmpty(header)) continue;
            foreach (var pair in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && string.Equals(pair[..eq], BoxDesktopTickets.CookieName, StringComparison.Ordinal))
                    yield return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
    }
}
