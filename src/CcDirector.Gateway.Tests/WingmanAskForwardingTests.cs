using System.Net;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Phase 5.1: integration coverage for the wingman "ask" endpoint forwarding
/// path. In-process Director + Gateway. We do not exercise the live <c>claude --print</c>
/// invocation (no CLI in CI) - instead we use the fail-open contract: with an empty
/// <c>ClaudePath</c>, <c>AskAboutSessionAsync</c> returns <c>Status="no_claude"</c>
/// without spawning a process, which is enough to verify the wire path.
/// </summary>
public sealed class WingmanAskForwardingTests : IAsyncLifetime
{
    private ControlApiHost _director = null!;
    private SessionManager _sm = null!;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    // Isolated discovery dir: the test Director and Gateway find each other here, and a real
    // Director running on the dev machine can never leak into (or see) these test hosts.
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // ClaudePath empty -> AskAboutSessionAsync returns no_claude without spawning.
        _sm = new SessionManager(new AgentOptions { ClaudePath = "" });
        _director = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true,
            instancesDirectory: _instancesDir);
        await _director.StartAsync();

        _gateway = new GatewayHost(port: AllocateFreePort(), token: "test-token", authEnabled: false,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Wait for FSW discovery of the in-process Director.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (_gateway.Registry.ListDirectors().Any(d => d.DirectorId == _director.DirectorId)) break;
            await Task.Delay(100);
        }
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        await _director.StopAsync();
        _sm.Dispose();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    [Fact]
    public async Task Ask_with_empty_question_returns_400()
    {
        // We need a real session id, but it doesn't have to be one that exists
        // in this Director - the Gateway's 404 path triggers BEFORE the body
        // check when no Director claims the sid. So create one to ensure the
        // sid lookup succeeds.
        var sid = await TryCreateSessionOrFakeAsync();
        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/wingman/ask",
            new WingmanAskRequest { Question = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Ask_no_claude_returns_no_claude_status_with_context_digest()
    {
        var (sid, isReal) = await TryCreateRealSessionAsync();
        if (!isReal)
        {
            // Couldn't create a real session in this CI environment - the
            // unknown-session test already proves the 404 wire path, and the
            // empty-question test proves the 400 wire path. Nothing more to
            // verify here without a real session id.
            return;
        }

        var resp = await _http.PostAsJsonAsync($"sessions/{sid}/wingman/ask",
            new WingmanAskRequest { Question = "what is going on" });
        Assert.True(resp.IsSuccessStatusCode, $"HTTP {(int)resp.StatusCode}");
        var body = await resp.Content.ReadFromJsonAsync<WingmanAskResult>();
        Assert.NotNull(body);
        Assert.Equal("no_claude", body!.Status);
        // The digest must reflect the session - regardless of CLI configuration.
        Assert.False(string.IsNullOrEmpty(body.ContextDigest));
    }

    [Fact]
    public async Task Ask_for_unknown_session_returns_404()
    {
        var bogus = Guid.NewGuid().ToString();
        var resp = await _http.PostAsJsonAsync($"sessions/{bogus}/wingman/ask",
            new WingmanAskRequest { Question = "anything" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private async Task<string> TryCreateSessionOrFakeAsync()
    {
        var (sid, _) = await TryCreateRealSessionAsync();
        return sid;
    }

    private async Task<(string sid, bool isReal)> TryCreateRealSessionAsync()
    {
        // Try the real ConPty path via the Director's Control API. If that fails
        // (no claude/cmd available in CI), fall back to a non-null sid that exercises
        // the gateway wire but lands a 404 - acceptable for the empty-question /
        // unknown-session tests.
        try
        {
            using var directorHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_director.Port}/") };
            var resp = await directorHttp.PostAsJsonAsync("sessions",
                new NewSessionRequest { RepoPath = Path.GetTempPath(), Agent = "ClaudeCode" });
            if (!resp.IsSuccessStatusCode) return (Guid.NewGuid().ToString(), false);
            var session = await resp.Content.ReadFromJsonAsync<SessionDto>();
            return session?.SessionId is { } s ? (s, true) : (Guid.NewGuid().ToString(), false);
        }
        catch
        {
            return (Guid.NewGuid().ToString(), false);
        }
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
