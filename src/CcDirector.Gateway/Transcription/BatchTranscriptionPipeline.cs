using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Audio;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// Gateway-owned batch transcription transport. Director surfaces must not call this class directly;
/// they call the Gateway <c>POST /transcription</c> endpoint through <c>GatewayTranscriptionClient</c>.
///
///   1. Whole audio in. A complete, already-gated audio blob (the Audio Completeness Gate, issue
///      #586, runs upstream - this pipeline assumes the bytes it receives are complete). There is
///      NO streaming/partial transcription here: the whole clip is transcribed once.
///   2. ONE batch request to the resolved method. The provider-compatible POST is encapsulated
///      here inside the Gateway process. The base URL, key, and model come from
///      <see cref="GatewayTranscriptionService.Resolve"/>, so every path uses the same hosted target.
///   3. Dictionary corrector ONLY. The raw transcript runs through the validated dictionary
///      corrector (<see cref="CleanupOrchestrator"/> + <see cref="TranscriptEditEngine"/>): the
///      wrong forms the user listed are applied deterministically, and an UNLISTED one is applied
///      only when a judge rules on it (<see cref="CcDirector.Core.Dictation.ICandidateJudge"/>,
///      which answers with candidate ids and never text). There is NO free-text language-model
///      cleanup - the only text change allowed is swapping
///      a known dictionary term, so a transcript with no dictionary hit comes back byte-identical.
///
/// Routing-and-text decisions (e.g. agent vs wingman, wake-phrase handling) are NOT this pipeline's
/// job and must never alter the transcript it returns; callers that still need them run them as a
/// separate non-text step on a copy of the text.
///
/// Stateless and side-effect-free apart from the network call and logging, so it is safe to call
/// per request.
/// </summary>
public sealed class BatchTranscriptionPipeline : IDisposable
{
    /// <summary>
    /// HTTP timeout for the batch transcription POST. Whole-clip uploads can be several seconds for
    /// longer recordings. The timeout is per request, so a recording split into several parts gets the
    /// full budget for each part.
    /// </summary>
    public static readonly TimeSpan TranscribeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The largest a single transcription request body may be. A recording over this size is split into
    /// several bounded parts, each transcribed with its own request, so no single upload can exceed the
    /// provider's limit. The DevThrottle managed proxy runs on a serverless function that rejects a body
    /// over roughly 4.5 megabytes with FUNCTION_PAYLOAD_TOO_LARGE / HTTP 413; this budget stays under
    /// that with room to spare for the small multipart framing (the model and format form fields).
    /// </summary>
    public const int MaxTranscriptionUploadBytes = 4_000_000;

    // Chunking parameters (transcription reliability epic, #324). A long recording is split by
    // DURATION - preferring a cut at nearby silence - into short parts transcribed IN PARALLEL, so an
    // hour of audio is many fast requests instead of one that times out. These are the shared spec
    // constants (docs/architecture/transcription-service-design.md); AgentEyes mirrors them exactly.

    /// <summary>Preferred chunk length. Short enough to transcribe fast and stay well under the
    /// per-request timeout; long enough to keep the part count low.</summary>
    public const int ChunkTargetSeconds = 60;

    /// <summary>Hard cap on a chunk's length, used when no quiet cut is found near the target.</summary>
    public const int ChunkMaxSeconds = 90;

    /// <summary>How far before/after the target the splitter searches for a silence to cut on.</summary>
    public const int ChunkSilenceWindowSeconds = 5;

    /// <summary>Chunks transcribed at once. Bounded so a long recording does not fan out an unbounded
    /// burst that would trip provider rate limits or the proxy's per-instance breaker.</summary>
    public const int MaxParallelChunks = 4;

    /// <summary>Extra attempts for a single chunk on a TRANSIENT failure, on top of the proxy's own
    /// internal provider fallback. A permanent (4xx) failure is not retried.</summary>
    public const int PerChunkRetries = 1;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cleanupModel;
    private readonly ICandidateJudge? _judge;
    private readonly UnlistedCorrectionMode _judgeMode;
    private readonly IAudioTranscoder _transcoder;

