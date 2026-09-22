using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Traffic;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CcDirector.Gateway.Tests.Traffic;

/// <summary>
/// Traffic optimization, phase 3: the traffic counters. The store (hour buckets, the 48-hour window, account
/// isolation), the client classification, the route names that must never carry an id, and the counting
/// pieces that sit in the pipeline - which must count exactly what left and must never buffer a stream.
///
/// Revert-proof: move the counting body inside the compressor (count before compression) and
/// CompressedAnswer_CountsTheCompressedBytes goes red; make the counting writer buffer instead of writing
/// through and StreamingAnswer_IsNotBuffered goes red; drop the account filter in TrafficMeter.Rows and the
/// isolation tests go red.
/// </summary>
public sealed class TrafficMeterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 10, 15, 0, TimeSpan.Zero);

    // ---- the store ----------------------------------------------------------------------------------------

    [Fact]
    public void Cell_SameHourSameKey_AddsUp()
    {
        var meter = new TrafficMeter(() => T0);

        meter.Cell(TrafficMeter.Http, "GET /sessions", "cockpit", "a").AddRequest(100, 0, 50, false, null);
        meter.Cell(TrafficMeter.Http, "GET /sessions", "cockpit", "a").AddRequest(0, 0, 40, true, null);

        var row = Assert.Single(meter.Rows());
        Assert.Equal(2, row.Numbers.Count);
        Assert.Equal(100, row.Numbers.BytesOut);
        Assert.Equal(90, row.Numbers.HeaderBytesOut);
        Assert.Equal(1, row.Numbers.NotModified);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero), row.HourUtc);
    }

    [Fact]
    public void Cell_NextHour_IsANewBucket_AndTheWindowDropsHoursOlderThan48()
    {
        var now = T0;
        var meter = new TrafficMeter(() => now);
        meter.Cell(TrafficMeter.Http, "GET /sessions", "cockpit", "a").AddRequest(1, 0, 0, false, null);

        now = T0.AddHours(1);
        meter.Cell(TrafficMeter.Http, "GET /sessions", "cockpit", "a").AddRequest(2, 0, 0, false, null);
        Assert.Equal(2, meter.Rows().Count);
        Assert.Single(meter.Rows(hours: 1));

        now = T0.AddHours(48);
        meter.Cell(TrafficMeter.Http, "GET /sessions", "cockpit", "a").AddRequest(3, 0, 0, false, null);
        var rows = meter.Rows();
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.Numbers.BytesOut == 1);
    }

    [Fact]
    public void Rows_ForOneAccount_NeverContainAnotherAccountsCells()
    {
        var meter = new TrafficMeter(() => T0);
        meter.Cell(TrafficMeter.Http, "GET /sessions/{sid}/history", "phone", "account-a").AddRequest(1000, 0, 0, false, false);
        meter.Cell(TrafficMeter.Http, "GET /sessions/{sid}/history", "phone", "account-b").AddRequest(7, 0, 0, false, true);
        meter.Cell(TrafficMeter.Outbound, "api.example.test", TrafficMeter.NoClient, "account-b").AddMessage(5, 5);

        var a = meter.Rows("account-a");

        var only = Assert.Single(a);
        Assert.Equal("account-a", only.Account);
        Assert.Equal(1000, only.Numbers.BytesOut);
        Assert.Equal(2, meter.Rows("account-b").Count);
        Assert.Empty(meter.Rows("account-c"));
    }

    [Fact]
    public void AdminBuild_ForOneAccount_EveryNumberIsThatAccountsAlone()
    {
        var meter = new TrafficMeter(() => T0);
        meter.Cell(TrafficMeter.Http, "GET /sessions", "cockpit", "account-a").AddRequest(10, 0, 0, false, null);
        meter.Cell(TrafficMeter.Http, "GET /sessions", "cockpit", "account-b").AddRequest(9000, 0, 0, false, null);

        var json = JsonSerializer.SerializeToElement(AdminTrafficEndpoint.Build(meter, "account-a", 48, T0));

        var summary = Assert.Single(json.GetProperty("summary").EnumerateArray());
        Assert.Equal(10, summary.GetProperty("numbers").GetProperty("bytesOut").GetInt64());
        var hour = Assert.Single(json.GetProperty("hours").EnumerateArray());
        Assert.Equal(10, hour.GetProperty("total").GetProperty("bytesOut").GetInt64());
        Assert.DoesNotContain("account-b", json.GetRawText());
    }

    [Fact]
    public void AdminBuild_HourTotal_DoesNotAddTheHubBreakdownOnTopOfTheWebSocketBytes()
    {
        var meter = new TrafficMeter(() => T0);
        meter.Cell(TrafficMeter.WebSocket, "/director-stream", "director", "a").AddMessage(0, 500);
        meter.Cell(TrafficMeter.SignalRIn, "/director-stream PushDelta", "director", "a").AddMessage(0, 480);

        var json = JsonSerializer.SerializeToElement(AdminTrafficEndpoint.Build(meter, null, 48, T0));

        var hour = Assert.Single(json.GetProperty("hours").EnumerateArray());
        Assert.Equal(500, hour.GetProperty("total").GetProperty("bytesIn").GetInt64());
    }

    // ---- client kinds ---------------------------------------------------------------------------------------

    private static DefaultHttpContext Request(string? userAgent = null, string? deviceType = null, string? referer = null,
        string path = "/sessions", bool session = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        if (userAgent is not null) ctx.Request.Headers.UserAgent = userAgent;
        if (referer is not null) ctx.Request.Headers.Referer = referer;
        if (deviceType is not null) ctx.Items[AuthMiddleware.DeviceTypeItemKey] = deviceType;
        if (session)
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(Guid.NewGuid(), TenantId.Local, "dir");
        return ctx;
    }

    private const string DesktopChrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36";
    private const string IPhoneSafari = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";

    [Theory]
    [InlineData("python-requests/2.32.3", null, TrafficClassifier.Cli)]
    [InlineData("python-requests/2.32.3", "workstation", TrafficClassifier.Cli)]
    [InlineData("cc-devthrottle", null, TrafficClassifier.Cli)]
    [InlineData(null, "phone", TrafficClassifier.Phone)]
    [InlineData(null, "browser", TrafficClassifier.Cockpit)]
    [InlineData(null, "workstation", TrafficClassifier.Director)]
    [InlineData("Microsoft SignalR/10.0 (10.0.0; Windows NT; .NET; .NET 10.0.0)", null, TrafficClassifier.Director)]
    [InlineData(DesktopChrome, null, TrafficClassifier.Cockpit)]
    [InlineData(IPhoneSafari, null, TrafficClassifier.Phone)]
    [InlineData(null, null, TrafficClassifier.Other)]
    [InlineData("curl/8.9.1", null, TrafficClassifier.Other)]
    public void ClientKind_IsClassifiedAsDocumented(string? userAgent, string? deviceType, string expected)
        => Assert.Equal(expected, TrafficClassifier.ClientKind(Request(userAgent, deviceType)));

    [Fact]
    public void ClientKind_ASessionKey_IsTheCommandLine()
        => Assert.Equal(TrafficClassifier.Cli, TrafficClassifier.ClientKind(Request(session: true)));

    [Fact]
    public void ClientKind_ADesktopBrowserOnAMobilePage_IsThePhoneApp()
        => Assert.Equal(TrafficClassifier.Phone,
            TrafficClassifier.ClientKind(Request(DesktopChrome, referer: "https://gw.example.test/mobile/sessions/abc")));

    // ---- route names never carry an id ------------------------------------------------------------------------

    [Theory]
    [InlineData("/sessions/5b0e1c52-7f59-4c1e-9d1a-0b8b1f6d0a11/history", "/sessions/** (no route)")]
    [InlineData("/mobile/sessions/5b0e1c52", "/mobile/** (no route)")]
    [InlineData("/secret-account-123/x", "(no route)")]
    [InlineData("/", "/")]
    public void UnroutedName_KeepsOnlyAKnownFirstSegment(string path, string expected)
        => Assert.Equal(expected, TrafficClassifier.UnroutedName(new PathString(path)));

    [Theory]
    [InlineData("get", "GET")]
    [InlineData("PROPFIND", "OTHER")]
    [InlineData("x-invented-by-a-client", "OTHER")]
    public void Method_IsFoldedOntoTheStandardSet(string method, string expected)
        => Assert.Equal(expected, TrafficClassifier.Method(method));

    // ---- the request counter in a real middleware pipeline -----------------------------------------------------

    private static async Task<(DefaultHttpContext Ctx, MemoryStream Wire)> Run(
        TrafficMeter meter, RequestDelegate endpoint, bool compress = false, Action<HttpContext>? arrange = null, string? account = "account-a")
    {
        var services = new ServiceCollection().AddLogging();
        GatewayResponseCompression.AddTo(services);
        var sp = services.BuildServiceProvider();
        var app = new ApplicationBuilder(sp);
        TrafficMiddleware.UseRequestCounting(app, meter, _ => account);
        if (compress) app.UseResponseCompression();
        app.Run(endpoint);
        var pipeline = app.Build();

        var wire = new MemoryStream();
        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(wire));
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/sessions/5b0e1c52-7f59-4c1e-9d1a-0b8b1f6d0a11/history";
        ctx.Request.Headers.UserAgent = DesktopChrome;
        arrange?.Invoke(ctx);
        await pipeline(ctx);
        await ctx.Response.CompleteAsync();
        return (ctx, wire);
    }

    private static TrafficRow HttpRow(TrafficMeter meter) => Assert.Single(meter.Rows(), r => r.Kind == TrafficMeter.Http);

    [Fact]
    public async Task PlainAnswer_CountsTheBodyBytesThatLeft()
    {
        var meter = new TrafficMeter();
        var body = Encoding.UTF8.GetBytes(new string('x', 1234));

        var (_, wire) = await Run(meter, ctx => ctx.Response.Body.WriteAsync(body).AsTask());

        var row = HttpRow(meter);
        Assert.Equal(1234, wire.Length);
        Assert.Equal(1234, row.Numbers.BytesOut);
        Assert.Equal(1, row.Numbers.Count);
        Assert.Equal("account-a", row.Account);
        Assert.Equal(TrafficClassifier.Cockpit, row.Client);
        Assert.DoesNotContain("5b0e1c52", row.Name);
    }

    [Fact]
    public async Task PipeWriterAnswer_IsCountedToo()
    {
        var meter = new TrafficMeter();

        await Run(meter, async ctx =>
        {
            var span = ctx.Response.BodyWriter.GetSpan(300);
            span[..300].Fill((byte)'y');
            ctx.Response.BodyWriter.Advance(300);
            await ctx.Response.BodyWriter.WriteAsync(new byte[20]);
        });

        Assert.Equal(320, HttpRow(meter).Numbers.BytesOut);
    }

    [Fact]
    public async Task CompressedAnswer_CountsTheCompressedBytes()
    {
        var meter = new TrafficMeter();
        var json = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat("{\"role\":\"Assistant\",\"text\":\"working on it\"}", 400)) + "]");

        var (ctx, wire) = await Run(meter, async c =>
        {
            c.Response.ContentType = "application/json";
            await c.Response.Body.WriteAsync(json);
        }, compress: true, arrange: c => c.Request.Headers.AcceptEncoding = "gzip");

        Assert.Equal("gzip", ctx.Response.Headers.ContentEncoding.ToString());
        Assert.True(wire.Length < json.Length / 4, $"expected a compressed body, got {wire.Length} of {json.Length}");
        Assert.Equal(wire.Length, HttpRow(meter).Numbers.BytesOut);
    }

    [Fact]
    public async Task NotModified_IsCountedWithNoBody()
    {
        var meter = new TrafficMeter();

        await Run(meter, ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            ctx.Response.Headers.ETag = "\"abc\"";
            return Task.CompletedTask;
        });

        var row = HttpRow(meter);
        Assert.Equal(1, row.Numbers.NotModified);
        Assert.Equal(0, row.Numbers.BytesOut);
        Assert.True(row.Numbers.HeaderBytesOut > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistoryAnswer_TailOrFull_IsCounted(bool tail)
    {
        var meter = new TrafficMeter();

        await Run(meter, ctx =>
        {
            TrafficMiddleware.MarkHistoryAnswer(ctx, tail);
            return ctx.Response.WriteAsync("{}");
        });

        var row = HttpRow(meter);
        Assert.Equal(tail ? 1 : 0, row.Numbers.HistoryTail);
        Assert.Equal(tail ? 0 : 1, row.Numbers.HistoryFull);
    }

    [Fact]
    public async Task RequestBody_WithContentLength_IsCountedFromTheHeader()
    {
        var meter = new TrafficMeter();

        await Run(meter, _ => Task.CompletedTask, arrange: ctx =>
        {
            ctx.Request.Method = "POST";
            ctx.Request.ContentLength = 77;
            ctx.Request.Body = new MemoryStream(new byte[77]);
        });

        Assert.Equal(77, HttpRow(meter).Numbers.BytesIn);
    }

    [Fact]
    public async Task RequestBody_Chunked_IsCountedAsRead()
    {
        var meter = new TrafficMeter();

        await Run(meter, async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            await reader.ReadToEndAsync();
        }, arrange: ctx =>
        {
            ctx.Request.Method = "POST";
            ctx.Request.Headers.TransferEncoding = "chunked";
            ctx.Request.Body = new MemoryStream(new byte[4096]);
        });

        Assert.Equal(4096, HttpRow(meter).Numbers.BytesIn);
    }

    [Fact]
    public async Task EndpointThatThrows_StillThrows_AndIsStillCounted()
    {
        var meter = new TrafficMeter();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(meter, _ => throw new InvalidOperationException("boom")));

        Assert.Equal(1, HttpRow(meter).Numbers.Count);
    }

    [Fact]
    public async Task AccountResolverThatThrows_NeverBreaksTheResponse()
    {
        var meter = new TrafficMeter();
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        TrafficMiddleware.UseRequestCounting(app, meter, _ => throw new InvalidOperationException("no tenant context"));
        app.Run(ctx => ctx.Response.WriteAsync("ok"));
        var wire = new MemoryStream();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(wire));

        await app.Build()(ctx);

        Assert.Equal("ok", Encoding.UTF8.GetString(wire.ToArray()));
        Assert.Equal(TrafficMeter.NoAccount, HttpRow(meter).Account);
    }

    [Fact]
    public async Task StreamingAnswer_IsNotBuffered()
    {
        // The event stream writes an event, flushes, and keeps the response open. The first event must be on the
        // wire while the request is still running - a counter that buffered would hold it until the end.
        var meter = new TrafficMeter();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new MemoryStream();

        var services = new ServiceCollection().AddLogging();
        GatewayResponseCompression.AddTo(services);
        var sp = services.BuildServiceProvider();
        var app = new ApplicationBuilder(sp);
        TrafficMiddleware.UseRequestCounting(app, meter, _ => "account-a");
        app.UseResponseCompression();
        app.Run(async ctx =>
        {
            ctx.Response.ContentType = GatewayResponseCompression.EventStreamMimeType;
            await ctx.Response.WriteAsync("data: one\n\n");
            await ctx.Response.Body.FlushAsync();
            firstFlushed.SetResult();
            await release.Task;
            await ctx.Response.WriteAsync("data: two\n\n");
        });
        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(wire));
        ctx.Request.Path = "/events";
        ctx.Request.Headers.AcceptEncoding = "gzip, br";

        var running = app.Build()(ctx);
        await firstFlushed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(running.IsCompleted);
        Assert.Equal("data: one\n\n", Encoding.UTF8.GetString(wire.ToArray()));

        release.SetResult();
        await running;
        Assert.Equal(wire.Length, HttpRow(meter).Numbers.BytesOut);
    }

    // ---- WebSocket frames -------------------------------------------------------------------------------------

    [Fact]
    public async Task CountingWebSocket_CountsPayloadBothWays_AndOneMessagePerEnd()
    {
        var meter = new TrafficMeter();
        using var inner = new ScriptedWebSocket(incoming: 64);
        using var socket = new CountingWebSocket(inner, meter, "/sessions/{sid}/stream", TrafficClassifier.Cockpit, "account-a");

        await socket.SendAsync(new byte[100], WebSocketMessageType.Binary, endOfMessage: false, CancellationToken.None);
        await socket.SendAsync(new byte[50], WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        await socket.ReceiveAsync(new byte[1024], CancellationToken.None);

        var row = Assert.Single(meter.Rows());
        Assert.Equal(TrafficMeter.WebSocket, row.Kind);
        Assert.Equal("/sessions/{sid}/stream", row.Name);
        Assert.Equal(150, row.Numbers.BytesOut);
        Assert.Equal(64, row.Numbers.BytesIn);
        Assert.Equal(2, row.Numbers.Count);
        Assert.Equal(150, inner.Sent);
    }

    private sealed class ScriptedWebSocket(int incoming) : WebSocket
    {
        public long Sent { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => Task.FromResult(new WebSocketReceiveResult(incoming, WebSocketMessageType.Binary, true));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Sent += buffer.Count;
            return Task.CompletedTask;
        }
    }

    // ---- SignalR messages -------------------------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, string> Known =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PushDelta"] = "PushDelta", ["StreamUp"] = "StreamUp" };

    private static CountingHubProtocol Json(TrafficMeter meter)
        => new(new JsonHubProtocol(Options.Create(new JsonHubProtocolOptions())), meter, Known);

    private sealed class Binder : IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName) => new[] { typeof(string) };
        public Type GetReturnType(string invocationId) => typeof(object);
        public Type GetStreamItemType(string streamId) => typeof(string);
    }

    [Fact]
    public void HubMessage_WrittenAndParsed_IsCountedByMethodWithItsExactSize()
    {
        var meter = new TrafficMeter();
        var protocol = Json(meter);
        var output = new ArrayBufferWriter<byte>();

        protocol.WriteMessage(new InvocationMessage("PushDelta", new object?[] { "a row" }), output);
        var input = new ReadOnlySequence<byte>(output.WrittenMemory);
        Assert.True(protocol.TryParseMessage(ref input, new Binder(), out var parsed));

        Assert.Equal("PushDelta", ((InvocationMessage)parsed!).Target);
        var sent = Assert.Single(meter.Rows(), r => r.Kind == TrafficMeter.SignalROut);
        var received = Assert.Single(meter.Rows(), r => r.Kind == TrafficMeter.SignalRIn);
        Assert.Equal("PushDelta", sent.Name);
        Assert.Equal(output.WrittenCount, sent.Numbers.BytesOut);
        Assert.Equal(output.WrittenCount, received.Numbers.BytesIn);
        Assert.Equal(TrafficMeter.NoAccount, received.Account);
    }

    [Fact]
    public void HubMessage_InboundMethodNobodyDeclared_IsNotAName()
    {
        var meter = new TrafficMeter();
        var protocol = Json(meter);
        var output = new ArrayBufferWriter<byte>();
        new JsonHubProtocol().WriteMessage(new InvocationMessage("Invented-5b0e1c52", new object?[] { "x" }), output);

        var input = new ReadOnlySequence<byte>(output.WrittenMemory);
        protocol.TryParseMessage(ref input, new Binder(), out _);

        Assert.Equal("(unknown method)", Assert.Single(meter.Rows()).Name);
    }

    [Fact]
    public void HubMessage_InsideAHubConnection_CarriesTheConnectionsAccountHubAndClient()
    {
        var meter = new TrafficMeter();
        var protocol = Json(meter);
        var output = new ArrayBufferWriter<byte>();
        new JsonHubProtocol().WriteMessage(new InvocationMessage("pushdelta", new object?[] { "x" }), output);

        TrafficScope.Current = new TrafficScope("account-a", TrafficClassifier.Director, "/director-stream");
        try
        {
            var input = new ReadOnlySequence<byte>(output.WrittenMemory);
            protocol.TryParseMessage(ref input, new Binder(), out _);
        }
        finally
        {
            TrafficScope.Current = null;
        }

        var row = Assert.Single(meter.Rows());
        Assert.Equal("/director-stream PushDelta", row.Name);
        Assert.Equal("account-a", row.Account);
        Assert.Equal(TrafficClassifier.Director, row.Client);
    }

    [Fact]
    public void HubMethods_AreTheHubsOwnPublicMethods()
    {
        var methods = CountingHubProtocol.HubMethods(typeof(Streaming.DirectorHub));

        Assert.True(methods.ContainsKey("PushSnapshot"));
        Assert.True(methods.ContainsKey("pushdelta"));
        Assert.False(methods.ContainsKey("ToString"));
    }

    // ---- outbound calls ---------------------------------------------------------------------------------------

    [Fact]
    public async Task OutboundCall_IsCountedByHostOnly_WithBytesBothWays()
    {
        var meter = new TrafficMeter();
        using var http = new HttpClient(new OutboundTraffic.CountingHandler(new FixedAnswer(new string('z', 900)), meter));

        using var response = await http.PostAsync("https://models.example.test/v1/chat/completions?key=secret",
            new StringContent(new string('q', 250)));

        var row = Assert.Single(meter.Rows());
        Assert.Equal(TrafficMeter.Outbound, row.Kind);
        Assert.Equal("models.example.test", row.Name);
        Assert.Equal(250, row.Numbers.BytesOut);
        Assert.Equal(900, row.Numbers.BytesIn);
    }

    private sealed class FixedAnswer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
