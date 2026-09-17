using System.Diagnostics;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Storage;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The single Gateway owner of speech-to-text (issue #839). Every module that needs audio turned
/// into text goes through this one service: it resolves the configured transcription mode and the
/// key, runs the provider-compatible DevThrottle batch endpoint, and optionally applies the validated
/// dictionary correction. No caller resolves the mode, reads the key, picks a provider, or talks to a
/// provider on its own.
///
/// This collapses the three hand-kept-in-step resolvers that previously each did "figure out the
/// mode, then read the key" - the phone Notes worker (<c>ResolveSelectedMethod</c>), the Settings
/// "Test it" endpoint, and the Gateway wingman-voice batch paths - into ONE place
/// (<see cref="Resolve"/>). The HTTP face of this service is <c>POST /transcription</c>
/// (<see cref="Api.TranscriptionBatchEndpoint"/>).
///
/// The key lives in exactly one store: the Gateway vault file (<see cref="KeyVault"/>). There is no
/// second config.json copy. Legacy provider mode values migrate forward because the (URL, key,
/// transport, model) tuple is composed from the one pure <see cref="TranscriptionEndpointResolver"/>.
///
/// Transcription integrity (CodingStyle section 16): the only post-transcription transform is the
/// validated dictionary corrector (<see cref="CleanupOrchestrator"/> / <see cref="TranscriptEditEngine"/>)
/// reached through <see cref="BatchTranscriptionPipeline"/>; there is no free-text rewrite, and the
/// corrector fails open to the raw transcript in local mode, with no key, or on any error.
/// </summary>
public sealed class GatewayTranscriptionService
{
    private readonly KeyVault _vault;
    private readonly Func<CcDirector.Core.Tenancy.TenantId, DictationDictionary> _dictionaryProvider;
    private readonly Func<TranscriptionMode> _modeProvider;
    private readonly HttpClient? _http;
    private readonly string _cleanupModel;
    private readonly TranscriptionHistoryLog _history;
    private readonly TranscriptionAudioArchive _audioArchive;
    private readonly TranscriptStore? _transcripts;

    /// <param name="vault">The Gateway key vault - the single store for the transcription key.</param>
    /// <param name="dictionaryProvider">Supplies the live dictation dictionary the corrector uses FOR
    /// A GIVEN TENANT; invoked fresh per cleanup so a glossary edit takes effect on the next
    /// transcription. Defaults to <see cref="TenantGlossary.Load"/>, the single owner of where a
    /// tenant's glossary lives - the Local tenant reads the shared flat file (self-host, unchanged)
    /// and every other tenant reads its own per-tenant file, so a hosted user's dictionary edits bias
    /// THEIR next dictation and nobody else's (issue #2482).</param>
    /// <param name="modeProvider">Supplies the current transcription mode; invoked fresh per resolve
    /// so a mode change in the Cockpit takes effect with no restart. Defaults to
    /// <see cref="TranscriptionModeConfig.Get"/>.</param>
    /// <param name="http">Optional shared HttpClient for the remote batch transcription and the
    /// dictionary-corrector POST (tests inject a stub). The pipeline creates and owns one when null.</param>
    /// <param name="cleanupModel">Cleanup identity used only for logging. It does NOT select the judge
    /// model - that is resolved through <see cref="DictationJudgeFactory"/> from the routing table.
    /// Defaults to the dictation default when blank.</param>
    /// <param name="history">Local, bounded transcription history. In production the host owns one
    /// instance and passes it here; when omitted it defaults to a fresh instance over the per-user
    /// location, and tests inject their own.</param>
    /// <param name="audioArchive">Rolling archive of the audio behind each turn. In production the host
    /// owns one instance and passes it here; when omitted it defaults to a fresh instance over the
    /// per-user location, and tests inject their own so they never write into the real user's archive.</param>
    /// <param name="transcripts">The per-tenant transcript store (issue #509). In production the host owns
    /// one instance (over the Gateway database) and passes it here, so every transcribed turn's raw and
    /// cleaned text lands in the caller tenant's partition for later mistranscription mining. When null (tests
    /// and any caller that does not persist) the transcript write is simply skipped - it is a write-only
    /// diagnostic aid and never gates a turn.</param>
    public GatewayTranscriptionService(
        KeyVault vault,
        Func<CcDirector.Core.Tenancy.TenantId, DictationDictionary>? dictionaryProvider = null,
        Func<TranscriptionMode>? modeProvider = null,
        HttpClient? http = null,
        string? cleanupModel = null,
        TranscriptionHistoryLog? history = null,
        TranscriptionAudioArchive? audioArchive = null,
        TranscriptStore? transcripts = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _dictionaryProvider = dictionaryProvider ?? TenantGlossary.Load;
        _modeProvider = modeProvider ?? TranscriptionModeConfig.Get;
        _http = http;
        _cleanupModel = string.IsNullOrWhiteSpace(cleanupModel) ? CleanupOrchestrator.DefaultModel : cleanupModel;
        _history = history ?? new TranscriptionHistoryLog();
        _audioArchive = audioArchive ?? new TranscriptionAudioArchive();
        _transcripts = transcripts;
    }

    /// <summary>
    /// THE one place that turns the configured mode into a routing target plus the key. The mode
    /// carries the vault key when present, or null when no key is set for that mode (the caller then
    /// reports it unavailable - never a baked-in URL, no-fallback rule).
    /// </summary>
    public GatewayTranscriptionRouting Resolve()
    {
        var endpoint = TranscriptionEndpointResolver.Resolve(_modeProvider());
        var key = _vault.Get(endpoint.RequireKeyName());
        var hasKey = !string.IsNullOrWhiteSpace(key);
        FileLog.Write($"[GatewayTranscriptionService] Resolve: mode={endpoint.Mode.ToConfigString()}, model={endpoint.Model}, hasKey={hasKey}");
        return new GatewayTranscriptionRouting { Endpoint = endpoint, Key = hasKey ? key : null };
    }

    /// <summary>
    /// Turn a complete audio clip into text using the configured mode, optionally applying the
    /// validated dictionary correction. Returns a result describing the outcome - the expected
    /// failures (no audio, no key for the mode, a provider rejection) are carried as values rather
    /// than thrown, so the HTTP face can map each to a status code in one place. This is the single
    /// audio-to-text entry point the <c>POST /transcription</c> endpoint, the Settings "Test it"
    /// button, and the Gateway wingman-voice batch paths all use.
    /// </summary>
    /// <param name="audio">The complete audio bytes (already gated upstream where applicable).</param>
    /// <param name="fileName">Filename hint whose extension tells a remote provider how to decode the bytes.</param>
    /// <param name="contentType">The clip's MIME type (used to derive the filename when none is given).</param>
    /// <param name="applyCorrection">When true, run the validated dictionary corrector on the raw text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="source">The surface that produced the utterance (e.g. "dictation", "voice", "batch"),
    /// stamped on the stored transcript (issue #509) for later mining. Defaults to "gateway" when blank.</param>
    /// <param name="language">Optional spoken-language hint (BCP 47 primary subtag, e.g. "da"). Null
    /// leaves the provider to detect the language, which is what every ordinary dictation path does.</param>
    public async Task<GatewayTranscriptionResult> TranscribeAsync(
        byte[] audio, string fileName, string contentType, bool applyCorrection, CancellationToken ct,
        CcDirector.Core.Tenancy.TenantId? tenant = null, string? source = null, string? language = null)
    {
        if (audio is null) throw new ArgumentNullException(nameof(audio));

        var routing = Resolve();
        var mode = routing.Mode.ToConfigString();

        if (audio.Length == 0)
            return GatewayTranscriptionResult.NoAudio(mode, routing.Endpoint.Model);

        if (routing.Key is null)
            return GatewayTranscriptionResult.NoKey(mode, routing.Endpoint.Model);

        var turnId = Guid.NewGuid().ToString("N");

        // Archive the clip BEFORE transcribing, keyed by the same turn id the local history records.
        // Before, so a crash or a hang cannot lose it; and kept afterwards WHATEVER the outcome, because
        // the failure this exists for is a transcription that succeeds and silently drops half the
        // speech. Only the audio can tell "the provider lost it" from "the microphone went quiet", and a
        // net that deletes on success is exactly the net that is never there when that happens.
        _audioArchive.TrySave(turnId, audio, contentType);

        var swTranscribe = Stopwatch.StartNew();
        PartTranscript part;
        try
        {
            part = await TranscribeCoreAsync(routing, audio, fileName, contentType, ct, language);
            swTranscribe.Stop();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InsufficientCreditsException ex)
        {
            // Out of credits (issue #885): a distinct, expected condition. Carry it as its own value so
            // the endpoint maps it to 402 and the client preserves the recording and offers "Add
            // credits" - never a raw error, never a lost clip.
            swTranscribe.Stop();
            FileLog.Write($"[GatewayTranscriptionService] TranscribeAsync OUT OF CREDITS: mode={mode}, code={ex.Code}");
            RecordHistory(turnId, "out_of_credits",
                swTranscribe.ElapsedMilliseconds, 0, applyCorrection, raw: null, cleanup: null, tenant: tenant, source: source);
            return GatewayTranscriptionResult.OutOfCredits(mode, routing.Endpoint.Model, ex.Code, ex.Message);
        }
        catch (TranscriptionPermanentException ex)
        {
            // A PERMANENT failure (issue #1139): the clip cannot be decoded/transcoded or is too large to
            // send, so retrying is pointless. Carry it as its own outcome with the machine-readable code
            // so the endpoint maps it to a non-retryable status and the durable dictation loop stops
            // resending a doomed clip, rather than looping like a transient provider outage.
            swTranscribe.Stop();
            FileLog.Write($"[GatewayTranscriptionService] TranscribeAsync PERMANENT: mode={mode}, code={ex.Code}, {ex.Message}");
            RecordHistory(turnId, "permanent_error",
                swTranscribe.ElapsedMilliseconds, 0, applyCorrection, raw: null, cleanup: null, tenant: tenant, source: source);
            return GatewayTranscriptionResult.PermanentError(mode, routing.Endpoint.Model, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            // A provider rejection (bad key, model not found, network) is an expected external
            // failure (CodingStyle: Result Objects for Expected Failures): carry it as a value so the
            // single endpoint maps it to 502, rather than throwing the same shape from N call sites.
            swTranscribe.Stop();
            FileLog.Write($"[GatewayTranscriptionService] TranscribeAsync provider FAILED: mode={mode}, {ex.Message}");
            RecordHistory(turnId, "provider_error",
                swTranscribe.ElapsedMilliseconds, 0, applyCorrection, raw: null, cleanup: null, tenant: tenant, source: source);
            return GatewayTranscriptionResult.ProviderError(mode, routing.Endpoint.Model, ex.Message);
        }

        // Issue #2926: the dictionary runs on what is DELIVERED (the gated text), the caller gets the
        // delivered text, and the record stores the model's FULL output as the raw transcript. Storing
        // the delivered text as raw - which this did until then - made every removal by the evidence
        // gate invisible, so the verbatim rule's audit (diff raw against cleaned) could not see one.
        CleanupOutcome? cleanup = null;
        var swCleanup = Stopwatch.StartNew();
        if (applyCorrection)
            cleanup = await CleanupCoreAsync(part.Delivered, tenant, ct);
        swCleanup.Stop();
        var text = cleanup?.Text ?? part.Delivered;

        FileLog.Write($"[GatewayTranscriptionService] TranscribeAsync OK: mode={mode}, corrected={applyCorrection}, "
                      + $"transcribeMs={swTranscribe.ElapsedMilliseconds}, cleanupMs={(applyCorrection ? swCleanup.ElapsedMilliseconds : 0)}, "
                      + $"rawChars={part.Raw.Length}, chars={text.Length}, droppedSentences={part.Dropped.Count}");
        RecordHistory(turnId, "ok", swTranscribe.ElapsedMilliseconds,
            applyCorrection ? swCleanup.ElapsedMilliseconds : 0, applyCorrection, part.Raw, cleanup, tenant: tenant, source: source,
            delivered: part.Delivered);
        return GatewayTranscriptionResult.Ok(text, mode, routing.Endpoint.Model) with { DroppedSentences = part.Dropped };
    }

    /// <summary>Append one minimized turn to the caller tenant's Transcription Health history (issue #2059).
    /// When a tenant is supplied the record is written to THAT tenant's partition
    /// (<see cref="TranscriptionHistoryLog.ForTenant"/>); when it is null the injected <see cref="_history"/>
    /// is used (self-host, tests, and the recording path which already injects its own per-tenant log). The
    /// old hosted write-skip is gone: the write is now per-tenant on hosted, so it accumulates in the caller's
    /// own partition and its Transcription Health page reads it back - never another tenant's.</summary>
    private void RecordHistory(
        string turnId, string outcome, long transcribeMs, long cleanupMs, bool corrected,
        string? raw, CleanupOutcome? cleanup, CcDirector.Core.Tenancy.TenantId? tenant = null,
        string? source = null, string? delivered = null)
    {
        var log = tenant is { } tv ? TranscriptionHistoryLog.ForTenant(tv) : _history;
        // What the user received: the corrected text, else the gated text, else (error outcomes) the raw.
        var finalText = cleanup?.Text ?? delivered ?? raw;
        log.Record(new TranscriptionHistoryRecord
        {
            TimestampUtc = DateTime.UtcNow,
            TurnId = turnId,
            Outcome = outcome,
            TranscriptionMs = transcribeMs,
            CleanupMs = cleanupMs,
            Corrected = corrected,
            CleanupApplied = cleanup?.Applied ?? false,
            ChangedWordCount = cleanup?.ChangedWords.Count ?? 0,
            Changes = cleanup is { ChangedWords.Count: > 0 }
                ? cleanup.ChangedWords.Select(TranscriptionHistoryEdit.From).ToList()
                : null,
            CharCount = finalText?.Length ?? 0,
            WordCount = CountWords(finalText),
        });

        // Store the transcript itself (issue #509), beside the health write and in the SAME swallow-and-log
        // guard so a persistence problem never fails a live turn. Only a turn that actually produced text is
        // stored - the error outcomes pass raw=null, and an empty raw is nothing to mine - so the 10,000-row
        // cap is spent on real transcripts, never on failures. The tenant is always concrete on the production
        // path; it defaults to Local when absent, with no IsHosted branch (self-host is just one tenant).
        if (_transcripts is not null && !string.IsNullOrEmpty(raw))
        {
            try
            {
                _transcripts.Append(
                    tenant: tenant ?? CcDirector.Core.Tenancy.TenantId.Local,
                    source: string.IsNullOrWhiteSpace(source) ? "gateway" : source,
                    rawText: raw,
                    cleanedText: finalText,
                    cleanupApplied: cleanup?.Applied ?? false,
                    turnId: turnId,
                    nowUtc: DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayTranscriptionService] transcript store append FAILED (swallowed): {ex.Message}");
            }
        }
    }

    private static int CountWords(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Transcribe one complete audio segment to its DELIVERED text - after the evidence gate, before any
    /// dictionary correction - using the configured mode. (It was named TranscribeSegmentRawAsync, and
    /// "raw" was untrue: it has always returned the gated text. Issue #2926.) This is the phone Notes per-segment path: it batch-transcribes each segment,
    /// then runs the dictionary corrector ONCE on the assembled concatenation (<see cref="CleanupAsync"/>),
    /// so the assembled transcript is provably the per-segment raw concatenation plus dictionary edits
    /// only. Throws when no key is set for the selected remote mode, which the ingest worker records as
    /// a retryable transcription failure - never a guessed URL.
    /// </summary>
    public async Task<string> TranscribeSegmentUncorrectedAsync(
        byte[] audio, string fileName, string contentType, CancellationToken ct = default)
    {
        if (audio is null) throw new ArgumentNullException(nameof(audio));
        if (audio.Length == 0) return "";

        var routing = Resolve();
        if (routing.Key is null)
            throw new InvalidOperationException(
                "No transcription method is available: no key is set for the selected transcription mode. "
                + "Set the key in the Cockpit Settings > Transcription tab.");

        return (await TranscribeCoreAsync(routing, audio, fileName, contentType, ct)).Delivered;
    }

    /// <summary>
    /// Apply the validated dictionary correction to an assembled raw transcript and return the
    /// outcome. Fails open (CodingStyle section 16 / issue #190): on an empty dictionary or any cleanup
    /// error, the raw transcript comes back byte-identical. Deterministic and in-process - needs no key.
    /// </summary>
    /// <param name="rawTranscript">The assembled raw transcript to correct.</param>
    /// <param name="tenant">The tenant whose glossary biases the correction - the requesting tenant,
    /// resolved by the calling endpoint (issue #2482). Null means the self-host single tenant (Local),
    /// which reads the shared flat file exactly as before.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<CleanupOutcome> CleanupAsync(
        string rawTranscript, CcDirector.Core.Tenancy.TenantId? tenant = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawTranscript))
            return new CleanupOutcome(rawTranscript ?? "", Applied: false, Reason: "empty transcript");
        return await CleanupCoreAsync(rawTranscript, tenant, ct);
    }

    /// <summary>
    /// Run the transcription provider for the resolved routing: ONE batch POST to the resolved
    /// provider-compatible endpoint for the mode, returning the model's full output, the gated text and the
    /// dropped sentences together. Throws on a provider error (a missing transcript is a real failure the
    /// caller must surface).
    /// </summary>
    private async Task<PartTranscript> TranscribeCoreAsync(
        GatewayTranscriptionRouting routing, byte[] audio, string fileName, string contentType, CancellationToken ct,
        string? language = null)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "audio." + ExtensionFor(contentType) : fileName;
        using var pipeline = new BatchTranscriptionPipeline(
            httpClient: _http, cleanupModel: _cleanupModel,
            judge: DictationJudgeFactory.FromVault(_vault), judgeMode: DictationJudgeMode.Current);
        FileLog.Write($"[GatewayTranscriptionService] transcribe remote: bytes={audio.Length}, mode={routing.Mode.ToConfigString()}, model={routing.Endpoint.Model}, language={language ?? "auto"}");
        return await pipeline.TranscribeUncorrectedAsync(audio, name, routing.ToResolved(language), ct);
    }

    /// <summary>
    /// The validated dictionary correction core, shared by the optional <c>correct</c> flag and the
    /// Notes assemble-then-clean path. Fails open to the raw transcript. The correction reads the
    /// REQUESTING TENANT's glossary (issue #2482): the tenant the endpoint resolved for the request,
    /// or the self-host single tenant (Local) when the caller carries none - the same null-means-Local
    /// convention <see cref="RecordHistory"/> uses, never a hosted tenant silently reading the
    /// global file.
    /// </summary>
    private async Task<CleanupOutcome> CleanupCoreAsync(
        string raw, CcDirector.Core.Tenancy.TenantId? tenant, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new CleanupOutcome(raw ?? "", Applied: false, Reason: "empty transcript");

        // The listed wrong forms are corrected in-process and work offline. An UNLISTED correction
        // needs a judge, which needs the deployment credential - and when there is none, the factory
        // returns null and no unlisted correction is applied, which is the safe answer rather than a
        // degraded one. CleanAsync short-circuits an empty dictionary to a verbatim passthrough; the
        // early check just avoids constructing the orchestrator for nothing.
        //
        // The dictionary read shares the corrector's fail-open contract (issue #2483): a malformed or
        // unreadable dictionary file must never fail a transcription that already produced text, so a
        // provider fault degrades to the raw transcript exactly like a fault inside CleanAsync does.
        // The read is THIS TENANT's glossary (issue #2482), so the fail-open covers a fault reading a
        // per-tenant glossary exactly as it covers one reading the shared flat file.
        DictationDictionary dictionary;
        try
        {
            dictionary = _dictionaryProvider(tenant ?? CcDirector.Core.Tenancy.TenantId.Local);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTranscriptionService] dictionary read FAILED (fail-open, returning raw text): {ex.Message}");
            return new CleanupOutcome(raw, Applied: false, Reason: "dictionary unavailable: " + ex.Message);
        }

        if (dictionary.Vocabulary.Count == 0 && dictionary.CommonMistranscriptions.Count == 0)
            return new CleanupOutcome(raw, Applied: false, Reason: "empty dictionary");

        var cleanup = new CleanupOrchestrator(
            model: _cleanupModel, judge: DictationJudgeFactory.FromVault(_vault), mode: DictationJudgeMode.Current);
        var outcome = await cleanup.CleanAsync(raw, dictionary, "default", ct);
        FileLog.Write($"[GatewayTranscriptionService] cleanup: applied={outcome.Applied}, changed={outcome.ChangedWords.Count}, reason=\"{outcome.Reason}\"");
        return outcome;
    }

    /// <summary>
    /// The shared flat dictation glossary file - the Local (self-host single) tenant's glossary, and
    /// the base location <see cref="TenantGlossary.PathFor"/> derives every per-tenant glossary from,
    /// so the path is defined in exactly one place.
    /// </summary>
    /// <remarks>The Director composes this same path via AgentOptions.ResolveDictationDictionaryPath;
    /// both now go through CcStorage so the two cannot drift apart.</remarks>
    public static string DictionaryPath() => CcStorage.DictationDictionary();

    /// <summary>The file extension (no dot) for an audio MIME type, used to name the upload so a
    /// remote provider decodes the bytes correctly. Defaults to <c>webm</c> (what browsers record).</summary>
    public static string ExtensionFor(string? contentType)
    {
        var ct = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        return ct switch
        {
            "audio/webm" => "webm",
            "audio/ogg" => "ogg",
            "audio/mpeg" => "mp3",
            "audio/mp4" => "m4a",
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/flac" => "flac",
            _ => "webm",
        };
    }
}

