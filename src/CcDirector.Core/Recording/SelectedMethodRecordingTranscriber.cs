using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Recording;

/// <summary>
/// Production <see cref="IRecordingTranscriber"/> for the phone recorder (issue #591). It brings the
/// recorder onto the ONE shared batch pipeline (<see cref="BatchTranscriptionPipeline"/>, issue #587)
/// and the user-SELECTED transcription method, replacing the old OpenAI-only path:
///
///   * Each finalized one-minute segment is transcribed by ONE whole-segment batch POST to the
///     resolved method's <c>/audio/transcriptions</c> endpoint (<see cref="TranscribeChunkAsync"/>),
///     returning RAW text only - no per-segment dictionary correction. The base URL, key, and model
///     come entirely from the resolved method, so the user-selected method governs the route:
///     bring-your-own OpenAI hits <c>api.openai.com</c>, DevThrottle hits <c>devthrottle.com</c>.
///   * The assembled raw concatenation runs through the validated dictionary corrector ONCE
///     (<see cref="CleanupAsync"/>), against the SAME resolved base URL/key so the bring-your-own key
///     is never crossed onto the other provider's URL. The only text change is swapping a known
///     dictionary term, so the assembled transcript is provably the per-segment raw concatenation plus
///     dictionary edits only - never a reword.
///
/// The method is resolved FRESH on every transcribe (<paramref name="routingResolver"/> is invoked per
/// call), so a transcription-mode change takes effect on the next recording with no restart - the same
/// live-read contract the desktop voice/dictation surfaces follow. When no method is resolvable (no key
/// set for the selected mode) it throws, which the ingest worker records as a retryable transcription
/// failure; the audio + notes are already safe on the server (the completeness gate ran upstream). It
/// never falls back to a baked-in OpenAI URL (no-fallback rule).
///
/// The segmented capture, resumable upload, completeness gate, and per-segment-then-assemble model are
/// all unchanged - only the route and the post-processing engine moved onto the shared pipeline.
/// </summary>
public sealed class SelectedMethodRecordingTranscriber : IRecordingTranscriber, IDisposable
{
    private readonly Func<CancellationToken, Task<ResolvedTranscription?>> _routingResolver;
    private readonly Func<CancellationToken, Task<DictationDictionary>> _dictionaryResolver;
    private readonly BatchTranscriptionPipeline _pipeline;
    private readonly HttpClient? _http;
    private readonly string _cleanupModel;

    /// <param name="routingResolver">Resolves the user-selected method (base URL + key + model) for the
    /// current transcription mode. Invoked fresh per transcribe so a mode change is honored without a
    /// restart. Returns null when no key is set for the mode - the transcriber then throws a clear,
    /// retryable error rather than guess a URL.</param>
    /// <param name="dictionaryResolver">Resolves the live dictionary the corrector uses; invoked fresh
    /// per cleanup so a glossary edit takes effect on the next recording.</param>
    /// <param name="cleanupModel">The chat model the dictionary corrector uses to PROPOSE edits
    /// (deterministic validation still gates them). Defaults to the dictation default when blank.</param>
    /// <param name="httpClient">Optional shared HttpClient for both the transcription POST and the
    /// dictionary-corrector POST (tests inject a stub). The shared pipeline creates and owns one when
    /// null; the cleanup corrector reuses this same client so the test can observe both calls.</param>
    public SelectedMethodRecordingTranscriber(
        Func<CancellationToken, Task<ResolvedTranscription?>> routingResolver,
        Func<CancellationToken, Task<DictationDictionary>> dictionaryResolver,
        string? cleanupModel = null,
        HttpClient? httpClient = null)
    {
        _routingResolver = routingResolver ?? throw new ArgumentNullException(nameof(routingResolver));
        _dictionaryResolver = dictionaryResolver ?? throw new ArgumentNullException(nameof(dictionaryResolver));
        _cleanupModel = string.IsNullOrWhiteSpace(cleanupModel) ? CleanupOrchestrator.DefaultModel : cleanupModel;
        _http = httpClient;
        _pipeline = new BatchTranscriptionPipeline(httpClient: httpClient, cleanupModel: _cleanupModel);

        FileLog.Write($"[SelectedMethodRecordingTranscriber] init: cleanup={_cleanupModel}");
    }

    public async Task<string> TranscribeChunkAsync(
        byte[] audio,
        string contentType,
        string fileName,
        CancellationToken ct = default)
    {
        if (audio.Length == 0) return "";

        var routing = await ResolveRoutingAsync(ct);
        FileLog.Write($"[SelectedMethodRecordingTranscriber] TranscribeChunk: bytes={audio.Length}, "
            + $"mode={routing.Mode.ToConfigString()}, baseUrl={routing.BaseUrl}, model={routing.Model}");
        return await _pipeline.TranscribeRawAsync(audio, fileName, routing, ct);
    }

    public async Task<CleanupOutcome> CleanupAsync(string rawTranscript, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawTranscript))
            return new CleanupOutcome(rawTranscript ?? "", Applied: false, Reason: "empty transcript");

        // Run the dictionary corrector against the SAME resolved method as transcription, so the key
        // is never crossed onto the wrong provider's URL. The corrector's own fail-open contract turns
        // any cleanup problem into a verbatim passthrough; only a missing method (no key) throws.
        var routing = await ResolveRoutingAsync(ct);
        var dictionary = await _dictionaryResolver(ct);
        using var cleanup = new CleanupOrchestrator(
            apiKey: routing.ApiKey, model: _cleanupModel, httpClient: _http, baseUrl: routing.BaseUrl);
        var outcome = await cleanup.CleanAsync(rawTranscript, dictionary, "default", ct);
        FileLog.Write($"[SelectedMethodRecordingTranscriber] Cleanup: applied={outcome.Applied}, "
            + $"changed={outcome.ChangedWords.Count}, reason=\"{outcome.Reason}\"");
        return outcome;
    }

    private async Task<ResolvedTranscription> ResolveRoutingAsync(CancellationToken ct)
    {
        var routing = await _routingResolver(ct);
        if (routing is null)
            throw new InvalidOperationException(
                "No transcription method is available: no key is set for the selected transcription mode. "
                + "Set the key in the Cockpit Settings > Transcription tab.");
        return routing;
    }

    public void Dispose() => _pipeline.Dispose();
}
