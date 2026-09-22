using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Utilities;

using CcDirector.Gateway.Speech;

namespace CcDirector.Core.Voice;

/// <summary>
/// Phase 3 of the voice mode. Wraps the hosted <c>/v1/audio/speech</c> endpoint so the Director can
/// produce natural-sounding TTS audio for the voice tab to play.
///
/// The credential resolution plus the model/voice defaults are supplied by the routing and settings
/// configuration; this service is a thin HTTP wrapper.
///
/// LONG TEXT HANDLING: hosted speech providers cap each call. For replies longer than that we chunk at
/// sentence boundaries (max <see cref="MaxChunkChars"/> per chunk), call the provider in parallel for each
/// chunk, and concatenate the returned MP3 byte streams.  MP3 is frame-based,
/// so byte concatenation produces a single valid stream that plays end-to-end.
/// Effective input limit is therefore bounded only by provider quota, not by the per-call cap.
///
/// RESILIENCE (issue #389): a single stalled provider request used to hang the
/// whole voice turn on the shared <c>HttpClient.Timeout</c> of 180 s, then
/// return empty audio.  To keep the voice experience snappy this service now
/// applies a short PER-REQUEST timeout (<see cref="PerRequestTimeout"/>) per
/// chunk via a linked <see cref="CancellationTokenSource"/> + CancelAfter,
/// retries a transient single-chunk failure once, and caps the whole reply at
/// an overall budget (<see cref="OverallBudget"/>).  When TTS ultimately fails
/// the caller is told promptly (text-only fallback already happens upstream),
/// and the elapsed time + failing chunk index + reason are logged.
/// </summary>
public sealed class TtsService
{
    public const string Endpoint = "https://devthrottle.com/api/v1/audio/speech";
    // Per-chunk size. Well under common hosted speech per-call limits.
    // We use a smaller chunk on purpose: speech latency scales roughly
    // linearly with input length, and several smaller calls running in parallel
    // finish faster than fewer big ones.  Empirically a 3400-char call takes
    // ~20-25 s; an 800-char call takes ~3-5 s.
    public const int MaxChunkChars = 800;
    // Kept for backwards compatibility with callers that read it.
    public const int MaxTextChars = MaxChunkChars;

    // Per-request timeout for a single hosted /v1/audio/speech call. An ~800-char
    // chunk normally returns in ~3-5 s, so 30 s is generous headroom while still
    // failing ~6x faster than the old 180 s ceiling.  Issue #389.
    public static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(30);

    // Hard ceiling for generating the audio for one whole reply (all chunks,
    // including the single retry).  Guarantees a voice turn can never block for
    // minutes on TTS - worst case is this budget, after which we fail to text.
    public static readonly TimeSpan OverallBudget = TimeSpan.FromSeconds(60);

    private readonly AgentOptions _options;
    private readonly HttpMessageHandler? _handler;
    private readonly HostedAiKeyResolver? _keyResolver;

    /// <param name="options">Chunking + legacy standalone defaults.</param>
    /// <param name="keyResolver">When supplied (the consolidated AI-provider path), the base URL, key,
    /// voice, and model are resolved for DevThrottle - the same routing the transcription path uses,
    /// plus the chosen voice (<see cref="TtsVoiceConfig"/>).</param>
    public TtsService(AgentOptions options, HostedAiKeyResolver? keyResolver = null)
    {
        _options = options;
        _handler = null;
        _keyResolver = keyResolver;
    }

    /// <summary>
    /// Test seam (issue #389): inject the <see cref="HttpMessageHandler"/> the
    /// internal <see cref="HttpClient"/> is built on, so the per-request timeout
    /// and retry behaviour are unit-testable without hitting the network.  The
    /// handler is owned by the caller and is NOT disposed by this service.
    /// </summary>
    public TtsService(AgentOptions options, HttpMessageHandler handler, HostedAiKeyResolver? keyResolver = null)
    {
        _options = options;
        _handler = handler;
        _keyResolver = keyResolver;
    }

    /// <summary>True when a legacy standalone key is configured. Prefer
    /// <see cref="IsAvailableAsync"/>, which uses the DevThrottle account key.</summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.ResolveOpenAiKey());

