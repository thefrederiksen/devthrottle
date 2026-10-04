using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Skills;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The central skill library (devthrottle_internal issue 995): the capabilities every agent on every
/// machine can reach for, held here and fetched, instead of copied onto each machine by the installer.
///
///   GET /gateway/skills                     -> { skills: [ ... ] }   the REGISTER LISTING
///   GET /gateway/skills/{id}                -> { ... } | 404
///   GET /gateway/skills/{id}/body           raw text/markdown (the agent read path)
///   GET /gateway/skills/{id}/files/{**path}  raw file bytes (text/plain, or octet-stream if binary)
///
/// THE LISTING IS THE FEATURE. It carries id, name, one line, triggers, version and hash - and no
/// bodies - because it is what every session's launch briefing is rendered from. The body is fetched
/// per skill, only by a session that is about to use that skill. If discovery ever costs more than the
/// listing, the change that made it so is the wrong change.
///
/// The routes sit under /gateway (the same convention as /gateway/workflows) and NOT at a bare
/// /skills: the Cockpit's Skills PAGE owns that path, and the Gateway falls unknown page paths back to
/// the single-page app, so an API mapped there would make a hard navigation render raw JSON.
///
/// Authoring - drafts are the safety boundary, publish is the fleet-visible act:
///
///   POST   /gateway/skills                  body SkillContentRequest -> 201 detail | 400 | 409
///   PUT    /gateway/skills/{id}/draft       body SkillContentRequest, optional If-Match (content
///                                           hash) -> 200 detail | 400 | 404 | 409
///   POST   /gateway/skills/{id}/publish     -> 200 SkillDto | 400 | 404
///   POST   /gateway/skills/{id}/clone       ?newId=&amp;by= copy published content into a new
///                                           tenant-owned editable skill -> 201 | 400 | 404 | 409
///   POST   /gateway/skills/{id}/enable      ?by= the owner's switch -> 200 | 400 | 404
///   POST   /gateway/skills/{id}/disable     ?by= -> 200 | 400 | 404
///   DELETE /gateway/skills/{id}             archive (never a built-in) -> 200 | 400 | 404
///   GET    /gateway/skills/{id}/versions    -> { versions: [...] } | 404
///   GET    /gateway/skills/{id}/versions/{n} -> full content snapshot | 404
///
/// Inherits the host-wide token middleware, like every other Gateway route.
/// </summary>
internal static class SkillEndpoints
{
    /// <summary>Where the routes live for the caller's own account.</summary>
    public const string DefaultRoot = "/gateway/skills";

