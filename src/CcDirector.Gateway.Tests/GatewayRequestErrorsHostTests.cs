using System.Net;
using System.Net.Http.Headers;
using CcDirector.Gateway.Api;
using Microsoft.Data.Sqlite;
using Xunit;
using CcDirector.Core.Tests;   // TestTempRoot, linked into this project

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Error Logging mission, step 2 (issue #3675), on a REAL started <see cref="GatewayHost"/>: the correlation scope is
/// installed first in the host's own pipeline, so an error answer from any layer - the authentication gate in front of
/// every route, or a route itself - carries the id its stored rows are filed under. The route-level behaviour is proved
/// in the unit suite (GatewayRequestErrorsTests); this proves the host wires it, which no hand-built pipeline can.
/// </summary>
[Collection("DirectorRoot")]
public sealed class GatewayRequestErrorsHostTests : IAsyncLifetime
{
    private const string Token = "gateway-request-errors-host-token";

    private readonly string _root = TestTempRoot.For("cc-gateway-request-errors-");
    private string? _previousRoot;
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        Directory.CreateDirectory(_root);
        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort,
            token: Token,
            authEnabled: true,
            instancesDirectory: Path.Combine(_root, "instances"),
            workListsPath: Path.Combine(_root, "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + _gateway.Port + "/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
                TestTempRoot.DeleteTree(_root);
        }
        catch (IOException)
        {
            // A throwaway test root; a file still held open is left for the operating system.
        }
    }

    [Fact]
    public async Task An_answer_from_the_authentication_gate_carries_a_correlation_id()
    {
        // No credential at all: refused by the gate that stands in front of every route.
        var response = await _http.GetAsync("gateway/director-errors");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(32, Assert.Single(response.Headers.GetValues(GatewayRequestErrors.HeaderName)).Length);
    }

    [Fact]
    public async Task An_answer_from_a_route_carries_a_correlation_id_and_a_success_carries_none()
    {
        using var bad = new HttpRequestMessage(HttpMethod.Post, "gateway/director-errors")
        {
            Content = new StringContent("{ this is not json", System.Text.Encoding.UTF8, "application/json"),
        };
        bad.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var fine = new HttpRequestMessage(HttpMethod.Get, "gateway/director-errors");
        fine.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var refused = await _http.SendAsync(bad);
        var served = await _http.SendAsync(fine);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(32, Assert.Single(refused.Headers.GetValues(GatewayRequestErrors.HeaderName)).Length);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.False(served.Headers.Contains(GatewayRequestErrors.HeaderName));
    }
}
