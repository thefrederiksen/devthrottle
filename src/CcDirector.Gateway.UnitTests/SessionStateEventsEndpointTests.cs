using System.Net;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace CcDirector.Gateway.Tests;

public sealed class SessionStateEventsEndpointTests : IAsyncDisposable
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-4000-8000-00000000000a");
    private const string SessionId = "5d1c0f3e-8a2b-4c6d-9e0f-1a2b3c4d5e6f";
    private readonly PushedSessionStore _sessions = new();
    private WebApplication? _application;
    private HttpClient? _client;

    public SessionStateEventsEndpointTests()
    {
        _sessions.RegisterConnection(Tenant, "director-a", "connection-a");
        Assert.True(_sessions.ApplySnapshot(Tenant, "director-a", "connection-a", 1,
        [
            State("Working", "agent", fallback: false),
        ]));
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_application is not null)
            await _application.DisposeAsync();
    }

    private static SessionDto State(string state, string source, bool fallback) => new()
    {
        SessionId = SessionId,
        ActivityState = state,
        ActivityStateSource = source,
        ActivityStateIsFallback = fallback,
    };

    private async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _application = builder.Build();
        _application.MapGet("/sessions/{sid}/events", (HttpContext context, string sid) =>
            SessionStateEventsEndpoint.StreamAsync(context, sid, Tenant, _sessions));
        await _application.StartAsync();
        _client = _application.GetTestClient();
    }

    [Fact]
    public async Task Stream_StateTransitions_CarriesStateSourceAndFallback()
    {
        await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/sessions/{SessionId}/events");
        using var response = await _client!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var initial = await ReadFrameAsync(reader);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(("1", "state", "Working", "agent", false), Values(initial));

        Assert.True(_sessions.ApplyDelta(Tenant, "director-a", "connection-a", 2,
            State("WaitingForInput", "screen", fallback: false)));
        var waiting = await ReadFrameAsync(reader);
        Assert.Equal(("2", "state", "WaitingForInput", "screen", false), Values(waiting));

        Assert.True(_sessions.ApplyDelta(Tenant, "director-a", "connection-a", 3,
            State("Exited", "core", fallback: false)));
        var exited = await ReadFrameAsync(reader);
        Assert.Equal(("3", "state", "Exited", "core", false), Values(exited));
    }

    [Fact]
    public async Task Stream_UnknownSession_ReturnsNotFound()
    {
        await StartAsync();

        using var response = await _client!.GetAsync("/sessions/not-here/events");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("session not found", await response.Content.ReadAsStringAsync());
    }

    private static async Task<Dictionary<string, string>> ReadFrameAsync(StreamReader reader)
    {
        var frame = new Dictionary<string, string>(StringComparer.Ordinal);
        while (true)
        {
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (line is null) throw new EndOfStreamException("The state stream ended before a complete frame arrived.");
            if (line.Length == 0) return frame;
            var split = line.IndexOf(':');
            if (split > 0)
                frame[line[..split]] = line[(split + 1)..].TrimStart();
        }
    }

    private static (string Id, string Event, string State, string Source, bool Fallback) Values(
        IReadOnlyDictionary<string, string> frame)
    {
        using var data = JsonDocument.Parse(frame["data"]);
        var root = data.RootElement;
        return (frame["id"], frame["event"], root.GetProperty("state").GetString() ?? "",
            root.GetProperty("source").GetString() ?? "", root.GetProperty("fallback").GetBoolean());
    }
}
