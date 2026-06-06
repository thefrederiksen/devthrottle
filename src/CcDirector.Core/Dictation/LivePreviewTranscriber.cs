using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// Live transcript preview for dictation (issue #215). While the user is
/// still recording, this re-transcribes the GROWING clip on a short cadence
/// and raises <see cref="OnPreview"/> with the full text so far, so the
/// dialog shows the words as they are spoken - continuously, not only at
/// speech pauses.
///
/// WHY WHOLE-CLIP RE-TRANSCRIPTION
/// -------------------------------
/// The realtime provider with turn detection disabled produces NO transcript
/// until the final commit, and chunk-committing mid-speech would split words
/// at every boundary and garble the final transcript. Re-transcribing the
/// whole accumulated clip each pass (the MyQuietShadow approach) has no
/// boundaries: each preview is a clean transcription of everything said so
/// far, and each pass self-corrects the previous pass's cut-off tail word.
///
/// The preview is DISPLAY-ONLY. The authoritative final transcript still
/// comes from the realtime provider's commit at stop, followed by cleanup;
/// nothing here touches that path. A failed preview pass is logged and the
/// next tick simply tries again with more audio.
///
/// Audio format: PCM16 mono. The sample rate is supplied by the caller
/// (24 kHz for both the desktop mic and the browser AudioWorklet). Each
/// pass wraps the raw PCM in a standard WAV header and posts it to OpenAI's
/// batch <c>/v1/audio/transcriptions</c> endpoint with the same vocabulary
/// prompt the realtime session uses, so preview and final hear the same
/// glossary bias.
///
/// Threading: <see cref="Append"/> is called from the session's push path
/// (arbitrary threads) and only copies bytes under a lock. One background
/// loop does all HTTP work; a pass runs to completion before the next tick
/// is considered, so passes never overlap and a slow pass naturally slows
/// the cadence instead of stacking requests.
/// </summary>
public sealed class LivePreviewTranscriber : IAsyncDisposable
{
    private const string TranscriptionEndpoint = "https://api.openai.com/v1/audio/transcriptions";
    public const string DefaultModel = "gpt-4o-mini-transcribe";

    /// <summary>
    /// Hard ceiling on the clip the preview will keep re-uploading. Beyond
    /// this the preview stops updating (with a log line) - the recording and
    /// the final transcript are unaffected. 10 minutes of 24 kHz PCM16 mono.
    /// </summary>
    internal long MaxPreviewBytes { get; set; } = 10L * 60 * 24000 * 2;

    /// <summary>Delay between preview passes. Internal-settable so tests run fast.</summary>
    internal TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Minimum new audio since the last pass before another pass is worth
    /// making. Quarter of a second at 24 kHz PCM16 mono - mirrors the
    /// MyQuietShadow live loop's threshold.
    /// </summary>
    internal int MinNewBytes { get; set; } = 12000;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _sampleRate;

    private readonly object _gate = new();
    private readonly MemoryStream _clip = new();
    private long _lastPassBytes;
    private bool _cappedLogged;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private string _sttPrompt = "";
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Full transcript-so-far of the growing clip. Raised on the preview
    /// loop's background thread after each successful pass. Semantically the
    /// same shape as a provider partial: one growing string per utterance.
    /// </summary>
    public event Action<string>? OnPreview;

