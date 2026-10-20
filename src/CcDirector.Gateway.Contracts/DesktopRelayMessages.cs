namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Factory Boxes (issue #4258), phase 1: the desktop relay leg. A browser opens a box's desktop at
/// <c>/boxes/{boxId}/desktop/</c> on the Gateway; the Gateway carries every HTTP request and every WebSocket
/// under that prefix down the Director's own outbound tunnel; the Director forwards it to the one
/// <c>127.0.0.1:&lt;port&gt;</c> its hosted-desktop list holds for that box id, and nowhere else.
///
/// <para><b>One relay stream per browser request or per browser WebSocket.</b> The Gateway sends the unary
/// <see cref="DesktopRelayProtocol.OpenVerb"/> command carrying an <see cref="OpenDesktopStreamRequest"/>; the
/// Director checks its allow-list and answers. On success the Director calls the hub method
/// <see cref="DesktopRelayProtocol.HubMethod"/> ONCE for that stream: the call carries the Director's frames UP
/// (a client-to-server stream) and returns the Gateway's frames DOWN (a server-to-client stream). Every
/// connection still starts at the Director; nothing connects in.</para>
///
/// <para><b>Flow control, and why it is not the terminal's.</b> The terminal stream relies on SignalR's own
/// stream buffer to push back on the producer. That buffer is per connection: when it is full the Gateway stops
/// reading the WHOLE connection, terminal frames and command replies included. One busy desktop moves 55 to 87
/// megabits a second, so the desktop leg must never let that buffer fill. Each side may have at most
/// <see cref="DesktopRelayLimits.WindowBytes"/> of frames outstanding at the other side; the receiver grants the
/// bytes back with a <see cref="DesktopRelayFrameKind.Credit"/> frame once it has delivered them to the browser
/// or the box. A receiver therefore always has room for what arrives, reads the tunnel at once, and a slow
/// browser slows only its own desktop (Selkies' 50-millisecond acknowledgements turn that into a lower frame
/// rate) - never the terminal.</para>
/// </summary>
public static class DesktopRelayProtocol
{
    /// <summary>The unary command the Gateway sends to open one relay stream. Payload: <see cref="OpenDesktopStreamRequest"/>.</summary>
    public const string OpenVerb = "open-desktop-stream";

    /// <summary>
    /// The hub method the Director calls once per opened stream:
    /// <c>IAsyncEnumerable&lt;DesktopRelayFrame&gt; DesktopRelay(string streamId, IAsyncEnumerable&lt;DesktopRelayFrame&gt; up)</c>.
    /// </summary>
    public const string HubMethod = "DesktopRelay";
}

/// <summary>What one relay stream carries: one HTTP request and its response, or one WebSocket.</summary>
public enum DesktopRelayMode
{
    /// <summary>One HTTP request: the request body goes down, the status, headers and body come up.</summary>
    Http = 0,

    /// <summary>One WebSocket: messages both ways until either side closes.</summary>
    WebSocket = 1,
}

/// <summary>The payload of <see cref="DesktopRelayProtocol.OpenVerb"/>.</summary>
public sealed class OpenDesktopStreamRequest
{
    /// <summary>The fresh id the Gateway minted for this stream. Never reused.</summary>
    public string StreamId { get; set; } = "";

    /// <summary>The box whose desktop is asked for. The Director resolves it to a local port through its own list, or refuses.</summary>
    public string BoxId { get; set; } = "";

    /// <summary>An HTTP request or a WebSocket.</summary>
    public DesktopRelayMode Mode { get; set; }

    /// <summary>The HTTP method (GET for a WebSocket).</summary>
    public string Method { get; set; } = "GET";

    /// <summary>
    /// The path under the box's prefix, with the prefix stripped, plus the query string - for example
    /// <c>/api/status?x=1</c>. Always starts with one slash. Never a host, a scheme or a port: the Director builds
    /// the address from <c>127.0.0.1</c> and the port its own list holds, and refuses anything that would leave it.
    /// </summary>
    public string PathAndQuery { get; set; } = "/";

    /// <summary>The browser's request headers that matter, with hop-by-hop headers and the Gateway's own credentials removed.</summary>
    public List<DesktopRelayHeader> Headers { get; set; } = new();

    /// <summary>For a WebSocket: the subprotocols the browser offered, in its order. Empty for none (Selkies asks for none).</summary>
    public List<string> SubProtocols { get; set; } = new();

    /// <summary>True when an HTTP request has a body to follow as Data frames, ended by a frame with <see cref="DesktopRelayFrame.EndOfMessage"/>.</summary>
    public bool HasBody { get; set; }
}

/// <summary>One header: a name and one of its values (a header with several values is several entries).</summary>
public sealed class DesktopRelayHeader
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>The kinds of frame on a relay stream. The same kinds travel both ways.</summary>
public enum DesktopRelayFrameKind
{
    /// <summary>The start of an HTTP response (up only): <see cref="DesktopRelayFrame.Status"/> and <see cref="DesktopRelayFrame.Headers"/>.</summary>
    Head = 0,

    /// <summary>
    /// A piece of a message or of a body: at most <see cref="DesktopRelayLimits.MaxFrameBytes"/> bytes. A WebSocket
    /// message larger than that is split into several Data frames; the last one has
    /// <see cref="DesktopRelayFrame.EndOfMessage"/> set, and the receiver puts the message back together before it
    /// hands it on. On an HTTP stream the frame with <see cref="DesktopRelayFrame.EndOfMessage"/> ends the body.
    /// </summary>
    Data = 1,

    /// <summary>The receiver has delivered <see cref="DesktopRelayFrame.Credit"/> bytes, so the sender may send that much more.</summary>
    Credit = 2,

    /// <summary>
    /// The stream is over. The last frame either side sends. Carries the WebSocket close status and reason, or
    /// "eof" at the natural end of an HTTP response.
    /// </summary>
    Closed = 3,
}

/// <summary>One frame of a relay stream.</summary>
public sealed class DesktopRelayFrame
{
    public DesktopRelayFrameKind Kind { get; set; }

    /// <summary>The bytes of a Data frame. Null on every other kind.</summary>
    public byte[]? Data { get; set; }

    /// <summary>On a WebSocket Data frame: true for a text message, false for binary. The type is kept end to end.</summary>
    public bool IsText { get; set; }

    /// <summary>On a Data frame: this is the last piece of the message (WebSocket) or of the body (HTTP).</summary>
    public bool EndOfMessage { get; set; }

    /// <summary>On a Head frame: the HTTP status.</summary>
    public int Status { get; set; }

    /// <summary>On a Head frame: the response headers, hop-by-hop headers removed.</summary>
    public List<DesktopRelayHeader>? Headers { get; set; }

    /// <summary>On a Credit frame: how many bytes the receiver has delivered and grants back.</summary>
    public int Credit { get; set; }

    /// <summary>On a Closed frame of a WebSocket: the close status to pass on (1000 for a normal close).</summary>
    public int CloseStatus { get; set; }

    /// <summary>On a Closed frame: why the stream ended, in words a person can read.</summary>
    public string? Reason { get; set; }
}

/// <summary>The relay leg's bounds. Each is named so a test can hold it, and each is the one place it is set.</summary>
public static class DesktopRelayLimits
{
    /// <summary>
    /// The most bytes in one Data frame. The SAME bound as every other stream frame on the tunnel
    /// (<see cref="DirectorStreamLimits.MaxBinaryFrameBytes"/>, 48 kilobytes), deliberately not raised: a desktop
    /// message of 244 kilobytes is split into frames of this size and put back together at the far end, so one
    /// desktop frame never holds the shared connection for longer than any other frame does.
    /// </summary>
    public const int MaxFrameBytes = DirectorStreamLimits.MaxBinaryFrameBytes;

    /// <summary>
    /// What each frame costs against the window on top of its data, so that a stream of tiny frames (the page's
    /// 50-millisecond acknowledgements) is bounded by count as well as by bytes.
    /// </summary>
    public const int FrameOverheadBytes = 64;

    /// <summary>
    /// How many bytes of frames one side may have outstanding at the other side of one relay stream. One megabyte
    /// carries 160 megabits a second at a 50-millisecond round trip, above the rate cap below, while bounding the
    /// memory one stream can hold at the far end to one megabyte per direction.
    /// </summary>
    public const int WindowBytes = 1024 * 1024;

    /// <summary>The largest WebSocket message either side will put back together. Selkies' largest seen is 244 kilobytes.</summary>
    public const int MaxWebSocketMessageBytes = 16 * 1024 * 1024;

    /// <summary>The largest request body relayed, per request. Selkies uploads a large file as 64-megabyte POSTs.</summary>
    public const long MaxRequestBodyBytes = 64L * 1024 * 1024;

    /// <summary>The most response header bytes one Head frame may carry, so a box cannot send an unbounded head.</summary>
    public const int MaxHeaderBytes = 64 * 1024;

    /// <summary>The most desktop WebSockets one Director relays at once, across every box it hosts.</summary>
    public const int MaxConcurrentDesktopSockets = 8;

    /// <summary>The most desktop HTTP requests one Director relays at once, across every box it hosts.</summary>
    public const int MaxConcurrentDesktopRequests = 32;

    /// <summary>
    /// How long an open desktop WebSocket may carry nothing in either direction before it is closed. At least an
    /// hour, as the brief requires: a Selkies page acknowledges every 50 milliseconds while open, so a socket this
    /// quiet has been left behind.
    /// </summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(1);

    /// <summary>
    /// The ceiling on bytes a box sends toward the browser, per box, across all its sockets and requests: one
    /// million bytes a second, which is 8 megabits a second. Every byte the box sends leaves Azure as paid
    /// outbound traffic (55 to 87 megabits a second uncapped is 25 to 40 gigabytes an hour). The Director enforces
    /// it by reading the box more slowly, which the box cannot talk its way past; Selkies turns the slower reads
    /// into a lower frame rate through its own acknowledgements.
    /// </summary>
    public const int MaxBoxBytesPerSecond = 1_000_000;

    /// <summary>How often the bytes relayed are written to the log while a socket is open (they are also written at close).</summary>
    public static readonly TimeSpan UsageLogInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long the Gateway waits for the Director to attach to a stream it opened before giving up.</summary>
    public static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// What a frame costs against the window: its data plus <see cref="FrameOverheadBytes"/>. Credit and Closed frames
    /// cost nothing. A Closed frame is the last frame of a stream and carries no data to hold; it is sent without
    /// waiting for window, so it must not be counted against the window at the far side either, or a close sent with
    /// the window full would arrive as a protocol fault instead of the close it is.
    /// </summary>
    public static int CostOf(DesktopRelayFrame frame) =>
        frame.Kind is DesktopRelayFrameKind.Credit or DesktopRelayFrameKind.Closed ? 0 : (frame.Data?.Length ?? 0) + FrameOverheadBytes;
}

/// <summary>The success body of <see cref="DesktopRelayProtocol.OpenVerb"/>.</summary>
public sealed class OpenDesktopStreamResponse
{
    /// <summary>For a WebSocket: the subprotocol the box accepted, which the Gateway accepts the browser with. Null for none.</summary>
    public string? SubProtocol { get; set; }
}
