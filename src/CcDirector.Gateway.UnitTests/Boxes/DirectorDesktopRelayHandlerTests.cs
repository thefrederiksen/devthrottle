using System.Net.WebSockets;
using System.Text.Json;
using CcDirector.ControlApi.Boxes;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Licenses;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Boxes;

/// <summary>
/// Factory Boxes (issue #4258): the Director's half of the relay, asked DIRECTLY - no Gateway in front of it - so the
/// allow-list is proven where it lives. Whatever a Gateway sends, the Director forwards only to the port its own
/// hosted-desktop list holds for that one box id, on 127.0.0.1, and logs every refusal.
/// </summary>
[Collection(Tests.FileLogCaptureCollection.Name)]
public sealed class DirectorDesktopRelayHandlerTests : IAsyncLifetime
{
    private readonly FakeDesktop _box = new();
    private readonly FakeHostedDesktops _desktops = new();
    private readonly FakeTimeProvider _time = new();
    private RecordingConnector _connector = null!;
    private int _relayCalls;

    public async Task InitializeAsync()
    {
        await _box.StartAsync();
        _connector = new RecordingConnector(_box.Server);
        _desktops.Ports["box-one"] = 3110;
    }

    public async Task DisposeAsync() => await _box.DisposeAsync();

    private DirectorDesktopRelayHandler NewHandler() =>
        new(_desktops, _connector, (streamId, up, ct) =>
        {
            Interlocked.Increment(ref _relayCalls);
            return Hold(ct);
        }, _time);

