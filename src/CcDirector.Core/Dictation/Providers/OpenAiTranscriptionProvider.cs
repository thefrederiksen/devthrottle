using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation.Providers;

/// <summary>
/// Phase 1 dictation provider: batch transcription via OpenAI's
/// <c>/v1/audio/transcriptions</c> endpoint with the <c>gpt-4o-transcribe</c>
/// model.
///
/// Audio is buffered in memory between <see cref="StartAsync"/> and
/// <see cref="StopAsync"/>, then sent as a single multipart upload. The
/// vocabulary prompt is attached as the <c>prompt</c> form field, which
/// biases decoding toward the user's glossary.
///
/// Streaming partial transcripts are not produced; <see cref="OnPartial"/>
/// fires once with the final text just before <see cref="StopAsync"/>
/// returns so consumers that wire UI off partials still get one update.
///
/// The Realtime WebSocket variant for true low-latency partials arrives in
/// Phase 3 as <c>OpenAiRealtimeProvider</c> behind the same interface.
/// </summary>
public sealed class OpenAiTranscriptionProvider : IDictationProvider
{
    /// <summary>Default OpenAI-compatible base URL (the bring-your-own-key path).</summary>
    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    public const string DefaultModel = "gpt-4o-transcribe";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _audioContentType;
    private readonly string _audioFileName;
    private readonly string _transcriptionEndpoint;
    private readonly bool _wrapPcmInWav;
    private readonly int _pcmSampleRate;
    private readonly int _pcmChannels;
    private readonly int _pcmBitsPerSample;

    private readonly object _gate = new();
    private readonly MemoryStream _audioBuffer = new();
    private string _sttPrompt = "";
    private bool _started;
    private bool _stopped;
    private bool _disposed;

    public event Action<string>? OnPartial;

    /// <param name="apiKey">OpenAI API key. Read from <c>OPENAI_API_KEY</c> if blank.</param>
    /// <param name="model">Transcription model. Defaults to <c>gpt-4o-transcribe</c>.</param>
    /// <param name="audioContentType">MIME type for the audio payload (e.g. <c>audio/mpeg</c>, <c>audio/wav</c>).</param>
    /// <param name="audioFileName">Filename hint sent in the multipart upload. Extension matters: it tells the server how to decode the bytes.</param>
    /// <param name="httpClient">Optional shared HttpClient. The provider creates and owns one if null.</param>
    /// <param name="baseUrl">OpenAI-compatible base URL (issue #497). Defaults to <see cref="DefaultBaseUrl"/>; pass DevThrottle's base URL to route through its managed proxy.</param>
    /// <param name="wrapPcmInWav">When true, the raw PCM bytes pushed via <see cref="PushAudioAsync"/> are wrapped in a RIFF WAV header on <see cref="StopAsync"/> before the multipart upload. Use this for the desktop mic path where NAudio delivers raw PCM16 that the transcription API cannot accept without a container header.</param>
    /// <param name="pcmSampleRate">Sample rate of the incoming PCM, used only when <paramref name="wrapPcmInWav"/> is true.</param>
    /// <param name="pcmChannels">Channel count of the incoming PCM, used only when <paramref name="wrapPcmInWav"/> is true.</param>
    /// <param name="pcmBitsPerSample">Bits per sample of the incoming PCM, used only when <paramref name="wrapPcmInWav"/> is true.</param>
    public OpenAiTranscriptionProvider(
        string? apiKey = null,
        string model = DefaultModel,
        string audioContentType = "audio/mpeg",
        string audioFileName = "audio.mp3",
        HttpClient? httpClient = null,
        string? baseUrl = null,
        bool wrapPcmInWav = false,
        int pcmSampleRate = 24000,
        int pcmChannels = 1,
        int pcmBitsPerSample = 16)
    {
        _apiKey = ResolveApiKey(apiKey);
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        _audioContentType = audioContentType;
        _audioFileName = audioFileName;
        _transcriptionEndpoint =
            (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/')) + "/audio/transcriptions";
        _wrapPcmInWav = wrapPcmInWav;
        _pcmSampleRate = pcmSampleRate;
        _pcmChannels = pcmChannels;
        _pcmBitsPerSample = pcmBitsPerSample;

        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }
    }

