using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Tools;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the Tools catalog REST surface so an external agent can browse the cc-* toolset, see which
/// tools are built, run their health checks, and read the skill links - the same data the Avalonia
/// Tools page shows (both consume the Core <see cref="ToolCatalogService"/> / <see cref="ToolTestRunner"/>
/// / <see cref="SkillToolLinker"/>, so there is one source of truth):
///
///   GET  /tools                  -> the catalog (descriptors + unmanaged binaries).
///   GET  /tools/{name}           -> one tool's detail plus its skill links.
///   POST /tools/{name}/test      -> run that tool's checks, return results.
///   POST /tools/test             -> run every built tool's checks (bounded concurrency).
///   POST /tools/run              -> invoke ONE catalog tool with args, streamed NDJSON result (#328).
///
/// Loopback-only and subject to the host's auth middleware, exactly like the other routes.
/// </summary>
internal static class ToolsEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var catalog = new ToolCatalogService();
        var runner = new ToolTestRunner();
        var linker = new SkillToolLinker();

        app.MapGet("/tools", () =>
        {
            FileLog.Write("[ToolsEndpoint] GET /tools");
            var tools = catalog.GetCatalog();
            return Results.Json(new
            {
                tools = tools.Select(ToSummary),
                unmanaged = catalog.GetUnmanagedBinaries(),
            });
        });

        app.MapGet("/tools/{name}", (string name) =>
        {
            FileLog.Write($"[ToolsEndpoint] GET /tools/{name}");
            ToolDescriptor tool;
            try { tool = catalog.GetTool(name); }
            catch (InvalidOperationException) { return Results.NotFound(new { error = $"unknown tool: {name}" }); }

            var links = linker.GetLinksForTool(name);
            return Results.Json(new
            {
                tool = ToSummary(tool),
                skills = links.Select(ToSkillLink),
            });
        });

        app.MapPost("/tools/{name}/test", async (string name, CancellationToken ct) =>
        {
            FileLog.Write($"[ToolsEndpoint] POST /tools/{name}/test");
            ToolDescriptor tool;
            try { tool = catalog.GetTool(name); }
            catch (InvalidOperationException) { return Results.NotFound(new { error = $"unknown tool: {name}" }); }

            var results = await runner.RunAllForToolAsync(tool, ct);
            return Results.Json(new
            {
                name = tool.Name,
                status = RollUp(tool, results).ToString(),
                results = results.Select(ToResult),
            });
        });

        app.MapPost("/tools/test", async (CancellationToken ct) =>
        {
            FileLog.Write("[ToolsEndpoint] POST /tools/test (run all)");
            var tools = catalog.GetCatalog();
            // Capped at 2 on purpose: many tools are one-file PyInstaller exes that each re-extract to
            // temp on launch (the heaviest cold-starts in ~45s solo), so a wide fan-out thrashes the
            // disk and pushes those past their timeout. Two at a time keeps the run honest.
            using var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount - 1, 1, 2));

            var tasks = tools.Select(async tool =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var results = await runner.RunAllForToolAsync(tool, ct);
                    return new { name = tool.Name, status = RollUp(tool, results).ToString(), results = results.Select(ToResult) };
                }
                finally { gate.Release(); }
            });

            var all = await Task.WhenAll(tasks);
            return Results.Json(new
            {
                total = all.Length,
                pass = all.Count(t => t.status == nameof(ToolStatus.Pass)),
                fail = all.Count(t => t.status == nameof(ToolStatus.Fail)),
                notBuilt = all.Count(t => t.status == nameof(ToolStatus.NotBuilt)),
                tools = all,
            });
        });

        // POST /tools/run (issue #328): invoke ONE catalog tool with args where its resources live.
        // Catalog-allowlisted (the name must resolve through the embedded manifest - no arbitrary
        // paths, no shell), bounded (timeout kills the process tree), fully audited, and STREAMED:
        // the response is application/x-ndjson, one ToolRunChunk per line, flushed as produced so
        // the caller sees output before the process exits.
        var toolRunner = new ToolRunner();
        app.MapPost("/tools/run", async (HttpContext ctx) =>
        {
            ToolRunRequest? req;
            try
            {
                req = await ctx.Request.ReadFromJsonAsync<ToolRunRequest>(ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[ToolsEndpoint] POST /tools/run: invalid JSON body ({ex.Message})");
                return Results.BadRequest(new { error = "invalid JSON request body" });
            }

            if (req is null)
                return Results.BadRequest(new { error = "request body is required: { name, args?, cwd?, timeoutS? }" });
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name is required (a catalog tool name, e.g. cc-vault)" });

            // Never executed: anything that is not a bare catalog name is rejected before the
            // catalog is even consulted (no path separators, no traversal, no drive prefixes).
            if (req.Name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || req.Name.Contains(".."))
            {
                FileLog.Write($"[ToolsEndpoint] POST /tools/run REJECTED (path-shaped name): {Truncate(req.Name)}");
                return Results.BadRequest(new { error = "name must be a bare catalog tool name (no path separators)" });
            }

            var timeoutS = req.TimeoutS ?? ToolRunRequest.DefaultTimeoutS;
            if (timeoutS is < ToolRunRequest.MinTimeoutS or > ToolRunRequest.MaxTimeoutS)
                return Results.BadRequest(new { error = $"timeoutS must be {ToolRunRequest.MinTimeoutS}..{ToolRunRequest.MaxTimeoutS} (got {timeoutS})" });

            if (req.Cwd is not null && !Directory.Exists(req.Cwd))
                return Results.BadRequest(new { error = $"cwd not found: {req.Cwd}" });

            ToolDescriptor tool;
            try { tool = catalog.GetTool(req.Name); }
            catch (InvalidOperationException)
            {
                FileLog.Write($"[ToolsEndpoint] POST /tools/run REJECTED (not in catalog): {Truncate(req.Name)}");
                return Results.NotFound(new { error = $"unknown tool: {req.Name} (not in the catalog)" });
            }

            if (!tool.IsAvailable)
                return Results.Json(new { error = $"tool is not available on this Director (neither bundled nor on PATH): {tool.Name} ({tool.BinaryPath})" },
                    statusCode: StatusCodes.Status409Conflict);

            // The audit line: resolved exe path + args + caller, on every invocation (issue #328).
            FileLog.Write($"[ToolsEndpoint] POST /tools/run: tool={tool.Name}, exe={tool.BinaryPath}, " +
                          $"args=[{string.Join(" ", req.Args)}], cwd={req.Cwd ?? "(exe dir)"}, timeoutS={timeoutS}, " +
                          $"caller={ctx.Connection.RemoteIpAddress}");

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            await foreach (var chunk in toolRunner.RunStreamAsync(
                tool.BinaryPath, req.Args, req.Cwd, TimeSpan.FromSeconds(timeoutS), ctx.RequestAborted))
            {
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(chunk, NdjsonOptions) + "\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            return Results.Empty;
        });
    }

    /// <summary>Wire shape for streamed run chunks: camelCase like every other route, nulls omitted.</summary>
    private static readonly JsonSerializerOptions NdjsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Keep rejected caller-supplied names log-safe (never log unbounded input).</summary>
    private static string Truncate(string value)
        => value.Length <= 80 ? value : value[..80] + "...";

    private static object ToSummary(ToolDescriptor t) => new
    {
        name = t.Name,
        category = t.Category,
        description = t.Description,
        note = t.Note,
        binaryPath = t.BinaryPath,
        isBuilt = t.IsBuilt,
        isOnPath = t.IsOnPath,
        isAvailable = t.IsAvailable,
        tests = t.Tests.Select(test => new { kind = test.Kind.ToString(), label = test.Label, args = test.Args }),
    };

    private static object ToResult(ToolTestResult r) => new
    {
        kind = r.Kind.ToString(),
        label = r.Label,
        passed = r.Passed,
        durationMs = r.DurationMs,
        exitCode = r.ExitCode,
        message = r.Message,
        stdout = r.Stdout,
        stderr = r.Stderr,
    };

    private static object ToSkillLink(SkillToolLink l) => new
    {
        skill = l.SkillName,
        relation = l.Relation.ToString(),
        source = l.Source.ToString(),
    };

    /// <summary>Roll the individual results up into the tool's status chip value.</summary>
    private static ToolStatus RollUp(ToolDescriptor tool, IReadOnlyList<ToolTestResult> results)
    {
        if (!tool.IsAvailable) return ToolStatus.NotBuilt;
        if (results.Count == 0) return ToolStatus.Untested;
        return results.All(r => r.Passed) ? ToolStatus.Pass : ToolStatus.Fail;
    }
}