    // A tunnel that never answers until the stream is cancelled: these tests are about the open, not the frames.
    private static async IAsyncEnumerable<DesktopRelayFrame> Hold([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
        yield break;
    }

    private static DirectorCommand Open(string boxId, DesktopRelayMode mode = DesktopRelayMode.Http, string path = "/") => new()
    {
        CommandId = Guid.NewGuid().ToString("N"),
        Verb = DesktopRelayProtocol.OpenVerb,
        PayloadJson = JsonSerializer.Serialize(new OpenDesktopStreamRequest
        {
            StreamId = Guid.NewGuid().ToString("N"),
            BoxId = boxId,
            Mode = mode,
            Method = "GET",
            PathAndQuery = path,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    [Fact]
    public async Task HandleAsync_BoxNotOnTheList_IsRefusedNotFoundAndLogged()
    {
        var handler = NewHandler();
        using var log = new CapturedLog();

        var result = await handler.HandleAsync(Open("box-two")).Within();

        Assert.False(result.Ok);
        Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        Assert.Equal("This Director does not host a desktop for that box.", result.Error);
        Assert.Contains(log.Lines(), l => l.Contains("[DirectorDesktopRelayHandler] REFUSED (NotFound): box=box-two"));
        Assert.Empty(_connector.Addresses);
        Assert.Equal(0, _relayCalls);
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("http://evil.example/x")]
    [InlineData("api/status")]
    [InlineData("/\\evil.example")]
    [InlineData("/a b")]
    public async Task HandleAsync_APathThatWouldLeaveTheBox_IsRefusedBadRequest(string path)
    {
        var handler = NewHandler();

        var result = await handler.HandleAsync(Open("box-one", path: path)).Within();

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Empty(_connector.Addresses);
        Assert.Equal(0, _relayCalls);
    }

    [Fact]
    public async Task HandleAsync_MalformedBoxId_IsRefusedBadRequest()
    {
        var handler = NewHandler();

        var result = await handler.HandleAsync(Open("../box-one")).Within();

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Equal(0, _relayCalls);
    }

    [Fact]
    public async Task HandleAsync_ListedBox_OpensOnLoopbackAtTheListedPortOnly()
    {
        var handler = NewHandler();

        var result = await handler.HandleAsync(Open("box-one", DesktopRelayMode.WebSocket, "/api/websockets")).Within();

        Assert.True(result.Ok, result.Error);
        var address = Assert.Single(_connector.Addresses);
        Assert.Equal("127.0.0.1", address.Host);
        Assert.Equal(3110, address.Port);
        Assert.Equal(1, handler.ActiveSocketCount);
        handler.CancelAll("test over");
    }

    [Fact]
    public async Task HandleAsync_BoxRefusesTheSocket_IsRefusedWithTheReason()
    {
        _connector.RefuseSockets = true;
        var handler = NewHandler();

        var result = await handler.HandleAsync(Open("box-one", DesktopRelayMode.WebSocket, "/api/websockets")).Within();

        Assert.Equal(DirectorCommandStatus.Error, result.Status);
        Assert.Contains("did not accept the connection", result.Error);
        Assert.Equal(0, handler.ActiveSocketCount);
    }

    [Fact]
    public async Task HandleAsync_MoreSocketsThanTheLimit_RefusesTheNextOne()
    {
        var handler = NewHandler();
        for (var i = 0; i < DesktopRelayLimits.MaxConcurrentDesktopSockets; i++)
            Assert.True((await handler.HandleAsync(Open("box-one", DesktopRelayMode.WebSocket, "/api/websockets")).Within()).Ok);

        var refused = await handler.HandleAsync(Open("box-one", DesktopRelayMode.WebSocket, "/api/websockets")).Within();

        Assert.Equal(DirectorCommandStatus.Conflict, refused.Status);
        Assert.Contains($"{DesktopRelayLimits.MaxConcurrentDesktopSockets} desktop connections", refused.Error);
        Assert.Equal(DesktopRelayLimits.MaxConcurrentDesktopSockets, handler.ActiveSocketCount);
        handler.CancelAll("test over");
    }

    [Fact]
    public async Task CancelAll_ClosesEveryBoxSocketAndFreesTheSlots()
    {
        var handler = NewHandler();
        Assert.True((await handler.HandleAsync(Open("box-one", DesktopRelayMode.WebSocket, "/api/websockets")).Within()).Ok);

        handler.CancelAll("the tunnel closed");
        await _box.SocketEnded.Task.Within();
        var watchdog = System.Diagnostics.Stopwatch.StartNew();
        while (handler.ActiveSocketCount != 0 && watchdog.Elapsed < Watchdog.Limit) await Task.Yield();

        Assert.Equal(0, handler.ActiveSocketCount);
    }
}

/// <summary>The one place a box address is made: always 127.0.0.1, always the listed port.</summary>
public sealed class DesktopBoxAddressTests
{
    [Theory]
    [InlineData("/", false, "http://127.0.0.1:3110/")]
    [InlineData("/api/status?x=1", false, "http://127.0.0.1:3110/api/status?x=1")]
    [InlineData("/api/websockets", true, "ws://127.0.0.1:3110/api/websockets")]
    [InlineData("/a/../../../etc/passwd", false, "http://127.0.0.1:3110/etc/passwd")]
    [InlineData("/%2F%2Fevil.example", false, "http://127.0.0.1:3110/%2F%2Fevil.example")]
    [InlineData("/@evil.example", false, "http://127.0.0.1:3110/@evil.example")]
    public void Build_AnyPath_StaysOnLoopbackAtThePort(string path, bool webSocket, string expected)
    {
        var address = DesktopBoxAddress.Build(3110, path, webSocket);

        Assert.Equal(expected, address.ToString());
        Assert.Equal("127.0.0.1", address.Host);
        Assert.Equal(3110, address.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("api")]
    [InlineData("//evil.example/")]
    [InlineData("http://evil.example/")]
    [InlineData("/\\evil")]
    [InlineData("/x\ny")]
    public void Build_APathThatNamesAnotherPlace_Throws(string path)
    {
        Assert.Throws<ArgumentException>(() => DesktopBoxAddress.Build(3110, path, webSocket: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Build_NotAPort_Throws(int port)
    {
        Assert.Throws<ArgumentException>(() => DesktopBoxAddress.Build(port, "/", webSocket: false));
    }
}
