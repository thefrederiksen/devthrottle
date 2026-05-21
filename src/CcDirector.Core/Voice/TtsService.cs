using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Voice;

/// <summary>
/// Phase 3 of the voice mode.  Wraps OpenAI's /v1/audio/speech endpoint so the
/// Director can produce natural-sounding TTS audio for the voice tab to play.
///
/// The OpenAI key resolution + the model/voice defaults all live in
/// <see cref="AgentOptions"/>; this service is a thin HTTP wrapper.
///
/// LONG TEXT HANDLING: OpenAI tts-1 caps each call at 4096 input chars.  For
/// replies longer than that we chunk at sentence boundaries (max
/// <see cref="MaxChunkChars"/> per chunk), call OpenAI in parallel for each
/// chunk, and concatenate the returned MP3 byte streams.  MP3 is frame-based,
/// so byte concatenation produces a single valid stream that plays end-to-end.
/// Effective input limit is therefore bounded only by OpenAI's per-minute
/// quota, not by the per-call 4096 cap.
/// </summary>
public sealed class TtsService
{
    public const string Endpoint = "https://api.openai.com/v1/audio/speech";
    // Per-chunk size.  Well under OpenAI's documented 4096-char per-call limit.
    // We use a smaller chunk on purpose: OpenAI tts-1 latency scales roughly
    // linearly with input length, and several smaller calls running in parallel
    // finish faster than fewer big ones.  Empirically a 3400-char call takes
    // ~20-25 s; an 800-char call takes ~3-5 s.
    public const int MaxChunkChars = 800;
    // Kept for backwards compatibility with callers that read it.
    public const int MaxTextChars = MaxChunkChars;

    private readonly AgentOptions _options;

    public TtsService(AgentOptions options)
    {
        _options = options;
    }

    /// <summary>True when an OpenAI key is configured.</summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.ResolveOpenAiKey());

    /// <summary>
    /// Generate audio for the given text.  Returns the raw audio bytes
    /// (mp3 / audio-mpeg) and a status string.  For inputs longer than
    /// <see cref="MaxChunkChars"/> the text is split at sentence boundaries
    /// and the resulting MP3s are concatenated.
    /// </summary>
    public async Task<TtsResult> GenerateAsync(string text, string? voiceOverride, string? modelOverride, CancellationToken ct = default)
    {
        var key = _options.ResolveOpenAiKey();
        if (string.IsNullOrWhiteSpace(key))
            return TtsResult.Error("no_key", "OpenAI API key missing");

        if (string.IsNullOrWhiteSpace(text))
            return TtsResult.Error("empty_text", "text is required");

        var voice = !string.IsNullOrWhiteSpace(voiceOverride) ? voiceOverride.Trim() : _options.TtsVoice;
        var model = !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride.Trim() : _options.TtsModel;

        var chunks = SplitIntoChunks(text, MaxChunkChars);
        FileLog.Write($"[TtsService] GenerateAsync: model={model}, voice={voice}, totalChars={text.Length}, chunks={chunks.Count}");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

            // Single-chunk fast path: no parallelism overhead.
            if (chunks.Count == 1)
            {
                var single = await CallOpenAiAsync(http, model, voice, chunks[0], ct);
                return single;
            }

            // Multi-chunk: fire all in parallel.  OpenAI's tts-1 latency is
            // ~2 s per chunk, so 10 chunks in parallel is ~2-3 s wall-clock
            // (vs ~20 s sequential).  If any chunk fails, propagate the
            // first error - partial audio is worse than no audio because
            // the listener would not know where it cut off.
            var tasks = chunks.Select(c => CallOpenAiAsync(http, model, voice, c, ct)).ToList();
            var results = await Task.WhenAll(tasks);

            for (int i = 0; i < results.Length; i++)
            {
                if (!results[i].Success)
                {
                    FileLog.Write($"[TtsService] chunk {i + 1}/{results.Length} failed: {results[i].ErrorMessage}");
                    return results[i];
                }
            }

            // Concatenate MP3 byte streams in order.  MP3 is a sequence of
            // independent frames; byte-level concatenation produces a single
            // valid stream that browsers play as one continuous audio.
            var total = results.Sum(r => r.AudioBytes!.Length);
            var combined = new byte[total];
            int offset = 0;
            foreach (var r in results)
            {
                var b = r.AudioBytes!;
                Buffer.BlockCopy(b, 0, combined, offset, b.Length);
                offset += b.Length;
            }
            FileLog.Write($"[TtsService] joined {results.Length} chunks into {combined.Length} bytes");
            return TtsResult.Ok(combined, "audio/mpeg");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TtsService] GenerateAsync FAILED: {ex.Message}");
            return TtsResult.Error("internal_error", ex.Message);
        }
    }

    /// <summary>One OpenAI /v1/audio/speech call.  Returns raw MP3 bytes.</summary>
    private static async Task<TtsResult> CallOpenAiAsync(HttpClient http, string model, string voice, string input, CancellationToken ct)
    {
        var payload = JsonContent.Create(new
        {
            model,
            voice,
            input,
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

    /// <summary>
    /// Split text into chunks of at most <paramref name="maxChars"/> chars,
    /// preferring sentence boundaries ('.', '!', '?').  Falls back to word
    /// boundaries if a single sentence is itself larger than maxChars.  Last
    /// resort: hard split at maxChars (only triggers for pathological inputs
    /// like a single 4000-char word).
    /// </summary>
    internal static List<string> SplitIntoChunks(string text, int maxChars)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text)) return chunks;
        if (text.Length <= maxChars) { chunks.Add(text); return chunks; }

        var sentences = SplitIntoSentences(text);
        var current = new System.Text.StringBuilder();

        foreach (var raw in sentences)
        {
            var s = raw;
            // Handle a single sentence that itself exceeds maxChars: split on
            // word boundaries.
            while (s.Length > maxChars)
            {
                // Flush whatever we have buffered first.
                if (current.Length > 0)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                }
                var cut = FindSafeBreak(s, maxChars);
                chunks.Add(s[..cut].Trim());
                s = s[cut..];
            }

            if (current.Length + s.Length + 1 > maxChars && current.Length > 0)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
            current.Append(s);
        }

        if (current.Length > 0) chunks.Add(current.ToString().Trim());
        return chunks.Where(c => c.Length > 0).ToList();
    }

    /// <summary>
    /// Walk the text and emit each sentence (keeping its terminal punctuation
    /// and trailing whitespace).  Treats '.', '!', '?' as boundaries when
    /// followed by whitespace or end-of-string.
    /// </summary>
    private static List<string> SplitIntoSentences(string text)
    {
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '.' || c == '!' || c == '?')
            {
                int j = i + 1;
                if (j == text.Length || char.IsWhiteSpace(text[j]))
                {
                    // Include trailing whitespace so concatenation reads
                    // naturally to the model.
                    while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                    result.Add(text[start..j]);
                    start = j;
                    i = j - 1;
                }
            }
        }
        if (start < text.Length) result.Add(text[start..]);
        return result;
    }

    /// <summary>
    /// Find the last whitespace at or before <paramref name="maxChars"/>; if
    /// none, return maxChars (hard split).  Used when a single sentence is
    /// larger than the chunk limit.
    /// </summary>
    private static int FindSafeBreak(string s, int maxChars)
    {
        int cap = Math.Min(s.Length, maxChars);
        for (int i = cap - 1; i > cap / 2; i--)
        {
            if (char.IsWhiteSpace(s[i])) return i + 1;
        }
        return cap;
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
