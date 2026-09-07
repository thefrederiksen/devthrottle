using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// PREVENTION, on a real host - issue #2725, from the first review round. A launcher that declared nothing
/// on joining is what every launcher shipped before the capability handshake looks like, and it ignores
/// onlyIfEmpty. Before this gate the restart reached it, it restarted a busy Director, and the relay turned
/// its bare success into a 502 after the fact. Now the guarded restart is refused BEFORE dispatch, on the
/// same read that would have named the connection, and the launcher receives nothing.
/// </summary>
public sealed class GuardedRestartDispatchRouteTests : IAsyncLifetime
{
    private const string Token = "guarded-dispatch-token";
    private const string Machine = "UNDECLARED-LAUNCHER-MACHINE";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection? _launcher;
    private readonly List<LauncherCommand> _received = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-guarded-dispatch-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // A launcher that declared NOTHING: the shape of every launcher built before the handshake. If a
        // command reaches it, it answers a bare success - exactly what an old launcher does with a flag it
        // cannot see.
        var conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/launcher-stream",
                options => options.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .Build();
        conn.On<LauncherCommand, LauncherCommandResult>("Command", cmd =>
        {
            lock (_received) _received.Add(cmd);
            return Task.FromResult(LauncherCommandResult.Ok());
        });
        await conn.StartAsync();
        await conn.InvokeAsync("Hello", new LauncherStreamHello { MachineName = Machine, Version = "1.9.8" });
        _launcher = conn;
        _gateway.Launchers.Upsert(TenantId.Local, new LauncherRegistrationRequest { MachineName = Machine, Pid = 1, Version = "1.9.8" });
    }

    public async Task DisposeAsync()
    {
        if (_launcher is not null) await _launcher.DisposeAsync();
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
    }

    [Fact]
    public async Task A_guarded_restart_is_refused_before_dispatch_to_a_launcher_that_declared_nothing()
    {
        var resp = await _http.PostAsJsonAsync($"machines/{Machine}/director/restart", new { onlyIfEmpty = true });
        var body = await resp.Content.ReadAsStringAsync();

        // A refusal, not the after-the-fact 502: the command was NOT carried out.
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("declared no capabilities", body);
        Assert.Contains("Nothing was done", body);

        // And the launcher received nothing at all.
        lock (_received) Assert.Empty(_received);
    }

    [Fact]
    public async Task An_ordinary_restart_still_reaches_that_launcher()
    {
        // The control: the gate is about the condition, not the launcher's age. The tray button and the
        // staged update restart old launchers' Directors exactly as before.
        var resp = await _http.PostAsync($"machines/{Machine}/director/restart", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        lock (_received) Assert.Single(_received);
    }
}