    /// <summary>
    /// True when a credential is configured for the SELECTED provider (the consolidated AI-provider
    /// path). Uses the injected key resolver when present and falls back to the legacy standalone check
    /// when no resolver was supplied.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_keyResolver is not null)
            return !string.IsNullOrWhiteSpace(await _keyResolver.ResolveAsync(ct));
        return !string.IsNullOrWhiteSpace(_options.ResolveOpenAiKey());
    }

    /// <summary>The resolved text-to-speech target for one call: where to POST, the credential, the
    /// voice, and the model.</summary>
    private sealed record TtsTarget(string Endpoint, string Key, string Voice, string Model, string KeyMissingMessage);

    private async Task<TtsTarget> ResolveTargetAsync(string? voiceOverride, string? modelOverride, CancellationToken ct)
    {
        if (_keyResolver is not null)
        {
            var mode = TranscriptionModeConfig.Get();
            var ep = TranscriptionEndpointResolver.ResolveTts(mode);
            var key = await _keyResolver.ResolveAsync(ct) ?? "";
            var voice = !string.IsNullOrWhiteSpace(voiceOverride) ? voiceOverride!.Trim() : TtsVoiceConfig.Resolve(mode);
            var model = !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride!.Trim() : TtsModelConfig.Resolve(mode);
            var missing = "DevThrottle account key missing - sign in to DevThrottle";
            return new TtsTarget(ep.BaseUrl.TrimEnd('/') + "/audio/speech", key, voice, model, missing);
        }

        // Legacy no-resolver path retained for older tests/callers.
        var legacyKey = _options.ResolveOpenAiKey() ?? "";
        var legacyVoice = !string.IsNullOrWhiteSpace(voiceOverride) ? voiceOverride!.Trim() : _options.TtsVoice;
        var legacyModel = !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride!.Trim() : _options.TtsModel;
        return new TtsTarget(Endpoint, legacyKey, legacyVoice, legacyModel, "DevThrottle account key missing - sign in to DevThrottle");
    }

    /// <summary>
    /// SPEAK AN UTTERANCE (issue #1031): the sink entry point, and the only one the desktop uses.
    ///
    /// The language and the voice were decided once, by the Gateway, and arrive together in a package this
    /// method could not have been handed without a language - a bare string does not compile against this
    /// parameter. Nothing here resolves a language or picks a voice, which is the whole point: the desktop
    /// used to resolve its voice from a machine-global file and never saw the account, so a French account
    /// was read aloud in the machine's English voice.
    ///
    /// THE UTTERANCE'S VOICE IS PASSED AND ITS LANGUAGE IS NOT USED TO PICK A MODEL. modelOverride is null,
    /// always, on this path. A language selects the VOICE inside the one engine that already serves English;
    /// a language selecting an ENGINE is the failure that got this feature reverted
    /// (devthrottle_internal#547). If a future change ever passes something language-derived as a model here,
    /// that is the failure returning.
    /// </summary>
    public Task<TtsResult> GenerateAsync(SpokenUtterance utterance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        // IT READS AND CHECKS THE LANGUAGE BEFORE IT POSTS (re-audit). This overload used to consume only Text and
        // Voice, so an utterance carrying a null or unknown language was perfectly speakable through it - a
        // compiled probe pushed code "zz" and a null code through to two real provider posts. Reading it here
        // means a language that is not one we speak fails at the sink, before anything is billed, instead of being
        // ignored on the way past.
        var language = SpokenLanguages.TryResolve(utterance.LanguageCode)
            ?? throw new InvalidOperationException(
                $"Refusing to speak: this utterance's language is '{utterance.LanguageCode}', which is not one "
                + "DevThrottle speaks. It did not come from the settings resolver, and speaking it would read the "
                + "words with whatever voice was attached.");
        FileLog.Write($"[TtsService] speaking {utterance.Length} chars in {language.Code} with voice {utterance.Voice}");
        return GenerateAsync(utterance.Text, voiceOverride: utterance.Voice, modelOverride: null, ct);
    }

    /// <summary>
    /// Generate audio for the given text.  Returns the raw audio bytes
    /// (mp3 / audio-mpeg) and a status string.  For inputs longer than
    /// <see cref="MaxChunkChars"/> the text is split at sentence boundaries
    /// and the resulting MP3s are concatenated.
    /// </summary>
    public async Task<TtsResult> GenerateAsync(string text, string? voiceOverride, string? modelOverride, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TtsResult.Error("empty_text", "text is required");

        var target = await ResolveTargetAsync(voiceOverride, modelOverride, ct);
        if (string.IsNullOrWhiteSpace(target.Key))
            return TtsResult.Error("no_key", target.KeyMissingMessage);

        var key = target.Key;
        var voice = target.Voice;
        var model = target.Model;
        var endpoint = target.Endpoint;

        var chunks = SplitIntoChunks(text, MaxChunkChars);
        FileLog.Write($"[TtsService] GenerateAsync: endpoint={endpoint}, model={model}, voice={voice}, totalChars={text.Length}, chunks={chunks.Count}");

        var stopwatch = Stopwatch.StartNew();

        // Overall budget for the whole reply.  Linked to the caller's token so a
        // caller cancel still wins, and self-cancels after OverallBudget so the
        // turn can never block for minutes (issue #389).
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(OverallBudget);

        try
        {
            // The shared client carries NO short timeout: the per-request and
            // overall-budget CancellationTokenSources are the only deadlines, so
            // a stalled chunk fails in PerRequestTimeout, not the old 180 s.  We
            // set a finite ceiling well above the overall budget purely as a
            // backstop; the linked tokens fire long before it.
            var client = CreateHttpClient(key);
            try
            {
                // Single-chunk fast path: no parallelism overhead.
                if (chunks.Count == 1)
                {
                    var single = await CallWithRetryAsync(client, endpoint, model, voice, chunks[0], 0, budgetCts.Token);
                    if (!single.Success)
                        FileLog.Write($"[TtsService] GenerateAsync FAILED after {stopwatch.ElapsedMilliseconds}ms: chunk 0/1 reason={single.ErrorMessage}");
                    return single;
                }

                // Multi-chunk: fire all in parallel. Hosted speech latency is
                // ~2 s per chunk, so 10 chunks in parallel is ~2-3 s wall-clock
                // (vs ~20 s sequential).  If any chunk fails (after its single
                // retry), propagate the first error - partial audio is worse
                // than no audio because the listener would not know where it
                // cut off.
                var tasks = chunks
                    .Select((c, i) => CallWithRetryAsync(client, endpoint, model, voice, c, i, budgetCts.Token))
                    .ToList();
                var results = await Task.WhenAll(tasks);

                for (int i = 0; i < results.Length; i++)
                {
                    if (!results[i].Success)
                    {
                        FileLog.Write($"[TtsService] GenerateAsync FAILED after {stopwatch.ElapsedMilliseconds}ms: chunk {i}/{results.Length} reason={results[i].ErrorMessage}");
                        return results[i];
                    }
                }

                // Concatenate MP3 byte streams in order.  MP3 is a sequence of
                // independent frames; byte-level concatenation produces a single
                // valid stream that browsers play as one continuous audio.
                var combined = Concatenate(results);
                FileLog.Write($"[TtsService] joined {results.Length} chunks into {combined.Length} bytes in {stopwatch.ElapsedMilliseconds}ms");
                return TtsResult.Ok(combined, "audio/mpeg");
            }
            finally
            {
                // Only dispose a client we created.  When an external handler is
                // injected the caller owns the handler's lifetime, so we leave it
                // (disposing the client would dispose the handler).
                if (_handler is null)
                    client.Dispose();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller cancelled - surface that rather than the budget message.
            FileLog.Write($"[TtsService] GenerateAsync cancelled by caller after {stopwatch.ElapsedMilliseconds}ms");
            return TtsResult.Error("cancelled", "TTS cancelled");
        }
        catch (OperationCanceledException)
        {
            // The overall budget elapsed before all chunks finished.
            FileLog.Write($"[TtsService] GenerateAsync FAILED after {stopwatch.ElapsedMilliseconds}ms: overall budget of {OverallBudget.TotalSeconds:0}s exceeded");
            return TtsResult.Error("tts_budget_exceeded", $"TTS exceeded the {OverallBudget.TotalSeconds:0}s budget");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TtsService] GenerateAsync FAILED after {stopwatch.ElapsedMilliseconds}ms: {ex.Message}");
            return TtsResult.Error("internal_error", ex.Message);
        }
    }

    /// <summary>
    /// Build the <see cref="HttpClient"/> used for the hosted speech calls. Uses the
    /// injected handler when present (the unit-test seam) and never relies on a
    /// short <c>HttpClient.Timeout</c> - cancellation is driven entirely by the
    /// per-request and overall-budget tokens (issue #389).
    /// </summary>
    private HttpClient CreateHttpClient(string key)
    {
        // Backstop timeout only - well above the overall budget so the linked
        // tokens always fire first.  This is NOT the 180 s per-turn block the
        // issue describes.
        var client = _handler is null
            ? new HttpClient()
            : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        // The Director's own speech, recorded as such under the signed-in user's own key (devthrottle_internal
        // #2213). The client is built per call and owned here, so the tag can ride its default headers.
        client.DefaultRequestHeaders.TryAddWithoutValidation(HostedAi.AiCallTag.FeatureHeader, HostedAi.AiFeature.DirectorSpeech);
        return client;
    }

    /// <summary>
    /// One hosted speech chunk with a short per-request timeout and a single retry on a
    /// transient failure (timeout, 5xx, or empty body).  Issue #389: turn 1 in
    /// the bug report would very likely have succeeded on a second attempt.
    /// </summary>
    private async Task<TtsResult> CallWithRetryAsync(HttpClient client, string endpoint, string model, string voice, string input, int chunkIndex, CancellationToken budgetToken)
    {
        var first = await CallOnceAsync(client, endpoint, model, voice, input, chunkIndex, attempt: 1, budgetToken);
        if (first.Success || !first.Transient)
            return first;

        FileLog.Write($"[TtsService] chunk {chunkIndex} attempt 1 transient failure ({first.ErrorMessage}); retrying once");
        var second = await CallOnceAsync(client, endpoint, model, voice, input, chunkIndex, attempt: 2, budgetToken);
        return second;
    }

    /// <summary>
    /// One hosted /v1/audio/speech call with a per-request deadline. Returns raw
    /// MP3 bytes on success; on a transient failure (per-request timeout, 5xx, or
    /// empty body) the result is flagged <see cref="TtsResult.Transient"/> so the
    /// caller can retry once.  A 4xx is permanent (not retried).
    /// </summary>
    private async Task<TtsResult> CallOnceAsync(HttpClient client, string endpoint, string model, string voice, string input, int chunkIndex, int attempt, CancellationToken budgetToken)
    {
        // Per-request deadline, linked to the overall budget so whichever fires
        // first wins.  CancelAfter is the per-chunk timeout (issue #389).
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(budgetToken);
        requestCts.CancelAfter(PerRequestTimeout);

        var payload = JsonContent.Create(new
        {
            model,
            voice,
            input,
            response_format = "mp3",
        });

        try
        {
            using var resp = await client.PostAsync(endpoint, payload, requestCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(requestCts.Token);
                var status = (int)resp.StatusCode;
                FileLog.Write($"[TtsService] chunk {chunkIndex} attempt {attempt} provider returned {status}: {Truncate(body, 400)}");
                // Out of credits / monthly cap (issue #940): carry the machine-readable code as the
                // status so the desktop player maps it to the ONE shared state and surfaces the
                // add-credits message, instead of swallowing a generic "openai_failed" silently. Not
                // transient - retrying an out-of-credits call is pointless.
                if (status == 402)
                    return TtsResult.Error(HostedAiErrorMapper.ParseErrorCode(body), $"text-to-speech returned 402: {Truncate(body, 300)}", transient: false);
                // 5xx is transient (server-side, worth a retry); other 4xx is a
                // permanent request error and must not be retried.
                var transient = status >= 500;
                return TtsResult.Error("provider_failed", $"Speech provider returned {status}: {Truncate(body, 300)}", transient);
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(requestCts.Token);
            if (bytes.Length == 0)
            {
                FileLog.Write($"[TtsService] chunk {chunkIndex} attempt {attempt} provider returned empty audio");
                return TtsResult.Error("provider_failed", "Speech provider returned empty audio", transient: true);
            }

            return TtsResult.Ok(bytes, "audio/mpeg");
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested && !budgetToken.IsCancellationRequested)
        {
            // The per-request timeout fired (the overall budget has NOT) - this
            // is the stalled-chunk case; transient, so the caller retries once.
            FileLog.Write($"[TtsService] chunk {chunkIndex} attempt {attempt} timed out after {PerRequestTimeout.TotalSeconds:0}s");
            return TtsResult.Error("timeout", $"per-request timeout of {PerRequestTimeout.TotalSeconds:0}s elapsed", transient: true);
        }
    }

    /// <summary>Concatenate MP3 chunk byte streams in order into one buffer.</summary>
    private static byte[] Concatenate(TtsResult[] results)
    {
        int total = 0;
        foreach (var r in results)
        {
            if (r.AudioBytes is null)
                throw new InvalidOperationException("Cannot concatenate a TTS result with no audio bytes");
            total += r.AudioBytes.Length;
        }

        var combined = new byte[total];
        int offset = 0;
        foreach (var r in results)
        {
            if (r.AudioBytes is null)
                throw new InvalidOperationException("Cannot concatenate a TTS result with no audio bytes");
            Buffer.BlockCopy(r.AudioBytes, 0, combined, offset, r.AudioBytes.Length);
            offset += r.AudioBytes.Length;
        }
        return combined;
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
/// <see cref="Transient"/> marks a failure that is worth a single retry (timeout,
/// 5xx, or empty body) versus a permanent one (4xx, missing key) - issue #389.
/// </summary>
public sealed class TtsResult
{
    public string Status { get; private init; } = "";
    public string? ErrorMessage { get; private init; }
    public byte[]? AudioBytes { get; private init; }
    public string? ContentType { get; private init; }

    /// <summary>True when this failure is transient and the caller may retry once.</summary>
    public bool Transient { get; private init; }

    public bool Success => Status == "ok";

    public static TtsResult Ok(byte[] bytes, string contentType)
        => new() { Status = "ok", AudioBytes = bytes, ContentType = contentType };

    public static TtsResult Error(string status, string message)
        => new() { Status = status, ErrorMessage = message };

    public static TtsResult Error(string status, string message, bool transient)
        => new() { Status = status, ErrorMessage = message, Transient = transient };
}
