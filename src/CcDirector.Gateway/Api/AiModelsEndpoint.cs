using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The AI model catalog + test surface for the Settings AI tab. It populates the model dropdowns, tests a
/// chat model with one round-trip, and persists the chosen wingman/speech model. The browser never sees
/// the credential - the Gateway resolves it from the vault and presents it to DevThrottle.
///
/// SINCE THE INCLUDED AI MISSION (issue #1360, design C3) the CHAT kind serves a FIXED list of
/// DevThrottle's internal included wingman ids and never the catalog: the wingman and Car Mode are
/// internal included features, and a catalog model there would bill credits. The speech kind still reads
/// the live catalog (text-to-speech is included in its entirety, so every speech model is safe to offer).
/// The wingman/Car Mode model setters refuse a non-included id with a 400 for the same reason, and so
/// does test-chat: it sends the requested model with the deployment credential, so an arbitrary id
/// there would bill credits just as a saved one would.
///
///   GET  /gateway/ai/models?kind=chat|speech -> { models:[ {id, description, voices[], defaultVoice} ] }
///   POST /gateway/ai/test-chat  { model }    -> { ok, reply, seconds } | { error }
///   PUT  /gateway/ai/wingman-model      { model } -> { model }
///   PUT  /gateway/ai/wingman-fast-model { model } -> { model }
///   PUT  /gateway/ai/tts-model          { model } -> { model }
///
/// DevThrottle serves a typed catalog (GET /models?type=chat|speech) where each speech model carries
/// its own voices.
///
/// SPLIT ON HOSTED (issues #1863, #2022). The per-account MODEL/VOICE SETTERS (wingman-model,
/// wingman-fast-model, tts-model) now write only the CALLER's tenant
/// override through <see cref="TenantSettingsResolver"/> (issue #2017 runtime threading), answering 403 on an
/// unresolved identity - never Local - so they SERVE on hosted, mapped onto the ungrouped builder in
/// <see cref="MapServedRoutes"/>. The CATALOG (GET /gateway/ai/models) and TEST-CHAT (POST /gateway/ai/test-chat)
/// STAY denied on hosted: both spend the SHARED deployment provider credential (vault key DEVTHROTTLE_API_KEY)
/// with no per-caller scoping, so serving them would let one tenant spend another account's credit. They map
/// onto the denied group handle in <see cref="MapDeniedRoutes"/> - un-deny only once the credential is
/// caller-scoped and quota/spend-controlled (see <c>Denial().unDenyInstruction</c>). Self-host serves both, as
/// before. The AI tab reads the Gateway-owned <c>catalogAvailable</c> flag (on the ai-provider snapshot) to
/// disable browsing/Test on hosted rather than offer a control that would refuse.
///
/// PER-ROUTE MODE, not an exclusive prefix. Although these seven routes DO sit under <c>/gateway/ai</c>
/// exclusively, the whole owner-settings group is expressed through ONE mechanism in ONE mode: its
/// <c>SettingsEndpoints</c> sibling is scattered across leaves under the shared <c>/gateway</c> prefix and
/// cannot be exclusive, so keeping this family per-route too gives the group one boundary and one revert
/// story rather than mixing modes. The primitive maps a verb-less refusal on each route's own pattern on
/// hosted - the handler is never mapped - so every request shape, including a verb the route never served,
/// meets the refusal rather than a 405.
/// </summary>
internal static class AiModelsEndpoint
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient Http = Traffic.OutboundTraffic.CreateClient(TimeSpan.FromSeconds(30));

    /// <summary>The single error string the hosted refusal serves, held here so a test asserts the exact
    /// string served rather than a copy that could drift.</summary>
    internal const string RefusalMessage = "the model settings are not available on the hosted gateway";

    /// <summary>
    /// The hosted refusal payload for every model-settings route (issue #1863). Validated on construction,
    /// so a blank field fails the Gateway at startup. The primitive reads <see cref="GatewayHostedMode.IsHosted"/>
    /// DIRECTLY, never an optional argument that fails OPEN when a caller forgets it. 404 rather than 403: on
    /// hosted these routes do not exist as a concept, and 403 would imply some credential could reach them.
    /// </summary>
    private static HostedDenial Denial() => new(
        family: "model-settings",
        message: RefusalMessage,
        reason: "the model settings are process-global config.json values with no tenant dimension, and the " +
                "catalog and test-chat routes spend the deployment's own provider credential - so on shared " +
                "infrastructure one subscriber would repoint every model and bill everyone's credential",
        unDenyInstruction: "do NOT simply remove this deny: give each model setting a per-tenant home " +
                "(config.json has none today), migrate any global value already written, and scope the credential " +
                "the catalog/test-chat routes spend to the caller, before restoring a tenant-scoped route",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Maps the model-settings group through the shared refusal primitive and RETURNS the denied handle, so a
    /// test can map a BRAND-NEW route onto the handle and find it already refused on hosted with no deny of
    /// its own. Without that return value nothing outside this file can state the future-route property.
    /// </summary>
    public static HostedDenyGroup Map(IEndpointRouteBuilder outer, KeyVault vault,
        TenantSettingsResolver resolver, HostedTenantBoundary? tenantBoundary)
    {
        FileLog.Write($"[AiModelsEndpoint] mapping the model settings; hosted={GatewayHostedMode.IsHosted} - per-account model setters serve; the catalog + test-chat (shared credential) are refused via the shared refusal primitive (issues #1863, #2022)");

        var group = HostedRouteDeny.Group(outer, "", Denial());

        // The per-account model/voice setters SERVE on hosted (issue #2022): each resolves the caller's tenant
        // and writes only that tenant's override, answering 403 on an unresolved identity - never Local. They
        // take the ungrouped builder because they are NOT denied; their fail-closed is ResolveReadTenant.
        MapServedRoutes(outer, resolver, tenantBoundary);

        // The catalog (GET /gateway/ai/models) and test-chat (POST /gateway/ai/test-chat) STAY denied on hosted:
        // they spend the SHARED deployment provider credential (vault key DEVTHROTTLE_API_KEY) with no per-caller
        // scoping, so serving them would let one tenant spend another account's credit. They map onto the group
        // handle ONLY - `outer` is out of scope inside MapDeniedRoutes - so neither can be moved off the refusal
        // by a one-word edit. Un-deny only after the credential is caller-scoped (see Denial().unDenyInstruction).
        MapDeniedRoutes(group, vault);
        return group;
    }

    /// <summary>The 403 a per-tenant model route answers when the caller's tenant cannot be resolved (issue
    /// #2017) - never the Local partition on hosted. Self-host always resolves to Local, so this never fires
    /// there.</summary>
    private static IResult TenantRequired()
        => Results.Json(new { error = "a tenant could not be resolved for this request" },
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// The catalog + test-chat routes that STAY denied on hosted (issue #2022). Takes the denied GROUP HANDLE
    /// and nothing else - the ungrouped builder is out of scope so neither can be mapped around the refusal.
    /// Both spend the shared deployment provider credential from <paramref name="vault"/> with no per-caller
    /// scoping, which is the un-deny precondition that is not yet met.
    /// </summary>
    private static void MapDeniedRoutes(HostedDenyGroup app, KeyVault vault)
    {
        app.MapGet("/gateway/ai/models", async (string? kind, CancellationToken ct) =>
        {
            var k = string.Equals(kind, "speech", StringComparison.OrdinalIgnoreCase) ? "speech" : "chat";

            // The CHAT kind feeds the wingman (and Car Mode) model pickers, and since the Included AI
            // mission (issue #1360, design consequence C3) those pickers offer ONLY DevThrottle's
            // internal included ids - never the catalog. A wingman pointed at a catalog id would bill
            // credits, which the ruling forbids for an internal feature. The list is fixed and local:
            // no upstream call, no provider credential spent, nothing for a catalog outage to break.
            if (k == "chat")
                return Results.Json(new { models = IncludedChatModels() });

            var mode = TranscriptionModeConfig.Get();
            var ep = TranscriptionEndpointResolver.ResolveTts(mode);   // base URL + key name (same per provider)
            var key = vault.Get(ep.KeyName);
            if (string.IsNullOrWhiteSpace(key))
                return Results.Json(new { error = ProviderKeyMissingMessage(mode) },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            try
            {
                var models = await ListModelsAsync(ep.BaseUrl, key!, k, ct);
                return Results.Json(new { models });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AiModelsEndpoint] list models FAILED ({mode.ToConfigString()}, {k}): {ex.Message}");
                return Results.Json(new { error = "could not list models: " + ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/gateway/ai/test-chat", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<ModelBody>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Model))
                    return Results.BadRequest(new { error = "body { \"model\": \"<id>\" } is required" });

                // The SAME non-included-id refusal the model setters apply (issue #1360). This route
                // sends the requested model with the deployment credential, so an arbitrary model id
                // here - a stale picker, a hand-written request - would run a catalog model on the
                // deployment key and bill the exact credits this mission eliminates. The mint is the
                // gate: the brain's constructor only accepts the minted type, so a raw string cannot
                // reach the wire even if this refusal were edited away.
                var model = body.Model.Trim();
                var minted = IncludedModelId.TryMint(model);
                if (minted is null) return NonIncludedModelRefusal(model, "test-chat model");

                var mode = TranscriptionModeConfig.Get();
                var ep = TranscriptionEndpointResolver.ResolveWingman(mode);
                var key = vault.Get(ep.KeyName);
                if (string.IsNullOrWhiteSpace(key))
                    return Results.Json(new { ok = false, error = ProviderKeyMissingMessage(mode) },
                        statusCode: StatusCodes.Status503ServiceUnavailable);

                // One real round-trip to the chosen model - the same path the wingman uses - so the user
                // can prove a newly-picked model actually responds before relying on it.
                var brain = new HostedInferenceBrain(ep.BaseUrl, key!, minted, log: FileLog.Write);
                var ask = await brain.AskAsync("Reply with exactly the single word: pong. Output nothing else.", ctx.RequestAborted);
                return Results.Json(new { ok = true, reply = ask.Text.Trim(), seconds = Math.Round(ask.ReplySeconds, 1) });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid JSON" }); }
            catch (Exception ex)
            {
                FileLog.Write($"[AiModelsEndpoint] test-chat FAILED: {ex.Message}");
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    /// <summary>
    /// The five per-account model/voice setters that SERVE on hosted (issue #2022). Takes the ungrouped builder:
    /// each resolves the caller's tenant through <paramref name="resolver"/> and answers 403 when none resolves,
    /// so they are safe on shared infrastructure without a deny. The catalog/test-chat credential routes are the
    /// ones that stay denied (see <see cref="MapDeniedRoutes"/>).
    /// </summary>
    private static void MapServedRoutes(IEndpointRouteBuilder app,
        TenantSettingsResolver resolver, HostedTenantBoundary? tenantBoundary)
    {

        app.MapPut("/gateway/ai/wingman-model", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                var body = await JsonSerializer.DeserializeAsync<ModelBody>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Model))
                    return Results.BadRequest(new { error = "body { \"model\": \"<id>\" } is required" });
                var model = body.Model.Trim();
                if (RejectNonIncludedModel(model, "wingman model") is { } refusal) return refusal;
                resolver.SetWingmanModel(t.Value, WingmanModelRole.Thinking, model, DateTime.UtcNow);
                FileLog.Write($"[AiModelsEndpoint] wingman thinking model set: {model} for tenant={t.Value.ToLogString()}");
                return Results.Json(new { model });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid JSON" }); }
        });

        app.MapPut("/gateway/ai/wingman-fast-model", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                var body = await JsonSerializer.DeserializeAsync<ModelBody>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Model))
                    return Results.BadRequest(new { error = "body { \"model\": \"<id>\" } is required" });
                var model = body.Model.Trim();
                if (RejectNonIncludedModel(model, "wingman fast model") is { } refusal) return refusal;
                resolver.SetWingmanModel(t.Value, WingmanModelRole.Fast, model, DateTime.UtcNow);
                FileLog.Write($"[AiModelsEndpoint] wingman fast model set: {model} for tenant={t.Value.ToLogString()}");
                return Results.Json(new { model });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid JSON" }); }
        });

        app.MapPut("/gateway/ai/tts-model", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                var body = await JsonSerializer.DeserializeAsync<ModelBody>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Model))
                    return Results.BadRequest(new { error = "body { \"model\": \"<id>\" } is required" });
                resolver.SetTtsModel(t.Value, body.Model, DateTime.UtcNow);
                var model = body.Model.Trim();
                FileLog.Write($"[AiModelsEndpoint] tts model set: {model} for tenant={t.Value.ToLogString()}");
                return Results.Json(new { model });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid JSON" }); }
        });
    }

    private static string ProviderKeyMissingMessage(TranscriptionMode mode) =>
        "not signed in to DevThrottle - sign in on the Account tab";

    /// <summary>
    /// The fixed chat-kind picker list (issue #1360, design C3): DevThrottle's internal included
    /// wingman ids and nothing else. Catalog models are deliberately absent - they bill credits.
    /// </summary>
    private static List<ModelDto> IncludedChatModels() => new()
    {
        new ModelDto(TranscriptionEndpointResolver.DevThrottleWingmanModel,
            "DevThrottle wingman - the thinking tier, included with your account", new List<string>(), null),
        new ModelDto(TranscriptionEndpointResolver.DevThrottleWingmanFastModel,
            "DevThrottle wingman fast - the low-latency tier, included with your account", new List<string>(), null),
    };

    /// <summary>
    /// The 400 a model setter answers when the value is not a DevThrottle internal included id
    /// (issue #1360). The wingman and Car Mode are internal features: a catalog id here would bill
    /// credits, so the write is refused loudly instead of being stored and silently ignored at
    /// resolution time. Validation is the <see cref="IncludedModelId"/> mint - the same single gate
    /// every wire path is typed against - never a second local check that could drift from it.
    /// </summary>
    private static IResult? RejectNonIncludedModel(string model, string settingName)
        => IncludedModelId.TryMint(model) is null ? NonIncludedModelRefusal(model, settingName) : null;

    /// <summary>The refusal payload for a non-included model id, shared by the setters and test-chat.</summary>
    private static IResult NonIncludedModelRefusal(string model, string settingName)
    {
        FileLog.Write($"[AiModelsEndpoint] {settingName} REFUSED non-included model '{model}'");
        return Results.BadRequest(new
        {
            error = $"'{model}' is not a DevThrottle included model id. The {settingName} must be one of the " +
                    $"devthrottle/ ids (for example {TranscriptionEndpointResolver.DevThrottleWingmanModel} or " +
                    $"{TranscriptionEndpointResolver.DevThrottleWingmanFastModel}).",
        });
    }

    /// <summary>
    /// List DevThrottle models for a kind. DevThrottle uses GET /models?type=chat|speech; speech
    /// models carry voices.
    /// </summary>
    private static async Task<List<ModelDto>> ListModelsAsync(string baseUrl, string key, string kind, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/models?type=" + kind;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{(int)resp.StatusCode} from /models");

        var list = new List<ModelDto>();
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var voices = new List<string>();
            if (item.TryGetProperty("voices", out var v) && v.ValueKind == JsonValueKind.Array)
                foreach (var voice in v.EnumerateArray())
                    if (voice.ValueKind == JsonValueKind.String) voices.Add(voice.GetString()!);

            var desc = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : "";
            var defVoice = item.TryGetProperty("defaultVoice", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() : null;
            list.Add(new ModelDto(id!, desc ?? "", voices, defVoice));
        }
        return list;
    }

    private sealed record ModelBody(string? Model);
    private sealed record ModelDto(string Id, string Description, List<string> Voices, string? DefaultVoice);
}