/// <summary>
/// The resolved transcription routing for the configured mode (issue #839): the pure
/// <see cref="TranscriptionEndpoint"/> plus the key read from the vault (null when no key is set for
/// the mode). Produced by the single resolver, <see cref="GatewayTranscriptionService.Resolve"/>.
/// </summary>
public sealed record GatewayTranscriptionRouting
{
    /// <summary>The pure routing target (base URL, key name, transport, model) for the mode.</summary>
    public required TranscriptionEndpoint Endpoint { get; init; }

    /// <summary>The vault key for the mode, or null when no key is set.</summary>
    public string? Key { get; init; }

    /// <summary>The configured transcription mode.</summary>
    public TranscriptionMode Mode => Endpoint.Mode;

    /// <summary>
    /// Compose the <see cref="ResolvedTranscription"/> the Gateway-local batch transport consumes.
    /// Throws when no key is set - call only after checking <see cref="Key"/> is non-null.
    /// </summary>
    public ResolvedTranscription ToResolved(string? language = null)
    {
        if (string.IsNullOrWhiteSpace(Key))
            throw new InvalidOperationException($"no key set for transcription mode {Mode.ToConfigString()}");
        return new ResolvedTranscription
        {
            BaseUrl = Endpoint.RequireBaseUrl(),
            ApiKey = Key,
            Transport = Endpoint.Transport,
            Model = Endpoint.Model,
            Mode = Mode,
            Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim(),
        };
    }
}

