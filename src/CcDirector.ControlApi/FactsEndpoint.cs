using CcDirector.Core.Configuration;
using CcDirector.Core.Diagnostics;
using CcDirector.Core.Tools;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the machine-facts surface (issue #330, plan 1B):
///
///   GET /facts -> the cc-* tool inventory (names + versions) and the launcher
///                 presence/port fact, served deterministically at request time.
///
/// This is the fleet-facing inventory the Gateway pulls through its proxy leg
/// (GET /directors/{id}/facts) - the "Director emits/serves everything the hub will
/// need" half of Phase 1. Loopback-only and subject to the host's auth middleware,
/// exactly like the other routes.
/// </summary>
internal static class FactsEndpoint
{
    public static void Map(IEndpointRouteBuilder app, string directorId, string version)
    {
        var catalog = new ToolCatalogService();

        app.MapGet("/facts", () =>
        {
            FileLog.Write("[FactsEndpoint] GET /facts");
            var tools = ToolInventory.Build(catalog, AboutInfo.InstalledComponents());
            var launcher = LauncherDiscovery.Read();
            return Results.Json(new DirectorFactsDto
            {
                DirectorId = directorId,
                MachineName = Environment.MachineName,
                Version = version,
                Tools = tools.Select(t => new ToolInventoryItemDto
                {
                    Name = t.Name,
                    Category = t.Category,
                    Version = t.Version,
                    IsBuilt = t.IsBuilt,
                }).ToList(),
                Launcher = new LauncherFactDto
                {
                    Installed = launcher.Installed,
                    Port = launcher.Port,
                    Error = launcher.Error,
                },
            });
        });
    }
}
