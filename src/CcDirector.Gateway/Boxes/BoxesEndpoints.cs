using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Boxes;

/// <summary>
/// <c>GET /boxes</c> (the Factory Boxes mission, phase 1): the desktops the calling account's connected Directors host.
/// Mapped inside <see cref="BoxesGate.Group"/>, so an account Boxes is off for gets 404 as if the route did not exist.
///
/// Every field is the Gateway's ruling and the Cockpit renders it verbatim (rule 7). Phase 1 has one state, <see cref="StateOnline"/>, because a list is only held while its Director is
/// connected (<see cref="PushedHostedDesktopStore"/>); a later phase adds others here, never in a client.
/// </summary>
internal static class BoxesEndpoints
{
    /// <summary>The route.</summary>
    public const string ListRoute = "/boxes";

    /// <summary>The one state phase 1 has: the desktop's Director is connected.</summary>
    public const string StateOnline = "Online";

    /// <summary>
    /// The desktop's path RELATIVE to the desktop host the relay serves it on - not an address the Cockpit navigates to.
    /// The desktop is served on a separate desktop host, and the Cockpit opens a box through the relay leg's one-time
    /// ticket endpoint, which answers the full address to open. Carried so the relay and the list name the same path.
    /// </summary>
    public static string RelativeDesktopPath(string boxId) => $"/boxes/{boxId}/desktop/";

    /// <summary>Map <c>GET /boxes</c> into <paramref name="group"/>, which must be the Boxes gate's group.</summary>
    public static void Map(RouteGroupBuilder group, PushedHostedDesktopStore desktops, Func<HttpContext, TenantId?> resolveTenant)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(desktops);
        ArgumentNullException.ThrowIfNull(resolveTenant);

        group.MapGet(ListRoute, (HttpContext ctx) =>
        {
            // The gate already resolved the tenant to let the request through; asked again here so the list can only
            // ever be the resolved account's, never a value carried from somewhere else.
            if (resolveTenant(ctx) is not { } tenant)
                return Results.NotFound();

            var rows = desktops.ListFor(tenant).Select(d => new BoxRow(
                d.BoxId,
                d.DisplayName,
                d.DirectorId,
                d.DirectorName,
                d.MachineName,
                StateOnline,
                RelativeDesktopPath(d.BoxId))).ToList();
            FileLog.Write($"[BoxesEndpoints] GET {ListRoute}: tenant={tenant.ToLogString()} count={rows.Count}");
            return Results.Json(new BoxListResponse(rows.Count, rows));
        });
    }

    /// <summary>The <c>GET /boxes</c> body.</summary>
    internal sealed record BoxListResponse(
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("boxes")] IReadOnlyList<BoxRow> Boxes);

    /// <summary>One box in <c>GET /boxes</c>.</summary>
    internal sealed record BoxRow(
        [property: JsonPropertyName("boxId")] string BoxId,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("directorId")] string DirectorId,
        [property: JsonPropertyName("directorName")] string DirectorName,
        [property: JsonPropertyName("machineName")] string MachineName,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("relativeDesktopPath")] string RelativeDesktopPath);
}
