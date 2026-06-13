using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using QRCoder;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Phone-pairing QR surface (issue #385; the display half of "pair your phone"). Renders the
/// pairing deep link <c>ccdirector://pair?u=&lt;front-door&gt;&amp;t=&lt;token&gt;</c> as a PNG
/// QR so the phone scanner (sibling slice) reads both the Gateway URL and the bearer token in one
/// scan instead of the user hand-typing a 40-char token.
///
///   GET /pair/qr.png   -> 200 image/png (a QR encoding the pairing payload), or
///                         503 { error } when the Tailscale front door is unavailable.
///   GET /pair/payload  -> 200 { url, token, payload } - the same values as copy-able text for
///                         the Cockpit "Connect a phone" panel's manual-entry fallback.
///
/// Both routes require the Gateway token (issue #369 convention) via
/// <see cref="AuthMiddleware.HasValidToken"/> - the same Bearer-or-cookie check every other
/// protected route uses - enforced HERE so the gate holds even in production mode where the
/// global auth middleware is off (the tray Gateway runs authEnabled=false). The Cockpit serves
/// the panel same-origin through the Gateway front door, so the browser sends the gateway cookie
/// automatically and the QR <img> loads without extra wiring.
///
/// No-fallback rule (criterion 3): when <see cref="TailscaleIdentity.TryGetFrontDoorBaseUrl"/>
/// returns null the endpoint refuses with a clear error naming the missing front door - it never
/// emits a QR carrying an empty or placeholder URL.
/// </summary>
internal static class GatewayPairingEndpoint
{
    private const string FrontDoorMissing =
        "Tailscale front-door URL unavailable - this machine has no MagicDNS identity " +
        "(Tailscale not running or not logged in). The phone reaches the Gateway over the " +
        "tailnet, so pairing needs the front door. Start Tailscale and reload.";

    public static void Map(IEndpointRouteBuilder app, string token)
    {
        // The QR image itself. image/png so an <img src="/pair/qr.png"> renders directly.
        app.MapGet("/pair/qr.png", (HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token))
            {
                FileLog.Write($"[GatewayPairing] GET /pair/qr.png: missing or invalid token from {ctx.Connection.RemoteIpAddress} -> 401");
                return Results.Json(new { error = "missing or invalid token" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var frontDoor = TailscaleIdentity.TryGetFrontDoorBaseUrl();
            if (string.IsNullOrWhiteSpace(frontDoor))
            {
                FileLog.Write("[GatewayPairing] GET /pair/qr.png: front-door URL unavailable -> 503 (no placeholder QR)");
                return Results.Json(new { error = FrontDoorMissing },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var payload = PairingPayload.Build(frontDoor, token);
            var png = RenderPng(payload);
            FileLog.Write($"[GatewayPairing] GET /pair/qr.png: {png.Length} bytes for {frontDoor}");
            return Results.Bytes(png, "image/png");
        });

        // The raw values for the Cockpit panel's copy-able manual-entry fallback. Same auth + same
        // no-fallback rule as the image route.
        app.MapGet("/pair/payload", (HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token))
            {
                FileLog.Write($"[GatewayPairing] GET /pair/payload: missing or invalid token from {ctx.Connection.RemoteIpAddress} -> 401");
                return Results.Json(new { error = "missing or invalid token" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var frontDoor = TailscaleIdentity.TryGetFrontDoorBaseUrl();
            if (string.IsNullOrWhiteSpace(frontDoor))
            {
                FileLog.Write("[GatewayPairing] GET /pair/payload: front-door URL unavailable -> 503");
                return Results.Json(new { error = FrontDoorMissing },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var payload = PairingPayload.Build(frontDoor, token);
            return Results.Json(new { url = frontDoor, token, payload });
        });
    }

    /// <summary>
    /// Render a payload string to PNG bytes. PngByteQRCode has no System.Drawing dependency, so it
    /// works on a headless server. ECC level Q (~25% recovery) keeps a phone scan reliable off a
    /// screen; 8 pixels-per-module is a comfortable on-screen size.
    /// </summary>
    private static byte[] RenderPng(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var qr = new PngByteQRCode(data);
        return qr.GetGraphic(pixelsPerModule: 8);
    }
}
