using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace CcDirector.Gateway.Api;

/// <summary>
/// A JSON answer that a client which already holds it is not sent again (traffic optimization, phase 1).
///
/// The two reads the Cockpit and the phone poll hardest - the conversation (<c>GET /sessions/{sid}/history</c>,
/// every 2.5 seconds per open Chat) and the roster (<c>GET /sessions</c>, every 2 seconds per open list) -
/// re-sent their whole body on every poll, although most polls return exactly what the last one did. This
/// serializes the answer once, stamps a strong <c>ETag</c> computed from those exact bytes, and answers a
/// request whose <c>If-None-Match</c> names that tag with <c>304 Not Modified</c> and no body.
///
/// Why no client has to change: the response also carries <c>Cache-Control: no-cache, private</c>. A browser
/// keeps the body in its own HTTP cache, and on the next poll it sends <c>If-None-Match</c> by itself; on a 304
/// it hands the page its cached body with status 200. The shipped Cockpit and phone therefore get 304s as
/// they are.
///
/// Why this cannot serve anyone a stale or foreign answer:
/// <list type="bullet">
/// <item>The tag is a hash of the serialized bytes and nothing else, so ANY change to the answer - a new turn,
/// a Wingman verdict, a roster field, the stale notice - is a different tag and a full 200.</item>
/// <item>A 304 is only ever a statement that the caller's own copy is byte-identical to what it would have been
/// sent now, so even two accounts whose answers happened to hash alike could only be told "what you hold is
/// what I would send you" - which is true. The body a 304 re-uses is the requester's own.</item>
/// <item><c>private</c> keeps any shared cache (a proxy, a CDN) from storing the body at all.</item>
/// </list>
/// </summary>
public static class ConditionalJson
{
    /// <summary>Revalidate on every use, and never store in a shared cache.</summary>
    public const string CacheControl = "no-cache, private";

    /// <summary>The content type <c>Results.Json</c> writes, kept identical so no reader sees a difference.</summary>
    public const string ContentType = "application/json; charset=utf-8";

    /// <summary>
    /// Serialize <paramref name="value"/> with the host's own JSON options (the ones <c>Results.Json</c> uses, so
    /// the bytes are exactly what the endpoint sent before), tag it, and answer 304 or 200.
    /// </summary>
    public static IResult Serve(HttpContext ctx, object value)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(value);
        return Serve(ctx, value, HostOptions(ctx));
    }

    /// <summary>The host's own JSON options - the ones <c>Results.Json</c> uses.</summary>
    public static JsonSerializerOptions HostOptions(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value.SerializerOptions;
    }

    /// <summary>The same, with the serializer options given explicitly.</summary>
    public static IResult Serve(HttpContext ctx, object value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        // The RUNTIME type, as Results.Json does for an object whose declared type is object.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), options);
        var tag = TagFor(bytes);

        // Both headers go on the 304 as well as the 200: the 304 must name the tag it is confirming, and the
        // browser keeps revalidating only while the cache directive says so.
        ctx.Response.Headers.ETag = tag;
        ctx.Response.Headers.CacheControl = CacheControl;

        if (Matches(ctx.Request.Headers.IfNoneMatch, tag))
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(bytes, ContentType);
    }

    /// <summary>A strong entity tag for these exact bytes: a quoted SHA-256, hex.</summary>
    public static string TagFor(ReadOnlySpan<byte> body) =>
        "\"" + Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant() + "\"";

    /// <summary>
    /// Whether an <c>If-None-Match</c> header names <paramref name="tag"/>. <c>If-None-Match</c> uses the WEAK
    /// comparison (RFC 9110 section 13.1.2), so <c>W/"x"</c> matches <c>"x"</c>; <c>*</c> matches any current
    /// answer. A header that does not parse matches nothing, which answers 200 - the safe direction.
    /// </summary>
    public static bool Matches(Microsoft.Extensions.Primitives.StringValues ifNoneMatch, string tag)
    {
        if (ifNoneMatch.Count == 0)
            return false;
        if (!EntityTagHeaderValue.TryParseList(ifNoneMatch, out var candidates))
            return false;
        var current = new EntityTagHeaderValue(tag);
        foreach (var candidate in candidates)
        {
            if (candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(current, useStrongComparison: false))
                return true;
        }
        return false;
    }
}