    /// <summary>
    /// Maps the routes under <paramref name="root"/>. The store answers for the AMBIENT tenant, so the same routes
    /// serve a team's library when mounted under <c>/teams/{teamId}/skills</c> with the team's tenant entered
    /// (<see cref="TeamLibraryEndpoints"/>, devthrottle_internal#2304) - one set of handlers, never a copy.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, SkillStore store, string root = DefaultRoot)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        app.MapGet(root, (HttpContext ctx) =>
        {
            var skills = store.ListPublished();
            FileLog.Write($"[SkillEndpoints] list skills: count={skills.Count}");
            // Every Director reads this once a minute and it changes only when someone edits it, so the answer is
            // tagged and an unchanged one is sent as 304 with no body (Money Saver, night traffic). See ConditionalJson.
            return ConditionalJson.Serve(ctx, new { skills });
        });

        app.MapGet(root + "/{id}", (string id) =>
        {
            var skill = store.GetPublished(id);
            if (skill is null)
            {
                FileLog.Write($"[SkillEndpoints] get skill: id={id}, result=not found");
                return NotFound(id);
            }

            FileLog.Write($"[SkillEndpoints] get skill: id={id}, result=found");
            return Results.Json(skill);
        });

        // The agent read path: raw markdown, no JSON envelope, so `cc-devthrottle skill get <id>` can
        // print it verbatim into an agent's context. This is the ONLY route that serves a body, and it
        // is reached once per skill actually used - never as part of discovery.
        app.MapGet(root + "/{id}/body", (string id, int? version) => Guard(() =>
        {
            var body = store.GetBody(id, version);
            return body is null ? NotFound(id) : Results.Text(body, "text/markdown");
        }));

        // A CATCH-ALL segment, because a skill is a directory: the file being asked for is
        // "references/tracing.md", not a bare name, and a single-segment route parameter would simply
        // fail to match it. Binary files are served as bytes with an octet-stream content type - a
        // skill can carry an image, an archive or a compiled program, and serving those as text would
        // corrupt them on the way out.
        app.MapGet(root + "/{id}/files/{**filePath}", (string id, string filePath, int? version) =>
            Guard(() =>
            {
                var file = store.GetFile(id, filePath, version);
                return file is null
                    ? Results.Json(new { error = $"no file '{filePath}' on skill '{id}'" },
                        statusCode: StatusCodes.Status404NotFound)
                    : Results.Bytes(file.Bytes, file.ContentType);
            }));

        // ---- authoring ----------------------------------------------------------------------------
        // Any agent may author and publish. Authorship is recorded on every version and a bad publish
        // is fixed by publishing again - which reaches the whole fleet just as fast as the mistake did.

        app.MapPost(root, async (HttpContext ctx) =>
        {
            var content = await ReadBody(ctx);
            if (content is null)
                return Results.BadRequest(new { error = "a skill body is required" });
            return Guard(() =>
            {
                var created = store.CreateDraft(content);
                FileLog.Write($"[SkillEndpoints] create skill: id={created.SkillId}, draft v{created.Version}");
                return Results.Json(created, statusCode: StatusCodes.Status201Created);
            });
        });

        app.MapPut(root + "/{id}/draft", async (string id, HttpContext ctx) =>
        {
            var content = await ReadBody(ctx);
            if (content is null)
                return Results.BadRequest(new { error = "a skill body is required" });
            var ifMatch = ReadIfMatch(ctx);
            return Guard(() =>
            {
                var updated = store.UpdateDraft(id, content, ifMatch);
                if (updated is null)
                    return NotFound(id);
                FileLog.Write($"[SkillEndpoints] update draft: id={id}, v{updated.Version}");
                return Results.Json(updated);
            });
        });

        app.MapPost(root + "/{id}/publish", (string id) => Guard(() =>
        {
            var published = store.Publish(id);
            if (published is null)
                return NotFound(id);
            FileLog.Write($"[SkillEndpoints] publish: id={id}, v{published.Version}");
            return Results.Json(published);
        }));

        // Clone: the sanctioned customization path for the read-only built-ins. ?newId names the clone;
        // ?by records who cloned.
        app.MapPost(root + "/{id}/clone", (string id, string? newId, string? by) => Guard(() =>
        {
            var clone = store.Clone(id, newId ?? "", by ?? "");
            if (clone is null)
                return NotFound(id);
            FileLog.Write($"[SkillEndpoints] clone: '{id}' -> '{clone.Id}' v{clone.Version}");
            return Results.Json(clone, statusCode: StatusCodes.Status201Created);
        }));

        app.MapDelete(root + "/{id}", (string id) => Guard(() =>
        {
            if (!store.Archive(id))
                return NotFound(id);
            FileLog.Write($"[SkillEndpoints] archive: id={id}");
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

        // The owner's switch. Off = left out of every briefing and the default fetch refused; nothing
        // deleted, instant both ways fleet-wide. Both verbs REQUIRE ?by=<who>.
        app.MapPost(root + "/{id}/enable", (string id, string? by) => Guard(() =>
            store.SetEnabled(id, true, by ?? "")
                ? Results.Json(new { id, enabled = true })
                : NotFound(id)));

        app.MapPost(root + "/{id}/disable", (string id, string? by) => Guard(() =>
            store.SetEnabled(id, false, by ?? "")
                ? Results.Json(new { id, enabled = false })
                : NotFound(id)));
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static async Task<SkillContentRequest?> ReadBody(HttpContext ctx)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<SkillContentRequest>(
                ctx.Request.Body, JsonOpts, ctx.RequestAborted);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[SkillEndpoints] bad JSON body: {ex.Message}");
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
        catch (SkillValidationException ex)
        {
            FileLog.Write($"[SkillEndpoints] rejected: {ex.Message}");
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (SkillConflictException ex)
        {
            FileLog.Write($"[SkillEndpoints] conflict: {ex.Message}");
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult NotFound(string id) =>
        Results.Json(new { error = $"no skill with id '{id}'" },
            statusCode: StatusCodes.Status404NotFound);
}
