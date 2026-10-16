using System.Buffers;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Api;

/// <summary>
/// API mode, phase 2 (mission #4070): the two session connection fields, <see cref="SessionDto.ConnectionMode"/>
/// and <see cref="SessionDto.WheelHolder"/>, on the Gateway's side of the wire.
///
/// Two promises are proved here. First, a session pushed by a Director that predates the fields - so it sends
/// neither - reads as terminal mode with no wheel holder, on both of the ways a session reaches the Gateway: the
/// Director tunnel's MessagePack hub protocol and the JSON of the HTTP routes. Second, the values a Director
/// pushes reach what the clients read: pushed into the <see cref="PushedSessionStore"/>, served by the real
/// <c>GET /sessions</c> route on an in-memory test server (no port), unchanged.
/// </summary>
public sealed class SessionConnectionFieldsTests : IDisposable
{
    private const string DirectorId = "director-connection-fields";
    private const string ConnectionId = "connection-1";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-connection-fields-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _pushed = new();
    private WebApplication? _app;
    private HttpClient? _http;

    public SessionConnectionFieldsTests()
    {
        Directory.CreateDirectory(_root);
        _registry = new DirectorRegistry(Path.Combine(_root, "instances"));
    }

    public void Dispose()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _registry.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---- an older Director sends neither field ------------------------------------------------------------------

    [Fact]
    public void Deserialize_JsonFromAnOlderDirectorWithNeitherField_ReadsTerminalModeWithNoWheelHolder()
    {
        // Arrange: a session as a Director that predates API mode writes it - neither field is on the wire.
        const string olderJson = """{"sessionId":"s-old","agent":"Pi","status":"Running","backendType":"UnixPty"}""";

        // Act
        var session = JsonSerializer.Deserialize<SessionDto>(olderJson, Web)!;

        // Assert
        Assert.Equal("s-old", session.SessionId);
        Assert.Equal(ConnectionModes.Terminal, session.ConnectionMode);
        Assert.Equal(WheelHolders.None, session.WheelHolder);
    }

    [Fact]
    public void ParseMessage_APushSnapshotFromAnOlderDirectorWithNeitherField_ReadsTerminalModeWithNoWheelHolder()
    {
        // Arrange: the Director tunnel speaks MessagePack. An older Director's PushSnapshot carries session maps
        // without the two keys; this writes exactly that with the hub protocol the Gateway uses.
        var older = new OlderDirectorSession { SessionId = "s-old", Agent = "Pi", Status = "Running" };

        // Act
        var sessions = RoundTripPushSnapshot(new object[] { 7L, new[] { older } });

        // Assert
        var session = Assert.Single(sessions);
        Assert.Equal("s-old", session.SessionId);
        Assert.Equal("Pi", session.Agent);
        Assert.Equal(ConnectionModes.Terminal, session.ConnectionMode);
        Assert.Equal(WheelHolders.None, session.WheelHolder);
    }

    [Fact]
    public void ParseMessage_APushSnapshotCarryingBothFields_KeepsTheDirectorsValues()
    {
        // Arrange: a current Director in API mode, the user holding the wheel.
        var current = new SessionDto { SessionId = "s-api", ConnectionMode = ConnectionModes.Api, WheelHolder = WheelHolders.User };

        // Act
        var sessions = RoundTripPushSnapshot(new object[] { 8L, new[] { current } });

        // Assert
        var session = Assert.Single(sessions);
        Assert.Equal(ConnectionModes.Api, session.ConnectionMode);
        Assert.Equal(WheelHolders.User, session.WheelHolder);
    }

    // ---- the pushed values reach what the clients read ----------------------------------------------------------

    [Fact]
    public void Clone_ASessionWithBothFields_CarriesThemToTheCopy()
    {
        // Arrange: the pushed store hands out clones, never its cached rows.
        var session = new SessionDto { SessionId = "s-api", ConnectionMode = ConnectionModes.Api, WheelHolder = WheelHolders.Protocol };

        // Act
        var copy = session.Clone();

        // Assert
        Assert.Equal(ConnectionModes.Api, copy.ConnectionMode);
        Assert.Equal(WheelHolders.Protocol, copy.WheelHolder);
    }

