using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace CcDirector.Core.Utilities;

/// <summary>
/// The last answer the Gateway sent for a polled read, held in memory so the next poll can ask
/// "has it changed?" instead of downloading it again (Money Saver, night traffic).
///
/// The Director's refresh cycle reads the skill register, the workflow catalog and the injected text
/// once a minute. Those change only when someone publishes a skill or edits the text, yet every read
/// was a full answer: 720 downloads an hour for one account's three Directors, about 6 MB an hour all
/// night (measured on the Gateway's traffic meter, 4 October 2026). The Gateway now tags those answers
/// with an <c>ETag</c> (a hash of the exact bytes) and answers <c>304 Not Modified</c>, with no body,
/// to a request that names the tag it already holds.
///
/// Why the held body is safe to use on a 304: a 304 says the caller's copy is byte-identical to what
/// the Gateway would send it now, so the caller parses the same bytes it would have downloaded. The
/// rest of each caller's code path runs exactly as before.
///
/// Why one held copy per address is safe across accounts: the answers are keyed by address only, so a
/// Director that switches account asks with the tag it got under the old one. That is safe ONLY because
/// every route read through this class tags its answer with a hash of the exact bytes (Gateway
/// <c>ConditionalJson</c>): the Gateway says 304 only when what it would send THIS caller is byte-identical
/// to the held copy. A route that tagged by anything else - a version number, a timestamp - must not be
/// read through this class.
///
/// Why memory and not disk: a new Director version may read the same answer differently, and it
/// arrives by restarting. Holding the answer for the life of the process means the first read after
/// every start is a full download, so nothing rendered by an older version is ever kept.
///
/// A Gateway that sends no tag (one older than this change) is never asked, so it keeps answering in
/// full, exactly as before.
/// </summary>
public sealed class HeldGatewayAnswers
{
    /// <summary>The one instance the Director's refresh cycle shares across its short-lived readers.</summary>
    public static HeldGatewayAnswers Shared { get; } = new();

    private readonly ConcurrentDictionary<string, HeldAnswer> _held = new(StringComparer.Ordinal);

    /// <summary>A held answer: the tag the Gateway gave it, its bytes, and its media type.</summary>
    private sealed record HeldAnswer(string ETag, byte[] Body, string? MediaType);

    /// <summary>How many answers are held. For tests and logging.</summary>
    public int Count => _held.Count;

    /// <summary>
    /// Send <paramref name="request"/>, naming the held tag for its address when there is one. A 304 is
    /// answered from the held bytes as a 200; a 200 replaces what is held (or drops it, when the Gateway
    /// sent no tag). Any other status is returned as it came, with an empty body, and leaves what is
    /// held alone.
    /// </summary>
    public async Task<GatewayAnswer> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        var key = request.RequestUri?.AbsoluteUri
            ?? throw new InvalidOperationException("A held Gateway read needs an absolute request address");

        _held.TryGetValue(key, out var held);
        if (held is not null)
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(held.ETag));

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (held is null)
                throw new InvalidOperationException(
                    $"The Gateway answered 304 Not Modified to {key}, which named no tag - there is no copy to reuse");
            FileLog.Write($"[HeldGatewayAnswers] SendAsync: {key} -> 304, reusing the held {held.Body.Length} bytes");
            return new GatewayAnswer(HttpStatusCode.OK, held.Body, held.MediaType, FromHeld: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            FileLog.Write($"[HeldGatewayAnswers] SendAsync: {key} -> HTTP {(int)response.StatusCode}");
            return new GatewayAnswer(response.StatusCode, Array.Empty<byte>(), null, FromHeld: false);
        }

        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var tag = response.Headers.ETag;
        if (tag is not null && !tag.IsWeak)
            _held[key] = new HeldAnswer(tag.ToString(), body, mediaType);
        else
            _held.TryRemove(key, out _);
        return new GatewayAnswer(response.StatusCode, body, mediaType, FromHeld: false);
    }
}

/// <summary>
/// One answer from <see cref="HeldGatewayAnswers.SendAsync"/>. <see cref="FromHeld"/> is true when the
/// Gateway said "not changed" and <see cref="Body"/> is the copy already held.
/// </summary>
public sealed record GatewayAnswer(HttpStatusCode Status, byte[] Body, string? MediaType, bool FromHeld)
{
    /// <summary>Whether the read succeeded (a 304 has already become a 200 here).</summary>
    public bool IsSuccess => (int)Status is >= 200 and <= 299;

    /// <summary>Throw, naming the address and status, unless the read succeeded - the same contract
    /// <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> gave the callers before.</summary>
    public void EnsureSuccess(string endpoint)
    {
        if (!IsSuccess)
            throw new HttpRequestException(
                $"GET {endpoint} answered HTTP {(int)Status} ({Status})", null, Status);
    }
}
