using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Opt-in HTTP end-to-end for the phone-recorder ingest endpoints. Boots only
/// the <see cref="RecordingEndpoints"/> on an ephemeral port (no Tailscale, no
/// registry, so it never disturbs a running Gateway), then drives the real
/// flow with the Phase 0 speech clips as injected segments: register, PUT each
/// clip, complete. The endpoint uses the real transcription pipeline and the
/// real cc-vault filer, so this both proves the wire contract and files a real
/// transcript into the vault.
///
/// Gated on OPENAI_API_KEY (real transcription) AND CC_REC_E2E=1 (because it
/// mutates the vault). Normal test runs skip it.
/// </summary>
public sealed class RecordingEndpointsE2ETests
{
    private static bool Enabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
        && Environment.GetEnvironmentVariable("CC_REC_E2E") == "1";

    [Fact]
    public async Task FullHttpFlow_InjectPhase0Clips_FilesTranscript()
    {
        if (!Enabled) return; // self-skip unless explicitly enabled

        var clips = FindPhase0Clips();
        Assert.NotEmpty(clips);

        var port = AllocateFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add(baseUrl);
        RecordingEndpoints.Map(app);
        await app.StartAsync();

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            var recordingId = "e2e-" + Guid.NewGuid().ToString("N")[..8];

            // 1. Register.
            var reg = await http.PostAsJsonAsync("/ingest/recording", new RecordingRegisterRequest(
                recordingId, "Phase0 Injected Call (E2E)", "test-injector",
                "2026-05-23T09:00:00Z", "mp3", 16000, 1));
            reg.EnsureSuccessStatusCode();

            // 2. PUT each clip as a segment, with its SHA-256.
            var chunks = new List<RecordingChunkInfo>();
            for (int i = 0; i < clips.Count; i++)
            {
                var bytes = await File.ReadAllBytesAsync(clips[i]);
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                using var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var req = new HttpRequestMessage(HttpMethod.Put,
                    $"/ingest/recording/{recordingId}/chunk/{i}") { Content = content };
                req.Headers.TryAddWithoutValidation("X-Chunk-Sha256", sha);
                var put = await http.SendAsync(req);
                put.EnsureSuccessStatusCode();
                chunks.Add(new RecordingChunkInfo(i, $"{i:D4}.mp3", i * 60000L, 60000, bytes.Length, sha));
            }

            // Idempotency: re-PUT chunk 0, expect success no-op.
            var bytes0 = await File.ReadAllBytesAsync(clips[0]);
            var sha0 = Convert.ToHexString(SHA256.HashData(bytes0)).ToLowerInvariant();
            using (var c = new ByteArrayContent(bytes0))
            using (var rq = new HttpRequestMessage(HttpMethod.Put, $"/ingest/recording/{recordingId}/chunk/0") { Content = c })
            {
                rq.Headers.TryAddWithoutValidation("X-Chunk-Sha256", sha0);
                var put = await http.SendAsync(rq);
                put.EnsureSuccessStatusCode();
            }

            // 3. Complete -> transcribe, clean, file to vault.
            var manifest = new RecordingManifest(recordingId, "Phase0 Injected Call (E2E)", "test-injector",
                "2026-05-23T09:00:00Z", "2026-05-23T09:03:00Z", 16000, 1, "mp3",
                chunks, new() { new RecordingNote(1000, "injected audio test") });

            var comp = await http.PostAsJsonAsync($"/ingest/recording/{recordingId}/complete", manifest);
            var body = await comp.Content.ReadAsStringAsync();
            Assert.True(comp.IsSuccessStatusCode, $"complete failed: {body}");

            var status = JsonSerializer.Deserialize<RecordingStatusDto>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(status);
            Assert.Equal("filed", status!.State);
            Assert.Equal(clips.Count, status.ChunksTranscribed);
            Assert.NotNull(status.VaultDocId);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static List<string> FindPhase0Clips()
    {
        var here = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && here is not null; i++)
        {
            var dir = Path.Combine(here, "docs", "features", "dictation", "phase0");
            if (Directory.Exists(dir))
            {
                var clips = new List<string>();
                foreach (var name in new[] { "clip1.mp3", "clip2.mp3", "clip3.mp3" })
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p)) clips.Add(p);
                }
                return clips;
            }
            here = Path.GetDirectoryName(here);
        }
        return new();
    }
}
