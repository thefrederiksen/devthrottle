using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>Where the Factory Manager routes read and write what they need, one delegate per fact.</summary>
/// <param name="Bound">The account's bound Factory Manager session id, or null.</param>
/// <param name="Bind">Bind a session as the account's Factory Manager, or clear the binding with null.</param>
/// <param name="Placement">The computer and folder saved for the Factory Manager, or null.</param>
/// <param name="SavePlacement">Save the computer and folder, together.</param>
/// <param name="Factories">The account's registered factories, archived ones included (they are filtered here).</param>
/// <param name="ListDirectors">The account's registered Directors.</param>
/// <param name="StartOnDirector">Start a session on one Director, as a person's New Session does.</param>
/// <param name="LastKnownSession">What the account's Directors last pushed for one session, or null.</param>
/// <param name="TimeZone">The account's time zone.</param>
internal sealed record FactoryManagerSources(
    Func<TenantId, string?> Bound,
    Action<TenantId, string?> Bind,
    Func<TenantId, (string Machine, string Folder)?> Placement,
    Action<TenantId, string, string> SavePlacement,
    Func<TenantId, IReadOnlyList<RegisteredFactoryDto>> Factories,
    Func<TenantId, IEnumerable<DirectorDto>> ListDirectors,
    Func<HttpContext, string, NewSessionRequest, Task<DirectorSpawnOutcome>> StartOnDirector,
    Func<TenantId, string, SessionDto?> LastKnownSession,
    Func<TenantId, TimeZoneInfo> TimeZone);

/// <summary>
/// THE FACTORY MANAGER SEAT (Factory Control, step 5; design in docs/qa/factory-control/factory-manager/DESIGN.md).
///
///   GET  /gateway/factory-manager                         -> FactoryManagerBindingDto   (cc-devthrottle factory manager show)
///   PUT  /gateway/factory-manager  { sessionId | null }   -> FactoryManagerBindingDto   (factory-manager set / clear)
///   GET  /gateway/factory/name-check                      -> FactoryNameCheckDto        (cc-devthrottle factory name-check)
///   GET  /gateway/factory-agents/factory-manager          -> FactoryManagerCardDto      (the Factories screen's card)
///   POST /gateway/factory-agents/factory-manager/talk     -> 200 (it runs) | 201 (started) FactoryTalkStartedDto
///
/// The first three sit in the factory switch's group (404 with no body when it is off) and are reachable by a session
/// key, so the seat itself can read them. The card and the talk are the owner's own views.
///
/// WHO MAY SET THE BINDING: the owner's own device, or any session key of the account - the Fleet Manager's rule, with
/// its stated gap (a session can mark itself). Every change is logged with who made it.
///
/// THE TALK is a new, visible, top-level session owned by the person, started through the path a person's New Session
/// takes - exactly as Talk to the boss is - on the saved computer, in the saved folder. When the bound Factory Manager
/// is still running the answer is that session and nothing is started: there is one Factory Manager per account. A new
/// one becomes the bound Factory Manager.
/// </summary>
internal static class FactoryManagerEndpoints
{
    public const string BindingRoute = "/gateway/factory-manager";
    public const string NameCheckRoute = "/gateway/factory/name-check";
    public const string CardRoute = FactoryAgentsViewEndpoints.Prefix + "/factory-manager";
    public const string TalkRoute = FactoryAgentsViewEndpoints.Prefix + "/factory-manager/talk";

