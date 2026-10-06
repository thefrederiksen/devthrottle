using System.Net;
using System.Net.Http;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// Keeps the body of the last 401 the Gateway answered on the tunnel's own HTTP requests (devthrottle_internal#2311,
/// live proof F3). SignalR turns a refused negotiate into an <see cref="HttpRequestException"/> that carries the status
/// code but not the body - and the body is where the Gateway says WHY it refused the key (revoked, revoked because the
/// person was removed from the team, or not known at all). Sitting in SignalR's own handler chain, this reads that body
/// from the very response SignalR refused, so the Director learns why without asking the Gateway a second time.
/// </summary>
public sealed class TunnelRefusalRecorder
{
    // Long enough for any Gateway refusal body, short enough that a proxy's error page cannot fill memory.
    internal const int MaxBodyChars = 4096;

    private readonly object _lock = new();
    private string? _lastUnauthorizedBody;

    /// <summary>The body of the last 401 seen, or null when none has been.</summary>
    public string? LastUnauthorizedBody
    {
        get { lock (_lock) return _lastUnauthorizedBody; }
    }

    /// <summary>Forget the last 401, before an attempt, so a body read now belongs to this attempt alone.</summary>
    public void Clear()
    {
        lock (_lock) _lastUnauthorizedBody = null;
    }

    /// <summary>Put the recorder in front of <paramref name="inner"/>, the handler SignalR's requests go through.</summary>
    public HttpMessageHandler Wrap(HttpMessageHandler inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new RecordingHandler(this) { InnerHandler = inner };
    }

    private void Record(string body)
    {
        var kept = body.Length > MaxBodyChars ? body[..MaxBodyChars] : body;
        lock (_lock) _lastUnauthorizedBody = kept;
        FileLog.Write($"[TunnelRefusalRecorder] the Gateway answered 401 on the tunnel: {kept}");
    }

    private sealed class RecordingHandler : DelegatingHandler
    {
        private readonly TunnelRefusalRecorder _owner;

        public RecordingHandler(TunnelRefusalRecorder owner) => _owner = owner;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Buffered, so SignalR can still read the content after this does.
                await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
                _owner.Record(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }
            return response;
        }
    }
}
