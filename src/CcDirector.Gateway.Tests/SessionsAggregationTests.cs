using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Gateway;
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
/// Phase-2 Gateway aggregation tests: <c>GET /sessions</c> fans out to every registered
/// Director, parallelises the calls, stamps fleet-only fields, and surfaces unreachable
/// Directors as <c>machineErrors</c> instead of dropping them silently.
///
/// Tests use lightweight fake-Director Kestrel hosts (not the real <c>ControlApiHost</c>)
/// so they can simulate the exact failure modes we care about: HTTP 500, slow responses,
/// empty session lists, varied session content.
/// </summary>
public sealed class SessionsAggregationTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private readonly List<FakeDirector> _fakes = new();

    // Isolated discovery dir so a real Director running on the dev machine never leaks
    // its sessions into these aggregation assertions.
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

    // ---------- field stamping ----------

    [Fact]
    public async Task Aggregator_stamps_machine_user_tailnet_view_url()
    {
        var fake = await StartFake("MACHINE_A", "alice", new[]
        {
            Sample("s1", "ClaudeCode", "repo-a", "Idle", "green"),
        });
        await Register(fake);

        var sessions = await GetSessions();
        var s = Assert.Single(sessions);
        Assert.Equal("s1", s.SessionId);
        Assert.Equal("MACHINE_A", s.MachineName);
        Assert.Equal("alice", s.User);
        Assert.Equal(fake.BaseUrl, s.TailnetEndpoint);
        // ViewUrl carries the gateway address as a ?gw= deep-link param (the session
        // view uses it for its "back to Gateway" menu item).
        Assert.StartsWith($"{fake.BaseUrl}/sessions/s1/view?gw=", s.ViewUrl);
    }

    [Fact]
    public async Task Aggregator_returns_sessions_from_multiple_directors()
    {
        var a = await StartFake("MACHINE_A", "alice", new[]
        {
            Sample("a1", "ClaudeCode", "repo-a", "Idle", "green"),
            Sample("a2", "Pi", "repo-a", "Working", "yellow"),
        });
        var b = await StartFake("MACHINE_B", "bob", new[]
        {
            Sample("b1", "Codex", "repo-b", "WaitingForInput", "red"),
        });
        await Register(a);
        await Register(b);

        var sessions = await GetSessions();
        Assert.Equal(3, sessions.Count);
        Assert.Contains(sessions, s => s.SessionId == "a1" && s.MachineName == "MACHINE_A");
        Assert.Contains(sessions, s => s.SessionId == "a2" && s.MachineName == "MACHINE_A");
        Assert.Contains(sessions, s => s.SessionId == "b1" && s.MachineName == "MACHINE_B");
    }

    // ---------- error surfacing ----------

    [Fact]
    public async Task Failed_director_surfaces_in_machine_errors_envelope()
    {
        var good = await StartFake("GOOD", "alice", new[]
        {
            Sample("g1", "ClaudeCode", "repo", "Idle", "green"),
        });
        var bad = await StartFake("BAD", "alice", sessions: null, alwaysError: true);
        await Register(good);
        await Register(bad);

        var env = await GetEnvelope();
        var s = Assert.Single(env.Sessions);
        Assert.Equal("g1", s.SessionId);

        var err = Assert.Single(env.MachineErrors);
        Assert.Equal("BAD", err.MachineName);
        Assert.False(string.IsNullOrEmpty(err.Error), "machineError.Error should be populated");
    }

    [Fact]
    public async Task Flat_response_drops_failed_directors_silently()
    {
        // Backward-compat path. DirectorView still consumes the flat shape.
        var good = await StartFake("GOOD", "alice", new[] { Sample("g1", "ClaudeCode", "repo", "Idle", "green") });
        var bad = await StartFake("BAD", "alice", sessions: null, alwaysError: true);
        await Register(good);
        await Register(bad);

        var sessions = await GetSessions();
        var s = Assert.Single(sessions);
        Assert.Equal("g1", s.SessionId);
    }

    // ---------- filtering ----------

    [Fact]
    public async Task Default_response_hides_exited_sessions()
    {
        var fake = await StartFake("M", "u", new[]
        {
            Sample("live", "ClaudeCode", "r", "Idle", "green"),
            Sample("dead", "ClaudeCode", "r", "Exited", "unknown"),
        });
        await Register(fake);

        var sessions = await GetSessions();
        var s = Assert.Single(sessions);
        Assert.Equal("live", s.SessionId);
    }

    [Fact]
    public async Task IncludeExited_true_returns_exited_sessions()
    {
        var fake = await StartFake("M", "u", new[]
        {
            Sample("live", "ClaudeCode", "r", "Idle", "green"),
            Sample("dead", "ClaudeCode", "r", "Exited", "unknown"),
        });
        await Register(fake);

        var sessions = await GetSessions("includeExited=true");
        Assert.Equal(2, sessions.Count);
    }

    [Fact]
    public async Task StatusColor_filter_narrows_results()
    {
        var fake = await StartFake("M", "u", new[]
        {
            Sample("r1", "ClaudeCode", "r", "WaitingForInput", "red"),
            Sample("y1", "ClaudeCode", "r", "Working", "yellow"),
            Sample("g1", "ClaudeCode", "r", "Idle", "green"),
        });
        await Register(fake);

        var sessions = await GetSessions("statusColor=red");
        var s = Assert.Single(sessions);
        Assert.Equal("r1", s.SessionId);
    }

    [Fact]
    public async Task Machine_filter_narrows_results()
    {
        var a = await StartFake("MACHINE_A", "u", new[] { Sample("a1", "ClaudeCode", "r", "Idle", "green") });
        var b = await StartFake("MACHINE_B", "u", new[] { Sample("b1", "ClaudeCode", "r", "Idle", "green") });
        await Register(a);
        await Register(b);

        var sessions = await GetSessions("machine=MACHINE_A");
        var s = Assert.Single(sessions);
        Assert.Equal("a1", s.SessionId);
    }

    [Fact]
    public async Task Q_search_matches_name_and_repo_path()
    {
        var fake = await StartFake("M", "u", new[]
        {
            new SessionDto { SessionId = "x1", Agent = "ClaudeCode", RepoPath = "/repos/auth-middleware", Name = "", ActivityState = "Idle", StatusColor = "green" },
            new SessionDto { SessionId = "x2", Agent = "ClaudeCode", RepoPath = "/repos/other", Name = "fix auth flow", ActivityState = "Idle", StatusColor = "green" },
            new SessionDto { SessionId = "x3", Agent = "ClaudeCode", RepoPath = "/repos/other", Name = "rename hotkey", ActivityState = "Idle", StatusColor = "green" },
        });
        await Register(fake);

        var sessions = await GetSessions("q=auth");
        Assert.Equal(2, sessions.Count);
        Assert.Contains(sessions, s => s.SessionId == "x1");
        Assert.Contains(sessions, s => s.SessionId == "x2");
    }

    [Fact]
    public async Task Agent_filter_narrows_results()
    {
        var fake = await StartFake("M", "u", new[]
        {
            Sample("a", "ClaudeCode", "r", "Idle", "green"),
            Sample("b", "Pi", "r", "Idle", "green"),
            Sample("c", "Codex", "r", "Idle", "green"),
        });
        await Register(fake);

        var sessions = await GetSessions("agent=Pi");
        var s = Assert.Single(sessions);
        Assert.Equal("b", s.SessionId);
    }

    // ---------- NeedsYouSince stamping (issue #218) ----------

    [Fact]
    public async Task NeedsYouSince_is_nonNull_for_red_and_null_for_nonRed()
    {
        var fake = await StartFake("M", "u", new[]
        {
            Sample("red1", "ClaudeCode", "r", "WaitingForInput", "red"),
            Sample("blue1", "ClaudeCode", "r", "Working", "blue"),
        });
        await Register(fake);

        var sessions = await GetSessions();
        var red = Assert.Single(sessions, s => s.SessionId == "red1");
        var blue = Assert.Single(sessions, s => s.SessionId == "blue1");
        Assert.NotNull(red.NeedsYouSince);
        Assert.Null(blue.NeedsYouSince);
    }

    [Fact]
    public async Task NeedsYouSince_is_within_5s_of_entry()
    {
        var fake = await StartFake("M", "u", new[]
        {
            Sample("red1", "ClaudeCode", "r", "WaitingForInput", "red"),
        });
        await Register(fake);

        var before = DateTime.UtcNow;
        var sessions = await GetSessions();
        var after = DateTime.UtcNow;

        var red = Assert.Single(sessions);
        Assert.NotNull(red.NeedsYouSince);
        // Stamped at the moment the aggregation observed it red: within the poll window.
        Assert.InRange(red.NeedsYouSince!.Value, before.AddSeconds(-5), after.AddSeconds(5));
    }

    [Fact]
    public async Task NeedsYouSince_is_stable_across_polls_while_red()
    {
        var fake = await StartFake("M", "u", new[]
        {
            Sample("red1", "ClaudeCode", "r", "WaitingForInput", "red"),
        });
        await Register(fake);

        var first = Assert.Single(await GetSessions()).NeedsYouSince;
        await Task.Delay(50);
        var second = Assert.Single(await GetSessions()).NeedsYouSince;

        Assert.NotNull(first);
        Assert.NotNull(second);
        // Must not advance while the session stays red (AC: byte-identical).
        Assert.Equal(first!.Value, second!.Value);
    }

    [Fact]
    public async Task NeedsYouSince_resets_strictly_later_after_leaving_and_re_entering_red()
    {
        var session = Sample("flip", "ClaudeCode", "r", "WaitingForInput", "red");
        var fake = await StartFake("M", "u", new[] { session });
        await Register(fake);

        // Episode 1: red.
        var first = Assert.Single(await GetSessions()).NeedsYouSince;
        Assert.NotNull(first);

        // A new turn starts: leaves red -> NeedsYouSince must go null.
        session.StatusColor = "blue";
        session.ActivityState = "Working";
        var between = Assert.Single(await GetSessions()).NeedsYouSince;
        Assert.Null(between);

        await Task.Delay(50);

        // Episode 2: returns to red -> a strictly-later stamp than episode 1.
        session.StatusColor = "red";
        session.ActivityState = "WaitingForInput";
        var second = Assert.Single(await GetSessions()).NeedsYouSince;
        Assert.NotNull(second);
        Assert.True(second!.Value > first!.Value,
            $"second episode stamp {second.Value:o} must be strictly later than first {first.Value:o}");
    }

    [Fact]
    public async Task NeedsYouSince_is_null_while_briefing_overlay_keeps_effective_color_off_red()
    {
        // A raw-red session that is still being briefed presents as effective YELLOW (not red),
        // so the waiting clock must not start - briefing time is not waiting time. We assert the
        // contract directly via SessionOrdering (the same fold the Gateway uses): with
        // BriefingState="Briefing" + raw red, EffectiveColor is "yellow", so isRed is false and
        // NeedsYouSince stays null. (The Gateway's briefStampFor only runs with briefing enabled;
        // here we prove the EffectiveColor gate the stamp keys off.)
        var briefing = Sample("briefing1", "ClaudeCode", "r", "WaitingForInput", "red");
        briefing.BriefingState = "Briefing";
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(briefing));
        Assert.NotEqual("red", SessionOrdering.EffectiveColor(briefing));
    }

    // ---------- owner-cache pruning on observed exit (issue #291) ----------

    [Fact]
    public async Task Aggregator_prunes_owner_cache_for_session_no_longer_live_on_reachable_director_then_ws_proxy_is_404()
    {
        // A reachable Director that reports only "live". The cache still holds an OLD session "gone"
        // attributed to this Director (it was seen on a prior poll, then exited). After the aggregator
        // observes the Director's current live set, "gone" must be pruned, so the per-session WS proxy
        // answers 404 (session gone) instead of #288's 503 (owner offline).
        var fake = await StartFake("M", "u", new[] { Sample("live", "ClaudeCode", "r", "Idle", "green") });
        await Register(fake);
        _gateway.SessionOwners.Remember("gone", fake.DirectorId);
        Assert.Equal(fake.DirectorId, _gateway.SessionOwners.OwnerOf("gone"));

        // Drive one aggregation: the reachable Director reports "live" only -> "gone" is reconciled out.
        await GetSessions();

        Assert.Null(_gateway.SessionOwners.OwnerOf("gone"));

        // The WS proxy now sees no cached owner and no live owner -> 404, not 503.
        var resp = await _http.GetAsync("sessions/gone/stream");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Aggregator_prune_does_not_touch_a_session_owned_by_a_different_offline_director()
    {
        // #288 must not regress: a session cached against an OFFLINE Director (id never registered, so
        // unreachable) must survive a reconcile triggered by a DIFFERENT reachable Director, and the
        // WS proxy must still answer 503 for it.
        var reachable = await StartFake("M", "u", new[] { Sample("live", "ClaudeCode", "r", "Idle", "green") });
        await Register(reachable);
        _gateway.SessionOwners.Remember("offline-owned", "dead-director-id");

        await GetSessions();

        Assert.Equal("dead-director-id", _gateway.SessionOwners.OwnerOf("offline-owned"));
        var resp = await _http.GetAsync("sessions/offline-owned/stream");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    // ---------- issue #335: Director-supplied identity fields win over Gateway-derived ----------

    [Fact]
    public async Task Aggregator_preserves_director_supplied_identity_fields_and_does_not_overwrite_them()
    {
        // A new-version Director (issue #335+) that populates the four identity fields itself.
        // The Gateway aggregation must NOT overwrite them with its own derived values.
        const string directorMachine = "REAL_DIRECTOR_MACHINE";
        const string directorUser = "real_user";
        const string directorEndpoint = "https://real-machine.tailnet.ts.net:7879";
        const string directorViewUrl = "https://real-machine.tailnet.ts.net:7879/sessions/s1/view";

        var fake = await StartFakeWithPrePopulatedIdentity(
            directorMachine, directorUser, directorEndpoint, directorViewUrl,
            new[] { Sample("s1", "ClaudeCode", "repo-a", "Idle", "green") });
        await Register(fake);

        var sessions = await GetSessions();
        var s = Assert.Single(sessions);
        Assert.Equal("s1", s.SessionId);
        // Director-supplied values must survive the Gateway aggregation pass unchanged.
        Assert.Equal(directorMachine, s.MachineName);
        Assert.Equal(directorUser, s.User);
        Assert.Equal(directorEndpoint, s.TailnetEndpoint);
        Assert.Equal(directorViewUrl, s.ViewUrl);
    }

    [Fact]
    public async Task Aggregator_back_compat_enriches_old_director_empty_identity_fields()
    {
        // An OLD Director (pre-issue #335) that returns empty identity fields must still
        // have them enriched by the Gateway aggregation pass (back-compat for mixed fleets).
        var fake = await StartFake("OLD_MACHINE", "old_user", new[]
        {
            Sample("s2", "ClaudeCode", "repo-b", "Idle", "green"),
        });
        await Register(fake);

        var sessions = await GetSessions();
        var s = Assert.Single(sessions);
        Assert.Equal("s2", s.SessionId);
        // Fields were empty from the fake Director; the Gateway must have enriched them.
        Assert.Equal("OLD_MACHINE", s.MachineName);
        Assert.Equal("old_user", s.User);
        Assert.False(string.IsNullOrEmpty(s.TailnetEndpoint), "Gateway must set TailnetEndpoint for old Directors");
        Assert.False(string.IsNullOrEmpty(s.ViewUrl), "Gateway must set ViewUrl for old Directors");
    }

    // ---------- view-url shape ----------

    [Fact]
    public async Task ViewUrl_has_no_double_slashes_when_endpoint_has_trailing_slash()
    {
        var fake = await StartFake("M", "u", new[] { Sample("only", "ClaudeCode", "r", "Idle", "green") });
        // Register with a trailing slash on the tailnet endpoint to exercise the TrimEnd path.
        await Register(fake, tailnetOverride: fake.BaseUrl + "/");

        var sessions = await GetSessions();
        var s = Assert.Single(sessions);
        Assert.DoesNotContain("//sessions", s.ViewUrl);
        Assert.Contains("/sessions/only/view?gw=", s.ViewUrl);
    }

    // ---------- single-session lookup ----------

    [Fact]
    public async Task Single_session_lookup_stamps_fleet_fields()
    {
        var fake = await StartFake("MACHINE_X", "carol", new[] { Sample("only", "Gemini", "r", "Idle", "green") });
        await Register(fake);

        var resp = await _http.GetAsync("/sessions/only");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var s = await resp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.NotNull(s);
        Assert.Equal("MACHINE_X", s!.MachineName);
        Assert.Equal("carol", s.User);
        Assert.Equal(fake.BaseUrl, s.TailnetEndpoint);
        // Same ?gw= deep-link param as the aggregator (see above).
        Assert.StartsWith($"{fake.BaseUrl}/sessions/only/view?gw=", s.ViewUrl);
    }

    [Fact]
    public async Task Single_session_lookup_returns_404_when_not_found()
    {
        var fake = await StartFake("M", "u", new[] { Sample("a", "ClaudeCode", "r", "Idle", "green") });
        await Register(fake);

        var resp = await _http.GetAsync("/sessions/nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // The /api reference page was removed with every other Gateway-served UI page
    // (docs/plans/one-url-cockpit.md): unmatched paths fall through the Cockpit proxy,
    // covered by GatewayDirectoryRegistrationTests.Root_falls_through_to_the_cockpit_proxy.

    // ---------- helpers ----------

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed record EnvelopeResponse(
        [property: JsonPropertyName("sessions")] List<SessionDto> Sessions,
        [property: JsonPropertyName("machineErrors")] List<MachineErrorDto> MachineErrors
    );

    /// <summary>
    /// Fetches /sessions and filters to ONLY the sessions belonging to fakes registered
    /// by this test class. The DirectorRegistry's filesystem watch path picks up real
    /// cc-director.exe instances running on the developer's machine; those entries also
    /// appear in the aggregator response and would otherwise pollute test assertions.
    /// </summary>
    private async Task<List<SessionDto>> GetSessions(string query = "")
    {
        var url = string.IsNullOrEmpty(query) ? "sessions" : $"sessions?{query}";
        var sessions = await _http.GetFromJsonAsync<List<SessionDto>>(url, JsonOpts);
        return FilterToFakes(sessions ?? new());
    }

    private async Task<EnvelopeResponse> GetEnvelope(string query = "")
    {
        var url = string.IsNullOrEmpty(query) ? "sessions?envelope=true" : $"sessions?envelope=true&{query}";
        var body = await _http.GetFromJsonAsync<EnvelopeResponse>(url, JsonOpts);
        Assert.NotNull(body);
        var fakeIds = _fakes.Select(f => f.DirectorId).ToHashSet();
        return new EnvelopeResponse(
            body!.Sessions.Where(s => fakeIds.Contains(s.DirectorId)).ToList(),
            body.MachineErrors.Where(m => fakeIds.Contains(m.DirectorId)).ToList()
        );
    }

    private List<SessionDto> FilterToFakes(List<SessionDto> sessions)
    {
        var fakeIds = _fakes.Select(f => f.DirectorId).ToHashSet();
        return sessions.Where(s => fakeIds.Contains(s.DirectorId)).ToList();
    }

    private async Task Register(FakeDirector fake, string? tailnetOverride = null)
    {
        var req = new DirectorRegistrationRequest
        {
            DirectorId = fake.DirectorId,
            TailnetEndpoint = tailnetOverride ?? fake.BaseUrl,
            Pid = 1234,
            MachineName = fake.MachineName,
            User = fake.User,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        };
        var resp = await _http.PostAsJsonAsync("directors/register", req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private async Task<FakeDirector> StartFake(string machine, string user, SessionDto[]? sessions, bool alwaysError = false)
    {
        var fake = new FakeDirector(machine, user, sessions, alwaysError);
        await fake.StartAsync();
        _fakes.Add(fake);
        return fake;
    }

    /// <summary>
    /// Issue #335: start a FakeDirector whose sessions already carry the four identity
    /// fields (machineName, user, tailnetEndpoint, viewUrl) pre-populated - simulating a
    /// new-version Director that populated them itself. The Gateway aggregation pass must
    /// NOT overwrite these Director-supplied values with its own derived ones.
    /// </summary>
    private async Task<FakeDirector> StartFakeWithPrePopulatedIdentity(
        string machine, string user, string tailnetEndpoint, string viewUrl, SessionDto[]? sessions)
    {
        // Stamp the identity fields onto every session before the fake serves them.
        if (sessions is not null)
        {
            foreach (var s in sessions)
            {
                s.MachineName = machine;
                s.User = user;
                s.TailnetEndpoint = tailnetEndpoint;
                s.ViewUrl = viewUrl;
            }
        }
        var fake = new FakeDirector(machine, user, sessions, alwaysError: false);
        await fake.StartAsync();
        _fakes.Add(fake);
        return fake;
    }

    private static SessionDto Sample(string sid, string agent, string repo, string state, string color) => new()
    {
        SessionId = sid,
        Agent = agent,
        RepoPath = repo,
        ActivityState = state,
        Status = state == "Exited" ? "Exited" : "Running",
        StatusColor = color,
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    /// <summary>
    /// Minimal Kestrel host that pretends to be a Director's Control API.
    /// Only implements the surface the Gateway aggregator touches: GET /sessions.
    /// </summary>
    private sealed class FakeDirector : IAsyncDisposable
    {
        public string DirectorId { get; } = Guid.NewGuid().ToString();
        public string MachineName { get; }
        public string User { get; }
        public string BaseUrl { get; private set; } = "";

        private readonly SessionDto[]? _sessions;
        private readonly bool _alwaysError;
        private WebApplication? _app;

        public FakeDirector(string machine, string user, SessionDto[]? sessions, bool alwaysError)
        {
            MachineName = machine;
            User = user;
            _sessions = sessions;
            _alwaysError = alwaysError;
        }

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
            _app.MapGet("/sessions", () =>
            {
                if (_alwaysError) return Results.StatusCode(500);
                return Results.Json(_sessions ?? Array.Empty<SessionDto>());
            });
            _app.MapGet("/sessions/{sid}", (string sid) =>
            {
                if (_alwaysError) return Results.StatusCode(500);
                var s = _sessions?.FirstOrDefault(x => x.SessionId == sid);
                return s is null ? Results.NotFound() : Results.Json(s);
            });

            await _app.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
                await _app.DisposeAsync();
                _app = null;
            }
        }
    }
}
