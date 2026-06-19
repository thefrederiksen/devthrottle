namespace CcDirector.Gateway;

/// <summary>
/// Single source of truth for how the Gateway dials a Director's Control API when it proxies a
/// per-session leg (the terminal stream, dictation, screenshots, per-session verbs) and when it
/// runs the stream-leg verification probe.
///
/// Two decisions live here, both learned from the remote-streaming bug:
///
/// 1. The Gateway proxies these legs with a hand-rolled <c>HttpClient</c> / <c>ClientWebSocket</c>
///    proxy (see SessionWsForwarder), NOT YARP's IHttpForwarder. On Windows, YARP's forwarder
///    cannot complete the TLS handshake to a Tailscale Serve (https) backend - every forward fails
///    with the Schannel error "The message received was unexpected or badly formatted" - while a
///    plain HttpClient / ClientWebSocket to the identical url works. (Same-machine loopback http
///    backends worked through YARP, which is why local Directors streamed but remote ones never did.)
///
/// 2. The WebSocket upgrade uses HTTP/1.1. ClientWebSocket already defaults to 1.1; pinning it here
///    is a single source of truth so the live stream proxy and the install/connect verification
///    probe always agree, and a runtime/env default can never flip either to HTTP/2.
/// </summary>
public static class DirectorForwarding
{
    /// <summary>HTTP version for the Director-bound WebSocket upgrade (proxy + verify probe).</summary>
    public static readonly Version HttpVersion = System.Net.HttpVersion.Version11;

    /// <summary>Exact: keep the upgrade on HTTP/1.1 regardless of any negotiated default.</summary>
    public static readonly System.Net.Http.HttpVersionPolicy HttpVersionPolicy =
        System.Net.Http.HttpVersionPolicy.RequestVersionExact;
}
