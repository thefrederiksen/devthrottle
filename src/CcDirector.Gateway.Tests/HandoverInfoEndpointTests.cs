using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1214: the read-only "Handover info" data path, AFTER the Gateway Cleanup cut.
///
/// The desktop "Copy Handover Info" identity block (name, session id, repo, director id, machine,
/// version) - minus the Control API endpoint, which is a Director address the browser must never
/// learn - is served by the Gateway at GET /sessions/{sid}/handover. Post-cut the Gateway resolves
/// the owner from the PUSH store and reads the block over the tunnel ("handover" verb); there is no
/// Director HTTP route left. The Director registers UNREACHABLE and answers the verb over the stream,
/// so a working identity block proves the tunnel by construction. The block still carries NO Director
/// address (HandoverInfoDto has no endpoint field), and an unresolvable session id is an honest 404,
/// never a leak or a silent body.
/// </summary>
public sealed class HandoverInfoTunnelTests : IAsyncLifetime
{
    private const string Token = "handover-tunnel-token";
    private const string DirectorId = "dir-handover";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _director = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-handover-tunnel-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        _director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, dispatch: cmd => cmd.Verb switch
        {
            // The Director's handover core sets DirectorId in its body; the identity block carries NO
            // Control API endpoint (HandoverInfoDto has no such field).
            "handover" => FakeTunnelDirector.Ok(new HandoverInfoDto
            {
                SessionId = cmd.SessionId,
                DisplayName = "handover-info-test",
                RepoPath = @"C:\test\handover-info-test",
                DirectorId = DirectorId,
                MachineName = Environment.MachineName,
                Version = "9.9.9-handover-test",
            }),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _director.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
    }

    [Fact]
    public async Task Handover_ReturnsIdentityBlock_ForAKnownSession()
    {
        var sid = Guid.NewGuid().ToString();
        await _director.PushSnapshotAsync(Session(sid));

        var dto = await _http.GetFromJsonAsync<HandoverInfoDto>($"sessions/{sid}/handover");

        Assert.NotNull(dto);
        Assert.Equal(sid, dto!.SessionId);
        Assert.Equal("9.9.9-handover-test", dto.Version);
        Assert.Equal(Environment.MachineName, dto.MachineName);
        Assert.Equal(@"C:\test\handover-info-test", dto.RepoPath);
        Assert.False(string.IsNullOrWhiteSpace(dto.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(dto.DirectorId));
        // A working body proves the tunnel: the Director is registered UNREACHABLE.
        //
        // Asked as PRESENCE, not as "the last one". This used to read LastCommand, and the Gateway's own
        // voice sweep lands a screen-grid on the same tunnel on its own schedule - so about one run in
        // three on macOS the last command was that sweep's and not this request's, and the test failed
        // reporting "screen-grid". What it means to assert is that the handover request went over the
        // tunnel, which no later traffic can undo.
        Assert.Contains("handover", _director.Commands.Select(c => c.Verb));
    }

    [Fact]
    public async Task Handover_NeverLeaksAControlApiEndpoint()
    {
        var sid = Guid.NewGuid().ToString();
        await _director.PushSnapshotAsync(Session(sid));

        // The raw JSON must carry no Director address - only the identity fields.
        var raw = await _http.GetStringAsync($"sessions/{sid}/handover");
        Assert.DoesNotContain("controlEndpoint", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("controlApi", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handover_UnresolvableSession_Returns404()
    {
        // No session pushed -> no Director owns it -> the honest "not found across any director" 404,
        // never a silent body. (Replaces the old Director-side GUID-format 400, which was a route
        // concern deleted with the Director's own handover endpoint.)
        var resp = await _http.GetAsync($"sessions/{Guid.NewGuid()}/handover");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static SessionDto Session(string sid) => new()
    {
        SessionId = sid,
        Agent = "ClaudeCode",
        Status = "WaitingForInput",
        ActivityState = "WaitingForInput",
    };

}

/// <summary>
/// Gateway front-door contract for GET /sessions/{sid}/handover (issue #1214). Boots a real GatewayHost
/// with auth ON and no Director present, so the credential gate and the session-lookup 404 are both
/// observable without a Director on the other side.
/// </summary>
public sealed class HandoverInfoGatewayTests : IAsyncLifetime
{
    private const string GatewayToken = "handover-test-token";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ccd-handover-gw-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-handover-instances-" + Guid.NewGuid().ToString("N"));
    private string? _prevRoot;

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: GatewayToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Handover_WithoutAToken_Returns401()
    {
        // No Authorization header: the global gate must reject before any Director lookup.
        var resp = await _http.GetAsync($"sessions/{Guid.NewGuid()}/handover");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Handover_WithAValidToken_ButNoOwningDirector_Returns404()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"sessions/{Guid.NewGuid()}/handover");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);

        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

}
