using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CcRecorder.Recording;

/// <summary>
/// Talks to the CC Director Gateway ingest API over HTTPS (Tailscale). Two
/// deliberately separate operations, because they are two separate things:
///
///   1. <see cref="UploadSegmentsAsync"/> - the UPLOAD. Moves the audio bytes
///      from the phone to the server. Resumable and idempotent. Succeeds the
///      moment every segment is on the server. Nothing to do with OpenAI.
///
///   2. <see cref="TranscribeAsync"/> - the TRANSCRIPTION. A separate server
///      job (POST complete) that turns the already-uploaded audio into text.
///      Slow, optional, and allowed to fail without ever affecting the upload.
///
/// The caller keeps upload state and transcription state apart so a
/// transcription problem can never make a successfully-uploaded recording look
/// like a failed upload.
/// </summary>
public sealed class IngestUploader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = null, // server is case-insensitive; keep PascalCase
    };
    private static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _baseUrl;
    private readonly string _token;

    public IngestUploader(string baseUrl, string token)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = token;
    }

    /// <summary>Result of the transcription step (server status after complete).</summary>
    public sealed record TranscriptionResult(string State, string? VaultDocId, string? Transcript);

    private HttpClient NewClient()
    {
        // 10 minutes: the byte transfer of a long recording can take a while on
        // a slow link. This is the phone's patience, independent of any
        // server-side transcription timeout.
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!string.IsNullOrWhiteSpace(_token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return http;
    }

    // ===== 1. UPLOAD =======================================================

    /// <summary>
    /// Transfers every not-yet-uploaded segment to the server, then returns.
    /// This is the whole upload. Segments already confirmed on a prior attempt
    /// are skipped (resume), so a dropped connection picks up where it left off.
    /// On return, all bytes are safely on the server. Does NOT trigger or wait
    /// on transcription.
    /// </summary>
    /// <param name="onProgress">Fired after each step; the latest sending
    /// progress is written onto the manifest first, so the caller just persists
    /// + refreshes. Also fired after each confirmed segment so the resume point
    /// survives a crash.</param>
    public async Task UploadSegmentsAsync(
        LocalManifest manifest,
        string recordingFolder,
        Action? onProgress = null,
        CancellationToken ct = default)
    {
        void Report(string label, int current, int total)
        {
            manifest.UploadProgress = label;
            manifest.UploadPhase = "sending";
            manifest.UploadCurrent = current;
            manifest.UploadTotal = total;
            onProgress?.Invoke();
        }

        var ordered = manifest.Chunks.OrderBy(c => c.Index).ToList();
        int total = ordered.Count;

        using var http = NewClient();

        // Register (idempotent).
        Report("Preparing upload", manifest.Chunks.Count(c => c.Uploaded), total);
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
        var regResp = await http.PostAsync($"{_baseUrl}/ingest/recording", JsonBody(register), ct);
        regResp.EnsureSuccessStatusCode();

        int sent = ordered.Count(c => c.Uploaded);
        Report($"Sending {sent}/{total}", sent, total);
        for (int i = 0; i < total; i++)
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

            // Persist the win immediately so a later failure resumes after it.
            chunk.Uploaded = true;
            sent++;
            Report($"Sending {sent}/{total}", sent, total);
        }
    }

    // ===== 2. TRANSCRIPTION ================================================

    /// <summary>
    /// Asks the server to transcribe the already-uploaded audio (POST complete)
    /// and reports progress by polling status alongside the blocking call.
    /// Throws if the server fails or times out transcription - the caller
    /// treats that as a transcription failure only, never as an upload failure.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeAsync(
        LocalManifest manifest,
        Action? onProgress = null,
        CancellationToken ct = default)
    {
        void Report(string label, int current, int total)
        {
            manifest.UploadProgress = label;
            manifest.UploadPhase = "transcribing";
            manifest.UploadCurrent = current;
            manifest.UploadTotal = total;
            onProgress?.Invoke();
        }

        int total = manifest.Chunks.Count;
        using var http = NewClient();

        Report($"Transcribing 0/{total}", 0, total);
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pollTask = PollTranscribeAsync(http, manifest.RecordingId, total, Report, pollCts.Token);

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
            throw new HttpRequestException($"transcription failed: {(int)compResp.StatusCode} {body}");

        Report($"Transcribing {total}/{total}", total, total);

        var status = JsonSerializer.Deserialize<CompleteStatus>(body, CaseInsensitive);
        return new TranscriptionResult(
            State: string.IsNullOrWhiteSpace(status?.State) ? "transcribed" : status!.State!,
            VaultDocId: NullIfEmpty(status?.VaultDocId),
            Transcript: NullIfEmpty(status?.Transcript));
    }

    /// <summary>
    /// Polls GET status while the blocking complete call runs, pushing the
    /// server's transcribed-segment count onto the progress bar. Best-effort:
    /// never throws (the complete response is the authoritative outcome), so the
    /// caller can simply await it after cancelling.
    /// </summary>
    private async Task PollTranscribeAsync(
        HttpClient http, string recordingId, int fallbackTotal,
        Action<string, int, int> report, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1500, ct);
                var resp = await http.GetAsync($"{_baseUrl}/ingest/recording/{recordingId}/status", ct);
                if (!resp.IsSuccessStatusCode) continue;
                var json = await resp.Content.ReadAsStringAsync(ct);
                var st = JsonSerializer.Deserialize<CompleteStatus>(json, CaseInsensitive);
                if (st is null) continue;
                var done = st.ChunksTranscribed;
                var total = st.ChunksTotal > 0 ? st.ChunksTotal : fallbackTotal;
                if (done > total) done = total;
                report($"Transcribing {done}/{total}", done, total);
            }
            catch (OperationCanceledException)
            {
                return; // complete finished; stop polling
            }
            catch (Exception)
            {
                // A dropped/garbled status poll is not a failure; the complete
                // call decides the outcome. Keep polling.
            }
        }
    }

    // ===== deletion sync ===================================================

    /// <summary>
    /// Returns the set of recording ids the server currently has, or null if the
    /// server could not be reached/parsed. Null means "unknown" - the caller
    /// must NOT treat it as "server has nothing" and must not delete anything.
    /// </summary>
    public async Task<HashSet<string>?> ListServerRecordingIdsAsync(CancellationToken ct = default)
    {
        using var http = NewClient();
        try
        {
            var resp = await http.GetAsync($"{_baseUrl}/ingest/recordings", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var items = JsonSerializer.Deserialize<List<ServerRecording>>(json, CaseInsensitive);
            if (items is null) return null;
            return items
                .Where(i => !string.IsNullOrWhiteSpace(i.RecordingId))
                .Select(i => i.RecordingId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Network/parse failure -> "unknown", which the caller treats as
            // "do not delete". Not a silent fallback: it deliberately prevents
            // a destructive action when the server state can't be confirmed.
            return null;
        }
    }

    private sealed record ServerRecording(string? RecordingId);

    private sealed record CompleteStatus(
        string? State, string? VaultDocId, string? Transcript,
        int ChunksTranscribed, int ChunksTotal);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static StringContent JsonBody(object o)
        => new(JsonSerializer.Serialize(o, JsonOpts), Encoding.UTF8, "application/json");
}
