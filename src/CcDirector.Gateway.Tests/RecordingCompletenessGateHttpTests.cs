using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP-level proof for the audio completeness gate (issue #586) on the
/// phone-recorder ingest endpoints. Boots only <see cref="RecordingEndpoints"/>
/// on an ephemeral loopback port (no Tailscale, no registry, so it never
/// disturbs a running Gateway) and drives the real wire contract.
///
/// These tests exercise ONLY the gate, which runs before any transcription, so
/// they need no OpenAI key and run on every CI pass. They use tiny byte
/// payloads as stand-in audio segments.
/// </summary>
public sealed class RecordingCompletenessGateHttpTests
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static async Task PutChunkAsync(HttpClient http, string id, int index, byte[] bytes, string sha)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/ingest/recording/{id}/chunk/{index}") { Content = content };
        req.Headers.TryAddWithoutValidation("X-Chunk-Sha256", sha);
        var put = await http.SendAsync(req);
        put.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Complete_MissingSegment_Returns409_Incomplete_NamingMissingIndex()
    {
        await WithEndpointsAsync(async http =>
        {
            var id = "gate-" + Guid.NewGuid().ToString("N")[..8];
            var c0 = new byte[] { 1, 2, 3, 4 };
            var c1 = new byte[] { 5, 6, 7, 8 };

            await http.PostAsJsonAsync("/ingest/recording", new RecordingRegisterRequest(
                id, "Gate Test", "test", "2026-05-23T09:00:00Z", "mp3", 16000, 1));

            // Store only segment 0; segment 1 never arrives.
            await PutChunkAsync(http, id, 0, c0, Sha(c0));

            var manifest = new RecordingManifest(id, "Gate Test", "test",
                "2026-05-23T09:00:00Z", null, 16000, 1, "mp3",
                new()
                {
                    new RecordingChunkInfo(0, "0000.mp3", 0, 60000, c0.Length, Sha(c0)),
                    new RecordingChunkInfo(1, "0001.mp3", 60000, 60000, c1.Length, Sha(c1)),
                },
                new());

            var resp = await http.PostAsJsonAsync($"/ingest/recording/{id}/complete", manifest);
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode); // 409, never 202

            var status = JsonSerializer.Deserialize<RecordingStatusDto>(
                await resp.Content.ReadAsStringAsync(), JsonOpts);
            Assert.NotNull(status);
            Assert.Equal("incomplete", status.State);
            Assert.NotNull(status.MissingOrBadIndices);
            Assert.Equal(new[] { 1 }, status.MissingOrBadIndices);

            // No transcript was produced.
            var transcript = await http.GetAsync($"/ingest/recording/{id}/transcript");
            Assert.Equal(HttpStatusCode.NotFound, transcript.StatusCode);
        });
    }

    [Fact]
    public async Task Complete_EmptyCapture_Returns400_NamedError()
    {
        await WithEndpointsAsync(async http =>
        {
            var id = "gate-" + Guid.NewGuid().ToString("N")[..8];

            await http.PostAsJsonAsync("/ingest/recording", new RecordingRegisterRequest(
                id, "Gate Test", "test", "2026-05-23T09:00:00Z", "mp3", 16000, 1));

            // Zero segments declared = empty capture.
            var manifest = new RecordingManifest(id, "Gate Test", "test",
                "2026-05-23T09:00:00Z", null, 16000, 1, "mp3", new(), new());

            var resp = await http.PostAsJsonAsync($"/ingest/recording/{id}/complete", manifest);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("empty capture", body, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static async Task WithEndpointsAsync(Func<HttpClient, Task> body)
    {
        // Issue #2161: bind an operating-system-assigned port, then learn the number it gave us. The
        // address cannot be built up front any more - "0" is not something a client can dial.
        var bindUrl = $"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add(bindUrl);
        // The boundary is required and non-nullable now (finding I1-01). Self-host harness, so the REAL
        // self-host boundary: built over the SingleTenantContext, it always resolves Local.
        RecordingEndpoints.Map(app, false,
            new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false));
        await app.StartAsync();
        var baseUrl = $"http://127.0.0.1:{BoundPort.Of(app)}";
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
            await body(http);
        }
        finally
        {
            await app.StopAsync();
        }
    }

}