/// <summary>How a <see cref="GatewayTranscriptionService.TranscribeAsync"/> call ended.</summary>
public enum TranscriptionOutcome
{
    /// <summary>Transcription succeeded; <see cref="GatewayTranscriptionResult.Text"/> is the text.</summary>
    Ok = 0,

    /// <summary>The request carried no audio bytes.</summary>
    NoAudio = 1,

    /// <summary>No key is set for the current remote transcription mode.</summary>
    NoKey = 2,

    /// <summary>The provider rejected the request or the key.</summary>
    ProviderError = 3,

    /// <summary>The DevThrottle account is out of credits (HTTP 402) - issue #885.</summary>
    OutOfCredits = 4,

    /// <summary>The clip can NEVER transcribe (unsupported/undecodable format, or too large to reduce) -
    /// a permanent, non-retryable failure so the durable dictation loop stops (issue #1139).</summary>
    PermanentError = 5,
}

/// <summary>
/// The outcome of a single audio-to-text call (issue #839): the outcome kind, the text when it
/// succeeded, the mode and model that ran, and the error when it did not.
/// </summary>
public sealed record GatewayTranscriptionResult(
    TranscriptionOutcome Outcome, string? Text, string Mode, string? Model, string? Error, string? Code = null)
{
    /// <summary>The sentences the evidence gate removed from the model's output before
    /// <see cref="Text"/> was delivered (issue #2926). Empty when nothing was removed or the call failed.</summary>
    public IReadOnlyList<TranscriptEvidenceGate.Dropped> DroppedSentences { get; init; } = Array.Empty<TranscriptEvidenceGate.Dropped>();

    public static GatewayTranscriptionResult Ok(string text, string mode, string? model)
        => new(TranscriptionOutcome.Ok, text, mode, model, null);

    public static GatewayTranscriptionResult NoAudio(string mode, string? model)
        => new(TranscriptionOutcome.NoAudio, null, mode, model, "no audio in the request body");

    public static GatewayTranscriptionResult NoKey(string mode, string? model)
        => new(TranscriptionOutcome.NoKey, null, mode, model, "no key set for the current transcription mode");

    public static GatewayTranscriptionResult ProviderError(string mode, string? model, string error)
        => new(TranscriptionOutcome.ProviderError, null, mode, model, error);

    /// <summary>Out of credits (issue #885): carries the hosted service's error code so the client can
    /// show the add-credits state and keep the recording.</summary>
    public static GatewayTranscriptionResult OutOfCredits(string mode, string? model, string code, string error)
        => new(TranscriptionOutcome.OutOfCredits, null, mode, model, error, code);

    /// <summary>Permanent, non-retryable failure (issue #1139): carries the machine-readable code
    /// (unsupported_format / audio_too_large / non_decodable) so the endpoint can map it to a stop.</summary>
    public static GatewayTranscriptionResult PermanentError(string mode, string? model, string code, string error)
        => new(TranscriptionOutcome.PermanentError, null, mode, model, error, code);
}

/// <summary>
/// Gateway-local resolved transcription target: provider base URL, credential, transport, model, and
/// mode. This deliberately lives in the Gateway transcription namespace so Director/Core code cannot
/// depend on a reusable provider-direct transcription DTO.
/// </summary>
public sealed record ResolvedTranscription
{
    /// <summary>The provider-compatible base URL.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>The credential to present (an <c>sk-</c> or <c>dt_</c> key, depending on mode).</summary>
    public required string ApiKey { get; init; }

    /// <summary>The provider transport to use.</summary>
    public required TranscriptionTransport Transport { get; init; }

    /// <summary>The transcription model to use.</summary>
    public required string Model { get; init; }

    /// <summary>The mode this target was resolved for.</summary>
    public required TranscriptionMode Mode { get; init; }

    /// <summary>
    /// Optional spoken-language hint (a BCP 47 primary subtag such as "da" or "zh") passed to the
    /// provider. Null means "let the provider detect it", which is the behaviour every existing caller
    /// keeps. It lives on this record rather than on the pipeline's method signatures because a long
    /// clip is split into several independent POSTs, and every one of them must carry the same hint -
    /// threading it here makes that automatic instead of a parameter each chunking method could drop.
    /// </summary>
    public string? Language { get; init; }
}
