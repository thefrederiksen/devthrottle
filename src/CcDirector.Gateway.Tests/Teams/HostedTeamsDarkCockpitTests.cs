using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using CcDirector.Gateway.Cockpit;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// THE GATEWAY'S OWN WIRING OF THE DARK FILTER (#3530 review round 3, R3-F3). <c>TeamsDarkRoutesTests</c> (Gateway.UnitTests) proves
/// the filter on a bare web host; this proves the REAL hosted <see cref="GatewayHost"/>, Teams not released, has it in
/// its pipeline before authentication - in the one arrangement the production defect needed: the Cockpit is in the web
/// root, so the single-page fallback answers any unmatched GET with 200 and the Cockpit page.
///
/// The Cockpit page is made sure of FIRST, and a control GET of a path that never existed proves the fallback really
/// serves it here, so the team answers cannot be a 404 for want of a Cockpit. Remove the two wiring lines in
/// <c>GatewayHost</c> (<c>if (!TeamsReleased) Teams.TeamsDarkRoutes.Use(_app);</c>) and the request with a key gets the
/// 200 page, and the request without one gets authentication's 401: both fail here. Gateway.Tests runs one test at a
/// time (<c>TestParallelization.cs</c>), so the Cockpit tests that delete the web root cannot race this one; a page this
/// class wrote is removed afterwards, and a built Cockpit it found is left alone.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamsDarkCockpitTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string ShellMarker = "<!-- cockpit-shell-for-the-dark-team-test -->";
    private readonly string _subject = "sub-tdc-" + Guid.NewGuid().ToString("N");
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-tdc-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _priorHosted;
    private string? _priorRoot;
    private bool _wroteWebRoot;

    public async Task InitializeAsync()
    {
        var index = Path.Combine(CockpitReactApp.WebRoot, "index.html");
        if (!File.Exists(index))
        {
            _wroteWebRoot = !Directory.Exists(CockpitReactApp.WebRoot);
            Directory.CreateDirectory(CockpitReactApp.WebRoot);
            await File.WriteAllTextAsync(index, "<!doctype html><html><body><div id=\"root\"></div>" + ShellMarker + "</body></html>");
        }

        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: false);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try
        {
            var index = Path.Combine(CockpitReactApp.WebRoot, "index.html");
            if (File.Exists(index) && File.ReadAllText(index).Contains(ShellMarker, StringComparison.Ordinal))
            {
                if (_wroteWebRoot) Directory.Delete(CockpitReactApp.WebRoot, true);
                else File.Delete(index);
            }
            if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true);
        }
        catch { /* best-effort */ }
    }

    private async Task<HttpResponseMessage> Get(string path, string? bearer)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await _http.SendAsync(req);
    }

    [Fact]
    public async Task Dark_GetTeams_WithTheCockpitInTheWebRoot_Is404_WithAKeyAndWithout()
    {
        Assert.False(_gateway.TeamsReleased);
        var key = HostedTestEnrollment.Enroll(_gateway, _subject, "tdc@example.com", "director-tdc", "M").DeviceKey;

        // Control: the fallback serves the Cockpit page here, so a 404 below is the dark filter's, not a missing Cockpit.
        using (var control = await Get("no-such-page-" + Guid.NewGuid().ToString("N"), key))
        {
            Assert.Equal(HttpStatusCode.OK, control.StatusCode);
            Assert.Equal("text/html", control.Content.Headers.ContentType?.MediaType);
        }

        foreach (var bearer in new[] { key, null })
        {
            using var resp = await Get("teams", bearer);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Equal("text/plain", resp.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain("<!doctype", await resp.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
