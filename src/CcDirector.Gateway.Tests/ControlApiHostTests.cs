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

    [Fact]
    public void IsListening_isTrue_and_noStartupError_after_successful_start()
    {
        // The host started successfully in InitializeAsync, so the UI's Control-API indicator
        // stays hidden (StartupError null) and remote access is reported up.
        Assert.True(_host.IsListening);
        Assert.Null(_host.StartupError);
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

            // Simulate a session that has already taken its first turn. A brand-new session is
            // painted green ("ready") at a turn-end by design - it is parked at its prompt, not
            // needing you. This test exercises the genuine "needs you" RED path, which applies
            // only once IsBrandNew has cleared (it clears when the first prompt is submitted).
            session.IsBrandNew = false;

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
    public void ReportStartupFailure_SetsErrorAndRaisesEvent()
    {
        var sm = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        try
        {
            // Before any failure: healthy defaults so the UI indicator stays hidden.
            Assert.False(host.IsListening);
            Assert.Null(host.StartupError);

            var raised = 0;
            host.StartupStatusChanged += () => raised++;

            // Simulate the App boundary catching a bind failure (e.g. all ports busy).
            host.ReportStartupFailure("All ports in range 7879..7898 are busy.");

            Assert.False(host.IsListening);
            Assert.Equal("All ports in range 7879..7898 are busy.", host.StartupError);
            Assert.Equal(1, raised);
        }
        finally
        {
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
            // Past its brand-new "ready" (green) window - see the note in the test above; this
            // asserts the genuine "needs you" red path. A pipe session starts already in
            // WaitingForInput, so drive Working first to guarantee a real transition back into
            // WaitingForInput fires the wingman handler (SetActivityState ignores same-state).
            session.IsBrandNew = false;
            session.ApplyTerminalActivityState(ActivityState.Working);
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

/// <summary>
/// Issue #697: when the fixed Control-API range [7879..7898] is genuinely exhausted, the production
/// loopback host falls back to an ephemeral loopback port instead of disabling the Control API. It
/// stays listening (no startup error, so the desktop "Control API down / free a port" notice never
/// fires) and answers normally. Isolation: CC_DIRECTOR_ROOT points config/registration at a temp
/// root; the PortAllocationOverride seam forces "exhausted" WITHOUT touching real OS ports; and
/// SuppressServeProvisioning keeps the test from mutating the host machine's real Tailscale serve table.
/// </summary>
[Collection("DirectorRoot")]
public sealed class ControlApiHostEphemeralFallbackTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public ControlApiHostEphemeralFallbackTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-697-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task StartAsync_FixedRangeExhausted_FallsBackToEphemeralPortAndStaysListening()
    {
        var sm = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(
            sm, "1.0.0-test", () => Task.CompletedTask,
            useEphemeralPort: false,                          // production loopback path (not the test ephemeral seam)
            directorId: Guid.NewGuid().ToString(),            // isolate the registration file
            instancesDirectory: Path.Combine(_root, "instances"))
        {
            PortAllocationOverride = _ => null,               // simulate a genuinely exhausted fixed range
            SuppressServeProvisioning = true,                 // do not mutate the real Tailscale serve table
        };

        try
        {
            var port = await host.StartAsync();

            // Bound a port OUTSIDE the fixed range, and Port reflects the OS-assigned value.
            Assert.True(port < PortAllocator.PortRangeStart || port > PortAllocator.PortRangeEnd,
                $"fallback must bind a port outside [{PortAllocator.PortRangeStart}..{PortAllocator.PortRangeEnd}], got {port}");
            Assert.Equal(port, host.Port);

            // A successful fallback is NOT a failure: no startup error -> the "Control API down" notice never fires.
            Assert.True(host.IsListening);
            Assert.Null(host.StartupError);

            // The Control API actually answers on the ephemeral port.
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", DirectorAuth.LoadOrCreateToken());
            var resp = await client.GetAsync("sessions");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            await host.StopAsync();
            sm.Dispose();
        }
    }
}

/// <summary>
/// Issue #846: the session-number backfill is now wired into production. These tests prove the two
/// wirings that were missing (BackfillNumbers had no caller before): (1) a single backfill pass runs
/// at Director startup (ControlApiHost.StartAsync), numbering any tracked session that lacks a number;
/// and (2) the POST /admin/backfill-numbers endpoint triggers the same backfill on a RUNNING Director
/// (no restart), returns the count newly numbered, is idempotent (a second call returns 0), and the
/// assigned numbers are unique and within the 100-999 range.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionNumberBackfillTests
{
    private sealed record BackfillResult(int Assigned);

    [Fact]
    public async Task StartAsync_NumbersTrackedSessionsThatLackANumber()
    {
        // Arrange: a session that is tracked but carries NO number (the pre-#820 / restored-without-a
        // -number state). CreatePipeModeSession numbers it at creation, so clear it to simulate the gap.
        var sm = new SessionManager(new AgentOptions());
        var session = sm.CreatePipeModeSession(Path.GetTempPath());
        session.Number = null;

        var host = new ControlApiHost(sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        try
        {
            // Act: starting the Director runs the one-time startup backfill.
            await host.StartAsync();

            // Assert: the previously-unnumbered session now has a number in range.
            Assert.NotNull(session.Number);
            Assert.InRange(session.Number.Value, SessionNumberAllocator.MinNumber, SessionNumberAllocator.MaxNumber);
        }
        finally
        {
            await host.StopAsync();
            sm.Dispose();
            try
            {
                var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{host.DirectorId}.json");
                if (File.Exists(f)) File.Delete(f);
            }
            catch { /* test cleanup */ }
        }
    }

    [Fact]
    public async Task BackfillEndpoint_NumbersUnnumberedSessions_AndIsIdempotent()
    {
        var sm = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        try
        {
            var port = await host.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", DirectorAuth.LoadOrCreateToken());

            // Arrange: two tracked sessions, both made unnumbered (the gap the backfill closes).
            var a = sm.CreatePipeModeSession(Path.GetTempPath());
            var b = sm.CreatePipeModeSession(Path.GetTempPath());
            a.Number = null;
            b.Number = null;

            // Act: trigger the backfill on the running Director (no restart).
            var resp = await client.PostAsync("admin/backfill-numbers", null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var result = await resp.Content.ReadFromJsonAsync<BackfillResult>();

            // Assert: both were numbered and the count is reported.
            Assert.NotNull(result);
            Assert.Equal(2, result!.Assigned);
            Assert.NotNull(a.Number);
            Assert.NotNull(b.Number);

            // GET /sessions now shows the numbers (proves the live roster reflects them, no restart).
            var sessions = await client.GetFromJsonAsync<List<SessionDto>>("sessions?includeExited=true");
            Assert.NotNull(sessions);
            foreach (var s in sessions!)
                Assert.NotNull(s.Number);

            // Uniqueness + range (AC5).
            var numbers = sessions.Select(s => s.Number!.Value).ToList();
            Assert.Equal(numbers.Count, numbers.Distinct().Count());
            Assert.All(numbers, n =>
                Assert.InRange(n, SessionNumberAllocator.MinNumber, SessionNumberAllocator.MaxNumber));

            // Act + Assert: a SECOND call changes nothing (idempotent, AC4).
            var resp2 = await client.PostAsync("admin/backfill-numbers", null);
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
            var result2 = await resp2.Content.ReadFromJsonAsync<BackfillResult>();
            Assert.NotNull(result2);
            Assert.Equal(0, result2!.Assigned);
        }
        finally
        {
            await host.StopAsync();
            sm.Dispose();
            try
            {
                var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{host.DirectorId}.json");
                if (File.Exists(f)) File.Delete(f);
            }
            catch { /* test cleanup */ }
        }
    }

    [Fact]
    public async Task BackfillEndpoint_RequiresBearerToken_WhenAuthEnabled()
    {
        // AC3: the endpoint is protected (not in DirectorAuth.PublicPaths). With auth enabled, a call
        // WITHOUT the bearer token is rejected 401; WITH it, the call succeeds.
        var sm = new SessionManager(new AgentOptions());
        var host = new ControlApiHost(sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true, authEnabled: true);
        try
        {
            var port = await host.StartAsync();
            using var noAuth = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            var denied = await noAuth.PostAsync("admin/backfill-numbers", null);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

            using var authed = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            authed.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", DirectorAuth.LoadOrCreateToken());
            var ok = await authed.PostAsync("admin/backfill-numbers", null);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        finally
        {
            await host.StopAsync();
            sm.Dispose();
            try
            {
                var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{host.DirectorId}.json");
                if (File.Exists(f)) File.Delete(f);
            }
            catch { /* test cleanup */ }
        }
    }
}