    public Task StartAsync(string sttPrompt, CancellationToken ct = default)
    {
        FileLog.Write($"[OpenAiTranscriptionProvider] StartAsync: prompt_len={sttPrompt?.Length ?? 0}, model={_model}");
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OpenAiTranscriptionProvider));
            if (_started && !_stopped)
                throw new InvalidOperationException("Provider is already in a session. Call StopAsync first.");
            _audioBuffer.SetLength(0);
            _sttPrompt = sttPrompt ?? "";
            _started = true;
            _stopped = false;
        }
        return Task.CompletedTask;
    }

    public Task PushAudioAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
    {
        if (chunk.Length == 0) return Task.CompletedTask;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OpenAiTranscriptionProvider));
            if (!_started || _stopped)
                throw new InvalidOperationException("PushAudioAsync called outside of a session. Call StartAsync first.");
            _audioBuffer.Write(chunk.Span);
        }
        return Task.CompletedTask;
    }

    public async Task<string> StopAsync(CancellationToken ct = default)
    {
        byte[] audioBytes;
        string prompt;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OpenAiTranscriptionProvider));
            if (!_started) throw new InvalidOperationException("StopAsync called without a session.");
            if (_stopped) throw new InvalidOperationException("Session already stopped.");
            _stopped = true;
            audioBytes = _audioBuffer.ToArray();
            _audioBuffer.SetLength(0);
            prompt = _sttPrompt;
        }

        if (_wrapPcmInWav)
            audioBytes = WrapPcmInWav(audioBytes);

        FileLog.Write($"[OpenAiTranscriptionProvider] StopAsync: audio={audioBytes.Length} bytes, wrapPcmInWav={_wrapPcmInWav}");

        if (audioBytes.Length == 0)
            return "";

        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _transcriptionEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var content = new MultipartFormDataContent();

            var audioContent = new ByteArrayContent(audioBytes);
            // MediaTypeHeaderValue's constructor only accepts the bare
            // "type/subtype" form; with parameters (e.g. "audio/webm;codecs=opus"
            // from MediaRecorder.mimeType) it throws FormatException. OpenAI
            // detects the codec from the file bytes regardless, so strip any
            // parameter suffix before assigning.
            var bareContentType = _audioContentType.Split(';')[0].Trim();
            audioContent.Headers.ContentType = new MediaTypeHeaderValue(bareContentType);
            content.Add(audioContent, "file", _audioFileName);

            content.Add(new StringContent(_model), "model");
            if (!string.IsNullOrWhiteSpace(prompt))
                content.Add(new StringContent(prompt), "prompt");
            content.Add(new StringContent("json"), "response_format");

            request.Content = content;

            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                FileLog.Write($"[OpenAiTranscriptionProvider] HTTP {(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms: {Truncate(body, 400)}");
                throw new HttpRequestException($"OpenAI transcription failed: HTTP {(int)response.StatusCode} - {Truncate(body, 200)}");
            }

            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.TryGetProperty("text", out var textProp)
                ? (textProp.GetString() ?? "")
                : "";
            text = text.Trim();

            sw.Stop();
            FileLog.Write($"[OpenAiTranscriptionProvider] StopAsync done in {sw.ElapsedMilliseconds}ms, text_len={text.Length}");

            // Fire one partial right before returning so listeners get something.
            OnPartial?.Invoke(text);
            return text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OpenAiTranscriptionProvider] StopAsync FAILED in {sw.ElapsedMilliseconds}ms: {ex.Message}");
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _audioBuffer.Dispose();
        }
        if (_ownsHttp) _http.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Wrap raw PCM bytes in a minimal RIFF WAV container. Transcription APIs
    /// require a properly-formed audio file; raw PCM without a header is rejected.
    /// </summary>
    private byte[] WrapPcmInWav(byte[] pcm)
    {
        int byteRate = _pcmSampleRate * _pcmChannels * _pcmBitsPerSample / 8;
        int blockAlign = _pcmChannels * _pcmBitsPerSample / 8;
        using var ms = new MemoryStream(44 + pcm.Length);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcm.Length);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)_pcmChannels);
        bw.Write(_pcmSampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)_pcmBitsPerSample);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(pcm.Length);
        bw.Write(pcm, 0, pcm.Length);
        return ms.ToArray();
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
