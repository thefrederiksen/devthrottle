using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Workspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The workspace surface (issue #2722): a named set of seats, authored by hand or captured from a running
/// Director, stored on the Gateway so it is readable when the machine it describes is down.
///
///   GET    /gateway/workspaces        -> { workspaces: [ summary, ... ] }
///   GET    /gateway/workspaces/{id}   -> the whole document | 404
///   POST   /gateway/workspaces        -> CAPTURE: fold a Director's live sessions into a new one
///                                       | 400 | 404 | 409
///   PUT    /gateway/workspaces/{id}   -> store (create or replace) a document | 400
///   DELETE /gateway/workspaces/{id}   -> 200 | 404
///
/// The routes sit under /gateway for the same reason the workflow routes do: the Gateway serves the
/// single-page app at "/" and falls unknown page paths back to index.html, so an API at a bare
/// /workspaces would win a path a page may later want.
///
/// WHY CAPTURE IS A SEPARATE VERB rather than the caller assembling a document and PUTting it: the facts
/// on a seat - the agent, the resolved role, the controller, the model, the transcript path - are ones
/// the Gateway already holds firsthand. A caller that assembles them is a caller that can get them wrong,
/// and this document is read after the sessions are gone, when nobody can check. So the capture reads
/// them, and the caller supplies only what the Gateway cannot know: what to call it and why it is being
/// taken. The store enforces the other half of that: a PUT can never mint or rewrite a capture header.
///
/// The capture CREATES and never replaces. Overwriting an existing workspace would destroy a drain that
/// somebody is halfway through, and it would do it silently.
///
/// AND IT REFUSES A DIRECTOR IT CANNOT SEE. The registry knows a Director for a while after it stops
/// talking, and the live roster comes from the push stream - so a Director that is registered but not
/// stream-connected folds to ZERO seats and would be recorded as an empty fleet. Empty is exactly what a
/// finished drain looks like, so that record would say "nothing was running" about a machine nobody could
/// reach. A capture whose emptiness cannot be distinguished from unreachability is refused.
///
/// AUTH: these are device-authed client routes carrying no per-route auth of their own. They sit under
/// the "/gateway/..." prefix, so the host-wide device-key middleware gates them exactly like every other
/// client data endpoint. An agent's SESSION key reaches them too - a session drives a drain, so a session
/// must be able to write the record of one - which is a route registration in SessionKeyGuard, not an
/// absence of one.
/// </summary>
internal static class WorkspaceEndpoints
{
    /// <summary>
    /// Map the workspace routes.
    /// </summary>
    /// <param name="app">The route builder.</param>
    /// <param name="store">The persisted workspace store.</param>
    /// <param name="connectedFleet">Whether one Director is stream-connected AND what it is running, as
    /// ONE atomic read. A delegate rather than the store itself, so the tenant resolution stays in the one
    /// place that owns it - and one delegate rather than two, so the two facts cannot come from either
    /// side of a disconnection.</param>
    /// <param name="lookupDirector">Resolve a Director's display name and version, for the capture header.
    /// Returns null when the Gateway does not know that Director.</param>
    public static void Map(
        IEndpointRouteBuilder app,
        WorkspaceStore store,
        Func<string, (bool Connected, IReadOnlyList<SessionDto> Sessions)> connectedFleet,
        Func<string, DirectorDto?> lookupDirector)
    {
        app.MapGet("/gateway/workspaces", () =>
        {
            var workspaces = store.List();
            FileLog.Write($"[WorkspaceEndpoints] list: count={workspaces.Count}");
            return Results.Json(new { workspaces });
        });

        app.MapGet("/gateway/workspaces/{id}", (string id) =>
        {
            var doc = store.Get(id);
            if (doc is null)
            {
                FileLog.Write($"[WorkspaceEndpoints] get: id={id}, result=not found");
                return Results.Json(new { error = $"no workspace with id '{id}'" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            FileLog.Write($"[WorkspaceEndpoints] get: id={id}, seats={doc.Seats.Count}");
            return Results.Json(doc, WorkspaceStore.DocumentJsonOptions);
        });

        // Create or replace. This is the write path for BOTH halves of the object's life: authoring a
        // workspace by hand, and writing the drain's judgments - the handover paths, the drain states, the
        // restore decisions, and afterwards what the restart actually produced - back onto a captured one.
        // The id in the route wins over the id in the body, so a body copied from another workspace cannot
        // quietly overwrite that one.
        app.MapPut("/gateway/workspaces/{id}", async (string id, HttpContext ctx) =>
        {
            WorkspaceDocument? doc;
            try
            {
                doc = await JsonSerializer.DeserializeAsync<WorkspaceDocument>(
                    ctx.Request.Body, WorkspaceStore.DocumentJsonOptions);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = $"the workspace body could not be read: {ex.Message}" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (doc is null)
                return Results.Json(new { error = "a workspace body is required" },
                    statusCode: StatusCodes.Status400BadRequest);

            doc.Id = id;

            try
            {
                var saved = store.Save(doc, DateTime.UtcNow);
                FileLog.Write($"[WorkspaceEndpoints] PUT: id={id}, seats={saved.Seats.Count}");
                return Results.Json(saved, WorkspaceStore.DocumentJsonOptions);
            }
            catch (WorkspaceValidationException ex)
            {
                FileLog.Write($"[WorkspaceEndpoints] PUT REFUSED: id={id}: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // CAPTURE. POST creates a workspace by folding a Director's live sessions - the only way a
        // workspace is created from something that is running, and the reason there is no POST that takes a
        // hand-assembled document (PUT is that path, and it names its own id).
        app.MapPost("/gateway/workspaces", (WorkspaceCaptureRequest? req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.DirectorId))
                return Results.Json(new { error = "directorId is required - a capture names the Director it folds" },
                    statusCode: StatusCodes.Status400BadRequest);

            var director = lookupDirector(req.DirectorId);
            if (director is null)
            {
                FileLog.Write($"[WorkspaceEndpoints] capture: directorId={req.DirectorId}, result=unknown Director");
                return Results.Json(
                    new { error = $"no Director with id '{req.DirectorId}' - this Gateway has never seen it, " +
                                  "or it belongs to another account" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            // ONE read for both facts. Asking "is it connected?" and then "what is it running?" as two
            // calls leaves a gap a disconnection fits through, and what comes out of that gap is a
            // capture recording an EMPTY fleet - the exact thing this refusal exists to prevent.
            var (connected, sessions) = connectedFleet(req.DirectorId);
            if (!connected)
            {
                FileLog.Write(
                    $"[WorkspaceEndpoints] capture REFUSED: directorId={req.DirectorId} is not stream connected");
                return Results.Json(
                    new
                    {
                        error =
                            $"Director '{req.DirectorId}' ({director.DisplayName} on {director.MachineName}) is " +
                            "registered but not connected to this Gateway, so its live sessions cannot be read. " +
                            "Capturing it now would record an EMPTY fleet, which is indistinguishable from a " +
                            "Director that had genuinely finished. Get it connected, then capture.",
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }

            var doc = WorkspaceCapture.Capture(
                req, sessions, director.DisplayName, director.Version, director.MachineName, DateTime.UtcNow);

            try
            {
                var saved = store.Create(doc, DateTime.UtcNow);
                FileLog.Write(
                    $"[WorkspaceEndpoints] capture: id={saved.Id}, directorId={req.DirectorId}, " +
                    $"seats={saved.Seats.Count}");
                return Results.Json(saved, WorkspaceStore.DocumentJsonOptions,
                    statusCode: StatusCodes.Status201Created);
            }
            catch (WorkspaceValidationException ex)
            {
                FileLog.Write($"[WorkspaceEndpoints] capture REFUSED: id={req.Id}: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (WorkspaceConflictException ex)
            {
                FileLog.Write($"[WorkspaceEndpoints] capture CONFLICT: id={req.Id}: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapDelete("/gateway/workspaces/{id}", (string id) =>
        {
            var deleted = store.Delete(id);
            FileLog.Write($"[WorkspaceEndpoints] DELETE: id={id}, deleted={deleted}");
            return deleted
                ? Results.Json(new { deleted = true, id })
                : Results.Json(new { error = $"no workspace with id '{id}'" },
                    statusCode: StatusCodes.Status404NotFound);
        });
    }
}
