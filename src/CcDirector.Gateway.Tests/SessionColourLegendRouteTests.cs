using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// <c>GET /gateway/session-colours</c> on a real <see cref="GatewayHost"/>, over real HTTP, through the real auth
/// middleware. What the unit tests beside <see cref="SessionColourLegend"/> cannot show: that the route is mapped at
/// the path the clients call, that it serialises in the camelCase the clients parse, and that it sits behind the
/// credential gate rather than being public.
/// </summary>
public sealed class SessionColourLegendRouteTests : IAsyncLifetime
{
    private const string Token = "test-token-colour-legend";
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-colour-legend-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
        };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task Get_WithACredential_ServesEveryColour_InTheShapeTheClientsParse()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, SessionColourLegend.Route);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        Assert.True(HttpStatusCode.OK == res.StatusCode, $"expected 200, got {(int)res.StatusCode}; body was: {body}");
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var expected = SessionColourLegend.Build();

        // The exact camelCase names packages/client-core/src/sessions/colourLegend.ts reads.
        var entries = root.GetProperty("entries");
        Assert.Equal(expected.Entries.Count, entries.GetArrayLength());
        for (var i = 0; i < expected.Entries.Count; i++)
        {
            var served = entries[i];
            Assert.Equal(expected.Entries[i].Colour, served.GetProperty("colour").GetString());
            Assert.Equal(SessionColorPalette.HexFor(expected.Entries[i].Colour), served.GetProperty("hex").GetString());
            Assert.Equal(expected.Entries[i].Title, served.GetProperty("title").GetString());
            Assert.Equal(expected.Entries[i].Means, served.GetProperty("means").GetString());
            Assert.Equal(expected.Entries[i].AsksForYou, served.GetProperty("asksForYou").GetString());
        }
        Assert.Equal(SessionColorPalette.Broken, root.GetProperty("broken").GetProperty("hex").GetString());
        Assert.Equal(expected.VerdictNote, root.GetProperty("verdictNote").GetString());
    }

    [Fact]
    public async Task Get_WithoutACredential_IsRefusedByTheGate()
    {
        using var res = await _http.GetAsync(SessionColourLegend.Route);
        var body = await res.Content.ReadAsStringAsync();

        Assert.True(HttpStatusCode.Unauthorized == res.StatusCode,
            $"expected the credential gate's 401, got {(int)res.StatusCode}; body was: {body}");
    }
}