    [Fact]
    public async Task GetSessions_APushedApiModeSession_ServesItsConnectionModeAndWheelHolderToTheClients()
    {
        // Arrange
        await StartAsync();
        Push(
            new SessionDto { SessionId = "s-protocol", Agent = "Pi", ConnectionMode = ConnectionModes.Api, WheelHolder = WheelHolders.Protocol },
            new SessionDto { SessionId = "s-user", Agent = "Pi", ConnectionMode = ConnectionModes.Api, WheelHolder = WheelHolders.User });

        // Act
        var roster = await ReadRosterAsync();

        // Assert: read off the wire as the clients read it, by the camel-case names in schema.ts.
        Assert.Equal(("api", "protocol"), roster["s-protocol"]);
        Assert.Equal(("api", "user"), roster["s-user"]);
    }

    [Fact]
    public async Task GetSessions_APushFromAnOlderDirector_ServesTerminalModeWithNoWheelHolder()
    {
        // Arrange: the push decoded from an older Director's MessagePack, as the hub would hand it to the store.
        await StartAsync();
        var decoded = RoundTripPushSnapshot(new object[] { 1L, new[] { new OlderDirectorSession { SessionId = "s-old", Agent = "Pi" } } });
        Push(decoded);

        // Act
        var roster = await ReadRosterAsync();

        // Assert
        Assert.Equal(("terminal", "none"), roster["s-old"]);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>A session as a Director that predates the connection fields sends it: the same member names as
    /// <see cref="SessionDto"/>, and neither of the two new ones. Public, because MessagePack's contractless
    /// resolver writes only public types.</summary>
    public sealed class OlderDirectorSession
    {
        public string SessionId { get; set; } = "";
        public string Agent { get; set; } = "";
        public string Status { get; set; } = "";
    }

    /// <summary>Writes a <c>PushSnapshot</c> invocation with the Gateway's MessagePack hub protocol and parses it
    /// back the way <see cref="DirectorHub.PushSnapshot"/> receives it.</summary>
    private static SessionDto[] RoundTripPushSnapshot(object[] arguments)
    {
        var protocol = new MessagePackHubProtocol();
        var buffer = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(new InvocationMessage(nameof(DirectorHub.PushSnapshot), arguments), buffer);

        var bytes = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        Assert.True(protocol.TryParseMessage(ref bytes, new PushSnapshotBinder(), out var message));
        var invocation = Assert.IsType<InvocationMessage>(message);
        return Assert.IsType<SessionDto[]>(invocation.Arguments[1]);
    }

    private sealed class PushSnapshotBinder : IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName) =>
            methodName == nameof(DirectorHub.PushSnapshot)
                ? new[] { typeof(long), typeof(SessionDto[]) }
                : throw new InvalidOperationException("Unexpected hub method " + methodName);

        public Type GetReturnType(string invocationId) => throw new InvalidOperationException("No completions in this test");

        public Type GetStreamItemType(string streamId) => throw new InvalidOperationException("No streams in this test");
    }

    private void Push(params SessionDto[] sessions)
    {
        _registry.RegisterFromStream(DirectorId, "TEST-PC", "tester", "1.0", 1, new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc), TenantId.Local);
        _pushed.RegisterConnection(TenantId.Local, DirectorId, ConnectionId);
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, ConnectionId, 1, sessions));
    }

    private async Task<Dictionary<string, (string ConnectionMode, string WheelHolder)>> ReadRosterAsync()
    {
        using var response = await _http!.GetAsync("/sessions");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray().ToDictionary(
            row => row.GetProperty("sessionId").GetString()!,
            row => (row.GetProperty("connectionMode").GetString()!, row.GetProperty("wheelHolder").GetString()!));
    }

    /// <summary>The real Gateway routes over the real pushed store, on an in-memory test server: no port. Self-host, so
    /// every read is the one Local tenant the pushes above went into.</summary>
    private async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.UseRouting();
        GatewayEndpoints.Map(app, false, _registry, version: "test", token: "test-token",
            tenantBoundary: new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(), hosted: false),
            aiGate: TenantAiGate.SelfHost, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown,
            pushedSessions: _pushed);
        await app.StartAsync();
        _app = app;
        _http = app.GetTestClient();
    }
}
