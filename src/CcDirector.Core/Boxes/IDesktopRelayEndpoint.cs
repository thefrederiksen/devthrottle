using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Boxes;

/// <summary>
/// One end of a relay stream as the <see cref="DesktopRelayPump"/> sees it: the browser's request or WebSocket on
/// the Gateway, or the box's local port on the Director. The pump moves frames between this and the far side of
/// the tunnel; the endpoint turns them into the real transport's calls.
/// </summary>
public interface IDesktopRelayEndpoint
{
    /// <summary>
    /// What this end sends toward the far side, as frames: Data pieces of at most
    /// <see cref="DesktopRelayLimits.MaxFrameBytes"/>, a Head, or a Closed that ends the stream. Ending the
    /// sequence WITHOUT a Closed means only that this direction is done (an HTTP request body has been sent) and
    /// the stream stays open for the other direction.
    /// </summary>
    IAsyncEnumerable<DesktopRelayFrame> ReadAsync(CancellationToken ct);

    /// <summary>Hand one Head or Data frame from the far side to this end. Throwing ends the stream.</summary>
    Task DeliverAsync(DesktopRelayFrame frame, CancellationToken ct);

    /// <summary>
    /// End this end with a WebSocket close status (or, for HTTP, <see cref="DesktopRelayEndpointClose.EndOfResponse"/>
    /// for a normal end) and a reason a person can read. Called once by the pump; safe to call again.
    /// </summary>
    Task CloseAsync(int closeStatus, string reason);
}

/// <summary>Close statuses and reasons the relay uses on both ends.</summary>
public static class DesktopRelayEndpointClose
{
    /// <summary>The reason a Director gives on the Closed frame that ends a complete HTTP response.</summary>
    public const string EndOfResponse = "eof";

    /// <summary>WebSocket 1000: a normal close.</summary>
    public const int Normal = 1000;

    /// <summary>WebSocket 1001: the other end is going away (the Director disconnected, the socket sat idle).</summary>
    public const int GoingAway = 1001;

    /// <summary>WebSocket 1009: a message was larger than the relay will carry.</summary>
    public const int MessageTooBig = 1009;

    /// <summary>WebSocket 1011: the relay hit an error it could not carry on from.</summary>
    public const int InternalError = 1011;

    /// <summary>
    /// A close status that may be SENT in a close frame. 1005, 1006 and 1015 are reserved for "none was given",
    /// "the connection dropped" and "TLS failed" and must never go on the wire; anything outside the ranges a peer
    /// may send becomes <see cref="GoingAway"/>.
    /// </summary>
    public static int Sendable(int status) =>
        status is >= 1000 and <= 1003 or >= 1007 and <= 1014 or >= 3000 and <= 4999 ? status : GoingAway;

    /// <summary>A close reason trimmed to what a close frame can carry (123 bytes of text), never empty.</summary>
    public static string Trim(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return "closed";
        var text = reason;
        while (System.Text.Encoding.UTF8.GetByteCount(text) > 123)
            text = text[..^1];
        return text;
    }
}
