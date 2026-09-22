using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The roster without its clock (traffic optimization, phase 2): an opt-in form of <c>GET /sessions</c> that can
/// answer an unchanged poll with a 304.
///
/// Phase 1 gave the roster an ETag, and it almost never matched. Two fields are recomputed from the Gateway's
/// clock on every read, so two reads a second apart are never byte-identical even when nothing happened:
/// <list type="bullet">
/// <item><c>sessions[].idleSeconds</c> - now minus <c>lastActivityAt</c> (<c>PushedSessionStore.RecomputeClocks</c>);</item>
/// <item><c>directors[].lastSeenAgeSeconds</c> - now minus <c>lastSeenUtc</c>.</item>
/// </list>
/// (<c>cumulativeIdleSeconds</c> is NOT one of them: it is a total the Director pushes and only changes on a push.
/// <c>directors[].lastSeenUtc</c> changes too, but on each push rather than each read - it is the arrival time
/// of the Director's latest report, an absolute fact, and stays.)
///
/// A client that sends <c>clockFields=absolute</c> gets the same answer with those two fields left out, and
/// works them out itself from the absolute timestamps the answer already carries. It needs the Gateway's "now"
/// to do that, and the choice of where that comes from is the whole question:
/// <list type="bullet">
/// <item>NOT the body. Any server time in the body changes on every read and puts the 304 straight back out of
/// reach.</item>
/// <item>NOT the client's own clock. A phone or laptop a minute out would print "last seen 1m ago" beside a
/// machine heard two seconds ago - a wrong answer that no test on this side could catch.</item>
/// <item>NOT the HTTP <c>Date</c> header. It is truncated to the second, and on a 304 served out of the
/// browser's cache what the page sees depends on how that browser merges the 304's headers into the stored
/// ones.</item>
/// <item>A header of our own, <c>X-Gateway-Time</c>, carrying the SAME instant the Gateway measured the ages
/// against, on every answer - 200 and 304 alike. Headers are not part of the ETag (it is the hash of the body),
/// so it costs the 304 nothing. The client sends its own <c>If-None-Match</c> and reads the 304 itself rather
/// than through the browser cache, so the time it reads is always the one from THIS answer. The client then
/// computes exactly what the Gateway would have computed at that instant - the same values the old answer
/// carried, with no dependence on the client's clock at all.</item>
/// </list>
/// A request without the parameter gets today's answer, unchanged, field for field.
/// </summary>
public static class RosterClockFields
{
    /// <summary>The query parameter a newer client sends.</summary>
    public const string Parameter = "clockFields";

    /// <summary>Its one recognised value. Any other value is ignored and answered in the old form.</summary>
    public const string Absolute = "absolute";

    /// <summary>The response header carrying the instant the roster's ages were measured against.</summary>
    public const string GatewayTimeHeader = "X-Gateway-Time";

    /// <summary>Whether the request asked for the roster without its clock fields.</summary>
    public static bool Requested(string? value) =>
        string.Equals(value, Absolute, StringComparison.OrdinalIgnoreCase);

    /// <summary>The header value for <paramref name="nowUtc"/>: ISO 8601, UTC, to the tick.</summary>
    public static string FormatTime(DateTime nowUtc) =>
        DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Serialize the roster answer - the plain session array or the envelope object - with the host's options
    /// and remove the clock-relative fields. <c>idleSeconds</c> is removed only where <c>lastActivityAt</c> is
    /// present, because that is the only case the Gateway recomputes it; without it the value is whatever the
    /// Director sent, is not clock-relative, and stays. <c>lastSeenAgeSeconds</c> is always removed: it is null
    /// exactly when <c>lastSeenUtc</c> is null, so the client can rebuild it in every case.
    /// </summary>
    public static JsonNode Strip(object roster, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(options);
        var node = JsonSerializer.SerializeToNode(roster, roster.GetType(), options)
                   ?? throw new InvalidOperationException("the roster serialized to null");

        var idle = Name(options, "IdleSeconds");
        var lastActivity = Name(options, "LastActivityAt");
        var age = Name(options, "LastSeenAgeSeconds");

        var envelope = node as JsonObject;
        var sessions = node as JsonArray ?? envelope?[Name(options, "Sessions")] as JsonArray;
        if (sessions is not null)
        {
            foreach (var session in sessions.OfType<JsonObject>())
            {
                if (session[lastActivity] is not null)
                    session.Remove(idle);
            }
        }
        if (envelope?[Name(options, "Directors")] is JsonArray directors)
        {
            foreach (var director in directors.OfType<JsonObject>())
                director.Remove(age);
        }
        return node;
    }

    private static string Name(JsonSerializerOptions options, string property) =>
        options.PropertyNamingPolicy?.ConvertName(property) ?? property;
}
