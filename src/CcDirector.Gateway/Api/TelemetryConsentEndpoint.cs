using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Centralization Phase 3 (issue #649): the fleet-wide richer-usage-telemetry consent
/// (opt-out) endpoints. The authoritative consent setting lives on the Gateway - one setting governs
/// the whole fleet - and a Director reads it to decide whether its richer usage telemetry flows. The
/// always-on login/director-startup auth-floor events (issues #628/#631) are NEVER gated by this.
///
///   GET /gateway/telemetry-consent              -> { enabled }  (default ON when never set)
///   PUT /gateway/telemetry-consent body { enabled: bool } -> { enabled }
///
/// State is the top-level config.json key <c>telemetry_consent</c> (<see cref="TelemetryConsentConfig"/>),
/// the same store the other Gateway settings use. Read at decision time, so a toggle takes effect
/// immediately - no Gateway restart. These endpoints inherit the host-wide Gateway token middleware
/// exactly like every other Gateway endpoint. The response carries no token and no user data.
/// </summary>
internal static class TelemetryConsentEndpoint
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Maps the GET/PUT telemetry-consent routes.</summary>
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/gateway/telemetry-consent", () =>
        {
            var enabled = TelemetryConsentConfig.Get();
            FileLog.Write($"[TelemetryConsentEndpoint] GET /gateway/telemetry-consent: enabled={enabled}");
            return Results.Json(new { enabled });
        });

        app.MapPut("/gateway/telemetry-consent", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<TelemetryConsentBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null)
                    return Results.BadRequest(new { error = "body { \"enabled\": true|false } is required" });

                TelemetryConsentConfig.Set(body.Enabled);
                FileLog.Write($"[TelemetryConsentEndpoint] PUT /gateway/telemetry-consent: enabled={body.Enabled}");
                return Results.Json(new { enabled = body.Enabled });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[TelemetryConsentEndpoint] PUT /gateway/telemetry-consent bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });
    }

    private sealed record TelemetryConsentBody(bool Enabled);
}
