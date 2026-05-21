using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Voice;

/// <summary>
/// Phase 3 of the voice mode.  Wraps OpenAI's /v1/audio/speech endpoint so the
/// Director can produce natural-sounding TTS audio for the voice tab to play.
///
/// The OpenAI key resolution + the model/voice defaults all live in
/// <see cref="AgentOptions"/>; this service is a thin HTTP wrapper.
/// </summary>
public sealed class TtsService
{
    public const string Endpoint = "https://api.openai.com/v1/audio/speech";
    // OpenAI's documented limit for the tts-1 model is 4096 characters.  An
    // earlier 1000-char cap silently truncated long replies (e.g. essays),
    // making the audio stop mid-sentence.
    public const int MaxTextChars = 4096;

    private readonly AgentOptions _options;

    public TtsService(AgentOptions options)
    {
        _options = options;
    }

    /// <summary>True when an OpenAI key is configured.</summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.ResolveOpenAiKey());

    /// <summary>
    /// Generate audio for the given text.  Returns the raw audio bytes
    /// (mp3 / audio-mpeg) and a status string.  Caller is responsible for
    /// streaming the bytes to the client.
    /// </summary>
    public async Task<TtsResult> GenerateAsync(string text, string? voiceOverride, string? modelOverride, CancellationToken ct = default)
    {
        var key = _options.ResolveOpenAiKey();
        if (string.IsNullOrWhiteSpace(key))
            return TtsResult.Error("no_key", "OpenAI API key missing");

        if (string.IsNullOrWhiteSpace(text))
            return TtsResult.Error("empty_text", "text is required");

        var trimmed = text.Length > MaxTextChars ? text[..MaxTextChars] : text;
        var voice = !string.IsNullOrWhiteSpace(voiceOverride) ? voiceOverride.Trim() : _options.TtsVoice;
        var model = !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride.Trim() : _options.TtsModel;

        FileLog.Write($"[TtsService] GenerateAsync: model={model}, voice={voice}, chars={trimmed.Length}");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

            var payload = JsonContent.Create(new
            {
                model,
                voice,
                input = trimmed,
                response_format = "mp3",
            });

            using var resp = await http.PostAsync(Endpoint, payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                FileLog.Write($"[TtsService] OpenAI returned {(int)resp.StatusCode}: {Truncate(body, 400)}");
                return TtsResult.Error("openai_failed", $"OpenAI returned {(int)resp.StatusCode}: {Truncate(body, 300)}");
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0)
                return TtsResult.Error("openai_failed", "OpenAI returned empty audio");

            return TtsResult.Ok(bytes, "audio/mpeg");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TtsService] GenerateAsync FAILED: {ex.Message}");
            return TtsResult.Error("internal_error", ex.Message);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}

/// <summary>
/// Result of a TTS call.  When <see cref="Status"/> is "ok", <see cref="AudioBytes"/>
/// and <see cref="ContentType"/> are populated.  Otherwise <see cref="ErrorMessage"/> is.
/// </summary>
public sealed class TtsResult
{
    public string Status { get; private init; } = "";
    public string? ErrorMessage { get; private init; }
    public byte[]? AudioBytes { get; private init; }
    public string? ContentType { get; private init; }

    public bool Success => Status == "ok";

    public static TtsResult Ok(byte[] bytes, string contentType)
        => new() { Status = "ok", AudioBytes = bytes, ContentType = contentType };

    public static TtsResult Error(string status, string message)
        => new() { Status = status, ErrorMessage = message };
}
