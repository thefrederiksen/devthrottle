using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Destructive-call gate on DELETE /directors/{id} (issue #212 W6/L4): a shutdown must
/// state a reason, and when the Director has live sessions the request must confirm
/// their count - otherwise 409 with the live-session list, and the Director's
/// POST /shutdown is never touched. Born from the 2026-06-06 post-mortem, where the
/// force-kill path could take down a Director plus 10 live sessions without a trace.
///
/// Uses lightweight fake-Director Kestrel hosts (same pattern as
/// SessionsAggregationTests) that record whether POST /shutdown was called.
/// </summary>
public sealed class DirectorShutdownGateTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private readonly List<FakeDirector> _fakes = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        foreach (var f in _fakes) await f.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    // ---------- reason required ----------

    [Fact]
    public async Task Delete_without_reason_returns_400_and_never_calls_shutdown()
    {
        var fake = await StartFake(live: 0);
        await Register(fake);

        var resp = await DeleteDirector(fake.DirectorId, new ShutdownDirectorRequest());

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(fake.ShutdownCalled, "POST /shutdown must not fire when the reason is missing");
    }

    [Fact]
    public async Task Delete_with_blank_reason_returns_400()
    {
        var fake = await StartFake(live: 0);
        await Register(fake);

        var resp = await DeleteDirector(fake.DirectorId, new ShutdownDirectorRequest { Reason = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(fake.ShutdownCalled);
    }

    // ---------- live-session gate ----------

    [Fact]
    public async Task Delete_with_live_sessions_and_no_confirm_returns_409_with_session_list()
    {
        var fake = await StartFake(live: 3);
        await Register(fake);

        var resp = await DeleteDirector(fake.DirectorId, new ShutdownDirectorRequest { Reason = "test stop" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.False(fake.ShutdownCalled, "POST /shutdown must not fire when the session gate blocks");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(3, doc.RootElement.GetProperty("liveSessionCount").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("sessions").GetArrayLength());
    }

    [Fact]
    public async Task Delete_with_wrong_confirm_count_returns_409()
    {
        var fake = await StartFake(live: 3);
        await Register(fake);

        var resp = await DeleteDirector(fake.DirectorId,
            new ShutdownDirectorRequest { Reason = "test stop", ConfirmSessions = 2 });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.False(fake.ShutdownCalled);
    }

    [Fact]
    public async Task Delete_with_matching_confirm_calls_graceful_shutdown()
    {
        var fake = await StartFake(live: 3);
        await Register(fake);

        var resp = await DeleteDirector(fake.DirectorId,
            new ShutdownDirectorRequest { Reason = "test stop", ConfirmSessions = 3 });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(fake.ShutdownCalled, "matching confirmSessions must let the graceful shutdown through");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task Delete_with_zero_live_sessions_needs_only_a_reason()
    {
        var fake = await StartFake(live: 0);
        await Register(fake);

        var resp = await DeleteDirector(fake.DirectorId, new ShutdownDirectorRequest { Reason = "idle teardown" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(fake.ShutdownCalled);
    }

    [Fact]
    public async Task Exited_sessions_do_not_count_toward_the_gate()
    {
        var fake = await StartFake(live: 0, exited: 2);
        await Register(fake);

        var resp = await DeleteDirector(fake.DirectorId, new ShutdownDirectorRequest { Reason = "idle teardown" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(fake.ShutdownCalled);
    }

    // ---------- unreachable Director ----------

    [Fact]
    public async Task Unreachable_director_skips_gate_and_returns_502_without_force()
    {
        var fake = await StartFake(live: 1);
        await Register(fake);
        await fake.StopAsync(); // now unreachable: sessions unknowable, graceful shutdown fails

        var resp = await DeleteDirector(fake.DirectorId, new ShutdownDirectorRequest { Reason = "hung director" });

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task Unreachable_director_with_force_attempts_kill_and_reports_failure_for_dead_pid()
    {
        // Registered with a PID that cannot exist, so the force path runs and reports
        // its failure instead of silently doing nothing (the pre-#212 behavior logged
        // nothing on this branch at all).
        var fake = await StartFake(live: 0);
        await Register(fake, pid: int.MaxValue - 1);
        await fake.StopAsync();

        var resp = await DeleteDirector(fake.DirectorId,
            new ShutdownDirectorRequest { Reason = "hung director", Force = true });

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("could not kill process", body);
    }

    [Fact]
    public async Task Delete_without_body_returns_404_for_unknown_director()
    {
        // Regression guard for the original GatewayHostTests contract: a body-less
        // DELETE (no Content-Type at all) must still route to the handler and 404 on
        // an unknown id, not bounce off content negotiation.
        var resp = await _http.DeleteAsync($"directors/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<HttpResponseMessage> DeleteDirector(string id, ShutdownDirectorRequest body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"directors/{id}")
        {
            Content = JsonContent.Create(body),
        };
        return await _http.SendAsync(req);
    }

    private async Task Register(FakeDirector fake, int pid = 1)
    {
        var req = new DirectorRegistrationRequest
        {
            DirectorId = fake.DirectorId,
            TailnetEndpoint = fake.BaseUrl,
            Pid = pid,
            MachineName = "GATE_TEST",
            User = "tester",
            Version = "test",
            StartedAt = DateTime.UtcNow,
        };
        var resp = await _http.PostAsJsonAsync("directors/register", req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private async Task<FakeDirector> StartFake(int live, int exited = 0)
    {
        var sessions = new List<SessionDto>();
        for (var i = 0; i < live; i++)
            sessions.Add(new SessionDto
            {
                SessionId = $"live-{i}",
                Name = $"live session {i}",
                Agent = "ClaudeCode",
                RepoPath = "/repo",
                Status = "Running",
                ActivityState = "Working",
                StatusColor = "blue",
            });
        for (var i = 0; i < exited; i++)
            sessions.Add(new SessionDto
            {
                SessionId = $"dead-{i}",
                Name = $"dead session {i}",
                Agent = "ClaudeCode",
                RepoPath = "/repo",
                Status = "Exited",
                ActivityState = "Exited",
                StatusColor = "unknown",
            });

        var fake = new FakeDirector(sessions.ToArray());
        await fake.StartAsync();
        _fakes.Add(fake);
        return fake;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    /// <summary>
    /// Minimal Kestrel host pretending to be a Director's Control API: GET /sessions
    /// returns the canned list, POST /shutdown records that it was called.
    /// </summary>
    private sealed class FakeDirector : IAsyncDisposable
    {
        public string DirectorId { get; } = Guid.NewGuid().ToString();
        public string BaseUrl { get; private set; } = "";
        public bool ShutdownCalled { get; private set; }

        private readonly SessionDto[] _sessions;
        private WebApplication? _app;

        public FakeDirector(SessionDto[] sessions) => _sessions = sessions;

        public async Task StartAsync()
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = "FakeDirector",
            });
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
            builder.Logging.ClearProviders();
            builder.Services.AddRoutingCore();

            _app = builder.Build();
            _app.UseRouting();
            _app.MapGet("/sessions", () => Results.Json(_sessions));
            _app.MapPost("/shutdown", () =>
            {
                ShutdownCalled = true;
                return Results.Json(new { accepted = true });
            });

            await _app.StartAsync();
        }

        public async Task StopAsync()
        {
            if (_app is not null)
            {
                try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
                await _app.DisposeAsync();
                _app = null;
            }
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }
}
