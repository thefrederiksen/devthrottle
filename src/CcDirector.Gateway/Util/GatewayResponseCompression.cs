using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;

namespace CcDirector.Gateway.Util;

/// <summary>
/// Response compression for the Gateway (traffic optimization, phase 1). Before this nothing the Gateway sent
/// was compressed, and its JSON compresses four to eight times: a 3 MB conversation or a 134 KB roster, every
/// few seconds, per open screen.
///
/// Brotli first, gzip second, both at <see cref="CompressionLevel.Fastest"/>: the answers are generated fresh
/// on every poll, so the time spent compressing is paid on every request and the fastest level already takes
/// most of the size away. <c>EnableForHttps</c> is on because every real client reaches the Gateway over HTTPS
/// (the hosted front end and Tailscale Serve both terminate it).
///
/// What is NEVER compressed, and why:
/// <list type="bullet">
/// <item>The Server-Sent Events feed (<c>GET /events</c>) and the two SignalR hubs (<c>/director-stream</c>,
/// <c>/launcher-stream</c>). A compressor holds bytes back until it has enough to compress, so an event would
/// sit in its buffer instead of reaching the subscriber. Those paths bypass the middleware entirely
/// (<see cref="IsStreamingPath"/>), and <c>text/event-stream</c> is also excluded by type, so a new stream
/// elsewhere is not compressed by accident either.</item>
/// <item>Anything not in the text and JSON types (<see cref="ResponseCompressionDefaults.MimeTypes"/>): images,
/// audio and fonts are already compressed, and audio is served with byte ranges, which compression would
/// break.</item>
/// </list>
/// A response that set Content-Length is fine: the middleware drops it when it compresses and the body is
/// sent chunked.
/// </summary>
public static class GatewayResponseCompression
{
    /// <summary>The streaming content type that must reach the client event by event.</summary>
    public const string EventStreamMimeType = "text/event-stream";

    /// <summary>Register the compression providers and options.</summary>
    public static void AddTo(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
            o.MimeTypes = ResponseCompressionDefaults.MimeTypes;
            o.ExcludedMimeTypes = new[] { EventStreamMimeType };
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
    }

    /// <summary>Add the middleware to every request except the streaming ones.</summary>
    public static void Use(IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseWhen(ctx => !IsStreamingPath(ctx.Request.Path), branch => branch.UseResponseCompression());
    }

    /// <summary>
    /// The paths whose responses are a live stream and must never pass through a compressor: the events feed
    /// and both SignalR hubs (including their negotiate and transport sub-paths).
    /// </summary>
    public static bool IsStreamingPath(PathString path) =>
        path.Equals("/events", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/director-stream", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/launcher-stream", StringComparison.OrdinalIgnoreCase);
}
