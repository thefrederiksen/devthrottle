using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Traffic;

/// <summary>
/// The Gateway's own calls out of the machine - the Wingman's model calls, the dictation judge, text to speech,
/// transcription, the model catalog - go through clients made here, so they are counted in one place: one row
/// per destination HOST (never a path or a query), with the request body bytes sent and the response body bytes
/// received when the response states its length.
///
/// The meter is process-wide because those clients are static and outlive any one Gateway instance; the
/// hosted Gateway runs one instance per process. Until a Gateway sets it, calls are simply not counted.
/// </summary>
public static class OutboundTraffic
{
    private static TrafficMeter? _meter;

    /// <summary>The meter outbound calls are counted into; set once by the Gateway at start.</summary>
    public static TrafficMeter? Meter
    {
        get => Volatile.Read(ref _meter);
        set => Volatile.Write(ref _meter, value);
    }

    /// <summary>A client whose every call is counted. Use it for any call that leaves the Gateway's host.</summary>
    public static HttpClient CreateClient(TimeSpan timeout)
        => new(new CountingHandler(new SocketsHttpHandler())) { Timeout = timeout };

    /// <summary>Counts one call into <see cref="Meter"/>, or into the meter it was given (tests).</summary>
    public sealed class CountingHandler : DelegatingHandler
    {
        private readonly TrafficMeter? _fixedMeter;

        public CountingHandler(HttpMessageHandler inner, TrafficMeter? meter = null) : base(inner)
            => _fixedMeter = meter;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            Count(request, response);
            return response;
        }

        private void Count(HttpRequestMessage request, HttpResponseMessage response)
        {
            try
            {
                var meter = _fixedMeter ?? Meter;
                if (meter is null) return;
                var host = request.RequestUri?.IsAbsoluteUri == true ? request.RequestUri.Host.ToLowerInvariant() : "(relative)";
                var sent = request.Content?.Headers.ContentLength ?? 0;
                var received = response.Content.Headers.ContentLength ?? 0;
                meter.Cell(TrafficMeter.Outbound, host, TrafficMeter.NoClient, TrafficScope.Current?.Account)
                    .AddMessage(sent, received);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[OutboundTraffic] call not counted ({ex.GetType().Name}): {ex.Message}");
            }
        }
    }
}
