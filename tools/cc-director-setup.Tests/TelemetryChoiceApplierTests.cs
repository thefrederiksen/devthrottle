using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Proves the installer Privacy step's choice applier (issue #659): the per-account server flag is the
/// source of truth (pre-fill via <c>GET /auth/me</c>, write via <c>PATCH /account/telemetry</c>), the
/// local config.json mirror always reflects the chosen value, the server write is best-effort (a failure
/// never blocks the install), and a missing token defaults the pre-fill ON and skips the server write
/// while still mirroring locally.
/// </summary>
public sealed class TelemetryChoiceApplierTests
{
    private const string TestBaseUrl = "http://127.0.0.1:9591";

    [Fact]
    public async Task ReadPrefillAsync_ServerEnabled_ReturnsTrue()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"telemetry_enabled\":true}");
        var applier = new TelemetryChoiceApplier(NewClient(handler), mirrorToConfig: _ => { });

        var enabled = await applier.ReadPrefillAsync("access-xyz");

        Assert.True(enabled);
    }

    [Fact]
    public async Task ReadPrefillAsync_ServerDisabled_ReturnsFalse()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"telemetry_enabled\":false}");
        var applier = new TelemetryChoiceApplier(NewClient(handler), mirrorToConfig: _ => { });

        var enabled = await applier.ReadPrefillAsync("access-xyz");

        Assert.False(enabled);
    }

    [Fact]
    public async Task ReadPrefillAsync_NoToken_DefaultsOnAndMakesNoCall()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"telemetry_enabled\":false}");
        var applier = new TelemetryChoiceApplier(NewClient(handler), mirrorToConfig: _ => { });

        var enabled = await applier.ReadPrefillAsync(null);

        Assert.True(enabled);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ReadPrefillAsync_ServerUnreachable_DefaultsOn()
    {
        // A stand-in token against the production endpoint returns 401; the pre-fill must default ON.
        var handler = new StubHandler(HttpStatusCode.Unauthorized, "");
        var applier = new TelemetryChoiceApplier(NewClient(handler), mirrorToConfig: _ => { });

        var enabled = await applier.ReadPrefillAsync("stand-in-token");

        Assert.True(enabled);
    }

    [Fact]
    public async Task ApplyChoiceAsync_UncheckedAndToken_PatchesFalseAndMirrorsFalse()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"telemetry_enabled\":false}");
        bool? mirrored = null;
        var applier = new TelemetryChoiceApplier(NewClient(handler), mirrorToConfig: v => mirrored = v);

        var serverWritten = await applier.ApplyChoiceAsync("access-xyz", enabled: false);

        Assert.True(serverWritten);
        Assert.Equal(HttpMethod.Patch, handler.Request!.Method);
        Assert.Equal($"{TestBaseUrl}{AccountTelemetryClient.TelemetryPath}", handler.Request.RequestUri!.ToString());
        Assert.Equal("access-xyz", handler.Request.Headers.Authorization!.Parameter);
        var body = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.False((bool)body["enabled"]!);
        Assert.Equal(false, mirrored);
    }

    [Fact]
    public async Task ApplyChoiceAsync_CheckedAndToken_PatchesTrueAndMirrorsTrue()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"telemetry_enabled\":true}");
        bool? mirrored = null;
        var applier = new TelemetryChoiceApplier(NewClient(handler), mirrorToConfig: v => mirrored = v);

        var serverWritten = await applier.ApplyChoiceAsync("access-xyz", enabled: true);

        Assert.True(serverWritten);
        var body = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.True((bool)body["enabled"]!);
        Assert.Equal(true, mirrored);
    }

    [Fact]
    public async Task ApplyChoiceAsync_ServerWriteFails_DoesNotThrowAndStillMirrors()
    {
        // A failed telemetry call must not block the install: the applier swallows it, reports the
        // server write as not done, and still writes the local mirror.
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "");
        bool? mirrored = null;
        var applier = new TelemetryChoiceApplier(NewClient(handler), mirrorToConfig: v => mirrored = v);

        var serverWritten = await applier.ApplyChoiceAsync("access-xyz", enabled: false);

        Assert.False(serverWritten);
        Assert.Equal(false, mirrored);
    }

    [Fact]
    public async Task ApplyChoiceAsync_NoToken_SkipsServerWriteButStillMirrors()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        bool? mirrored = null;
        var applier = new TelemetryChoiceApplier(NewClient(handler), mirrorToConfig: v => mirrored = v);

        var serverWritten = await applier.ApplyChoiceAsync(null, enabled: true);

        Assert.False(serverWritten);
        Assert.Equal(0, handler.CallCount);
        Assert.Equal(true, mirrored);
    }

    private static AccountTelemetryClient NewClient(StubHandler handler) =>
        new(new HttpClient(handler), baseUrl: TestBaseUrl);

    /// <summary>Captures the outgoing request and returns a configured status + body.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseBody;

        public StubHandler(HttpStatusCode status, string responseBody)
        {
            _status = status;
            _responseBody = responseBody;
        }

        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