    /// <summary>The seat id a Factory Manager talk answers with, where a factory talk names its seat.</summary>
    public const string SeatId = "factory-manager";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder app, RouteGroupBuilder factoryGate, FactoryAgentsSwitch factorySwitch,
        Func<HttpContext, TenantId?> resolveTenant, FactoryManagerSources sources)
    {
        ArgumentNullException.ThrowIfNull(factoryGate);
        ArgumentNullException.ThrowIfNull(factorySwitch);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(sources);

        factoryGate.MapGet(BindingRoute, (HttpContext ctx) => GetBinding(ctx, resolveTenant, sources));
        factoryGate.MapPut(BindingRoute, async Task<IResult> (HttpContext ctx) => await PutBindingAsync(ctx, resolveTenant, sources));
        factoryGate.MapGet(NameCheckRoute, (HttpContext ctx) => NameCheck(ctx, resolveTenant, sources));
        factoryGate.MapGet(CardRoute, (HttpContext ctx) => Card(ctx, resolveTenant, sources));
        app.MapPost(TalkRoute, async Task<IResult> (HttpContext ctx) => await TalkAsync(ctx, factorySwitch, resolveTenant, sources));

        FileLog.Write($"[FactoryManagerEndpoints] mapped {BindingRoute}, {NameCheckRoute}, {CardRoute} and {TalkRoute}");
    }

    internal static IResult GetBinding(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, FactoryManagerSources s)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        return Results.Json(Binding(tenant, s));
    }

    internal static async Task<IResult> PutBindingAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, FactoryManagerSources s)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        Dictionary<string, JsonElement>? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(ctx.Request.Body, Json, ctx.RequestAborted);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[FactoryManagerEndpoints] PUT binding bad JSON: {ex.Message}");
            return Refused(StatusCodes.Status400BadRequest, "invalid JSON");
        }
        // A body with no "sessionId" is refused rather than read as a set or a clear: guessing answers "saved" for a
        // choice nobody made.
        if (body is null || !body.TryGetValue("sessionId", out var value))
            return Refused(StatusCodes.Status400BadRequest, "body { \"sessionId\": \"<full session id>\" | null } is required");

        var who = FleetManagerOwnerDevice.Caller(ctx);
        if (value.ValueKind == JsonValueKind.Null)
        {
            s.Bind(tenant, null);
            FileLog.Write($"[FactoryManagerEndpoints] binding cleared for tenant={tenant.ToLogString()} by {who}");
            return Results.Json(Binding(tenant, s));
        }
        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (raw is null || !Guid.TryParse(raw.Trim(), out var id))
            return Refused(StatusCodes.Status400BadRequest, "\"sessionId\" must be a full session id or null");
        s.Bind(tenant, id.ToString("D"));
        FileLog.Write($"[FactoryManagerEndpoints] binding set to {id:D} for tenant={tenant.ToLogString()} by {who}");
        return Results.Json(Binding(tenant, s));
    }

    internal static IResult NameCheck(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, FactoryManagerSources s)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        return Results.Json(FactoryNameCheck.Check(Live(s.Factories(tenant))));
    }

    internal static IResult Card(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, FactoryManagerSources s)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        var card = CardOf(tenant, s);
        FileLog.Write($"[FactoryManagerEndpoints] GET card: running={card.SessionHref is not null}, needsPlacement={card.NeedsPlacement}, names={card.NameCheck.Count}");
        return Results.Json(card);
    }

    /// <summary>The Factories screen's Factory Manager card, folded once.</summary>
    internal static FactoryManagerCardDto CardOf(TenantId tenant, FactoryManagerSources s)
    {
        var running = RunningSession(tenant, s);
        var placement = s.Placement(tenant);
        var bound = s.Bound(tenant);
        return new FactoryManagerCardDto
        {
            Title = FactoryManagerSeed.SeatName,
            Purpose = "The one seat over all your factories. Ask it about any factory: it points you to that factory's boss, "
                      + "chases each open problem with the boss concerned, and deals itself with what no single boss owns.",
            StateText = running is not null
                ? $"Running on {running.MachineName}"
                : bound is not null ? "Not running - its last session has ended" : "Not started yet",
            StateTone = running is not null ? FactoryTone.Ok : FactoryTone.Grey,
            SessionHref = running is null ? null : "/session/" + Uri.EscapeDataString(running.SessionId),
            ActionLabel = running is not null ? "Open the Factory Manager" : "Talk to the Factory Manager",
            BusyLabel = "Starting the Factory Manager...",
            NeedsPlacement = placement is null,
            Computers = placement is null ? RunningComputers(tenant, s) : new List<string>(),
            PlacementText = placement is null
                ? "Choose the computer it runs on and a folder there for it to work in - for example the folder that holds your factories. They are saved for every later start."
                : null,
            PlacementSavedText = placement is { } p ? $"Runs on {p.Machine} in {p.Folder}" : null,
            NameCheck = FactoryNameCheck.Check(Live(s.Factories(tenant))),
        };
    }

    internal static async Task<IResult> TalkAsync(HttpContext ctx, FactoryAgentsSwitch factorySwitch,
        Func<HttpContext, TenantId?> resolveTenant, FactoryManagerSources s)
    {
        FileLog.Write("[FactoryManagerEndpoints] POST talk");
        if (FleetManagerOwnerDevice.Require(ctx, "start a talk with the Factory Manager",
                "a session may not start a talk with the Factory Manager; a talk is the owner's own session, opened from the owner's phone or browser",
                nameof(FactoryManagerEndpoints), out var notOwner) is null)
            return notOwner!;
        if (!FactoryAgentsGate.IsOnFor(ctx, factorySwitch, resolveTenant, out var why))
        {
            FileLog.Write($"[FactoryManagerEndpoints] REFUSED talk: {why}");
            return Refused(StatusCodes.Status404NotFound,
                "Factory agents are switched off for this account, so no talk was started. The administrator switches them on per account.");
        }
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();

        // The body is optional (a later start uses the saved placement). It is read whole rather than judged by its
        // Content-Length, which a chunked request does not send.
        FactoryManagerTalkRequest? body = null;
        using var reader = new StreamReader(ctx.Request.Body);
        var text = await reader.ReadToEndAsync(ctx.RequestAborted);
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                body = JsonSerializer.Deserialize<FactoryManagerTalkRequest>(text, Json);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[FactoryManagerEndpoints] POST talk bad JSON: {ex.Message}");
                return Refused(StatusCodes.Status400BadRequest, "invalid JSON");
            }
        }

        // One Factory Manager per account: when it still runs, the answer is that session.
        if (RunningSession(tenant, s) is { } running)
        {
            FileLog.Write($"[FactoryManagerEndpoints] talk: the Factory Manager {running.SessionId} still runs - opening it");
            return Results.Json(Answer(running.SessionId, running.Name ?? FactoryManagerSeed.SessionName, running.MachineName ?? "",
                running.DirectorId ?? ""), statusCode: StatusCodes.Status200OK);
        }

        return await StartAsync(ctx, tenant, s, body, FactoryManagerSeed.SessionName,
            FactoryManagerSeed.Compose(Live(s.Factories(tenant))));
    }

    /// <summary>
    /// Start a new Factory Manager talk and bind it: on the computer and in the folder the body names (both, or
    /// neither), else the saved ones. A placement given in the body is saved only once the start succeeded, so a folder
    /// the Director refused is never kept. Used by the talk and by Handle the issue on a factory with no boss.
    /// </summary>
    internal static async Task<IResult> StartAsync(HttpContext ctx, TenantId tenant, FactoryManagerSources s,
        FactoryManagerTalkRequest? body, string name, string prePrompt)
    {
        var sentComputer = body?.Computer?.Trim();
        var sentFolder = body?.Folder?.Trim();
        if (string.IsNullOrEmpty(sentComputer) != string.IsNullOrEmpty(sentFolder))
            return Refused(StatusCodes.Status400BadRequest, "Send both the computer and the folder, or neither.");
        var fromBody = !string.IsNullOrEmpty(sentComputer);
        (string Machine, string Folder)? placement = fromBody ? (sentComputer!, sentFolder!) : s.Placement(tenant);
        if (placement is not { } place)
        {
            FileLog.Write($"[FactoryManagerEndpoints] REFUSED: no placement saved for tenant={tenant.ToLogString()}");
            return Refused(StatusCodes.Status409Conflict,
                "The Factory Manager has no computer and folder yet, so it was not started. Choose them on the Factories "
                + "screen's Factory Manager card and press Talk to the Factory Manager once; they are saved for every later start.");
        }

        var director = s.ListDirectors(tenant).FirstOrDefault(d =>
            d.StoppedAtUtc is null && string.Equals(d.MachineName, place.Machine, StringComparison.OrdinalIgnoreCase));
        if (director is null)
        {
            FileLog.Write($"[FactoryManagerEndpoints] REFUSED: no Director running on {place.Machine}");
            return Refused(StatusCodes.Status409Conflict,
                $"No Director is running on {place.Machine}, the computer the Factory Manager runs on, so it was not started. "
                + $"Start the Director on {place.Machine} and press it again.");
        }

        var request = new NewSessionRequest
        {
            RepoPath = place.Folder,
            Agent = "ClaudeCode",
            Name = name,
            PrePrompt = prePrompt,
            // The session is the person's: it answers to nobody but them.
            ControllerSessionId = null,
            ParentSessionId = null,
        };
        var outcome = await s.StartOnDirector(ctx, director.DirectorId, request);
        if (outcome.Session is not { } started)
        {
            FileLog.Write($"[FactoryManagerEndpoints] the create on {director.DirectorId} was refused");
            return outcome.Refusal!;
        }

        if (fromBody)
            s.SavePlacement(tenant, place.Machine, place.Folder);
        s.Bind(tenant, started.SessionId);
        FileLog.Write($"[FactoryManagerEndpoints] started Factory Manager sid={started.SessionId} on {place.Machine} in {place.Folder}; bound for tenant={tenant.ToLogString()}");
        return Results.Json(Answer(started.SessionId, name, place.Machine, director.DirectorId), statusCode: StatusCodes.Status201Created);
    }

    /// <summary>The bound Factory Manager when its session still runs; null when there is none or it has ended.</summary>
    internal static SessionDto? RunningSession(TenantId tenant, FactoryManagerSources s)
    {
        var bound = s.Bound(tenant);
        if (bound is null) return null;
        var session = s.LastKnownSession(tenant, bound);
        return session is null || FleetManagerSessions.IsGone(session) ? null : session;
    }

    /// <summary>The account's registered factories that are not archived.</summary>
    internal static IReadOnlyList<RegisteredFactoryDto> Live(IReadOnlyList<RegisteredFactoryDto> all) =>
        all.Where(f => f.ArchivedAtUtc is null).ToList();

    private static List<string> RunningComputers(TenantId tenant, FactoryManagerSources s) =>
        s.ListDirectors(tenant).Where(d => d.StoppedAtUtc is null && !string.IsNullOrWhiteSpace(d.MachineName))
            .Select(d => d.MachineName!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();

    private static FactoryManagerBindingDto Binding(TenantId tenant, FactoryManagerSources s)
    {
        var placement = s.Placement(tenant);
        return new FactoryManagerBindingDto
        {
            SessionId = s.Bound(tenant),
            Computer = placement?.Machine,
            Folder = placement?.Folder,
        };
    }

    private static FactoryTalkStartedDto Answer(string sessionId, string name, string computer, string directorId) => new()
    {
        SessionId = sessionId,
        SessionName = name,
        Href = "/session/" + Uri.EscapeDataString(sessionId),
        Factory = "",
        Seat = SeatId,
        Computer = computer,
        DirectorId = directorId,
    };

    private static IResult NoAccount() => Refused(StatusCodes.Status403Forbidden, "No account is bound to this request.");

    private static IResult Refused(int status, string error) => Results.Json(new { error }, statusCode: status);
}
