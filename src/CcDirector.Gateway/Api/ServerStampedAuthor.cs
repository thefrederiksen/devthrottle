using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// WHO MADE A CHANGE, WHEN THE SERVER KNOWS (devthrottle_internal#2304, review findings F1 and F3). The skill and
/// workflow routes record an author on every new version and on every switch, from the request body's
/// <c>authoredBy</c> or the <c>?by=</c> query value - whatever the client typed. Inside a team that record is the only
/// answer to "who changed this shared skill", so a route that has identified the member making the request stamps the
/// author here first, and the client's claim is ignored. A route that stamps nothing keeps today's behaviour exactly.
///
/// The stamp is an opaque member reference (<see cref="TeamLibraryEndpoints.MemberReference"/>), never an email or an
/// account subject: the stores write the author into the Gateway log on every create, clone and switch.
/// </summary>
internal static class ServerStampedAuthor
{
    /// <summary>The <see cref="HttpContext.Items"/> key the stamp lives under.</summary>
    public const string ItemKey = "cc.library.server-stamped-author";

    /// <summary>Stamp the author for the rest of this request.</summary>
    public static void Set(HttpContext ctx, string author)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        ctx.Items[ItemKey] = author;
    }

    /// <summary>The author to record: the server's stamp when there is one, otherwise what the client claimed.</summary>
    public static string? Resolve(HttpContext ctx, string? claimed)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.Items.TryGetValue(ItemKey, out var stamped) && stamped is string author ? author : claimed;
    }
}
