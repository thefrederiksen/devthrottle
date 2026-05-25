using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CcDirector.Core.Communications.Services;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
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
/// </summary>
internal static class CommQueueEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // GET /comm-queue?status=pending_review
        //   status defaults to pending_review; "all" returns every status.
        app.MapGet("/comm-queue", async (string? status) =>
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
