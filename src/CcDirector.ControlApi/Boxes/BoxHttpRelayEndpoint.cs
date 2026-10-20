using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using CcDirector.Core.Boxes;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Boxes;

/// <summary>
/// One HTTP request to a box's desktop, as the Director's end of a relay stream. The request starts as soon as the
/// stream does; its body arrives from the Gateway as Data frames and is streamed into the request as it comes (a
/// 64-megabyte upload is never held whole). The response comes back as a Head frame, then the body in Data frames of
/// at most <see cref="DesktopRelayLimits.MaxFrameBytes"/>, then a Closed frame with
/// <see cref="DesktopRelayEndpointClose.EndOfResponse"/>.
///
/// Set-Cookie is not carried back: the desktop is served on the Gateway's own origin, so a cookie the box set
/// would land on the Gateway's site and could overwrite the Gateway's own sign-in cookie. Selkies sets none (the
/// spike, phase1-spike-path-prefix.md).
/// </summary>
internal sealed class BoxHttpRelayEndpoint : IDesktopRelayEndpoint, IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpMethod _method;
    private readonly Uri _address;
    private readonly IReadOnlyList<DesktopRelayHeader> _headers;
    private readonly string _label;
    private readonly Pipe? _body;
    private readonly CancellationTokenSource _abort = new();
    private HttpResponseMessage? _response;
    private long _bodyBytes;
    private int _closed;

    public BoxHttpRelayEndpoint(HttpClient http, string method, Uri address, IReadOnlyList<DesktopRelayHeader> headers, bool hasBody, string label)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _method = new HttpMethod(method);
        _address = address ?? throw new ArgumentNullException(nameof(address));
        _headers = headers ?? Array.Empty<DesktopRelayHeader>();
        _label = label;
        _body = hasBody ? new Pipe() : null;
    }

    public async IAsyncEnumerable<DesktopRelayFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _abort.Token);
        var (response, failure) = await SendOnceAsync(linked.Token).ConfigureAwait(false);
        if (response is null)
        {
            yield return Closed(DesktopRelayEndpointClose.InternalError, $"the box's desktop did not answer: {failure}");
            yield break;
        }
        _response = response;
        FileLog.Write($"[BoxHttpRelayEndpoint] {_label}: {_method} {_address.PathAndQuery} -> {(int)response.StatusCode}");
        yield return new DesktopRelayFrame
        {
            Kind = DesktopRelayFrameKind.Head,
            Status = (int)response.StatusCode,
            Headers = ResponseHeaders(response),
        };

        var (body, openFailure) = await OpenBodyAsync(response, linked.Token).ConfigureAwait(false);
        if (body is null)
        {
            yield return Closed(DesktopRelayEndpointClose.InternalError, $"the box's response broke off: {openFailure}");
            yield break;
        }
        var buffer = new byte[DesktopRelayLimits.MaxFrameBytes];
        while (true)
        {
            var (read, readFailure) = await ReadOnceAsync(body, buffer, linked.Token).ConfigureAwait(false);
            if (readFailure is not null)
            {
                yield return Closed(DesktopRelayEndpointClose.InternalError, $"the box's response broke off: {readFailure}");
                yield break;
            }
            if (read == 0)
            {
                yield return Closed(DesktopRelayEndpointClose.Normal, DesktopRelayEndpointClose.EndOfResponse);
                yield break;
            }
            yield return new DesktopRelayFrame { Kind = DesktopRelayFrameKind.Data, Data = buffer.AsSpan(0, read).ToArray() };
        }
    }

    public async Task DeliverAsync(DesktopRelayFrame frame, CancellationToken ct)
    {
        if (frame.Kind != DesktopRelayFrameKind.Data)
            throw new InvalidOperationException($"a box request takes only Data frames from the Gateway, not {frame.Kind}");
        if (_body is null)
            throw new InvalidOperationException("the Gateway sent body data for a request it opened without a body");
        var data = frame.Data ?? Array.Empty<byte>();
        _bodyBytes += data.Length;
        if (_bodyBytes > DesktopRelayLimits.MaxRequestBodyBytes)
            throw new InvalidOperationException($"the request body is over {DesktopRelayLimits.MaxRequestBodyBytes} bytes");
        if (data.Length > 0)
            await _body.Writer.WriteAsync(data, ct).ConfigureAwait(false);
        if (frame.EndOfMessage)
            await _body.Writer.CompleteAsync().ConfigureAwait(false);
    }

    public Task CloseAsync(int closeStatus, string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return Task.CompletedTask;
        FileLog.Write($"[BoxHttpRelayEndpoint] {_label}: closing ({reason})");
        _abort.Cancel();
        _body?.Writer.Complete();
        _response?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _response?.Dispose();
        _abort.Dispose();
    }

    // A box that refuses or drops the connection is a normal outcome to report, not a fault of the relay; each is
    // returned as a value so the iterator above can turn it into a Closed frame with a reason.
    private async Task<(HttpResponseMessage? Response, string? Failure)> SendOnceAsync(CancellationToken ct)
    {
        var request = new HttpRequestMessage(_method, _address);
        if (_body is not null)
            request.Content = new StreamContent(_body.Reader.AsStream());
        foreach (var header in _headers)
        {
            if (DesktopRelayHeaders.IsNeverCarried(header.Name)) continue;
            if (request.Headers.TryAddWithoutValidation(header.Name, header.Value)) continue;
            request.Content?.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }
        try
        {
            return (await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    private static async Task<(Stream? Body, string? Failure)> OpenBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return (await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (null, ex.Message);
        }
    }

    private static async Task<(int Read, string? Failure)> ReadOnceAsync(Stream body, byte[] buffer, CancellationToken ct)
    {
        try
        {
            return (await body.ReadAsync(buffer, ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (0, ex.Message);
        }
    }

    private static List<DesktopRelayHeader> ResponseHeaders(HttpResponseMessage response)
    {
        var connectionTokens = DesktopRelayHeaders.ConnectionTokens(response.Headers.Connection).ToList();
        var headers = new List<DesktopRelayHeader>();
        var bytes = 0;
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (DesktopRelayHeaders.IsNeverCarried(name, connectionTokens)) continue;
            if (string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var value in values)
            {
                bytes += name.Length + value.Length;
                if (bytes > DesktopRelayLimits.MaxHeaderBytes)
                    throw new InvalidOperationException($"the box sent more than {DesktopRelayLimits.MaxHeaderBytes} bytes of response headers");
                headers.Add(new DesktopRelayHeader { Name = name, Value = value });
            }
        }
        return headers;
    }

    private static DesktopRelayFrame Closed(int status, string reason) =>
        new() { Kind = DesktopRelayFrameKind.Closed, CloseStatus = status, Reason = reason };
}
