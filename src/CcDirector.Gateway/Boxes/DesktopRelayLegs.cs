using System.Net.WebSockets;
using CcDirector.Core.Boxes;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CcDirector.Gateway.Boxes;

/// <summary>
/// Factory Boxes (issue #4258): serve one browser request, or one browser WebSocket, to a box's desktop over the
/// tunnel of the Director that hosts the box. The shape copies the terminal stream's leg
/// (<see cref="TunnelStreamLegs"/>): mint a fresh stream id, register it with its owner, send the open command
/// down, and let the Director attach its frames - but the frames run through a <see cref="DesktopRelayPump"/>, with
/// its own window, so a busy desktop cannot fill the connection the terminal streams share.
///
/// Refusals before anything is relayed are plain HTTP answers, for a WebSocket upgrade too (the upgrade is accepted
/// only once the Director has opened the box's socket): 404 when the Director does not host the box, 503 when it is
/// not connected or already carries its most desktops, 502 or 504 when the tunnel fails.
/// </summary>
internal sealed class DesktopRelayLegs
{
    private readonly DesktopRelayRegistry _registry;
    private readonly DirectorCommandRouter.SendDirectorCommandAsync _sendCommand;
    private readonly TimeProvider _time;

    public DesktopRelayLegs(DesktopRelayRegistry registry, DirectorCommandRouter.SendDirectorCommandAsync sendCommand, TimeProvider? time = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The per-box byte totals this Gateway has relayed.</summary>
    public DesktopRelayBoxTally Tally { get; } = new();

    /// <summary>Relay the request in <paramref name="ctx"/> to <paramref name="boxId"/>'s desktop on <paramref name="directorId"/>.</summary>
    /// <param name="pathAndQuery">The path under the box's prefix, with the prefix stripped, plus the query string.</param>
    public async Task ServeAsync(HttpContext ctx, TenantId tenant, string directorId, string boxId, string pathAndQuery)
    {
        var isSocket = ctx.WebSockets.IsWebSocketRequest;
        var prefix = BoxDesktopEndpoints.PrefixFor(boxId);
        var hasBody = !isSocket && HasBody(ctx.Request);
        FileLog.Write($"[DesktopRelayLegs] ServeAsync: box={boxId} director={directorId} {(isSocket ? "websocket" : ctx.Request.Method)} {pathAndQuery}");

        if (!isSocket && ctx.Request.ContentLength > DesktopRelayLimits.MaxRequestBodyBytes)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge,
                $"The request body is over {DesktopRelayLimits.MaxRequestBodyBytes} bytes, the most one desktop request may carry.");
            return;
        }
        if (hasBody && ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
            bodyLimit.MaxRequestBodySize = DesktopRelayLimits.MaxRequestBodyBytes;

        var streamId = Guid.NewGuid().ToString("N");
        var pending = _registry.Register(streamId, new StreamOwner(tenant, directorId));
        WebSocket? socket = null;
        try
        {
            var open = await OpenAsync(ctx, directorId, new OpenDesktopStreamRequest
            {
                StreamId = streamId,
                BoxId = boxId,
                Mode = isSocket ? DesktopRelayMode.WebSocket : DesktopRelayMode.Http,
                Method = ctx.Request.Method,
                PathAndQuery = pathAndQuery,
                Headers = BoxDesktopHeaders.RequestHeadersFor(ctx.Request.Headers),
                HasBody = hasBody,
                SubProtocols = isSocket ? ctx.WebSockets.WebSocketRequestedProtocols.ToList() : new List<string>(),
            });
            if (open is null) return;

            IDesktopRelayEndpoint endpoint;
            var label = $"browser box {boxId} stream {streamId}";
            if (isSocket)
            {
                socket = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { SubProtocol = open.SubProtocol });
                endpoint = new WebSocketRelayEndpoint(socket, label);
            }
            else
            {
                endpoint = new BrowserHttpRelayEndpoint(ctx, prefix, hasBody, label);
            }

            DesktopRelayAttachment attachment;
            try
            {
                attachment = await pending.Attached.WaitAsync(DesktopRelayLimits.AttachTimeout, _time, ctx.RequestAborted);
            }
            catch (TimeoutException)
            {
                FileLog.Write($"[DesktopRelayLegs] stream={streamId} box={boxId}: the Director never attached within {DesktopRelayLimits.AttachTimeout.TotalSeconds:0} seconds");
                await endpoint.CloseAsync(DesktopRelayEndpointClose.InternalError, "The Director hosting this box did not start the desktop stream.");
                return;
            }
            catch (OperationCanceledException)
            {
                FileLog.Write($"[DesktopRelayLegs] stream={streamId} box={boxId}: the browser left before the Director attached");
                await endpoint.CloseAsync(DesktopRelayEndpointClose.GoingAway, "the browser left");
                return;
            }

            var pump = new DesktopRelayPump(endpoint, pending.Down.Writer,
                new DesktopRelayUsage(boxId, streamId, "gateway"), Tally, endpointIsBrowser: true, _time,
                idleTimeout: isSocket ? DesktopRelayLimits.IdleTimeout : null, outboundLimit: null,
                peerGoneReason: "The Director hosting this box disconnected.",
                endpointGoneReason: "the browser left");
            var reason = await pump.RunAsync(attachment.Up, attachment.HubCancellation, ctx.RequestAborted);
            FileLog.Write($"[DesktopRelayLegs] stream={streamId} box={boxId} ended: {reason}");
        }
        finally
        {
            _registry.Remove(streamId);
            socket?.Dispose();
        }
    }

    // Send the open and answer every refusal; null means the refusal has been written and there is nothing to relay.
    private async Task<OpenDesktopStreamResponse?> OpenAsync(HttpContext ctx, string directorId, OpenDesktopStreamRequest request)
    {
        DirectorCommandResult? open;
        try
        {
            open = await DirectorCommandRouter.TrySendAsync(_sendCommand, directorId, DesktopRelayProtocol.OpenVerb, "", request, ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            FileLog.Write($"[DesktopRelayLegs] stream={request.StreamId} box={request.BoxId}: the browser left during the open");
            return null;
        }

        if (open is null)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "The Director hosting this box is not connected right now.");
            return null;
        }
        if (!open.Ok)
        {
            var code = open.Status switch
            {
                DirectorCommandStatus.NotFound => StatusCodes.Status404NotFound,
                DirectorCommandStatus.BadRequest => StatusCodes.Status400BadRequest,
                DirectorCommandStatus.Conflict => StatusCodes.Status503ServiceUnavailable,
                DirectorCommandStatus.Timeout => StatusCodes.Status504GatewayTimeout,
                _ => StatusCodes.Status502BadGateway,
            };
            FileLog.Write($"[DesktopRelayLegs] stream={request.StreamId} box={request.BoxId}: the Director refused the open: {open.Status} {open.Error} -> {code}");
            await WriteErrorAsync(ctx, code, open.Error ?? $"The Director answered {open.Status}.");
            return null;
        }
        return DirectorCommandRouter.ReadBody<OpenDesktopStreamResponse>(open) ?? new OpenDesktopStreamResponse();
    }

    private static bool HasBody(HttpRequest request)
    {
        if (request.ContentLength is { } length) return length > 0;
        if (request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>() is { } detection) return detection.CanHaveBody;
        return request.Headers.TransferEncoding.Count > 0;
    }

    private static async Task WriteErrorAsync(HttpContext ctx, int status, string error)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { error });
    }
}
