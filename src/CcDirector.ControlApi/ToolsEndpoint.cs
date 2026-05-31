using CcDirector.Core.Tools;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
    }

    private static object ToSummary(ToolDescriptor t) => new
    {
        name = t.Name,
        category = t.Category,
        description = t.Description,
        note = t.Note,
        binaryPath = t.BinaryPath,
        isBuilt = t.IsBuilt,
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
        if (!tool.IsBuilt) return ToolStatus.NotBuilt;
        if (results.Count == 0) return ToolStatus.Untested;
        return results.All(r => r.Passed) ? ToolStatus.Pass : ToolStatus.Fail;
    }
}
