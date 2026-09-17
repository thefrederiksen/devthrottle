using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Fleet Manager setting (the Fleet Manager mission, step 5):
///
///   GET  /gateway/fleet-manager/placement   the folded answer the Settings tab renders
///   PUT  /gateway/fleet-manager/placement   { "agent", "machine" } - save where it runs
///   POST /gateway/fleet-manager/start       start it where the setting says
///   POST /gateway/fleet-manager/restart     start a new one there, then close the old one after its turn
///   POST /gateway/fleet-manager/move        { "agent", "machine" } - save, then restart (or start) there
///
/// Every success answers the refreshed placement. A refusal answers { "error": "&lt;sentence&gt;" } with 400 (a bad
/// request), 409 (the state does not allow it) or 502 (the computer did not start it).
///
/// THE OWNER'S ROUTES. The four writes change what runs on a computer, so <see cref="SessionKeyGuard"/> refuses a
/// session key on all of them - the Fleet Manager cannot move or restart itself - and the read is refused too. A
/// person's device reaches them through the host-wide middleware like every other /gateway route.
/// </summary>
internal static class FleetManagerPlacementEndpoints
{
    public const string PlacementRoute = FleetManagerEndpoints.Prefix + "/placement";
    public const string StartRoute = FleetManagerEndpoints.Prefix + "/start";
    public const string RestartRoute = FleetManagerEndpoints.Prefix + "/restart";
    public const string MoveRoute = FleetManagerEndpoints.Prefix + "/move";

