using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Teams.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The Mentor's page for a person's own account (owner, 8 Oct 2026), over REAL HTTP on a real hosted Gateway with
/// Teams released: each account's device key reads its own block and only its own; another account's key is served
/// nothing of it; and with Teams dark the route is not there.
///
/// This class sets the process-wide CC_GATEWAY_HOSTED, so it belongs to the hosted-mode collection.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class PersonalMentorHostedTests : IAsyncLifetime
{
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-personal-mentor-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;
    private GatewayHost? _gateway;

    public Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_gateway is not null) await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    private async Task<GatewayHost> StartAsync(bool teamsReleased)
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "personal-mentor-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: teamsReleased);
        await _gateway.StartAsync();
        return _gateway;
    }

    private static MentorBlock BlockAbout(string subject, MentorWeek week, string workedOn) => new(
        week.ToString(), subject, MentorTones.Mixed, workedOn, "23 pull requests merged.", null,
        Array.Empty<MentorQuote>(), "Name the file and what done looks like.", DateTime.UtcNow, "test");

    private async Task<(HttpStatusCode Status, string Body)> Get(GatewayHost gateway, string path, string key)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await http.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OverTheWire_EachAccountReadsOnlyItsOwnBlock()
    {
        var gateway = await StartAsync(teamsReleased: true);
        var ann = HostedTestEnrollment.Enroll(gateway, $"sub-pm-ann-{_runId}", $"ann-{_runId}@example.com", $"dev-pm-ann-{_runId}", "MPMA");
        var ben = HostedTestEnrollment.Enroll(gateway, $"sub-pm-ben-{_runId}", $"ben-{_runId}@example.com", $"dev-pm-ben-{_runId}", "MPMB");
        var week = new MentorWeek(2026, 40);
        gateway.TeamMentorStore.SaveBlock(ann.Tenant, BlockAbout($"sub-pm-ann-{_runId}", week, "Ann's own week of work."));

        var (annStatus, annBody) = await Get(gateway, $"{PersonalMentorEndpoints.Route.TrimStart('/')}?week={week}", ann.DeviceKey);
        var (benStatus, benBody) = await Get(gateway, $"{PersonalMentorEndpoints.Route.TrimStart('/')}?week={week}", ben.DeviceKey);

        Assert.Equal(HttpStatusCode.OK, annStatus);
        using (var page = JsonDocument.Parse(annBody))
        {
            Assert.Equal("personal", page.RootElement.GetProperty("scope").GetString());
            var block = Assert.Single(page.RootElement.GetProperty("blocks").EnumerateArray());
            Assert.Equal("Ann's own week of work.", block.GetProperty("workedOn").GetString());
        }

        // The stranger: another account's key is served its own empty page, never Ann's block.
        Assert.Equal(HttpStatusCode.OK, benStatus);
        Assert.DoesNotContain("Ann's own week of work.", benBody, StringComparison.Ordinal);
        using (var page = JsonDocument.Parse(benBody))
        {
            Assert.Equal(0, page.RootElement.GetProperty("blocks").GetArrayLength());
            Assert.Equal(PersonalMentorEndpoints.FirstPageNote, page.RootElement.GetProperty("emptyNote").GetString());
        }
    }

    [Fact]
    public async Task OverTheWire_NoKey_IsRefused()
    {
        var gateway = await StartAsync(teamsReleased: true);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
        using var req = new HttpRequestMessage(HttpMethod.Get, PersonalMentorEndpoints.Route.TrimStart('/'));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task OverTheWire_TeamsDark_TheRouteIsNotThere()
    {
        var gateway = await StartAsync(teamsReleased: false);
        var ann = HostedTestEnrollment.Enroll(gateway, $"sub-pm-dark-{_runId}", $"dark-{_runId}@example.com", $"dev-pm-dark-{_runId}", "MPMD");

        var (status, body) = await Get(gateway, PersonalMentorEndpoints.Route.TrimStart('/'), ann.DeviceKey);

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.DoesNotContain("\"scope\"", body, StringComparison.Ordinal);
    }
}