    /// <param name="apiKey">OpenAI API key. Read from <c>OPENAI_API_KEY</c> if blank.</param>
    /// <param name="model">Transcription model for preview passes. Defaults to <see cref="DefaultModel"/> (cheap + fast; the final transcript uses the realtime provider's model).</param>
    /// <param name="sampleRate">PCM16 mono sample rate of the appended audio. Both dictation surfaces produce 24 kHz.</param>
    /// <param name="httpClient">Optional shared HttpClient. The transcriber creates and owns one if null.</param>
    public LivePreviewTranscriber(
        string? apiKey = null,
        string model = DefaultModel,
        int sampleRate = 24000,
        HttpClient? httpClient = null)
    {
        _apiKey = ResolveApiKey(apiKey);
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        _sampleRate = sampleRate > 0
            ? sampleRate
            : throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }
    }

    /// <summary>Total PCM bytes accumulated. Exposed for diagnostics and tests.</summary>
    public long ClipBytes { get { lock (_gate) return _clip.Length; } }

    /// <summary>Start the preview loop. The prompt biases every pass toward the user's vocabulary.</summary>
    public void Start(string sttPrompt)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LivePreviewTranscriber));
        if (_started) throw new InvalidOperationException("LivePreviewTranscriber already started.");
        _started = true;
        _sttPrompt = sttPrompt ?? "";
        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_loopCts.Token));
        FileLog.Write($"[LivePreviewTranscriber] Start: model={_model}, rate={_sampleRate}, tick={TickInterval.TotalMilliseconds}ms");
    }

    /// <summary>
    /// Append a chunk of captured PCM16 audio. Cheap: copies the bytes under
    /// a lock and returns; all transcription happens on the preview loop.
    /// </summary>
    public void Append(ReadOnlyMemory<byte> chunk)
    {
        if (_disposed || chunk.Length == 0) return;
        lock (_gate)
        {
            _clip.Write(chunk.Span);
        }
    }

    /// <summary>
    /// Stop the preview loop. Any in-flight pass is cancelled; no
    /// <see cref="OnPreview"/> fires after this returns.
    /// </summary>
    public async Task StopAsync()
    {
        var cts = _loopCts;
        var task = _loopTask;
        _loopCts = null;
        _loopTask = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { /* disposed */ }
        if (task is not null)
        {
            try { await task; }
            catch (Exception ex) { FileLog.Write($"[LivePreviewTranscriber] StopAsync: loop ended with: {ex.Message}"); }
        }
        cts.Dispose();
        FileLog.Write($"[LivePreviewTranscriber] StopAsync: clip={ClipBytes} bytes");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TickInterval, ct); }
            catch (OperationCanceledException) { return; }

            var clip = SnapshotIfGrown();
            if (clip is null) continue;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var text = await TranscribeAsync(clip, ct);
                if (ct.IsCancellationRequested) return;
                FileLog.Write($"[LivePreviewTranscriber] pass done in {sw.ElapsedMilliseconds}ms: clip={clip.Length} bytes, text_len={text.Length}");
                if (text.Length > 0)
                {
                    try { OnPreview?.Invoke(text); }
                    catch (Exception ex) { FileLog.Write($"[LivePreviewTranscriber] OnPreview handler threw: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // A dropped preview pass is harmless: the box keeps the last
                // preview and the next tick retries with more audio. The final
                // transcript does not depend on this loop at all.
                FileLog.Write($"[LivePreviewTranscriber] pass FAILED in {sw.ElapsedMilliseconds}ms (next tick retries): {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Snapshot the whole clip if enough new audio arrived since the last
    /// pass; null when there is nothing worth transcribing yet or the clip
    /// passed the preview size cap.
    /// </summary>
    private byte[]? SnapshotIfGrown()
    {
        lock (_gate)
        {
            if (_clip.Length > MaxPreviewBytes)
            {
                if (!_cappedLogged)
                {
                    _cappedLogged = true;
                    FileLog.Write($"[LivePreviewTranscriber] clip exceeded {MaxPreviewBytes} bytes - preview frozen, recording and final transcript unaffected");
                }
                return null;
            }
            if (_clip.Length - _lastPassBytes < MinNewBytes) return null;
            _lastPassBytes = _clip.Length;
            return _clip.ToArray();
        }
    }

    private async Task<string> TranscribeAsync(byte[] pcm, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var content = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(WrapPcm16InWav(pcm, _sampleRate));
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "file", "preview.wav");
        content.Add(new StringContent(_model), "model");
        if (!string.IsNullOrWhiteSpace(_sttPrompt))
            content.Add(new StringContent(_sttPrompt), "prompt");
        content.Add(new StringContent("json"), "response_format");
        request.Content = content;

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"preview transcription failed: HTTP {(int)response.StatusCode} - {Truncate(body, 200)}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var textProp)
            ? (textProp.GetString() ?? "").Trim()
            : "";
    }

    /// <summary>
    /// Wrap raw PCM16 mono samples in a standard 44-byte RIFF/WAV header so
    /// the batch endpoint can decode them. Internal for header-level tests.
    /// </summary>
    internal static byte[] WrapPcm16InWav(byte[] pcm, int sampleRate)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = channels * (bitsPerSample / 8);

        var wav = new byte[44 + pcm.Length];
        using var ms = new MemoryStream(wav);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                  // PCM fmt chunk size
        w.Write((short)1);            // audio format: PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write(bitsPerSample);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        return wav;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        lock (_gate) _clip.Dispose();
        if (_ownsHttp) _http.Dispose();
    }

    private static string ResolveApiKey(string? explicitKey)
    {
        if (!string.IsNullOrWhiteSpace(explicitKey))
            return explicitKey.Trim();
        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(env))
            throw new InvalidOperationException(
                "OpenAI API key not provided and OPENAI_API_KEY environment variable is not set.");
        return env.Trim();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}
