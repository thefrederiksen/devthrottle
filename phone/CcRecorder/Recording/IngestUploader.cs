using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CcRecorder.Recording;

/// <summary>
/// Uploads a finished recording to the CC Director Gateway ingest API over
/// HTTPS (Tailscale). Resumable and idempotent: it registers the recording,
/// PUTs each not-yet-uploaded segment (the server no-ops a re-PUT of the same
/// index + hash), then POSTs complete to trigger transcription + vault filing.
/// </summary>
public sealed class IngestUploader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = null, // server is case-insensitive; keep PascalCase
    };

    private readonly string _baseUrl;
    private readonly string _token;

    public IngestUploader(string baseUrl, string token)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = token;
    }

    /// <summary>Parsed result of a completed upload (from the server status).</summary>
    public sealed record UploadResult(string State, string? VaultDocId, string? Transcript);

    public async Task<UploadResult> UploadAsync(
        LocalManifest manifest,
        string recordingFolder,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!string.IsNullOrWhiteSpace(_token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        // 1. Register.
        progress?.Invoke("Registering...");
        var register = new
        {
            manifest.RecordingId,
            manifest.Title,
            manifest.DeviceId,
            manifest.StartedAt,
            manifest.Codec,
            manifest.SampleRateHz,
            manifest.Channels,
        };
        var regResp = await http.PostAsync(
            $"{_baseUrl}/ingest/recording",
            JsonBody(register), ct);
        regResp.EnsureSuccessStatusCode();

        // 2. Upload each segment (idempotent by index + hash).
        int n = 0;
        foreach (var chunk in manifest.Chunks.OrderBy(c => c.Index))
        {
            var path = Path.Combine(recordingFolder, chunk.File);
            if (!File.Exists(path)) continue;
            progress?.Invoke($"Uploading segment {++n}/{manifest.Chunks.Count}...");

            var bytes = await File.ReadAllBytesAsync(path, ct);
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var req = new HttpRequestMessage(
                HttpMethod.Put,
                $"{_baseUrl}/ingest/recording/{manifest.RecordingId}/chunk/{chunk.Index}")
            { Content = content };
            req.Headers.TryAddWithoutValidation("X-Chunk-Sha256", chunk.Sha256);

            var putResp = await http.SendAsync(req, ct);
            putResp.EnsureSuccessStatusCode();
            chunk.Uploaded = true;
        }

        // 3. Complete -> server transcribes, cleans, files to vault.
        progress?.Invoke("Finalizing + transcribing...");
        var compResp = await http.PostAsync(
            $"{_baseUrl}/ingest/recording/{manifest.RecordingId}/complete",
            JsonBody(manifest), ct);
        var body = await compResp.Content.ReadAsStringAsync(ct);
        if (!compResp.IsSuccessStatusCode)
            throw new HttpRequestException($"complete failed: {(int)compResp.StatusCode} {body}");

        progress?.Invoke("Filed to vault.");

        try
        {
            // The server (ASP.NET Core) serializes camelCase, so parse
            // case-insensitively. This is what previously made the transcript
            // look "pending" when it was actually present.
            var status = JsonSerializer.Deserialize<CompleteStatus>(
                body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return new UploadResult(
                State: string.IsNullOrWhiteSpace(status?.State) ? "uploaded" : status!.State!,
                VaultDocId: NullIfEmpty(status?.VaultDocId),
                Transcript: NullIfEmpty(status?.Transcript));
        }
        catch (JsonException)
        {
            return new UploadResult("uploaded", null, null);
        }
    }

    private sealed record CompleteStatus(string? State, string? VaultDocId, string? Transcript);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static StringContent JsonBody(object o)
        => new(JsonSerializer.Serialize(o, JsonOpts), Encoding.UTF8, "application/json");
}