    private static readonly JsonSerializerOptions BodyJsonOptions = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant, FleetManagerPlacementService service)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(service);

        app.MapGet(PlacementRoute, (Func<HttpContext, Task<IResult>>)(ctx => ReadAsync(ctx, resolveTenant, service)));
        app.MapPut(PlacementRoute, (Func<HttpContext, Task<IResult>>)(ctx => SaveAsync(ctx, resolveTenant, service)));
        app.MapPost(StartRoute, (Func<HttpContext, Task<IResult>>)(ctx => StartAsync(ctx, resolveTenant, service)));
        app.MapPost(RestartRoute, (Func<HttpContext, Task<IResult>>)(ctx => RestartAsync(ctx, resolveTenant, service)));
        app.MapPost(MoveRoute, (Func<HttpContext, Task<IResult>>)(ctx => MoveAsync(ctx, resolveTenant, service)));

        FileLog.Write($"[FleetManagerPlacementEndpoints] mapped {PlacementRoute}, {StartRoute}, {RestartRoute} and {MoveRoute}");
    }

    internal static async Task<IResult> ReadAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerPlacementService service)
    {
        FileLog.Write("[FleetManagerPlacementEndpoints] GET placement");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var dto = await service.ReadAsync(tenant, ctx.RequestAborted);
            FileLog.Write($"[FleetManagerPlacementEndpoints] GET placement: status={dto.Status.State}");
            return Results.Json(dto);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[FleetManagerPlacementEndpoints] GET placement FAILED: {ex.Message}");
            throw;
        }
    }

    internal static async Task<IResult> SaveAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerPlacementService service)
    {
        FileLog.Write("[FleetManagerPlacementEndpoints] PUT placement");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var (body, error) = await ReadBodyAsync(ctx);
            if (error is not null) return error;
            return Answer("PUT placement", await service.SaveAsync(tenant, body, ctx.RequestAborted));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[FleetManagerPlacementEndpoints] PUT placement FAILED: {ex.Message}");
            throw;
        }
    }

    internal static async Task<IResult> StartAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerPlacementService service)
    {
        FileLog.Write("[FleetManagerPlacementEndpoints] POST start");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            if (SessionCaller(ctx) is { } refused) return refused;
            return Answer("POST start", await service.StartAsync(tenant, StampOrigin(ctx, StartRoute), ctx.RequestAborted));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[FleetManagerPlacementEndpoints] POST start FAILED: {ex.Message}");
            throw;
        }
    }

    internal static async Task<IResult> RestartAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerPlacementService service)
    {
        FileLog.Write("[FleetManagerPlacementEndpoints] POST restart");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            if (SessionCaller(ctx) is { } refused) return refused;
            return Answer("POST restart", await service.RestartAsync(tenant, StampOrigin(ctx, RestartRoute), ctx.RequestAborted));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[FleetManagerPlacementEndpoints] POST restart FAILED: {ex.Message}");
            throw;
        }
    }

    internal static async Task<IResult> MoveAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerPlacementService service)
    {
        FileLog.Write("[FleetManagerPlacementEndpoints] POST move");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            if (SessionCaller(ctx) is { } refused) return refused;
            var (body, error) = await ReadBodyAsync(ctx);
            if (error is not null) return error;
            return Answer("POST move", await service.MoveAsync(tenant, body, StampOrigin(ctx, MoveRoute), ctx.RequestAborted));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[FleetManagerPlacementEndpoints] POST move FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// The origin stamp both spawn doors apply, applied here too: a person's verified device overwrites what the
    /// request says. The session-key arm of <see cref="SpawnOrigin"/> is never reached - these routes refuse a
    /// session key before this runs, and the guard refuses it before that.
    /// </summary>
    private static Action<NewSessionRequest> StampOrigin(HttpContext ctx, string route)
        => req =>
        {
            if (!SpawnOrigin.TryEstablish(req, ctx, route, out _))
                throw new InvalidOperationException($"{route}: the spawn origin was refused for a request that is not a session's");
        };

    /// <summary>A second line behind <see cref="SessionKeyGuard"/>: a session key never reaches a start.</summary>
    private static IResult? SessionCaller(HttpContext ctx)
    {
        if (AuthMiddleware.CallingSession(ctx) is null) return null;
        FileLog.Write("[FleetManagerPlacementEndpoints] REFUSED: a session key asked to start, restart or move the Fleet Manager");
        return Results.Json(new { error = "Only the owner can start, restart or move the Fleet Manager." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static IResult Answer(string what, FleetManagerPlacementResult result)
    {
        FileLog.Write($"[FleetManagerPlacementEndpoints] {what}: status={result.Status}");
        return result.Placement is not null
            ? Results.Json(result.Placement, statusCode: result.Status)
            : Results.Json(new { error = result.Error }, statusCode: result.Status);
    }

    private static async Task<(FleetManagerPlacementRequest? Body, IResult? Error)> ReadBodyAsync(HttpContext ctx)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<FleetManagerPlacementRequest>(ctx.Request.Body, BodyJsonOptions, ctx.RequestAborted);
            return (body, null);
        }
        catch (JsonException ex)
        {
            return (null, Results.Json(new { error = $"The body is not valid JSON: {ex.Message}" }, statusCode: StatusCodes.Status400BadRequest));
        }
    }

    private static IResult NoTenant()
        => Results.Json(new { error = "no account is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
}

/// <summary>The production wiring of the Fleet Manager setting: the Gateway's registries, the session history, the
/// pushed roster, the tunnel, and the account's time zone.</summary>
internal sealed class GatewayFleetManagerPlacementEnvironment : IFleetManagerPlacementEnvironment
{
    public required LauncherRegistry Launchers { get; init; }
    public required Streaming.LauncherConnectionRegistry LauncherConnections { get; init; }
    public required DirectorRegistry Directors { get; init; }
    public required History.SessionHistoryStore History { get; init; }
    public required Streaming.PushedSessionStore Pushed { get; init; }
    public required TimeSpan StaleAfter { get; init; }
    public required Streaming.FleetManagerHomeCapabilityRegistry Capabilities { get; init; }
    public required Running.MachineSessionSpawner Spawner { get; init; }
    public required DirectorCommandRouter.SendDirectorCommandAsync SendCommand { get; init; }
    public required TenantSettingsResolver Settings { get; init; }
    public required Func<TenantId, IDisposable> EnterTenantScope { get; init; }
    public required Fleet.FleetManagerMarkHistory Marks { get; init; }
    public required Fleet.FleetManagerPromotionStore Promotions { get; init; }
    public required Func<Fleet.FleetManagerEventService?> Events { get; init; }

    public IReadOnlyList<FleetManagerMachineFacts> Machines(TenantId tenant)
    {
        var now = DateTime.UtcNow;
        var launchers = Launchers.ListLaunchers(tenant);
        var directors = Directors.ListDirectors(tenant);
        IReadOnlyList<History.MachineHistory> history;
        using (EnterTenantScope(tenant))
            history = History.Machines();

        var names = launchers.Select(l => l.MachineName)
            .Concat(directors.Select(d => d.MachineName))
            .Concat(history.Select(h => h.MachineName))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Select(name =>
        {
            var launcher = launchers.FirstOrDefault(l => FleetManagerPlacementFold.SameMachine(l.MachineName, name));
            var capability = MachineRestartCapability.Judge(name, launcher, LauncherConnections.GetActiveConnection(tenant, name), now);
            var declaredCantStart = capability.Declaration == LauncherDeclarationState.Declared
                && !capability.DeclaredCommands.Any(c => string.Equals(c, LauncherCapabilities.DirectorStart, StringComparison.OrdinalIgnoreCase));
            var past = history.FirstOrDefault(h => FleetManagerPlacementFold.SameMachine(h.MachineName, name));
            return new FleetManagerMachineFacts(name, launcher, capability.Reach, !declaredCantStart,
                directors.Where(d => !string.IsNullOrWhiteSpace(d.MachineName) && FleetManagerPlacementFold.SameMachine(d.MachineName, name)).ToList(),
                past?.FirstStartedUtc, past?.FirstAgent, past?.LastSeenUtc);
        }).ToList();
    }

    public IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant)
        => Pushed.SnapshotFresh(tenant, StaleAfter);

    public async Task<IReadOnlyList<AgentChoiceDto>?> AgentsOfferedAsync(TenantId tenant, string directorId, CancellationToken ct)
    {
        using var scope = EnterTenantScope(tenant);
        var director = Directors.Get(tenant, directorId);
        if (director is null) return null;
        var result = await DirectorCommandRouter.TrySendAsync(SendCommand, directorId, "agents-list", "", null, ct,
            machineName: director.MachineName);
        if (result is null || !result.Ok)
        {
            FileLog.Write($"[GatewayFleetManagerPlacementEnvironment] agents-list from {directorId} not read: {result?.Error ?? "not connected"}");
            return null;
        }
        return DirectorCommandRouter.ReadBody<List<AgentChoiceDto>>(result);
    }

    public bool CreatesFleetManagerHome(TenantId tenant, string directorId) => Capabilities.CreatesFleetManagerHome(tenant, directorId);

    public async Task<(bool Ok, SessionDto? Session, string? Error, string? DirectorId)> SpawnAsync(TenantId tenant, string machine,
        NewSessionRequest request, Func<string, string?> refuseDirector, CancellationToken ct)
    {
        // The resolver, the launcher and the tunnel all read the account ambiently, and hold it across the awaits.
        using var scope = EnterTenantScope(tenant);
        return await Spawner.SpawnOnMachineAsync(machine, request, ct, refuseDirector);
    }

    public async Task<bool> CloseSessionAsync(TenantId tenant, string directorId, string sessionId, string reason, CancellationToken ct)
    {
        using var scope = EnterTenantScope(tenant);
        var director = Directors.Get(tenant, directorId);
        var result = await DirectorCommandRouter.TrySendAsync(SendCommand, directorId, "kill", sessionId,
            new SessionStopRequest { Reason = reason }, ct, machineName: director?.MachineName);
        if (result is null || !result.Ok)
        {
            FileLog.Write($"[GatewayFleetManagerPlacementEnvironment] close {sessionId} on {directorId} FAILED: {result?.Error ?? "not connected"}");
            return false;
        }
        return true;
    }

    public void RecordMark(TenantId tenant, string sessionId, DateTime nowUtc) => Marks.Record(tenant, sessionId, nowUtc);

    public void PromoteSuccessor(TenantId tenant, string sessionId, DateTime nowUtc)
    {
        Promotions.Promote(tenant, sessionId, nowUtc);
        var events = Events();
        if (events is null)
            FileLog.Write($"[GatewayFleetManagerPlacementEnvironment] PromoteSuccessor: no event service yet; the event for {sessionId} is stored and waits for the next delivery");
        else
            events.OnEventQueued(tenant);
    }

    public bool MarkByOwner(TenantId tenant, string sessionId, DateTime nowUtc)
    {
        var told = Promotions.MarkByOwner(tenant, sessionId, nowUtc);
        if (told) Events()?.OnEventQueued(tenant);
        return told;
    }

    public TimeZoneInfo TimeZone(TenantId tenant)
        => TimeZoneInfo.FindSystemTimeZoneById(Settings.TimeZone(tenant));

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    public DateTime NowUtc() => DateTime.UtcNow;
}
