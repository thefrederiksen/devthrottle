using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Providers;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Recording;

/// <summary>
/// Production <see cref="IRecordingTranscriber"/>. Reuses the existing
/// dictation pipeline: <see cref="OpenAiTranscriptionProvider"/> (batch
/// transcription of a finalized file) for each segment, and
/// <see cref="CleanupOrchestrator"/> for the final glossary-aware cleanup.
/// The vocabulary dictionary biases both stages toward the user's terms.
///
/// One provider is created per segment because the batch provider buffers a
/// whole file between Start and Stop; segments are independent files.
/// </summary>
public sealed class OpenAiRecordingTranscriber : IRecordingTranscriber, IDisposable
{
    private readonly string _apiKey;
    private readonly string _transcriptionModel;
    private readonly string _cleanupModel;
    private readonly DictionaryLoader _dictionary;
    private readonly string _sttPrompt;
    private readonly CleanupOrchestrator _cleanup;

    /// <param name="apiKey">OpenAI API key. Reads OPENAI_API_KEY if blank.</param>
    /// <param name="dictionaryPath">Path to the vocabulary YAML. Missing file means no bias and no cleanup glossary.</param>
    /// <param name="transcriptionModel">STT model. Defaults to the provider default.</param>
    /// <param name="cleanupModel">Chat model for cleanup. Defaults to the orchestrator default.</param>
    public OpenAiRecordingTranscriber(
        string? apiKey = null,
        string? dictionaryPath = null,
        string? transcriptionModel = null,
        string? cleanupModel = null)
    {
        _apiKey = apiKey ?? "";
        _transcriptionModel = string.IsNullOrWhiteSpace(transcriptionModel)
            ? OpenAiTranscriptionProvider.DefaultModel
            : transcriptionModel;
        _cleanupModel = string.IsNullOrWhiteSpace(cleanupModel)
            ? CleanupOrchestrator.DefaultModel
            : cleanupModel;

        _dictionary = new DictionaryLoader(dictionaryPath ?? "", watch: false);
        _sttPrompt = DictionaryLoader.BuildSttPrompt(_dictionary.Current);
        _cleanup = new CleanupOrchestrator(apiKey: _apiKey, model: _cleanupModel);

        FileLog.Write($"[OpenAiRecordingTranscriber] init: dict={dictionaryPath}, "
            + $"vocab={_dictionary.Current.Vocabulary.Count}, stt={_transcriptionModel}, cleanup={_cleanupModel}");
    }

    public async Task<string> TranscribeChunkAsync(
        byte[] audio,
        string contentType,
        string fileName,
        CancellationToken ct = default)
    {
        if (audio.Length == 0) return "";

        await using var provider = new OpenAiTranscriptionProvider(
            apiKey: string.IsNullOrWhiteSpace(_apiKey) ? null : _apiKey,
            model: _transcriptionModel,
            audioContentType: contentType,
            audioFileName: fileName);

        await provider.StartAsync(_sttPrompt, ct);
        await provider.PushAudioAsync(audio, ct);
        return await provider.StopAsync(ct);
    }

    public Task<CleanupOutcome> CleanupAsync(string rawTranscript, CancellationToken ct = default)
        => _cleanup.CleanAsync(rawTranscript, _dictionary.Current, "default", ct);

    public void Dispose()
    {
        _dictionary.Dispose();
        _cleanup.Dispose();
    }
}
