using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CcDirector.Core.Communications.Services;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Read-only view of the local Communication Manager approval queue, served by the
/// Gateway so a remote client (the phone) can see pending drafts over the tailnet.
///
/// This is step 1 of centralizing the comm queue on the Gateway (cc-director issue
/// #139): READ ONLY. No writes, no migration. Approve/reject, client repointing, and
/// moving the live data come in later steps. The underlying store is the existing
/// SQLite at config/comm-queue/communications.db; SQLite tolerates concurrent readers,
/// so the desktop Comm Manager keeps working while this serves reads.
///
/// DENIED IN WHOLE ON HOSTED (CR-6). The store behind this surface is ONE process-global
/// SQLite file at the shared storage root. It carries no tenant anywhere: not in the file,
/// not in the store, not in the route. The route sits behind only the host-wide
/// authentication gate, and that gate admits ANY enrolled device key from ANY account - so
/// on shared hosted infrastructure every subscriber could read the operator's outbound
/// communications queue: draft emails, posts, recipients, personas. The Communication
/// Manager app this surface backed was retired 2026-07-06 and the comm queue is not part
/// of the hosted launch, so there is no per-tenant answer to serve and the whole family is
/// refused rather than partitioned.
///
/// The deny is expressed through <see cref="HostedRouteDeny.ExclusiveGroup"/>, the ONE
/// hosted-refusal boundary every deny family on this Gateway adopts (the same primitive
/// /vault/keys and /shutdown use). On hosted the handler is NEVER MAPPED: the exclusive
/// prefix maps one verb-less catch-all refusal over everything under /comm-queue plus a
/// root refusal at the prefix itself, so every request shape - a valid request, a wrong
/// verb, a malformed body, and a route added under the prefix LATER - meets the refusal.
/// The exclusivity claim is CHECKED at startup by HostedRefusalRouteSpace. Off hosted the
/// real handler maps byte-identically to before, which is the self-host control.
/// </summary>
internal static class CommQueueEndpoints
{
    /// <summary>The prefix this family owns outright; the exclusive catch-all claims everything under it.</summary>
    internal const string Prefix = "/comm-queue";

    /// <summary>The exact refusal string served on hosted, shared with the tests so they assert what is served.</summary>
    internal const string RefusalMessage = "the comm queue is not available on the hosted Gateway";

    /// <summary>
    /// This family's refusal payload. Validated on construction, so a malformed payload fails the
    /// Gateway at startup rather than serving a refusal a caller cannot act on. 404 rather than 403:
    /// on hosted there is no per-tenant comm queue, so this surface does not exist as a concept and
    /// "not here" is the truthful answer; 403 would imply some credential could reach it, and none can.
    /// </summary>
    private static HostedDenial Denial() => new(
        family: "comm-queue",
        message: RefusalMessage,
        reason: "the comm queue is one process-global SQLite store with no tenant in the file, the store or " +
                "the route, and the host-wide auth gate admits any enrolled device key from any account - so " +
                "one subscriber could read the operator's outbound communications drafts, recipients and " +
                "personas; the Communication Manager app it backed was retired 2026-07-06 and the queue is " +
                "not part of the hosted launch",
        unDenyInstruction: "do NOT simply remove this deny: the queue store is still process-global and data " +
                "may have kept accumulating behind the refusal. If the comm queue is ever revived for hosted, " +
                "first migrate it to a tenant-scoped store the way every sibling concept (work lists, " +
                "workflows, cron, mission notes) already is, then purge or attribute whatever accumulated in " +
                "the global SQLite file, and only then restore a tenant-scoped route",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Maps the comm-queue route and RETURNS the denied group it was mapped through.
    ///
    /// The route is mapped through the group HANDLE (<see cref="HostedDenyGroup"/>), never through the
    /// ungrouped builder: the handle is obtainable only from <see cref="HostedRouteDeny.ExclusiveGroup"/>,
    /// and <see cref="MapRoutes"/> takes only the handle, so a route mapped around the refusal is not
    /// expressible there without changing this method's plumbing. The return value exists so the
    /// future-route property is statable from outside this file: a test can map a brand-new route through
    /// the returned handle and show the refusal already covers routes nobody has written yet.
    /// </summary>
    public static HostedDenyGroup Map(IEndpointRouteBuilder outer, bool hosted)
    {
        FileLog.Write($"[CommQueueEndpoints] mapping {Prefix}; hosted={hosted} - on hosted the whole group is refused via the shared refusal primitive");

        var group = HostedRouteDeny.ExclusiveGroup(outer, Prefix, Denial(), hosted);
        MapRoutes(group);
        return group;
    }

    /// <summary>
    /// The comm-queue read route, mapped relative to the <see cref="Prefix"/> so the full path is
    /// <c>/comm-queue</c> exactly as before. Takes the denied GROUP HANDLE and nothing else: the
    /// ungrouped route builder is deliberately out of scope here so no route can be mapped around the
    /// hosted refusal.
    /// </summary>
    private static void MapRoutes(HostedDenyGroup app)
    {
        // GET /comm-queue?status=pending_review
        //   status defaults to pending_review; "all" returns every status.
        app.MapGet("", async (string? status) =>
        {
            var filter = string.IsNullOrWhiteSpace(status) ? "pending_review" : status.Trim();
            FileLog.Write($"[CommQueueEndpoints] GET /comm-queue: status={filter}");
            try
            {
                var dbPath = CcStorage.CommQueueDb();
                if (!File.Exists(dbPath))
                {
                    FileLog.Write("[CommQueueEndpoints] GET /comm-queue: no comm-queue DB on this machine, returning empty");
                    return Results.Json(new
                    {
                        status = filter,
                        count = 0,
                        stats = new Dictionary<string, int>(),
                        items = Array.Empty<object>(),
                    });
                }

                var contentPath = CcStorage.ToolConfig("comm-queue");
                using var db = new DatabaseService(contentPath);
                // Idempotent on an existing DB (CREATE/INDEX IF NOT EXISTS, no ALTER when
                // columns already exist); also ensures the temp media dir exists so media
                // metadata loads without error. It never deletes or rewrites queue rows.
                await db.InitializeAsync();

                var items = filter.Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? await db.LoadAllItemsAsync()
                    : await db.LoadItemsByStatusAsync(filter);

                var stats = await db.GetStatsAsync();

                // Slim projection: enough to render the queue on a phone, no media bytes.
                var projected = items.Select(i => new
                {
                    ticketNumber = i.TicketNumber,
                    id = i.Id,
                    platform = i.Platform,
                    type = i.Type,
                    persona = i.Persona,
                    personaDisplay = i.PersonaDisplay,
                    status = i.Status,
                    createdAt = i.CreatedAt,
                    title = i.DisplayTitle,
                    preview = i.PreviewContent,
                    sendTiming = i.SendTiming,
                    sendFrom = i.SendFromDisplay,
                    recipient = i.RecipientDisplay,
                    hasMedia = i.HasMedia,
                    mediaCount = i.MediaCount,
                }).ToList();

                FileLog.Write($"[CommQueueEndpoints] GET /comm-queue OK: status={filter}, count={projected.Count}");
                return Results.Json(new
                {
                    status = filter,
                    count = projected.Count,
                    stats,
                    items = projected,
                });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[CommQueueEndpoints] GET /comm-queue FAILED: {ex.Message}");
                return Results.Json(new { error = "failed to read comm queue" }, statusCode: 500);
            }
        });
    }
}
