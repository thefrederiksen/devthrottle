using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CcRecorder.Recording;

/// <summary>
/// Uploads a finished recording to the CC Director Gateway ingest API over
/// HTTPS (Tailscale). Resumable and idempotent: it registers the recording,
/// PUTs each not-yet-uploaded segment (the server no-ops a re-PUT of the same
/// index + hash), then POSTs complete to trigger transcription. Transcripts are
/// stored locally on the server and promoted to the vault only by the user.
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

    /// <param name="onProgress">
    /// Invoked after each progress update. The latest message is written onto
    /// <paramref name="manifest"/>'s <see cref="LocalManifest.UploadProgress"/>
    /// and per-segment <see cref="ChunkInfo.Uploaded"/> flags are set on it
    /// before the call, so the caller's only job is to persist the manifest and
    /// refresh the UI. The same callback fires after each confirmed segment so
    /// the resume point survives a crash mid-upload.
    /// </param>
    public async Task<UploadResult> UploadAsync(
        LocalManifest manifest,
        string recordingFolder,
        Action? onProgress = null,
        CancellationToken ct = default)
    {
        // Push one structured progress update onto the manifest and let the
        // caller persist + refresh the UI. Phase drives the determinate bar.
        void Report(string label, string phase, int current, int total)
        {
            manifest.UploadProgress = label;
            manifest.UploadPhase = phase;
            manifest.UploadCurrent = current;
            manifest.UploadTotal = total;
            onProgress?.Invoke();
        }

        var ordered = manifest.Chunks.OrderBy(c => c.Index).ToList();
        int segTotal = ordered.Count;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!string.IsNullOrWhiteSpace(_token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        // 1. Register.
        Report("Preparing upload", "sending", 0, segTotal);
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

        // 2. Upload each segment (idempotent by index + hash). Segments already
        //    confirmed on a previous attempt are skipped, so a connection drop
        //    resumes from the first unsent segment instead of re-sending bytes.
        int sent = ordered.Count(c => c.Uploaded);
        Report($"Sending {sent}/{segTotal}", "sending", sent, segTotal);
        for (int i = 0; i < segTotal; i++)
        {
            var chunk = ordered[i];
            var path = Path.Combine(recordingFolder, chunk.File);
            if (!File.Exists(path)) continue;
            if (chunk.Uploaded) continue;

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

            // Persist the win right away so a later failure resumes after it.
            chunk.Uploaded = true;
            sent++;
            Report($"Sending {sent}/{segTotal}", "sending", sent, segTotal);
        }

        // 3. Complete -> server transcribes + cleans into the local transcripts
        //    area. This call blocks until every segment is transcribed, so we
        //    poll the status endpoint alongside it to keep the bar moving (it
        //    reports transcribed-segment count) instead of freezing on one line.
        Report("Transcribing 0/" + segTotal, "transcribing", 0, segTotal);
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pollTask = PollTranscribeAsync(http, manifest.RecordingId, segTotal, Report, pollCts.Token);

        HttpResponseMessage compResp;
        try
        {
            compResp = await http.PostAsync(
                $"{_baseUrl}/ingest/recording/{manifest.RecordingId}/complete",
                JsonBody(manifest), ct);
        }
        finally
        {
            pollCts.Cancel();
            await pollTask; // never throws; see PollTranscribeAsync
        }

        var body = await compResp.Content.ReadAsStringAsync(ct);
        if (!compResp.IsSuccessStatusCode)
            throw new HttpRequestException($"complete failed: {(int)compResp.StatusCode} {body}");

        Report("Transcribed", "transcribing", segTotal, segTotal);

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

    /// <summary>
    /// Polls <c>GET /status</c> while the (blocking) complete call runs, pushing
    /// the server's transcribed-segment count onto the progress bar. Best-effort
    /// and self-contained: it never throws (the authoritative success/failure is
    /// the complete response), so the caller can simply await it after cancel.
    /// </summary>
    private async Task PollTranscribeAsync(
        HttpClient http, string recordingId, int fallbackTotal,
        Action<string, string, int, int> report, CancellationToken ct)
    {
        var caseInsensitive = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1500, ct);
                var resp = await http.GetAsync(
                    $"{_baseUrl}/ingest/recording/{recordingId}/status", ct);
                if (!resp.IsSuccessStatusCode) continue;
                var json = await resp.Content.ReadAsStringAsync(ct);
                var st = JsonSerializer.Deserialize<CompleteStatus>(json, caseInsensitive);
                if (st is null) continue;
                var done = st.ChunksTranscribed;
                var total = st.ChunksTotal > 0 ? st.ChunksTotal : fallbackTotal;
                if (done > total) done = total;
                report($"Transcribing {done}/{total}", "transcribing", done, total);
            }
            catch (OperationCanceledException)
            {
                return; // complete finished; stop polling
            }
            catch (Exception)
            {
                // A dropped/garbled status poll is not an upload failure; the
                // complete call decides the outcome. Keep polling.
            }
        }
    }

    private sealed record CompleteStatus(
        string? State, string? VaultDocId, string? Transcript,
        int ChunksTranscribed, int ChunksTotal);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static StringContent JsonBody(object o)
        => new(JsonSerializer.Serialize(o, JsonOpts), Encoding.UTF8, "application/json");
}
