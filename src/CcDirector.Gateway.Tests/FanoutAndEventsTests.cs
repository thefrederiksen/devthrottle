using System.Net;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

public sealed class FanoutAndEventsTests : IAsyncLifetime
{
    private ControlApiHost _director = null!;
    private SessionManager _sm = null!;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _director = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        await _director.StartAsync();

        _gateway = new GatewayHost(port: AllocateFreePort(), token: "test-token", authEnabled: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Wait for discovery
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (_gateway.Registry.ListDirectors().Count >= 1) break;
            await Task.Delay(100);
        }
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        await _director.StopAsync();
        _sm.Dispose();
        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_director.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { }
    }

    [Fact]
    public async Task Fanout_validates_empty_session_ids()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "fanout")
        {
            Content = JsonContent.Create(new FanoutRequest { SessionIds = new(), Text = "hi" }),
        };
        req.Headers.Add("Authorization", "Bearer test-token");
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fanout_validates_empty_text()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "fanout")
        {
            Content = JsonContent.Create(new FanoutRequest
            {
                SessionIds = new() { Guid.NewGuid().ToString() },
                Text = "",
            }),
        };
        req.Headers.Add("Authorization", "Bearer test-token");
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fanout_requires_auth()
    {
        var resp = await _http.PostAsJsonAsync("fanout", new FanoutRequest
        {
            SessionIds = new() { Guid.NewGuid().ToString() },
            Text = "hi",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Fanout_unknown_session_returns_not_found_in_results()
    {
        var unknownSid = Guid.NewGuid().ToString();
        using var req = new HttpRequestMessage(HttpMethod.Post, "fanout")
        {
            Content = JsonContent.Create(new FanoutRequest
            {
                SessionIds = new() { unknownSid },
                Text = "hi",
                WaitForIdle = false,
            }),
        };
        req.Headers.Add("Authorization", "Bearer test-token");
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<FanoutResponse>();
        Assert.NotNull(body);
        Assert.Single(body!.Results);
        Assert.Equal(unknownSid, body.Results[0].SessionId);
        Assert.Equal("not_found", body.Results[0].Status);
    }

    [Fact]
    public async Task Events_endpoint_streams_director_events()
    {
        // Connect to SSE stream
        using var ctsClient = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var streamTask = Task.Run(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "events");
            req.Headers.Add("Authorization", "Bearer test-token");
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctsClient.Token);
            Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

            using var stream = await resp.Content.ReadAsStreamAsync(ctsClient.Token);
            using var reader = new StreamReader(stream);
            var buffer = new System.Text.StringBuilder();
            try
            {
                while (!ctsClient.Token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ctsClient.Token);
                    if (line is null) break;
                    buffer.AppendLine(line);
                    if (buffer.ToString().Contains("director.added")) return buffer.ToString();
                }
            }
            catch (OperationCanceledException) { }
            return buffer.ToString();
        });

        await Task.Delay(200);

        // Create a SECOND director to trigger a director.added event
        using var sm2 = new SessionManager(new AgentOptions());
        var d2 = new ControlApiHost(sm2, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        await d2.StartAsync();

        try
        {
            var result = await streamTask;
            Assert.Contains("director.added", result);
        }
        finally
        {
            await d2.StopAsync();
            try
            {
                var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{d2.DirectorId}.json");
                if (File.Exists(f)) File.Delete(f);
            }
            catch { }
        }
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
