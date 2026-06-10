using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Running;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The queue-runner REST surface (issue #274, child 3 of #270). The runner itself
/// (<see cref="WorkListRunner"/>) is the orchestration; this endpoint is how a caller (a wingman,
/// the Cockpit, or a test harness) starts a drain of a named list against a target Director and
/// reads the result. It lives entirely at the Gateway - the Director host gains nothing (criterion 7).
///
///   POST /lists/{name}/run   body { directorId, repoPath, listConsumer?, machineKey? }
///       -> 200 { listName, consumer, items: [ { source, id, signal?, outcome, sessionId?, note } ], consumerReleased }
///       -> 404 no such list / no such director
///       -> 409 list already claimed (criterion 5) OR machine already draining (criterion 8)
///
/// The drain runs synchronously for the request: the runner starts one implementation session per
/// github item, watches it to its terminal sentinel (#272), then advances - so the response is the
/// completed timeline. A caller that wants a non-blocking start fires the request and reads the
/// list/consumer state separately; v1 keeps the path simple and proves the sequencing directly.
/// </summary>
internal static class WorkListRunnerEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void Map(
        IEndpointRouteBuilder app,
        WorkListStore store,
        DirectorRegistry registry,
        DirectorEndpointClient client,
        WorkListRunnerManager manager)
    {
        app.MapPost("/lists/{name}/run", async (string name, HttpContext ctx) =>
        {
            RunBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<RunBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[WorkListRunnerEndpoints] POST /lists/{name}/run bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }

            if (body is null || string.IsNullOrWhiteSpace(body.DirectorId) || string.IsNullOrWhiteSpace(body.RepoPath))
                return Results.BadRequest(new { error = "body { \"directorId\": \"...\", \"repoPath\": \"...\", \"listConsumer\"?: \"...\", \"machineKey\"?: \"...\" } is required" });

            if (store.Get(name) is null)
                return Results.NotFound(new { error = "no such list", name });

            var director = registry.Get(body.DirectorId);
            if (director is null || string.IsNullOrEmpty(director.ControlEndpoint))
                return Results.NotFound(new { error = "no such director (or it has no control endpoint)", directorId = body.DirectorId });

            // The machine guard key defaults to the target Director's machine name (criterion 8):
            // one slot-5 test Director per machine, so one drain per machine.
            var machineKey = string.IsNullOrWhiteSpace(body.MachineKey)
                ? (string.IsNullOrWhiteSpace(director.MachineName) ? body.DirectorId : director.MachineName)
                : body.MachineKey;

            // Single-machine guard FIRST (criterion 8): refuse a second concurrent drain on a machine
            // already draining a list, before we touch the list's own consumer claim.
            var admit = manager.TryAdmit(machineKey, name);
            if (admit == WorkListRunnerManager.AdmitResult.RefusedMachineBusy)
                return Results.Conflict(new
                {
                    error = "machine is already draining another list (v1 same-machine guard)",
                    machineKey,
                    activeList = manager.ActiveList(machineKey),
                });

            try
            {
                var consumer = string.IsNullOrWhiteSpace(body.ListConsumer)
                    ? $"runner:{machineKey}:{Guid.NewGuid():N}"
                    : body.ListConsumer;

                var driver = new DirectorImplSessionDriver(client, director.ControlEndpoint, body.RepoPath);
                var runner = new WorkListRunner(store, driver);

                var result = await runner.DrainAsync(name, consumer, ctx.RequestAborted);
                return Results.Json(Project(result));
            }
            catch (WorkListClaimRefusedException)
            {
                // The list itself is already being drained by another consumer (criterion 5) - rides
                // #273's single-consumer claim 409.
                return Results.Conflict(new { error = "list already has an active draining consumer", name });
            }
            finally
            {
                manager.Complete(machineKey);
            }
        });

        FileLog.Write("[WorkListRunnerEndpoints] mapped /lists/{name}/run route");
    }

    private static object Project(WorkListRunResult result) => new
    {
        listName = result.ListName,
        consumer = result.ConsumerToken,
        consumerReleased = result.ConsumerReleased,
        items = result.Items.Select(i => new
        {
            source = i.Item.Source,
            id = i.Item.Id,
            area = i.Item.Area,
            outcome = i.Outcome.ToString(),
            signal = i.Signal?.ToString(),
            sessionId = i.SessionId,
            note = i.Note,
            startedAtUtc = i.StartedAtUtc,
            finishedAtUtc = i.FinishedAtUtc,
        }).ToList(),
    };

    private sealed record RunBody(string? DirectorId, string? RepoPath, string? ListConsumer, string? MachineKey);
}
