using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The two-way connectivity handshake (issues #223/#224): POST /directors/{id}/verify
/// carries a Director-generated nonce; the Gateway proves the return leg by dialing
/// GET {endpoint}/verify/{nonce} back, and PASS requires both legs plus the nonce and
/// Director-id correlation. These tests drive the REAL GatewayClient + monitor against a
/// REAL Gateway host, with a minimal callback host standing in for the Director's
/// Control API surface (one GET route, same contract as ControlEndpoints).
/// </summary>
public sealed class TwoWayVerifyTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: "", authEnabled: false,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    // ===== Gateway endpoint contract =====

    [Fact]
    public async Task Verify_UnknownDirector_Returns410()
    {
        var resp = await _http.PostAsJsonAsync($"directors/{Guid.NewGuid()}/verify",
            new DirectorVerifyRequest { Nonce = "n-1" });
        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
    }

    [Fact]
    public async Task Verify_MissingNonce_Returns400()
    {
        var id = await RegisterDirectorAsync("http://127.0.0.1:1"); // endpoint irrelevant here
        var resp = await _http.PostAsJsonAsync($"directors/{id}/verify", new DirectorVerifyRequest { Nonce = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Verify_EndpointNeverAnswers_VerdictFalse_NamesTheBrokenLeg()
    {
        // The SORENLAPTOP shape: registration lands (leg 1 fine), but the advertised
        // endpoint has no listener. The verdict must be FALSE with a per-leg reason -
        // exactly what a heartbeat-keyed "connected" light would have gotten wrong.
        var deadEndpoint = $"http://127.0.0.1:{FreePort()}"; // allocated then released: nothing listens
        var id = await RegisterDirectorAsync(deadEndpoint);

        var resp = await _http.PostAsJsonAsync($"directors/{id}/verify", new DirectorVerifyRequest { Nonce = "n-dead" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var verdict = await resp.Content.ReadFromJsonAsync<DirectorVerifyResultDto>();
        Assert.NotNull(verdict);
        Assert.False(verdict!.Verified);
        Assert.False(verdict.CallbackOk);
        Assert.NotNull(verdict.CallbackError);
        Assert.Equal("n-dead", verdict.Nonce);
        Assert.Equal(deadEndpoint, verdict.CallbackEndpoint);
        Assert.Null(_gateway.Registry.Get(id)!.TwoWayVerifiedAt);
    }

    [Fact]
    public async Task Verify_CallbackAnswersAsWrongDirector_VerdictFalse()
    {
        // The advertised URL reaches A director - just not the registered one (port
        // collision / stale serve mapping). The id echo must catch it.
        var monitor = new GatewayConnectionMonitor();
        await using var callback = new CallbackHost("some-OTHER-director-id", monitor);
        await callback.StartAsync();

        var id = await RegisterDirectorAsync(callback.BaseUrl);
        var resp = await _http.PostAsJsonAsync($"directors/{id}/verify", new DirectorVerifyRequest { Nonce = "n-wrong" });
        var verdict = await resp.Content.ReadFromJsonAsync<DirectorVerifyResultDto>();

        Assert.NotNull(verdict);
        Assert.False(verdict!.Verified);
        Assert.Contains("DIFFERENT Director", verdict.CallbackError);
        Assert.Null(_gateway.Registry.Get(id)!.TwoWayVerifiedAt);
    }

    [Fact]
    public async Task Verify_HappyPath_StampsTwoWayVerifiedAt()
    {
        var id = Guid.NewGuid().ToString();
        var monitor = new GatewayConnectionMonitor();
        monitor.Reset(gatewayConfigured: true);
        await using var callback = new CallbackHost(id, monitor);
        await callback.StartAsync();
        await RegisterDirectorAsync(callback.BaseUrl, id);

        var nonce = monitor.BeginHandshake();
        var resp = await _http.PostAsJsonAsync($"directors/{id}/verify", new DirectorVerifyRequest { Nonce = nonce });
        var verdict = await resp.Content.ReadFromJsonAsync<DirectorVerifyResultDto>();

        Assert.NotNull(verdict);
        Assert.True(verdict!.Verified);
        Assert.True(verdict.CallbackOk);
        Assert.Null(verdict.CallbackError);
        Assert.True(monitor.CallbackReceived(nonce)); // the callback really landed on this side
        Assert.NotNull(_gateway.Registry.Get(id)!.TwoWayVerifiedAt);
    }

    // ===== Full client loop: register -> automatic handshake -> monitor verdict =====

    [Fact]
    public async Task GatewayClient_AfterRegistration_EarnsVerifiedAutomatically()
    {
        var id = Guid.NewGuid().ToString();
        var monitor = new GatewayConnectionMonitor();
        await using var callback = new CallbackHost(id, monitor);
        await callback.StartAsync();

        // No MagicDNS in tests: advertise the callback host via the config override path.
        var cfg = new GatewayConfig
        {
            Url = $"http://127.0.0.1:{_gateway.Port}",
            Token = "",
            TailnetEndpoint = callback.BaseUrl,
        };
        using var client = new GatewayClient(cfg, id, port: 65510, version: "9.9.9-test", monitor: monitor)
        {
            MagicDnsResolver = () => null,
        };
        client.Start();

        await WaitFor(() => monitor.Status == GatewayConnectionStatus.Verified, TimeSpan.FromSeconds(10));
        Assert.Equal(GatewayConnectionStatus.Verified, monitor.Status);
        Assert.NotNull(monitor.LastVerifiedAt);
        Assert.Null(monitor.FailureSummary);
        Assert.NotNull(_gateway.Registry.Get(id)!.TwoWayVerifiedAt);

        await client.StopAsync();
    }

    [Fact]
    public async Task GatewayClient_GatewayLiesAboutCallback_MonitorRefusesGreen()
    {
        // An impostor gateway claims its callback succeeded without ever dialing back.
        // The Director-side nonce cross-check must refuse the green: no callback landed here.
        var id = Guid.NewGuid().ToString();
        await using var liar = new LyingGatewayHost();
        await liar.StartAsync();

        var monitor = new GatewayConnectionMonitor();
        var cfg = new GatewayConfig { Url = liar.BaseUrl, Token = "", TailnetEndpoint = "http://127.0.0.1:1" };
        using var client = new GatewayClient(cfg, id, port: 65511, version: "9.9.9-test", monitor: monitor)
        {
            MagicDnsResolver = () => null,
        };
        client.Start();

        await WaitFor(() => monitor.Status == GatewayConnectionStatus.Failed, TimeSpan.FromSeconds(10));
        Assert.Equal(GatewayConnectionStatus.Failed, monitor.Status);
        Assert.Contains("not this Director", monitor.FailureSummary);
        Assert.Null(monitor.LastVerifiedAt);

        await client.StopAsync();
    }

    // ===== Helpers =====

    private async Task<string> RegisterDirectorAsync(string endpoint, string? id = null)
    {
        id ??= Guid.NewGuid().ToString();
        var resp = await _http.PostAsJsonAsync("directors/register", new DirectorRegistrationRequest
        {
            DirectorId = id,
            TailnetEndpoint = endpoint,
            Pid = Environment.ProcessId,
            MachineName = "test-machine",
            User = "test-user",
            Version = "9.9.9-test",
            StartedAt = DateTime.UtcNow,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return id;
    }

    private static async Task WaitFor(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(50);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    /// <summary>
    /// Minimal Kestrel host serving ONLY the handshake callback route, with the same
    /// contract as ControlEndpoints: echo the Director id, echo the nonce, record the
    /// receipt in the monitor.
    /// </summary>
    private sealed class CallbackHost : IAsyncDisposable
    {
        private readonly string _directorId;
        private readonly GatewayConnectionMonitor _monitor;
        private WebApplication? _app;
        public string BaseUrl { get; private set; } = "";

        public CallbackHost(string directorId, GatewayConnectionMonitor monitor)
        {
            _directorId = directorId;
            _monitor = monitor;
        }

        public async Task StartAsync()
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "CallbackHost" });
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
            builder.Logging.ClearProviders();
            _app = builder.Build();
            _app.MapGet("/verify/{nonce}", (string nonce) => Results.Json(new VerifyCallbackDto
            {
                DirectorId = _directorId,
                Nonce = nonce,
                Known = _monitor.RecordCallback(nonce),
            }));
            await _app.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null) await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// A gateway that accepts registration and FABRICATES a passing verify verdict
    /// without ever dialing the Director back - the exact one-leg lie the nonce
    /// cross-check exists to catch.
    /// </summary>
    private sealed class LyingGatewayHost : IAsyncDisposable
    {
        private WebApplication? _app;
        public string BaseUrl { get; private set; } = "";

        public async Task StartAsync()
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "LyingGateway" });
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
            builder.Logging.ClearProviders();
            _app = builder.Build();
            _app.MapPost("/directors/register", () => Results.Json(new { ok = true }, statusCode: StatusCodes.Status201Created));
            _app.MapPost("/directors/{id}/verify", (string id, DirectorVerifyRequest req) =>
                Results.Json(new DirectorVerifyResultDto
                {
                    Verified = true,
                    Nonce = req.Nonce,
                    CallbackOk = true,
                    CallbackEndpoint = "http://127.0.0.1:1",
                    VerifiedAt = DateTime.UtcNow,
                }));
            await _app.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null) await _app.DisposeAsync();
        }
    }
}
