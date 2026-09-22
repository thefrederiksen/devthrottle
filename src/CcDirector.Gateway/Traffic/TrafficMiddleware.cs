using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CcDirector.Gateway.Traffic;

/// <summary>
/// The two places the traffic counters sit in the Gateway pipeline.
///
/// <see cref="UseRequestCounting"/> goes OUTSIDE response compression: it swaps in a counting body feature, lets
/// the request run, and records one row - route template, client kind, account, body bytes out (after
/// compression), body bytes in, an estimate of the header bytes, whether it was a 304, and for the conversation
/// route whether it was a tail or a full answer.
///
/// <see cref="UseConnectionCounting"/> goes AFTER routing (so the route template is known) and after the
/// authentication and tenant middleware (so the account is known). It wraps the WebSocket feature, and it sets
/// the <see cref="TrafficScope"/> the SignalR protocol counter reads to name a hub message's account.
///
/// NEITHER CAN BREAK A RESPONSE. Everything they compute runs after the request finished and inside its own
/// catch; a failure is logged and the request's own outcome - success or exception - is left exactly as it was.
/// </summary>
public static class TrafficMiddleware
{
    /// <summary>The per-request state the endpoints can mark (<see cref="MarkHistoryAnswer"/>).</summary>
    internal sealed class RequestMarks
    {
        public bool? HistoryTail;
    }

    /// <summary>
    /// Record that the conversation route answered with the tail (only the new messages) or in full. Called by
    /// the endpoint; a no-op when the counters are not installed.
    /// </summary>
    public static void MarkHistoryAnswer(HttpContext ctx, bool tail)
    {
        if (ctx.Features.Get<RequestMarks>() is { } marks)
            marks.HistoryTail = tail;
    }

    public static void UseRequestCounting(IApplicationBuilder app, TrafficMeter meter, Func<HttpContext, string?> resolveAccount)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(resolveAccount);

        app.Use(async (ctx, next) =>
        {
            var marks = new RequestMarks();
            ctx.Features.Set(marks);

            var originalBody = ctx.Features.Get<IHttpResponseBodyFeature>();
            CountingResponseBody? counting = null;
            if (originalBody is not null)
            {
                counting = new CountingResponseBody(originalBody);
                ctx.Features.Set<IHttpResponseBodyFeature>(counting);
            }

            var bytesIn = ctx.Request.ContentLength ?? 0;
            CountingReadStream? chunkedIn = null;
            if (ctx.Request.ContentLength is null
                && ctx.Request.Headers.TransferEncoding.ToString().Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                chunkedIn = new CountingReadStream(ctx.Request.Body);
                ctx.Request.Body = chunkedIn;
            }

            try
            {
                await next();
            }
            finally
            {
                if (originalBody is not null)
                    ctx.Features.Set(originalBody);
                Record(ctx, meter, resolveAccount, counting, chunkedIn?.Bytes ?? bytesIn, marks);
            }
        });
    }

    public static void UseConnectionCounting(IApplicationBuilder app, TrafficMeter meter, Func<HttpContext, string?> resolveAccount)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(resolveAccount);

        app.Use(async (ctx, next) =>
        {
            string? account = null;
            string client = TrafficClassifier.Other;
            try
            {
                account = SafeAccount(ctx, resolveAccount);
                client = TrafficClassifier.ClientKind(ctx);
                var hub = HubRouteOf(ctx.Request.Path);
                TrafficScope.Current = new TrafficScope(account ?? TrafficMeter.NoAccount, client, hub);

                if (ctx.Features.Get<IHttpWebSocketFeature>() is { IsWebSocketRequest: true } ws)
                    ctx.Features.Set<IHttpWebSocketFeature>(new CountingWebSocketFeature(
                        ws, meter, TrafficClassifier.RouteTemplate(ctx), client, account ?? TrafficMeter.NoAccount));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TrafficMiddleware] connection counting skipped for this request ({ex.GetType().Name}): {ex.Message}");
            }

            await next();
        });
    }

    /// <summary>The request's account, or null when none resolves - including when resolving it throws, which
    /// is logged: a request the counters cannot attribute is still counted, under no account.</summary>
    private static string? SafeAccount(HttpContext ctx, Func<HttpContext, string?> resolveAccount)
    {
        try
        {
            return resolveAccount(ctx);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TrafficMiddleware] account not resolved for counting ({ex.GetType().Name}): {ex.Message}");
            return null;
        }
    }

    private static string? HubRouteOf(PathString path)
    {
        if (path.StartsWithSegments("/director-stream", StringComparison.OrdinalIgnoreCase)) return "/director-stream";
        if (path.StartsWithSegments("/launcher-stream", StringComparison.OrdinalIgnoreCase)) return "/launcher-stream";
        return null;
    }

    private static void Record(HttpContext ctx, TrafficMeter meter, Func<HttpContext, string?> resolveAccount,
        CountingResponseBody? body, long bytesIn, RequestMarks marks)
    {
        try
        {
            // An accepted WebSocket is counted frame by frame under the "websocket" kind; its upgrade request is
            // still one request here, with no body.
            var status = ctx.Response.StatusCode;
            meter.Cell(TrafficMeter.Http, TrafficClassifier.HttpRouteName(ctx), TrafficClassifier.ClientKind(ctx), SafeAccount(ctx, resolveAccount))
                .AddRequest(body?.Bytes ?? 0, bytesIn, HeaderBytes(ctx.Response), status == StatusCodes.Status304NotModified, marks.HistoryTail);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TrafficMiddleware] request not counted ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// An ESTIMATE of the response header bytes, as HTTP/1.1 would spell them: the status line plus
    /// "name: value\r\n" for each header, plus the blank line. HTTP/2 compresses headers, so on the wire it is
    /// usually less. It exists because a 304 has no body and costs only its headers.
    /// </summary>
    internal static long HeaderBytes(HttpResponse response)
    {
        long total = 17 + 2; // "HTTP/1.1 200 OK\r\n" and the blank line.
        foreach (var (name, values) in response.Headers)
        {
            foreach (var value in values)
                total += name.Length + 2 + (value?.Length ?? 0) + 2;
        }
        return total;
    }
}

/// <summary>
/// The account, client kind and hub of the request or hub connection the current code is running in. The hub
/// connection's receive loop runs inside the WebSocket request that opened it, so an inbound hub message sees
/// the scope of its own connection. A hub message the Gateway SENDS while serving some other request sees that
/// request's scope; one sent from a timer sees none.
/// </summary>
public sealed class TrafficScope
{
    private static readonly AsyncLocal<TrafficScope?> CurrentScope = new();
    private const int MaxStreams = 64;
    private ConcurrentDictionary<string, string>? _streams;

    public TrafficScope(string account, string client, string? hub)
    {
        Account = account;
        Client = client;
        Hub = hub;
    }

    public static TrafficScope? Current
    {
        get => CurrentScope.Value;
        internal set => CurrentScope.Value = value;
    }

    public string Account { get; }
    public string Client { get; }

    /// <summary>The hub route when this is a hub connection (<c>/director-stream</c>), null otherwise.</summary>
    public string? Hub { get; }

    /// <summary>Remember which hub method a client-to-server stream id belongs to, so its items can be named.</summary>
    internal void RememberStream(string streamId, string method)
    {
        var streams = _streams ??= new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        if (streams.Count >= MaxStreams) streams.Clear();
        streams[streamId] = method;
    }

    internal string? StreamMethod(string? streamId)
        => streamId is not null && _streams is { } s && s.TryGetValue(streamId, out var m) ? m : null;
}