    /// <param name="httpClient">Optional shared HttpClient (tests inject a stub). The pipeline creates
    /// and owns one when null.</param>
    /// <param name="cleanupModel">Cleanup identity used only for logging. It does NOT select the
    /// judge model - pass <paramref name="judge"/>, or the pipeline builds one from its resolved
    /// route. Defaults to the dictation default.</param>
    /// <param name="transcoder">Turns a non-WAV clip too large to send into a splittable PCM WAV (issue
    /// #1139). Defaults to the bundled-ffmpeg transcoder; tests inject a stub. ffmpeg is resolved lazily,
    /// so this default never touches disk unless a clip actually needs transcoding.</param>
    public BatchTranscriptionPipeline(HttpClient? httpClient = null, string? cleanupModel = null,
        IAudioTranscoder? transcoder = null, ICandidateJudge? judge = null,
        UnlistedCorrectionMode judgeMode = UnlistedCorrectionMode.Shadow)
    {
        _cleanupModel = string.IsNullOrWhiteSpace(cleanupModel) ? CleanupOrchestrator.DefaultModel : cleanupModel;
        _judge = judge;
        _judgeMode = judgeMode;
        _transcoder = transcoder ?? new FfmpegAudioTranscoder();
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TranscribeTimeout };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }
    }

    /// <summary>
    /// Transcribe a complete audio blob using the resolved method, then apply the dictionary
    /// corrector only. Returns the raw transcript, the corrected transcript, and the list of
    /// dictionary terms that changed.
    ///
    /// The dictionary correction fails open (issue #190 contract): on an empty dictionary, a missing
    /// key, or any cleanup error the corrected text equals the raw text and the change list is empty,
    /// so a dictionary problem never costs the user their words. Transcription itself does NOT fail
    /// open - if the provider rejects the request it throws, because a missing transcript is a real
    /// failure the caller must surface, not paper over.
    /// </summary>
    /// <param name="audio">The complete audio bytes (already gated upstream).</param>
    /// <param name="fileName">Filename hint for the multipart upload; its extension tells the server how to decode the bytes.</param>
    /// <param name="routing">The resolved method: base URL, key, and model from the Gateway routing resolver.</param>
    /// <param name="dictionary">The dictionary the corrector uses; pass <see cref="DictationDictionary.Empty"/> for none.</param>
    /// <param name="profileName">Dictation profile selecting whether correction runs. Defaults to "default".</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<BatchTranscriptionResult> TranscribeAsync(
        byte[] audio,
        string fileName,
        ResolvedTranscription routing,
        DictationDictionary dictionary,
        string profileName = "default",
        CancellationToken ct = default,
        IProgress<TranscriptionProgress>? progress = null)
    {
        if (audio is null) throw new ArgumentNullException(nameof(audio));
        if (routing is null) throw new ArgumentNullException(nameof(routing));
        if (dictionary is null) throw new ArgumentNullException(nameof(dictionary));
        if (audio.Length == 0)
            throw new ArgumentException("audio blob is empty; the Audio Completeness Gate must run before transcription", nameof(audio));

        FileLog.Write($"[BatchTranscriptionPipeline] TranscribeAsync: bytes={audio.Length}, mode={routing.Mode.ToConfigString()}, model={routing.Model}");

        var raw = await TranscribeBatchAsync(audio, fileName, routing, ct, progress);
        FileLog.Write($"[BatchTranscriptionPipeline] raw transcript len={raw.Length}");

        var corrected = await ApplyDictionaryAsync(raw, routing, dictionary, profileName, ct);

        return new BatchTranscriptionResult(
            RawTranscript: raw,
            CorrectedTranscript: corrected.Text,
            DictionaryApplied: corrected.Applied,
            ChangedWords: corrected.ChangedWords,
            Reason: corrected.Reason);
    }

    /// <summary>
    /// Transcribe one complete audio blob to RAW text using the resolved method, with NO dictionary
    /// correction. This is the transcription half of <see cref="TranscribeAsync"/> on its own, for
    /// callers that batch-transcribe several segments and then run the dictionary corrector ONCE on
    /// the assembled concatenation (the phone recorder, issue #591) - so the assembled transcript is
    /// provably the per-segment raw concatenation plus dictionary edits only, never a per-segment
    /// reword. Same single transport: ONE batch POST to <c>{baseUrl}/audio/transcriptions</c> with the
    /// resolved key and model. Throws on a provider error (a missing transcript is a real failure the
    /// caller must surface, never paper over).
    /// </summary>
    /// <param name="audio">The complete audio segment bytes (already gated upstream).</param>
    /// <param name="fileName">Filename hint for the multipart upload; its extension tells the server how to decode the bytes.</param>
    /// <param name="routing">The resolved method: base URL, key, and model from the Gateway routing resolver.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string> TranscribeRawAsync(
        byte[] audio, string fileName, ResolvedTranscription routing, CancellationToken ct = default,
        IProgress<TranscriptionProgress>? progress = null)
    {
        if (audio is null) throw new ArgumentNullException(nameof(audio));
        if (routing is null) throw new ArgumentNullException(nameof(routing));
        if (audio.Length == 0)
            throw new ArgumentException("audio blob is empty; the Audio Completeness Gate must run before transcription", nameof(audio));

        FileLog.Write($"[BatchTranscriptionPipeline] TranscribeRawAsync: bytes={audio.Length}, mode={routing.Mode.ToConfigString()}, model={routing.Model}");
        return await TranscribeBatchAsync(audio, fileName, routing, ct, progress);
    }

    /// <summary>
    /// Transcribe a whole audio blob to raw text, splitting it first when it is over the per-request
    /// size limit so no single upload can exceed the provider's body limit. A clip within the budget is
    /// one part = one POST, exactly as before. A long clip is cut into several bounded WAV parts (see
    /// <see cref="WavSplitter"/>), each transcribed with its own POST, and the raw per-part texts are
    /// joined in order. A caller that asked for the dictionary corrector still runs it ONCE on this
    /// assembled text, so the result is provably the raw concatenation plus dictionary edits only - the
    /// same assemble-then-clean contract the phone recorder uses across its segments.
    ///
    /// Only PCM WAV can be split. An over-budget blob that is NOT a splittable WAV fails loud rather
    /// than posting an oversized body, because a silent 413 is exactly the failure this removes (the
    /// no-fallback rule). Every capture surface sends PCM WAV, so the long-recording paths are covered.
    /// </summary>
    private async Task<string> TranscribeBatchAsync(
        byte[] audio, string fileName, ResolvedTranscription routing, CancellationToken ct,
        IProgress<TranscriptionProgress>? progress = null)
    {
        // Duration + silence aware split. For PCM WAV this returns ONE part when the clip is within a
        // single target window (both bytes AND duration), or several bounded parts otherwise. Note this
        // splits a WAV that is under the byte budget but LONG in duration too - the old bytes-only gate
        // let a long-but-small compressed-style WAV through as one slow request.
        if (WavSplitter.TrySplitByDuration(
                audio, ChunkTargetSeconds, ChunkMaxSeconds, ChunkSilenceWindowSeconds,
                MaxTranscriptionUploadBytes, out var parts) && parts is not null)
        {
            if (parts.Count == 1)
            {
                progress?.Report(new TranscriptionProgress(0, 1));
                var only = await PostOneAsync(parts[0], fileName, routing, ct);
                progress?.Report(new TranscriptionProgress(1, 1));
                return only;
            }

            FileLog.Write($"[BatchTranscriptionPipeline] split {audio.Length} bytes into {parts.Count} parts "
                          + $"(target={ChunkTargetSeconds}s, max={ChunkMaxSeconds}s, budget={MaxTranscriptionUploadBytes}); "
                          + $"transcribing up to {MaxParallelChunks} in parallel");
            return await TranscribeChunksInParallelAsync(parts, fileName, routing, ct, progress);
        }

        // Not a splittable PCM WAV. A clip within the byte budget is one POST as before (the provider
        // decodes the codec itself), so a short WebM/Opus dictation keeps its fast single-request path.
        if (audio.Length <= MaxTranscriptionUploadBytes)
        {
            progress?.Report(new TranscriptionProgress(0, 1));
            var only = await PostOneAsync(audio, fileName, routing, ct);
            progress?.Report(new TranscriptionProgress(1, 1));
            return only;
        }

        // An over-budget NON-WAV (e.g. a long WebM/Opus recording - issue #1139) cannot be sent whole and
        // the splitter only understands PCM WAV, so transcode it to PCM WAV first, then run the exact same
        // duration split + parallel path. A clip ffmpeg cannot decode throws a permanent, non-retryable
        // failure (loop-stop) rather than being retried forever.
        FileLog.Write($"[BatchTranscriptionPipeline] over-budget non-WAV ({audio.Length:N0} bytes) - transcoding to PCM WAV to enable splitting");
        var wav = await Task.Run(() => _transcoder.ToPcmWav(audio, fileName, ct), ct);
        var wavName = Path.ChangeExtension(string.IsNullOrEmpty(fileName) ? "audio.wav" : fileName, ".wav");

        if (WavSplitter.TrySplitByDuration(
                wav, ChunkTargetSeconds, ChunkMaxSeconds, ChunkSilenceWindowSeconds,
                MaxTranscriptionUploadBytes, out var wavParts) && wavParts is not null && wavParts.Count > 0)
        {
            if (wavParts.Count == 1)
            {
                progress?.Report(new TranscriptionProgress(0, 1));
                var only = await PostOneAsync(wavParts[0], wavName, routing, ct);
                progress?.Report(new TranscriptionProgress(1, 1));
                return only;
            }
            FileLog.Write($"[BatchTranscriptionPipeline] transcoded WAV {wav.Length:N0} bytes -> {wavParts.Count} parts; "
                          + $"transcribing up to {MaxParallelChunks} in parallel");
            return await TranscribeChunksInParallelAsync(wavParts, wavName, routing, ct, progress);
        }

        // A valid PCM WAV always splits; reaching here means the transcode produced something unusable.
        throw new TranscriptionPermanentException(TranscriptionPermanentException.AudioTooLarge,
            $"Audio is {audio.Length:N0} bytes and could not be transcoded into a splittable PCM WAV.");
    }

    /// <summary>
    /// Transcribe the ordered chunks concurrently, at most <see cref="MaxParallelChunks"/> in flight,
    /// and join the raw per-chunk texts in ORIGINAL order (never completion order). Each chunk is
    /// retried on a transient failure (<see cref="PerChunkRetries"/>); if any chunk still fails, the
    /// whole job throws the original typed exception - no silent partial transcripts. The split is
    /// non-overlapping, so a single space joins the parts with no de-duplication.
    /// </summary>
    private async Task<string> TranscribeChunksInParallelAsync(
        IReadOnlyList<byte[]> parts, string fileName, ResolvedTranscription routing, CancellationToken ct,
        IProgress<TranscriptionProgress>? progress)
    {
        var texts = new string[parts.Count];
        int completed = 0;
        progress?.Report(new TranscriptionProgress(0, parts.Count));

        using var gate = new SemaphoreSlim(MaxParallelChunks);

        async Task ProcessAsync(int idx)
        {
            await gate.WaitAsync(ct);
            try
            {
                var text = await PostOneWithRetryAsync(parts[idx], PartFileName(fileName, idx), routing, idx, ct);
                texts[idx] = text;
                int done = Interlocked.Increment(ref completed);
                FileLog.Write($"[BatchTranscriptionPipeline] chunk {idx + 1}/{parts.Count} done: bytes={parts[idx].Length}, chars={text.Length}");
                progress?.Report(new TranscriptionProgress(done, parts.Count));
            }
            finally
            {
                gate.Release();
            }
        }

        var tasks = new Task[parts.Count];
        for (int i = 0; i < parts.Count; i++) tasks[i] = ProcessAsync(i);
        await Task.WhenAll(tasks);   // throws the first chunk failure -> job fails clean

        return string.Join(" ", texts.Where(t => !string.IsNullOrEmpty(t)));
    }

    /// <summary>
    /// One chunk POST with a bounded retry on a TRANSIENT failure (network error, a per-request
    /// timeout, or a 5xx/429 from the proxy - which has already tried its own provider fallback, so the
    /// retry gives the breaker a moment to recover). A permanent 4xx (bad request, auth) or a 402
    /// (out of credits) is NOT retried and propagates its original typed exception so the caller's
    /// credit/retry handling still works. The chunk index is logged on failure.
    /// </summary>
    private async Task<string> PostOneWithRetryAsync(
        byte[] audio, string fileName, ResolvedTranscription routing, int chunkIndex, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await PostOneAsync(audio, fileName, routing, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < PerChunkRetries && IsTransient(ex))
            {
                FileLog.Write($"[BatchTranscriptionPipeline] chunk {chunkIndex} transient failure "
                              + $"(attempt {attempt + 1}/{PerChunkRetries + 1}): {ex.Message} - retrying");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[BatchTranscriptionPipeline] chunk {chunkIndex} failed permanently: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>A chunk failure worth one more attempt: a network error, a per-request timeout, or a
    /// 5xx/429 from the proxy. A 4xx/402 is permanent (do not retry).</summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        TranscriptionFailedException t => t.IsTransient,
        HttpRequestException => true,
        TaskCanceledException => true,
        _ => false,
    };

    /// <summary>
    /// Name one part of a split upload, keeping the original extension so the server still decodes the
    /// bytes correctly (e.g. <c>dictation.wav</c> -&gt; <c>dictation.0.wav</c>).
    /// </summary>
    private static string PartFileName(string fileName, int index)
    {
        if (string.IsNullOrEmpty(fileName)) return $"audio.{index}.wav";
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return $"{stem}.{index}{ext}";
    }

    /// <summary>
    /// ONE whole-audio batch POST to the provider-compatible <c>/audio/transcriptions</c> endpoint of
    /// the resolved base URL, presenting the resolved key and model. This is the single transcription
    /// transport for the shared pipeline - there is no streaming/partial path here. Callers over the
    /// per-request size limit are split into several of these by <see cref="TranscribeBatchAsync"/>.
    /// </summary>
    private async Task<string> PostOneAsync(
        byte[] audio, string fileName, ResolvedTranscription routing, CancellationToken ct)
    {
        var endpoint = routing.BaseUrl.TrimEnd('/') + "/audio/transcriptions";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", routing.ApiKey);

        using var form = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(audio);
        // MediaTypeHeaderValue's ctor only accepts a bare "type/subtype"; a parameter suffix
        // (e.g. "audio/webm;codecs=opus") throws. The server detects the codec from the bytes
        // anyway, so a bare type is correct.
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(GuessAudioContentType(fileName));
        form.Add(audioContent, "file", string.IsNullOrEmpty(fileName) ? "audio.webm" : fileName);
        form.Add(new StringContent(routing.Model), "model");
        // verbose_json, not json: it returns each sentence with the span it claims to occupy, which
        // is what the evidence gate needs to check the words against the sound. This changes only
        // what OUR proxy hands back - the proxy's own request to the speech model carries no format -
        // so the transcript itself cannot change. A provider that ignores it simply returns no
        // segments, and the gate then fails open (pipeline v2.0).
        form.Add(new StringContent("verbose_json"), "response_format");
        // Spoken-language hint, when the caller knows it. Only sent when set, so the provider keeps
        // auto-detecting for every existing caller. Detection is what fails hardest on the cases this
        // matters for - a short clip, an accent, or a language that shares vocabulary with English -
        // and a wrong detection produces confident nonsense rather than an error.
        if (!string.IsNullOrWhiteSpace(routing.Language))
            form.Add(new StringContent(routing.Language), "language");
        request.Content = form;

        using var resp = await _http.SendAsync(request, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        // Out of credits (issue #885): the hosted service returns HTTP 402 with
        // code=insufficient_credits. This is an expected, distinct condition - surface it as a typed
        // exception so the stack preserves the recording and offers "Add credits" rather than treating
        // it as an opaque provider rejection.
        if ((int)resp.StatusCode == 402)
            throw new InsufficientCreditsException(ParseErrorCode(body), $"Transcription returned 402: {Truncate(body, 400)}");

        // Any other non-success status becomes a typed failure carrying the status code, so the durable
        // dictation retry loop (issue #1130) can tell a transient 5xx/429 (retry) from a permanent 4xx
        // (do not retry). The message format is unchanged, and the type still derives from
        // InvalidOperationException so existing catches keep working.
        if (!resp.IsSuccessStatusCode)
            throw new TranscriptionFailedException((int)resp.StatusCode, $"Transcription returned {(int)resp.StatusCode}: {Truncate(body, 400)}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("text", out var textProp))
            throw new InvalidOperationException("Transcription response missing 'text' field");
        var text = (textProp.GetString() ?? "").Trim();

        // THE EVIDENCE GATE (pipeline v2.0). Run it HERE, per part, because a long recording is
        // split and each part's segment times start at zero - gating after the parts are joined
        // would point every time at the wrong audio. Fails open on anything it cannot measure.
        var gated = TranscriptEvidenceGate.Apply(audio, ReadSegments(doc.RootElement), text);
        if (gated.Applied)
            FileLog.Write($"[BatchTranscriptionPipeline] evidence gate {TranscriptEvidenceGate.Version}: {gated.Reason}");
        return gated.Text;
    }

    /// <summary>
    /// The per-sentence spans from a verbose_json body, or an empty list when the provider did not
    /// return any. An empty list makes the gate fail open, which is the intended behaviour for a
    /// provider or format that carries no times.
    /// </summary>
    private static IReadOnlyList<TranscriptEvidenceGate.Segment> ReadSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segs) || segs.ValueKind != JsonValueKind.Array)
            return Array.Empty<TranscriptEvidenceGate.Segment>();

        var list = new List<TranscriptEvidenceGate.Segment>();
        foreach (var s in segs.EnumerateArray())
        {
            if (!s.TryGetProperty("start", out var a) || !s.TryGetProperty("end", out var b)) continue;
            if (!s.TryGetProperty("text", out var t)) continue;
            if (a.ValueKind != JsonValueKind.Number || b.ValueKind != JsonValueKind.Number) continue;
            list.Add(new TranscriptEvidenceGate.Segment(a.GetDouble(), b.GetDouble(), t.GetString() ?? ""));
        }
        return list;
    }

    /// <summary>
    /// Apply the validated dictionary corrector - the ONLY post-transcription transform. The model
    /// proposes edits and <see cref="TranscriptEditEngine"/> validates/applies them to the RAW text,
    /// so the only possible change is swapping a known dictionary term. Fails open: returns the raw
    /// text unchanged (and an empty change list) on an empty transcript, an empty dictionary, or any
    /// error.
    /// </summary>
    private async Task<CleanupOutcome> ApplyDictionaryAsync(
        string raw, ResolvedTranscription routing, DictationDictionary dictionary, string profileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new CleanupOutcome(raw, Applied: false, Reason: "empty transcript");

        // Listed wrong forms are corrected in-process; an UNLISTED one now needs a judge, and this
        // pipeline gets the same one live dictation uses. Without it the caller would still get a
        // successful response that could never correct anything - see DictationJudgeFactory.
        var judge = _judge ?? DictationJudgeFactory.FromKey(routing.BaseUrl, routing.ApiKey);
        var cleanup = new CleanupOrchestrator(model: _cleanupModel, judge: judge, mode: _judgeMode);
        return await cleanup.CleanAsync(raw, dictionary, profileName, ct);
    }

    private static string GuessAudioContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".webm" => "audio/webm",
            ".ogg" => "audio/ogg",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".mp4" => "audio/mp4",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            _ => "application/octet-stream",
        };
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

    /// <summary>
    /// Best-effort read of the machine-readable error code from a provider-compatible 402 body. Delegates
    /// to the shared <see cref="HostedAiErrorMapper.ParseErrorCode"/> so the whole stack parses the 402
    /// code in exactly one place (issue #938) - the transcription path and the epic-wide gate never drift.
    /// </summary>
    private static string ParseErrorCode(string body) => HostedAiErrorMapper.ParseErrorCode(body);

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>
/// The result of one shared batch transcription (issue #587): the raw transcript, the corrected
/// transcript (dictionary-only), and exactly which dictionary terms were swapped.
/// <see cref="CorrectedTranscript"/> equals <see cref="RawTranscript"/> byte-for-byte whenever no
/// dictionary term matched (<see cref="ChangedWords"/> empty), proving the pipeline never rewords.
/// </summary>
public sealed record BatchTranscriptionResult(
    string RawTranscript,
    string CorrectedTranscript,
    bool DictionaryApplied,
    IReadOnlyList<TranscriptEdit> ChangedWords,
    string? Reason);

/// <summary>
/// Progress of a batch transcription while it runs: how many bounded parts have finished out of the
/// total the clip was split into. A short clip reports one part; a long recording reports one update
/// per part as each finishes, so a surface can show "transcribing part N of M" instead of a silent
/// wait. <see cref="TotalParts"/> is known from the first report (parts are planned before any POST).
/// </summary>
public sealed record TranscriptionProgress(int CompletedParts, int TotalParts);
