using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end smoke tests for the Director's internal Control API.
/// Uses a real SessionManager (with no sessions) so we exercise the
/// HTTP plumbing, JSON serialization, and routing.
/// </summary>
[Collection("DirectorRoot")]
public sealed class ControlApiHostTests : IAsyncLifetime
{
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;
    private bool _shutdownRequested;

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () =>
        {
            _shutdownRequested = true;
            return Task.CompletedTask;
        }, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var token = DirectorAuth.LoadOrCreateToken();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();

        // Best-effort cleanup of the registration file
        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup, ignore */ }
    }

    [Fact]
    public async Task Healthz_returns_ok()
    {
        var dto = await _client.GetFromJsonAsync<HealthDto>("healthz");
        Assert.NotNull(dto);
        Assert.Equal("ok", dto!.Status);
        Assert.Equal(0, dto.Sessions);
        Assert.Equal(1, dto.Directors);
        Assert.Equal("1.0.0-test", dto.Version);
    }

    [Fact]
    public async Task Sessions_empty_when_none_running()
    {
        var sessions = await _client.GetFromJsonAsync<List<SessionDto>>("sessions");
        Assert.NotNull(sessions);
        Assert.Empty(sessions!);
    }

    [Fact]
    public async Task Sessions_get_by_id_returns_404_for_unknown_guid()
    {
        var resp = await _client.GetAsync($"sessions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Sessions_get_by_id_returns_400_for_bad_format()
    {
        var resp = await _client.GetAsync("sessions/not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Sessions_prompt_returns_404_for_unknown_guid()
    {
        var resp = await _client.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/prompt", new PromptRequest { Text = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Sessions_prompt_returns_400_for_empty_text()
    {
        var resp = await _client.PostAsJsonAsync($"sessions/{Guid.NewGuid()}/prompt", new PromptRequest { Text = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Sessions_buffer_returns_404_for_unknown_guid()
    {
        var resp = await _client.GetAsync($"sessions/{Guid.NewGuid()}/buffer");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Sessions_interrupt_returns_404_for_unknown_guid()
    {
        var resp = await _client.PostAsync($"sessions/{Guid.NewGuid()}/interrupt", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Shutdown_triggers_callback()
    {
        Assert.False(_shutdownRequested);
        var resp = await _client.PostAsync("shutdown", null);
        Assert.True(resp.IsSuccessStatusCode);

        // Callback runs on a Task.Delay(100) so wait a beat
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!_shutdownRequested && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(_shutdownRequested);
    }

    [Fact]
    public void Registration_file_exists_after_start()
    {
        var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
        Assert.True(File.Exists(f), $"Registration file should exist at {f}");

        var json = File.ReadAllText(f);
        Assert.Contains(_host.DirectorId, json);
        Assert.Contains($"127.0.0.1:{_host.Port}", json);
    }
}

/// <summary>
/// Regression for the "session never turns red when the Control API can't bind" bug
/// (2026-06-15). The per-session state services (SessionStatusWingman + TerminalStateDetector)
/// used to start mid-StartAsync, AFTER PortAllocator.Allocate. When every port in [7879..7898]
/// was busy, Allocate threw and aborted StartAsync before those services ran, so the desktop
/// badge (Session.StatusColor) froze on its last colour and a silent session could never flip
/// to the red "needs you" state. StartSessionStateServices() now runs up front, independent of
/// the bind. These tests start ONLY those services (never call StartAsync, so no port is ever
/// bound) and prove the badge pipeline is live.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionStateServicesDecouplingTests
{
    [Fact]
    public async Task StateServices_DriveBadgeColour_WithoutAnyControlApiBind()
    {
        var sm = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);

        // Start the state services directly. We never call StartAsync, so Kestrel never binds a
        // port -- exactly the state a Director is in after "all ports in [7879..7898] busy".
        host.StartSessionStateServices();
        try
        {
            // Pipe-mode session: no process is spawned, but the SessionStatusWingman wires its
            // activity handler so StatusColor tracks ActivityState.
            var session = sm.CreatePipeModeSession(Path.GetTempPath());

            // Drive the activity state the way TerminalStateDetector would (byte -> Working;
            // QuietThreshold of silence -> WaitingForInput). The wingman is the sole writer of
            // StatusColor; if it is running, the badge follows -- with no Control API bound.
            session.ApplyTerminalActivityState(ActivityState.Working);
            Assert.Equal("blue", session.StatusColor);

            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Equal("red", session.StatusColor);
            Assert.Equal("needs you", session.LastStatusReason);
        }
        finally
        {
            await host.StopAsync();
            sm.Dispose();
        }
    }

    [Fact]
    public async Task StartSessionStateServices_IsIdempotent()
    {
        var sm = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        try
        {
            // Calling twice must not throw or double-wire (StartAsync also calls it once).
            host.StartSessionStateServices();
            host.StartSessionStateServices();

            var session = sm.CreatePipeModeSession(Path.GetTempPath());
            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Equal("red", session.StatusColor);
        }
        finally
        {
            await host.StopAsync();
            sm.Dispose();
        }
    }
}

/// <summary>
/// Issue #335 regression tests: the Director's own /sessions and /sessions/{sid} endpoints
/// always populate machineName, user, tailnetEndpoint, and viewUrl when a tailnet identity
/// resolves. Uses a pinned resolver so the test never requires a live Tailscale daemon.
/// Fails red if any of the four identity fields is empty when the resolver returns a resolved
/// endpoint.
/// </summary>
[Collection("DirectorRoot")]
public sealed class DirectorSessionIdentityFieldsTests : IAsyncLifetime
{
    private const string PinnedEndpoint = "https://testmachine.testnet.ts.net:7879";

    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        // Issue #335 test seam: pin the resolver so the four identity fields always resolve.
        _host.TailnetEndpointResolverOverride = () => new TailnetEndpointResolution
        {
            Endpoint = PinnedEndpoint,
            Source = "test-pin",
        };
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", DirectorAuth.LoadOrCreateToken());
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup */ }
    }

    [Fact]
    public async Task SessionsEndpoint_WithResolvedIdentity_PopulatesAllFourIdentityFields()
    {
        // Arrange: create a pipe-mode session (no process spawned) so the list is non-empty.
        // PipeBackend.Start only requires a non-empty executable path and an existing directory,
        // so it succeeds in tests without a real Claude installation.
        var session = _sm.CreatePipeModeSession(Path.GetTempPath());
        var sessionId = session.Id.ToString();

        // Act: GET /sessions (the Director-local endpoint, no Gateway).
        var sessions = await _client.GetFromJsonAsync<List<SessionDto>>("sessions?includeExited=true");

        // Assert: ALL four identity fields must be non-empty (the regression pin: if any
        // of them goes empty, this test catches the regression before it ships).
        Assert.NotNull(sessions);
        var s = Assert.Single(sessions!);
        Assert.False(string.IsNullOrEmpty(s.MachineName),
            "MachineName must be populated by the Director (issue #335 regression)");
        Assert.False(string.IsNullOrEmpty(s.User),
            "User must be populated by the Director (issue #335 regression)");
        Assert.False(string.IsNullOrEmpty(s.TailnetEndpoint),
            "TailnetEndpoint must be populated by the Director (issue #335 regression)");
        Assert.False(string.IsNullOrEmpty(s.ViewUrl),
            "ViewUrl must be populated by the Director (issue #335 regression)");

        // Verify the exact values match the pinned resolver.
        Assert.Equal(Environment.MachineName, s.MachineName);
        Assert.Equal(Environment.UserName, s.User);
        Assert.Equal(PinnedEndpoint, s.TailnetEndpoint);
        Assert.Contains($"{PinnedEndpoint}/sessions/{sessionId}/view", s.ViewUrl);
    }

    [Fact]
    public async Task SingleSessionEndpoint_WithResolvedIdentity_PopulatesAllFourIdentityFields()
    {
        // Arrange: same pipe-mode session trick as above.
        var session = _sm.CreatePipeModeSession(Path.GetTempPath());
        var sessionId = session.Id.ToString();

        // Act: GET /sessions/{sid}.
        var resp = await _client.GetAsync($"sessions/{sessionId}");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var s = await resp.Content.ReadFromJsonAsync<SessionDto>();

        // Assert.
        Assert.NotNull(s);
        Assert.False(string.IsNullOrEmpty(s!.MachineName),
            "MachineName must be populated by the Director (issue #335 regression)");
        Assert.False(string.IsNullOrEmpty(s.User),
            "User must be populated by the Director (issue #335 regression)");
        Assert.False(string.IsNullOrEmpty(s.TailnetEndpoint),
            "TailnetEndpoint must be populated by the Director (issue #335 regression)");
        Assert.False(string.IsNullOrEmpty(s.ViewUrl),
            "ViewUrl must be populated by the Director (issue #335 regression)");
        Assert.Contains($"{PinnedEndpoint}/sessions/{sessionId}/view", s.ViewUrl);
    }

    [Fact]
    public async Task SessionsEndpoint_WithUnresolvedIdentity_EmitsEmptyTailnetFields()
    {
        // When the resolver returns unresolved (Tailscale not running, no override),
        // tailnetEndpoint and viewUrl must be empty so the Gateway back-compat pass
        // can enrich them. machineName and user still resolve from the environment.
        var sm2 = new SessionManager(new AgentOptions());
        var unresolvedHost = new ControlApiHost(sm2, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        unresolvedHost.TailnetEndpointResolverOverride = () => new TailnetEndpointResolution
        {
            FailureReason = "Tailscale not running (pinned test failure)",
        };
        var port2 = await unresolvedHost.StartAsync();
        using var client2 = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port2}/") };
        client2.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", DirectorAuth.LoadOrCreateToken());

        try
        {
            sm2.CreatePipeModeSession(Path.GetTempPath());

            var sessions = await client2.GetFromJsonAsync<List<SessionDto>>("sessions?includeExited=true");
            Assert.NotNull(sessions);
            var s = Assert.Single(sessions!);
            // Tailnet fields must be empty when unresolved (Gateway will enrich them).
            Assert.Empty(s.TailnetEndpoint);
            Assert.Empty(s.ViewUrl);
            // Environment-based fields still resolve.
            Assert.False(string.IsNullOrEmpty(s.MachineName));
            Assert.False(string.IsNullOrEmpty(s.User));
        }
        finally
        {
            await unresolvedHost.StopAsync();
            sm2.Dispose();
            try
            {
                var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{unresolvedHost.DirectorId}.json");
                if (File.Exists(f)) File.Delete(f);
            }
            catch { }
        }
    }
}
