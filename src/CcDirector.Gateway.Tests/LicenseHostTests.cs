using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE LICENSE WRITES ON A SELF-HOSTED GATEWAY: the shared machine token is a credential there - every Director on
/// the machine has it - and it reads the license routes, but it is not a signed-in person, so every write is refused
/// to it (the Tech Lead's ruling on the review of pull request 3936, finding 1): the renewal date, Switch now and
/// Forget this machine.
/// </summary>
public sealed class LicenseWritesSelfHostedTests : IAsyncLifetime
{
    private const string SharedToken = "license-self-hosted-token";
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-license-self-hosted-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!;
    private HttpClient _sharedToken = null!;

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _sharedToken = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _sharedToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SharedToken);
    }

    public async Task DisposeAsync()
    {
        _sharedToken.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task TheSharedMachineToken_ReadsTheLicenses_AndIsRefusedEveryWrite()
    {
        using var read = await _sharedToken.GetAsync("gateway/licenses");
        using var renewal = await _sharedToken.PutAsync("gateway/licenses/accounts/renewal",
            new StringContent("{\"accountEmail\":\"account-a@example.com\",\"renewsOn\":\"2026-10-17\"}", System.Text.Encoding.UTF8, "application/json"));
        using var switchNow = await _sharedToken.PostAsync("gateway/licenses/switch-now",
            new StringContent("{\"accountEmail\":\"account-a@example.com\"}", System.Text.Encoding.UTF8, "application/json"));
        using var forget = await _sharedToken.PostAsync("gateway/licenses/machines/forget",
            new StringContent("{\"machineId\":\"94ebcb15a4bf40438dca738dd31189d9\"}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        foreach (var refused in new[] { renewal, switchNow, forget })
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var json = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("owner_only", json.GetProperty("code").GetString());
        }
    }
}
