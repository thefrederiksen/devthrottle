using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Issue #649 (Gateway Centralization Phase 3): the Director-side reader of the GATEWAY-OWNED,
/// fleet-wide richer-usage-telemetry consent. Proves the Director honors the Gateway value, caches the
/// last-known value, falls back to that last-known value when the Gateway is unreachable (degraded,
/// decision #3), uses the documented default (ON) when nothing was ever cached, and that the richer
/// usage telemetry gate (<see cref="UsageTelemetry.ForDirector"/>) is driven by that consent while the
/// always-on auth-floor reporter is NOT.
///
/// Each test passes an explicit gatewayUrl and a temp cache path so the reader never reads the test
/// machine's config.json or real cache - the inputs are determined by the test, not the environment.
/// </summary>
public sealed class GatewayTelemetryConsentTests : IDisposable
{
    private const string TestGatewayUrl = "http://127.0.0.1:7878";
    private readonly string _cachePath;

    public GatewayTelemetryConsentTests()
    {
        _cachePath = Path.Combine(Path.GetTempPath(), "ccd-consent-cache-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        try { if (File.Exists(_cachePath)) File.Delete(_cachePath); } catch { /* best effort */ }
    }

    // ----- AC3: a Director reads the gateway consent and honors it; caches the last-known value -----

    [Fact]
    public async Task RefreshAsync_ReadsGatewayValue_AndCachesIt()
    {
        var consent = new GatewayTelemetryConsent(
            new HttpClient(new StubHandler(enabled: false)), gatewayUrl: TestGatewayUrl, cachePath: _cachePath);

        var value = await consent.RefreshAsync();

        Assert.False(value);
        // Cached on disk for the degraded path to read later.
        var cached = consent.ReadCache();
        Assert.NotNull(cached);
        Assert.False(cached!.Enabled);
    }

    [Fact]
    public async Task IsConsentedCached_AfterRefresh_ReturnsTheGatewayValue()
    {
        var consent = new GatewayTelemetryConsent(
            new HttpClient(new StubHandler(enabled: false)), gatewayUrl: TestGatewayUrl, cachePath: _cachePath);

        await consent.RefreshAsync();

        // The synchronous gate read reflects what the gateway last reported.
        Assert.False(consent.IsConsentedCached());
    }

    // ----- AC3: gateway unreachable -> last-known cached value (degraded, decision #3) -----

    [Fact]
    public async Task IsConsentedCached_WhenGatewayUnreachable_UsesLastKnownValue()
    {
        // First a successful refresh records OFF...
        var first = new GatewayTelemetryConsent(
            new HttpClient(new StubHandler(enabled: false)), gatewayUrl: TestGatewayUrl, cachePath: _cachePath);
        await first.RefreshAsync();

        // ...now the gateway is unreachable. RefreshAsync would throw, but the SYNCHRONOUS gate keeps
        // honoring the last-known OFF rather than guessing ON.
        var unreachable = new GatewayTelemetryConsent(
            new HttpClient(new ThrowingHandler()), gatewayUrl: TestGatewayUrl, cachePath: _cachePath);

        Assert.False(unreachable.IsConsentedCached());
    }

    [Fact]
    public async Task RefreshAsync_WhenGatewayUnreachable_Throws_AndCacheIsUnchanged()
    {
        var first = new GatewayTelemetryConsent(
            new HttpClient(new StubHandler(enabled: false)), gatewayUrl: TestGatewayUrl, cachePath: _cachePath);
        await first.RefreshAsync();

        var unreachable = new GatewayTelemetryConsent(
            new HttpClient(new ThrowingHandler()), gatewayUrl: TestGatewayUrl, cachePath: _cachePath);

        // The network read fails loudly (no hidden fallback inside RefreshAsync); the caller decides.
        await Assert.ThrowsAnyAsync<Exception>(() => unreachable.RefreshAsync());
        // The last-known cache is untouched by the failed refresh.
        Assert.False(unreachable.ReadCache()!.Enabled);
    }

    // ----- AC1/AC3: documented default ON when nothing was ever cached -----

    [Fact]
    public void IsConsentedCached_WhenNothingCached_DefaultsOn()
    {
        var consent = new GatewayTelemetryConsent(
            new HttpClient(new ThrowingHandler()), gatewayUrl: TestGatewayUrl, cachePath: _cachePath);

        // No prior successful refresh and no cache file -> the documented default is ON.
        Assert.True(consent.IsConsentedCached());
        Assert.Equal(TelemetryConsentConfig.Default, consent.IsConsentedCached());
    }

    // ----- AC2: the richer usage telemetry is gated by the gateway consent -----

    [Fact]
    public async Task UsageTelemetry_ForDirector_WhenGatewayConsentOff_DoesNotRecord()
    {
        var sink = Path.Combine(Path.GetTempPath(), "ccd-usage-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            // Gateway consent is OFF (cached from a refresh).
            var consent = new GatewayTelemetryConsent(
                new HttpClient(new StubHandler(enabled: false)), gatewayUrl: TestGatewayUrl, cachePath: _cachePath);
            await consent.RefreshAsync();

            // Local toggle is ON, so ONLY the gateway gate is what stops the event.
            var telemetry = UsageTelemetry.ForDirector(consent, localToggle: () => true, sinkPath: sink);

            var recorded = telemetry.Record("session.created");

            Assert.False(recorded);
            Assert.False(File.Exists(sink));
        }
        finally
        {
            try { if (File.Exists(sink)) File.Delete(sink); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task UsageTelemetry_ForDirector_WhenGatewayConsentOn_Records()
    {
        var sink = Path.Combine(Path.GetTempPath(), "ccd-usage-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var consent = new GatewayTelemetryConsent(
                new HttpClient(new StubHandler(enabled: true)), gatewayUrl: TestGatewayUrl, cachePath: _cachePath);
            await consent.RefreshAsync();

            var telemetry = UsageTelemetry.ForDirector(consent, localToggle: () => true, sinkPath: sink);

            var recorded = telemetry.Record("session.created");

            Assert.True(recorded);
            Assert.Single(telemetry.ReadAll());
        }
        finally
        {
            try { if (File.Exists(sink)) File.Delete(sink); } catch { /* best effort */ }
        }
    }

    // ----- AC2/AC4: the auth-floor (director-startup) event is NOT gated by the consent -----

    [Fact]
    public async Task AuthFloorReporter_StillPosts_WhenGatewayConsentOff()
    {
        // Consent is OFF fleet-wide...
        var consent = new GatewayTelemetryConsent(
            new HttpClient(new StubHandler(enabled: false)), gatewayUrl: TestGatewayUrl, cachePath: _cachePath);
        await consent.RefreshAsync();
        Assert.False(consent.IsConsentedCached());

        // ...the always-on director-startup reporter has no dependency on the consent at all, so it
        // still POSTs. This is the proof that the auth floor remains ungated.
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var reporter = new DevThrottleDirectorStartupTelemetryReporter(
            new HttpClient(handler), machineName: "TEST-MACHINE", appVersion: "1.2.3", gatewayUrl: TestGatewayUrl);

        await reporter.ReportStartupAsync("dir-abc");

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
    }

    // ----- Stubs -----

    /// <summary>Always answers the consent GET with <c>{ "enabled": &lt;value&gt; }</c> and 200 OK.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly bool _enabled;
        public StubHandler(bool enabled) => _enabled = enabled;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = $"{{\"enabled\":{(_enabled ? "true" : "false")}}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Simulates an unreachable Gateway by throwing on every send.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("simulated unreachable gateway");
    }

    /// <summary>Captures the one auth-floor POST so the test can assert it fired regardless of consent.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public int CallCount { get; private set; }
        public HttpRequestMessage? Request { get; private set; }

        public CapturingHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            return Task.FromResult(new HttpResponseMessage(_status));
        }
    }
}
