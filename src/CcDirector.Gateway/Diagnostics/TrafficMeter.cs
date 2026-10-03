using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CcDirector.Gateway.Diagnostics;

/// <summary>
/// Bytes OUT per route, per kind of caller, per UTC hour - the measurement the money-saver work needs before it
/// changes anything. Azure reports only the site's total (about 110 to 140 MB an hour, all night, when nobody is
/// looking at anything), App Service request logging is off, and the Gateway's own access log records the path
/// and status of a request but not its size. So the question "what sends the overnight traffic" had no instrument.
///
/// What is counted, and where:
/// <list type="bullet">
/// <item>Response body bytes AFTER compression: the middleware sits OUTSIDE <see cref="GatewayResponseCompression"/>,
/// so the compressor writes its output into the counting stream and the count is what goes on the wire.</item>
/// <item>WebSocket bytes, live: the upgrade feature is wrapped, so every frame the SignalR hubs and the terminal
/// stream proxy send is counted in the hour it is SENT, not when the socket finally closes. A socket that stays
/// open all night therefore shows up in the night's hours, which is exactly the question.</item>
/// <item>Response header bytes, ESTIMATED as status line plus name, value and separators, because a 304 has no
/// body and ten thousand polls an hour would otherwise count as nothing.</item>
/// </list>
/// What is NOT counted, so the total reads below Azure's BytesSent: the readiness gate's 503 answers and the
/// exception boundary's 500 bodies (both sit above the meter), chunked-encoding framing, and TLS.
///
/// What a row can contain, and why it is safe to serve: the route is the endpoint's route TEMPLATE
/// (<c>GET /sessions/{sid}/history</c>), never the concrete path, so no identifier is kept. A response with no
/// endpoint (the Cockpit and phone shells, static assets, a refusal before routing) is keyed by its first path
/// segment ONLY when that segment begins a mapped route or is a known shell prefix; anything else, and any
/// unknown method, is "other", and a known segment is printed in the Gateway's spelling, not the request's. A caller
/// cannot write its own text into the meter, and rows per hour are capped per partition, so a stream of invented
/// or re-cased paths can neither grow the meter nor crowd a tenant's real routes out of it. The caller is one of a closed set
/// of credential kinds. Each row also carries the tenant that authenticated it, and a reader is shown only its
/// own tenant's rows and the UNAUTHENTICATED rows - an authenticated request that resolves to no tenant (the machine
/// token on hosted) is shown to nobody - so on the hosted
/// Gateway one account cannot read the shape of another's activity.
///
/// MEASUREMENT ONLY: nothing here changes a response. Interlocked adds per write and no FileLog line per request,
/// for the same reason as <see cref="LoadTestMetrics"/>. Hours older than <see cref="RetainedHours"/> are dropped.
/// Read it at <c>GET /diag/traffic</c>. The counts live in memory, so a deploy or restart starts them again.
/// </summary>
public sealed class TrafficMeter
{
    /// <summary>How many UTC hours are kept; two days covers one full night and the day either side.</summary>
    public const int RetainedHours = 48;

    /// <summary>Distinct rows one hour may hold PER PARTITION (each tenant, and the unauthenticated callers) before
    /// further new keys of that partition fold into one overflow row.</summary>
    public const int MaxRowsPerHour = 400;

    /// <summary>The route of everything the meter will not name: an unknown path, an unknown method.</summary>
    public const string OtherRoute = "other";

    /// <summary>The caller kind of a request no credential authenticated.</summary>
    public const string UnauthenticatedCaller = "unauthenticated";

    /// <summary>The route new keys fold into once a partition holds <see cref="MaxRowsPerHour"/> rows.</summary>
    public const string OverflowRoute = "other (row cap reached)";

    private static readonly HashSet<string> KnownMethods = new(StringComparer.Ordinal)
    {
        "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS",
    };

