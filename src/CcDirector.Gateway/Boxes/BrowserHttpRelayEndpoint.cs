using System.Runtime.CompilerServices;
using CcDirector.Core.Boxes;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Boxes;

/// <summary>
/// The browser's HTTP request, as the Gateway's end of a relay stream. The request body is read in pieces of at
/// most <see cref="DesktopRelayLimits.MaxFrameBytes"/> and sent as it is read (never held whole); the box's status,
/// headers and body are written back as they arrive, flushed piece by piece, so a large download streams.
///
/// A body over <see cref="DesktopRelayLimits.MaxRequestBodyBytes"/> ends the stream with 413. A response the box
/// did not finish is never passed off as complete: before the response has started the browser gets a 502 with the
/// reason, after it has started the connection is aborted so the browser sees a broken download, not a short one.
/// </summary>
internal sealed class BrowserHttpRelayEndpoint : IDesktopRelayEndpoint
{
    private readonly HttpContext _ctx;
    private readonly string _prefix;
    private readonly string _label;
    private readonly bool _hasBody;
    private int? _refusal;
    private int _closed;

    /// <param name="ctx">The browser's request.</param>
    /// <param name="prefix">The box's route prefix, <c>/boxes/{boxId}/desktop</c>, for keeping a redirect inside it.</param>
    /// <param name="hasBody">True when the request carries a body to relay.</param>
    /// <param name="label">Who this is, for the log.</param>
    public BrowserHttpRelayEndpoint(HttpContext ctx, string prefix, bool hasBody, string label)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _prefix = prefix;
        _hasBody = hasBody;
        _label = label;
    }

    public async IAsyncEnumerable<DesktopRelayFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!_hasBody) yield break;
        var buffer = new byte[DesktopRelayLimits.MaxFrameBytes];
        long total = 0;
        while (true)
        {
            var read = await _ctx.Request.Body.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                yield return new DesktopRelayFrame { Kind = DesktopRelayFrameKind.Data, Data = Array.Empty<byte>(), EndOfMessage = true };
                yield break;
            }
            total += read;
            if (total > DesktopRelayLimits.MaxRequestBodyBytes)
            {
                _refusal = StatusCodes.Status413PayloadTooLarge;
                yield return new DesktopRelayFrame
                {
                    Kind = DesktopRelayFrameKind.Closed,
                    CloseStatus = DesktopRelayEndpointClose.MessageTooBig,
                    Reason = $"the request body is over {DesktopRelayLimits.MaxRequestBodyBytes} bytes, the most one request may carry",
                };
                yield break;
            }
            yield return new DesktopRelayFrame { Kind = DesktopRelayFrameKind.Data, Data = buffer.AsSpan(0, read).ToArray() };
        }
    }

    public async Task DeliverAsync(DesktopRelayFrame frame, CancellationToken ct)
    {
        switch (frame.Kind)
        {
            case DesktopRelayFrameKind.Head:
                if (_ctx.Response.HasStarted)
                    throw new InvalidOperationException("the box sent a second response head");
                _ctx.Response.StatusCode = frame.Status;
                BoxDesktopHeaders.CopyResponseHeaders(frame.Headers, _ctx.Response, _prefix);
                break;
            case DesktopRelayFrameKind.Data:
                var data = frame.Data ?? Array.Empty<byte>();
                if (data.Length == 0) return;
                await _ctx.Response.Body.WriteAsync(data, ct).ConfigureAwait(false);
                await _ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"a browser request takes Head and Data frames, not {frame.Kind}");
        }
    }

    public async Task CloseAsync(int closeStatus, string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        if (_refusal is null && string.Equals(reason, DesktopRelayEndpointClose.EndOfResponse, StringComparison.Ordinal))
        {
            await _ctx.Response.CompleteAsync().ConfigureAwait(false);
            return;
        }
        FileLog.Write($"[BrowserHttpRelayEndpoint] {_label}: the response did not complete: {reason}");
        if (!_ctx.Response.HasStarted)
        {
            _ctx.Response.StatusCode = _refusal ?? StatusCodes.Status502BadGateway;
            await _ctx.Response.WriteAsJsonAsync(new { error = reason }).ConfigureAwait(false);
            return;
        }
        _ctx.Abort();
    }
}
