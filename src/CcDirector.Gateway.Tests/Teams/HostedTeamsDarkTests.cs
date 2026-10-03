using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// Teams merges DARK (devthrottle_internal#2300, review finding F1). With the release switch off - the default - a
/// hosted Gateway does not map the team routes at all: an enrolled account gets the ordinary not-found answer on
/// every one, and no team can be created. <see cref="HostedTeamEndpointsTests"/> is the other half: switched on, the
/// same routes answer. A REAL hosted <see cref="GatewayHost"/> over REAL HTTP, so the proof is about what is mapped.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamsDarkTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private readonly string _subject = "sub-dark-" + Guid.NewGuid().ToString("N");
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-teams-dark-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";
    private string? _priorHosted;
    private string? _priorRoot;
    private string? _priorTeams;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);
        // The DEFAULT is what is under test, so the switch is read from an environment where it is not set at all,
        // rather than passed in as off.
        _priorTeams = Environment.GetEnvironmentVariable(CcDirector.Gateway.Teams.TeamsReleaseSwitch.EnvVar);
        Environment.SetEnvironmentVariable(CcDirector.Gateway.Teams.TeamsReleaseSwitch.EnvVar, null);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject(_subject, "dark@example.com");
        _key = _gateway.Devices.Register("dev-dark", "M-dark").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-dark", _subject, tenant.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        Environment.SetEnvironmentVariable(CcDirector.Gateway.Teams.TeamsReleaseSwitch.EnvVar, _priorTeams);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task SwitchUnset_EveryTeamRouteIsAbsent_AndNoTeamCanBeCreated()
    {
        Assert.False(_gateway.TeamsReleased);

        foreach (var (method, path) in new[] { (HttpMethod.Get, "teams"), (HttpMethod.Post, "teams"), (HttpMethod.Get, $"teams/{Guid.NewGuid()}/members") })
        {
            using var req = new HttpRequestMessage(method, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            if (method == HttpMethod.Post) req.Content = JsonContent.Create(new { name = "Should not exist" });
            using var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            Assert.True(resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} /{path} answered {resp.StatusCode}");
            // Not the team route's own refusal: the route is not there at all.
            Assert.DoesNotContain("team", body, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Empty(_gateway.TeamRegistry.ListTeamsFor(_subject));
    }
}