    /// <summary>First segments served without an endpoint: the Cockpit and phone shells and their assets.</summary>
    private static readonly string[] ShellPrefixes = { "assets", "c", "mobile", "cockpit", "signin", "device-callback", "favicon.ico", "r" };

    private static readonly HashSet<string> KnownDeviceTypes = new(StringComparer.Ordinal)
    {
        DeviceRegistry.DefaultDeviceType,
        GatewayDeviceRegistrationService.GatewayDeviceType,
        MobileDeviceEnrollmentService.PhoneDeviceType,
        MobileDeviceEnrollmentService.BrowserDeviceType,
    };

    /// <summary>The one meter the Gateway's pipeline feeds and <c>GET /diag/traffic</c> reads.</summary>
    public static readonly TrafficMeter Shared = new(() => DateTime.UtcNow);

    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<DateTime, HourRows> _hours = new();
    private readonly DateTime _startedUtc;
    private Dictionary<string, string>? _knownSegments;

    public TrafficMeter(Func<DateTime> utcNow)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _startedUtc = utcNow();
    }

    /// <summary>Add the counting middleware. Must be placed BEFORE response compression and the WebSocket
    /// middleware, so it sees compressed bytes and can wrap the upgrade. <paramref name="tenantOf"/> names the
    /// tenant that authenticated a request, or null when none did.</summary>
    public static void Use(IApplicationBuilder app, Func<HttpContext, TenantId?> tenantOf)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(tenantOf);
        app.Use((ctx, next) => Shared.InvokeAsync(ctx, next, tenantOf));
        FileLog.Write("[TrafficMeter] counting bytes out per route, caller and hour; read at GET /diag/traffic");
    }

    /// <summary>Run one request through the meter.</summary>
    public async Task InvokeAsync(HttpContext ctx, Func<Task> next, Func<HttpContext, TenantId?> tenantOf)
    {
        var request = new RequestCount(this, ctx, tenantOf);

        var body = ctx.Features.Get<IHttpResponseBodyFeature>()
            ?? throw new InvalidOperationException("The request has no response body feature to count.");
        var counted = new CountingBodyFeature(new CountingStream(body.Stream, request.BodyWritten), body);
        ctx.Features.Set<IHttpResponseBodyFeature>(counted);

        var upgrade = ctx.Features.Get<IHttpUpgradeFeature>();
        if (upgrade is not null)
            ctx.Features.Set<IHttpUpgradeFeature>(new CountingUpgradeFeature(upgrade, request));

        var connect = ctx.Features.Get<IHttpExtendedConnectFeature>();
        if (connect is not null)
            ctx.Features.Set<IHttpExtendedConnectFeature>(new CountingExtendedConnectFeature(connect, request));

        try
        {
            await next();
            // Bytes a handler advanced into the counted writer and never flushed would otherwise be lost: the
            // server flushes ITS writer at the end of the request, and it does not know this one exists.
            if (!request.Upgraded && !ctx.RequestAborted.IsCancellationRequested)
                await counted.FlushUnflushedWriterAsync();
        }
        finally
        {
            ctx.Features.Set(body);
            if (upgrade is not null) ctx.Features.Set(upgrade);
            if (connect is not null) ctx.Features.Set(connect);
            request.Finish();
        }
    }

    /// <summary>The rows <paramref name="viewer"/> may see - its own and the unattributed ones - oldest hour
    /// first, each hour's rows largest first.</summary>
    public TrafficSnapshot Snapshot(TenantId viewer)
    {
        Prune();
        var hours = _hours
            .OrderBy(h => h.Key)
            .Select(h =>
            {
                var rows = h.Value.Rows
                    .Where(r => r.Key.Tenant == viewer || (r.Key.Tenant is null && r.Key.Caller == UnauthenticatedCaller))
                    .Select(r => r.Value.ToRow(r.Key))
                    .OrderByDescending(r => r.Bytes)
                    .ToList();
                return new TrafficHour
                {
                    HourUtc = h.Key,
                    Bytes = rows.Sum(r => r.Bytes),
                    Requests = rows.Sum(r => r.Requests),
                    Rows = rows,
                };
            })
            .ToList();
        return new TrafficSnapshot { StartedUtc = _startedUtc, ServerTimeUtc = _utcNow(), Hours = hours };
    }

    /// <summary>How many hours are held right now, without the prune a snapshot performs.</summary>
    internal int HeldHourCount => _hours.Count;

    private static DateTime HourOf(DateTime utc) => new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);

    private Tally TallyFor(DateTime hour, RowKey key)
    {
        if (!_hours.TryGetValue(hour, out var rows))
        {
            rows = _hours.GetOrAdd(hour, static _ => new HourRows());
            // A new hour began: drop the ones past retention, so an unread meter does not grow without end.
            Prune();
        }
        return rows.TallyFor(key);
    }

    /// <summary>One hour's rows. The cap is counted PER PARTITION - each tenant, and the unauthenticated callers,
    /// have their own - so anonymous requests can fill only their own share and never fold a tenant's real routes
    /// into the overflow row. The count is an interlocked counter, so a request past the cap takes no lock.</summary>
    private sealed class HourRows
    {
        public readonly ConcurrentDictionary<RowKey, Tally> Rows = new();
        private readonly ConcurrentDictionary<string, StrongBox<int>> _rowsPerPartition = new();

        public Tally TallyFor(RowKey key)
        {
            if (Rows.TryGetValue(key, out var tally))
                return tally;

            var partition = key.Caller == UnauthenticatedCaller ? UnauthenticatedCaller : $"tenant:{key.Tenant?.Value}";
            var count = _rowsPerPartition.GetOrAdd(partition, static _ => new StrongBox<int>());
            if (Interlocked.Increment(ref count.Value) > MaxRowsPerHour)
            {
                Interlocked.Decrement(ref count.Value);
                key = key with { Route = OverflowRoute };
                return Rows.GetOrAdd(key, static _ => new Tally());
            }

            var added = new Tally();
            var held = Rows.GetOrAdd(key, added);
            // Another request added the same key first, so this one did not use its slot: give it back.
            if (!ReferenceEquals(held, added))
                Interlocked.Decrement(ref count.Value);
            return held;
        }
    }

    private void Prune()
    {
        var oldest = HourOf(_utcNow()).AddHours(-(RetainedHours - 1));
        foreach (var hour in _hours.Keys.Where(h => h < oldest).ToList())
            _hours.TryRemove(hour, out _);
    }

    /// <summary>The route part of the key: the endpoint's method and template, or - with no endpoint - the first
    /// path segment when it is one the Gateway serves, else <see cref="OtherRoute"/>.</summary>
    internal string RouteOf(HttpContext ctx)
    {
        var method = ctx.Request.Method;
        if (!KnownMethods.Contains(method))
            return OtherRoute;

        if (ctx.GetEndpoint() is RouteEndpoint endpoint)
            return $"{method} /{endpoint.RoutePattern.RawText?.TrimStart('/')}";

        var path = ctx.Request.Path.Value ?? "";
        var segment = path.TrimStart('/').Split('/', 2)[0];
        if (segment.Length == 0)
            return $"{method} /";
        // Named in the Gateway's own spelling, never the request's: /SESSIONS and /sessions are one row.
        return KnownSegments(ctx).TryGetValue(segment, out var canonical)
            ? $"{method} /{canonical}/* (no endpoint)"
            : OtherRoute;
    }

    /// <summary>The first segments of every mapped route, plus the shell prefixes. Read once, on the first request
    /// that needs it, from the endpoints the application mapped.</summary>
    private Dictionary<string, string> KnownSegments(HttpContext ctx)
    {
        if (_knownSegments is { } known)
            return known;

        var segments = ShellPrefixes.ToDictionary(p => p, p => p, StringComparer.OrdinalIgnoreCase);
        var dataSource = ctx.RequestServices.GetRequiredService<EndpointDataSource>();
        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var first = endpoint.RoutePattern.PathSegments.FirstOrDefault();
            if (first is not null && first.IsSimple && first.Parts[0] is Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart literal)
                segments.TryAdd(literal.Content, literal.Content);
        }
        _knownSegments = segments;
        return segments;
    }

    /// <summary>The caller part of the key: which kind of credential authenticated the request.</summary>
    internal static string CallerOf(HttpContext ctx)
    {
        if (ctx.Items.ContainsKey(AuthMiddleware.AuthenticatedSessionItemKey)) return "session-key";
        if (ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var type) && type is string deviceType)
            return KnownDeviceTypes.Contains(deviceType) ? $"device:{deviceType}" : "device:other";
        if (ctx.Items.ContainsKey(AuthMiddleware.AuthenticatedCredentialItemKey)) return "machine-token";
        return UnauthenticatedCaller;
    }

    internal static long HeaderBytesOf(HttpResponse response)
    {
        // "HTTP/1.1 200 OK\r\n" and the blank line that ends the headers.
        long bytes = 17 + 2;
        foreach (var header in response.Headers)
            foreach (var value in header.Value)
                bytes += header.Key.Length + 2 + (value?.Length ?? 0) + 2;
        return bytes;
    }

    private readonly record struct RowKey(string Route, string Caller, TenantId? Tenant);

    /// <summary>One request's share of the meter. The key is resolved at the first write and kept: by the first
    /// body write the endpoint and the authenticated caller are known. A request that writes nothing resolves it
    /// when it finishes. The tally is re-looked-up only when the hour changes, so a socket frame costs a clock
    /// read and two adds.</summary>
    private sealed class RequestCount
    {
        private readonly TrafficMeter _meter;
        private readonly HttpContext _ctx;
        private readonly Func<HttpContext, TenantId?> _tenantOf;
        private RowKey? _key;
        private DateTime _hour;
        private Tally? _tally;

        public RequestCount(TrafficMeter meter, HttpContext ctx, Func<HttpContext, TenantId?> tenantOf)
        {
            _meter = meter;
            _ctx = ctx;
            _tenantOf = tenantOf;
        }

        public bool Upgraded { get; set; }

        private Tally Current()
        {
            var key = _key ??= new RowKey(_meter.RouteOf(_ctx), CallerOf(_ctx), _tenantOf(_ctx));
            var hour = HourOf(_meter._utcNow());
            var tally = _tally;
            if (tally is null || hour != _hour)
            {
                tally = _meter.TallyFor(hour, key);
                _tally = tally;
                _hour = hour;
            }
            return tally;
        }

        public void BodyWritten(int count)
        {
            if (count > 0) Interlocked.Add(ref Current().BodyBytes, count);
        }

        public void SocketWritten(int count)
        {
            if (count > 0) Interlocked.Add(ref Current().SocketBytes, count);
        }

        public void Finish()
        {
            var tally = Current();
            Interlocked.Increment(ref tally.Requests);
            Interlocked.Add(ref tally.HeaderBytes, HeaderBytesOf(_ctx.Response));
            if (_ctx.Response.StatusCode == StatusCodes.Status304NotModified)
                Interlocked.Increment(ref tally.NotModified);
        }
    }

    private sealed class Tally
    {
        public long Requests;
        public long NotModified;
        public long HeaderBytes;
        public long BodyBytes;
        public long SocketBytes;

        public TrafficRow ToRow(RowKey key)
        {
            var header = Interlocked.Read(ref HeaderBytes);
            var body = Interlocked.Read(ref BodyBytes);
            var socket = Interlocked.Read(ref SocketBytes);
            return new TrafficRow
            {
                Route = key.Route,
                Caller = key.Caller,
                Requests = Interlocked.Read(ref Requests),
                NotModified = Interlocked.Read(ref NotModified),
                HeaderBytes = header,
                BodyBytes = body,
                SocketBytes = socket,
                Bytes = header + body + socket,
            };
        }
    }

    /// <summary>The counted body, which remembers whether anyone asked for its writer so the meter can flush it.
    /// Composition, not inheritance: <see cref="StreamResponseBodyFeature.Writer"/> is not virtual.</summary>
    private sealed class CountingBodyFeature : IHttpResponseBodyFeature
    {
        private readonly StreamResponseBodyFeature _inner;
        private bool _writerCreated;

        public CountingBodyFeature(Stream stream, IHttpResponseBodyFeature prior)
        {
            _inner = new StreamResponseBodyFeature(stream, prior);
        }

        public Stream Stream => _inner.Stream;

        public PipeWriter Writer
        {
            get
            {
                _writerCreated = true;
                return _inner.Writer;
            }
        }

        public void DisableBuffering() => _inner.DisableBuffering();
        public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
            => _inner.SendFileAsync(path, offset, count, cancellationToken);
        public Task CompleteAsync() => _inner.CompleteAsync();

        /// <summary>Flush only when a handler left bytes in the writer: a request that flushed its own (or never
        /// used the writer) is not touched, so a client that has already gone produces no write at all.</summary>
        public async Task FlushUnflushedWriterAsync()
        {
            if (_writerCreated && _inner.Writer.CanGetUnflushedBytes && _inner.Writer.UnflushedBytes > 0)
                await _inner.Writer.FlushAsync(CancellationToken.None);
        }
    }

    /// <summary>A write-through stream that reports every byte written to it.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<int> _written;

        public CountingStream(Stream inner, Action<int> written)
        {
            _inner = inner;
            _written = written;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            _written(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            _written(buffer.Length);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
            _written(count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken);
            _written(buffer.Length);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class CountingUpgradeFeature : IHttpUpgradeFeature
    {
        private readonly IHttpUpgradeFeature _inner;
        private readonly RequestCount _request;

        public CountingUpgradeFeature(IHttpUpgradeFeature inner, RequestCount request)
        {
            _inner = inner;
            _request = request;
        }

        public bool IsUpgradableRequest => _inner.IsUpgradableRequest;

        public async Task<Stream> UpgradeAsync()
        {
            var stream = await _inner.UpgradeAsync();
            _request.Upgraded = true;
            return new CountingStream(stream, _request.SocketWritten);
        }
    }

    private sealed class CountingExtendedConnectFeature : IHttpExtendedConnectFeature
    {
        private readonly IHttpExtendedConnectFeature _inner;
        private readonly RequestCount _request;

        public CountingExtendedConnectFeature(IHttpExtendedConnectFeature inner, RequestCount request)
        {
            _inner = inner;
            _request = request;
        }

        public bool IsExtendedConnect => _inner.IsExtendedConnect;
        public string? Protocol => _inner.Protocol;

        public async ValueTask<Stream> AcceptAsync()
        {
            var stream = await _inner.AcceptAsync();
            _request.Upgraded = true;
            return new CountingStream(stream, _request.SocketWritten);
        }
    }
}

/// <summary>The answer of <c>GET /diag/traffic</c>.</summary>
public sealed class TrafficSnapshot
{
    /// <summary>When this process began counting; hours before it are not missing traffic, they were not measured.</summary>
    public DateTime StartedUtc { get; init; }
    public DateTime ServerTimeUtc { get; init; }
    public List<TrafficHour> Hours { get; init; } = new();
}

public sealed class TrafficHour
{
    public DateTime HourUtc { get; init; }
    public long Bytes { get; init; }
    public long Requests { get; init; }
    public List<TrafficRow> Rows { get; init; } = new();
}

public sealed class TrafficRow
{
    public string Route { get; init; } = "";
    public string Caller { get; init; } = "";
    public long Requests { get; init; }
    public long NotModified { get; init; }
    public long HeaderBytes { get; init; }
    public long BodyBytes { get; init; }
    public long SocketBytes { get; init; }
    public long Bytes { get; init; }
}
