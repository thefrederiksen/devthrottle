using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway as the single source of truth for the WHOLE transcription routing target
/// (issue #506). Before this, a Director on a Gateway only fetched the vault KEY and decided the
/// base URL + mode locally (compile-time constants). Now the Gateway composes the full pair
/// server-side and a Director asks for it in one call:
///
///   GET /transcription/routing -> { mode, baseUrl, model, key } | 404 (no key set for the mode)
///
/// The Gateway already owns the mode (Cockpit Settings &gt; Transcription, #497) and the keys (its
/// vault), so it pairs URL+key here. The security invariant is now enforced SERVER-SIDE: the pair
/// is composed from the one pure <see cref="TranscriptionEndpointResolver"/>, so the bring-your-own
/// OpenAI key is only ever returned with the OpenAI base URL and is NEVER returned alongside the
/// devthrottle.com URL (and vice versa). Inherits the host-wide token middleware like every other
/// Gateway route.
/// </summary>
internal static class TranscriptionRoutingEndpoint
{
    public static void Map(IEndpointRouteBuilder app, KeyVault vault)
    {
        app.MapGet("/transcription/routing", (HttpContext ctx) =>
        {
            // Stamp every response from THIS route (including the 404-no-key below) with a marker
            // header. It lets a Director tell apart "Gateway has this route but no key set yet"
            // (header present, 404) from "older Gateway that never mapped this route" (header
            // absent, framework 404) - so the Director can surface a clear "update your Gateway"
            // message instead of silently using a baked-in URL (issue #506, no-fallback rule).
            ctx.Response.Headers["X-Transcription-Routing"] = "1";

            // Resolve the Gateway's configured mode -> (baseUrl, key, transport, model) through the
            // SINGLE transcription owner (issue #839), the same resolve-mode-and-key path every batch
            // caller uses. Composing URL+key here is what makes the never-cross invariant a
            // server-side guarantee; the transport (issue #513) lets the Director honor the provider's
            // wire (batch for DevThrottle/Groq, realtime for BYO).
            var routing = new Transcription.GatewayTranscriptionService(vault).Resolve();
            var endpoint = routing.Endpoint;

            // Local mode (issue #541): in-process Whisper.net, no key and no remote endpoint. Local
            // is ALWAYS available (the model is auto-downloaded on first use), so it NEVER hits the
            // key gate below - it returns 200 { mode: "local" } with no baseUrl/key. The 404-no-key
            // path is for the remote modes (byo, devthrottle) only.
            if (routing.IsLocal)
            {
                FileLog.Write($"[TranscriptionRoutingEndpoint] GET /transcription/routing: mode=local (in-process), transport={endpoint.Transport.ToConfigString()}, model={endpoint.Model}");
                return Results.Json(new
                {
                    mode = endpoint.Mode.ToConfigString(),
                    transport = endpoint.Transport.ToConfigString(),
                    model = endpoint.Model,
                });
            }

            if (routing.Key is null)
            {
                // No silent default: the Gateway reachable but the key for this mode is not set yet.
                // The Director reports transcription unavailable for the mode (never a baked-in URL).
                FileLog.Write($"[TranscriptionRoutingEndpoint] GET /transcription/routing: mode={endpoint.Mode.ToConfigString()}, no key for {endpoint.RequireKeyName()}");
                return Results.NotFound(new { error = "no key set for the current transcription mode", mode = endpoint.Mode.ToConfigString() });
            }

            FileLog.Write($"[TranscriptionRoutingEndpoint] GET /transcription/routing: mode={endpoint.Mode.ToConfigString()}, transport={endpoint.Transport.ToConfigString()}, baseUrl={endpoint.BaseUrl}, model={endpoint.Model}");
            return Results.Json(new
            {
                mode = endpoint.Mode.ToConfigString(),
                transport = endpoint.Transport.ToConfigString(),
                baseUrl = endpoint.BaseUrl,
                model = endpoint.Model,
                key = routing.Key,
            });
        });
    }
}
