using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The workflow catalog (issue #1617): the shapes of work this fleet knows how to run.
///
///   GET /gateway/workflows        -> { workflows: [ ... ] }
///   GET /gateway/workflows/{id}   -> { ... } | 404
///
/// The routes sit under /gateway (the same convention as /gateway/ai/*) and NOT at a bare /workflows,
/// because the Cockpit's Workflows PAGE owns the /workflows path. The Gateway serves the single-page
/// app at "/" and falls unknown page paths back to index.html, so an API mapped at /workflows would win
/// that path and a hard navigation to the page would render raw JSON instead of the Cockpit.
///
/// The Gateway is the HOME for workflows. It serves them and every Director asks it, rather than each
/// machine carrying its own private copy of how the team works. That is what makes an organisation-wide
/// rollout possible later: an administrator defines the workflows once on their Gateway and every
/// Director picks them up.
///
/// The catalog is PERSISTED (Workflows mission, phase 1): reads come from the WorkflowStore (EF data
/// layer), where the built-ins are seeded at startup and user-defined workflows live beside them.
/// The legacy read shape is frozen - fields are only ever ADDED.
///
/// Authoring (phase 2) - drafts are the safety boundary, publish is the fleet-visible act:
///
///   POST   /gateway/workflows                        body WorkflowContentRequest -> 201 detail | 400 | 409
///   PUT    /gateway/workflows/{id}/draft             body WorkflowContentRequest, optional If-Match
///                                                    (content hash) -> 200 detail | 400 | 404 | 409
///   POST   /gateway/workflows/{id}/publish           -> 200 WorkflowDto | 400 | 404
///   POST   /gateway/workflows/{id}/clone             ?newId=&by= copy published content into a new
///                                                    tenant-owned editable workflow -> 201 | 400 | 404 | 409
///   DELETE /gateway/workflows/{id}                   archive (never a built-in) -> 200 | 400 | 404
///   GET    /gateway/workflows/{id}/versions          -> { versions: [...] } | 404
///   GET    /gateway/workflows/{id}/versions/{n}      -> full content snapshot | 404
///   GET    /gateway/workflows/{id}/instructions      raw text/markdown (agent read path) | 404
///   GET    /gateway/workflows/{id}/files/{fileName}  raw text/plain | 404
///
/// Inherits the host-wide token middleware, like every other Gateway route.
/// </summary>
internal static class WorkflowEndpoints
{
    /// <summary>Where the routes live for the caller's own account.</summary>
    public const string DefaultRoot = "/gateway/workflows";

    /// <summary>
    /// Maps the routes under <paramref name="root"/>. The store answers for the AMBIENT tenant, so the same routes
    /// serve a team's library when mounted under <c>/teams/{teamId}/workflows</c> with the team's tenant entered
    /// (<see cref="TeamLibraryEndpoints"/>, devthrottle_internal#2304) - one set of handlers, never a copy.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, WorkflowStore store, string root = DefaultRoot)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        app.MapGet(root, (HttpContext ctx) =>
        {
            var workflows = store.ListPublished();
            FileLog.Write($"[WorkflowEndpoints] list workflows: count={workflows.Count}");
            // Every Director reads this once a minute and it changes only when someone edits it, so the answer is
            // tagged and an unchanged one is sent as 304 with no body (Money Saver, night traffic). See ConditionalJson.
            return ConditionalJson.Serve(ctx, new { workflows });
        });

        app.MapGet(root + "/{id}", (string id) =>
        {
            var workflow = store.GetPublished(id);
            if (workflow is null)
            {
                FileLog.Write($"[WorkflowEndpoints] get workflow: id={id}, result=not found");
                return Results.Json(new { error = $"no workflow with id '{id}'" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            FileLog.Write($"[WorkflowEndpoints] get workflow: id={id}, result=found");
            return Results.Json(workflow);
        });

        // ---- authoring (Workflows mission, phase 2) -----------------------------------------------
        // Drafts are the safety boundary; publish is the fleet-visible act. Agents may publish (owner
        // ruling, 2026-07-17) - authorship is recorded on every version and a bad publish is fixed by
        // publishing again.

        app.MapPost(root, async (HttpContext ctx) =>
        {
            var content = await ReadBody(ctx);
            if (content is null)
                return Results.BadRequest(new { error = "a workflow body is required" });
            content.AuthoredBy = ServerStampedAuthor.Resolve(ctx, content.AuthoredBy);
            return Guard(() =>
            {
                var created = store.CreateDraft(content);
                FileLog.Write($"[WorkflowEndpoints] create workflow: id={created.WorkflowId}, draft v{created.Version}");
                return Results.Json(created, statusCode: StatusCodes.Status201Created);
            });
        });

        app.MapPut(root + "/{id}/draft", async (string id, HttpContext ctx) =>
        {
            var content = await ReadBody(ctx);
            if (content is null)
                return Results.BadRequest(new { error = "a workflow body is required" });
            content.AuthoredBy = ServerStampedAuthor.Resolve(ctx, content.AuthoredBy);
            var ifMatch = ReadIfMatch(ctx);
            return Guard(() =>
            {
                var updated = store.UpdateDraft(id, content, ifMatch);
                if (updated is null)
                    return NotFound(id);
                FileLog.Write($"[WorkflowEndpoints] update draft: id={id}, v{updated.Version}");
                return Results.Json(updated);
            });
        });

        app.MapPost(root + "/{id}/publish", (string id) => Guard(() =>
        {
            var published = store.Publish(id);
            if (published is null)
                return NotFound(id);
            FileLog.Write($"[WorkflowEndpoints] publish: id={id}, v{published.Version}");
            return Results.Json(published);
        }));

        // POST /gateway/workflows/{id}/reset was RETIRED in the Shared Workflow Library phase 3:
        // built-ins are read-only, can never diverge from shipped content, and have nothing to reset.

        // Clone (Shared Workflow Library phase 4): copy a workflow's published content into a new
        // tenant-owned, fully editable workflow - the sanctioned customization path for the
        // read-only built-ins. ?newId names the clone; ?by records who cloned (the authoring actor).
        app.MapPost(root + "/{id}/clone", (string id, string? newId, string? by, HttpContext ctx) => Guard(() =>
        {
            var clone = store.Clone(id, newId ?? "", ServerStampedAuthor.Resolve(ctx, by) ?? "");
            if (clone is null)
                return NotFound(id);
            FileLog.Write($"[WorkflowEndpoints] clone: '{id}' -> '{clone.Id}' v{clone.Version}");
            return Results.Json(clone, statusCode: StatusCodes.Status201Created);
        }));

        app.MapDelete(root + "/{id}", (string id) => Guard(() =>
        {
            if (!store.Archive(id))
                return NotFound(id);
            FileLog.Write($"[WorkflowEndpoints] archive: id={id}");
            return Results.Json(new { id, archived = true });
        }));

        app.MapGet(root + "/{id}/versions", (string id) =>
        {
            var versions = store.ListVersions(id);
            return versions is null ? NotFound(id) : Results.Json(new { versions });
        });

        app.MapGet(root + "/{id}/versions/{version:int}", (string id, int version) =>
        {
            var detail = store.GetVersionDetail(id, version);
            return detail is null ? NotFound(id) : Results.Json(detail);
        });

        // The agent read path: raw markdown, no JSON envelope, so `cc-devthrottle workflow
        // instructions <id>` can print it verbatim into an agent's context.
        app.MapGet(root + "/{id}/instructions", (string id, int? version) => Guard(() =>
        {
            // Guard: a workflow the owner turned OFF refuses the default read with a clear 400
            // message (never a misleading 404); pinned explicit-version reads keep serving.
            var markdown = store.GetInstructions(id, version);
            return markdown is null ? NotFound(id) : Results.Text(markdown, "text/markdown");
        }));

        // The owner's switch (register redesign): turn a workflow on or off - built-ins included.
        // Off = hidden from agents' briefings, default reads refused, no new runs or seats; nothing
        // deleted, instant both ways fleet-wide.
        // Both verbs REQUIRE ?by=<who> - a governance change has an actor, always (the run-
        // acceptance posture). Attribution is log-based until governance's event ledger lands.
        app.MapPost(root + "/{id}/enable", (string id, string? by, HttpContext ctx) => Guard(() =>
            store.SetEnabled(id, true, ServerStampedAuthor.Resolve(ctx, by) ?? "")
                ? Results.Json(new { id, enabled = true })
                : NotFound(id)));

        app.MapPost(root + "/{id}/disable", (string id, string? by, HttpContext ctx) => Guard(() =>
            store.SetEnabled(id, false, ServerStampedAuthor.Resolve(ctx, by) ?? "")
                ? Results.Json(new { id, enabled = false })
                : NotFound(id)));

        app.MapGet(root + "/{id}/files/{fileName}", (string id, string fileName, int? version) =>
        {
            var content = store.GetFileContent(id, fileName, version);
            return content is null
                ? Results.Json(new { error = $"no file '{fileName}' on workflow '{id}'" },
                    statusCode: StatusCodes.Status404NotFound)
                : Results.Text(content, "text/plain");
        });
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static async Task<WorkflowContentRequest?> ReadBody(HttpContext ctx)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<WorkflowContentRequest>(
                ctx.Request.Body, JsonOpts, ctx.RequestAborted);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[WorkflowEndpoints] bad JSON body: {ex.Message}");
            return null;
        }
    }

    /// <summary>The If-Match header as a bare hash (clients may send it RFC-quoted).</summary>
    private static string? ReadIfMatch(HttpContext ctx)
    {
        string? raw = ctx.Request.Headers.IfMatch;
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().Trim('"');
    }

    /// <summary>Translate the store's authoring exceptions to their status codes.</summary>
    private static IResult Guard(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (WorkflowValidationException ex)
        {
            FileLog.Write($"[WorkflowEndpoints] rejected: {ex.Message}");
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (WorkflowConflictException ex)
        {
            FileLog.Write($"[WorkflowEndpoints] conflict: {ex.Message}");
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult NotFound(string id) =>
        Results.Json(new { error = $"no workflow with id '{id}'" },
            statusCode: StatusCodes.Status404NotFound);
}
