using System.IO.Compression;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Interfaces;
using Vosk;

namespace CcDirector.VoskStt;

/// <summary>
/// Vosk-based streaming speech-to-text service.
/// Implements IStreamingSpeechToText from CcDirector.Core for seamless integration
/// with VoiceModeController. Auto-downloads vosk-model-small-en-us-0.15 (~40 MB)
/// to %LOCALAPPDATA%/cc-director/models/vosk/ on first use.
/// </summary>
public sealed class VoskSttService : IStreamingSpeechToText
{
    private const string ModelName = "vosk-model-small-en-us-0.15";
    private const string ModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";
    private const float SampleRate = 16000f;

    private readonly string _modelsDir;
    private Model? _model;
    private VoskRecognizer? _recognizer;
    private readonly List<string> _completedUtterances = [];

    public event Action<string>? OnPartialResult;

    public bool IsAvailable => _model is not null;

    public string? UnavailableReason { get; private set; } = "Model not loaded. Call InitializeAsync() first.";

    public string? ModelPath { get; private set; }

    public VoskSttService()
    {
        _modelsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cc-director", "models", "vosk");

        Directory.CreateDirectory(_modelsDir);
        FileLog.Write($"[VoskSttService] Created. Models dir: {_modelsDir}");
    }

    /// <summary>
    /// Initialize the Vosk model. Downloads if not present.
    /// Must be called before StartSession/ProcessAudioChunk/EndSession.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        FileLog.Write("[VoskSttService] InitializeAsync: starting");

        var modelDir = Path.Combine(_modelsDir, ModelName);
        if (!Directory.Exists(modelDir))
        {
            FileLog.Write($"[VoskSttService] Model not found at {modelDir}, downloading...");
            await DownloadAndExtractModelAsync(modelDir, ct);
        }

        // Suppress Vosk native logging
        Vosk.Vosk.SetLogLevel(-1);

        _model = new Model(modelDir);
        ModelPath = modelDir;
        UnavailableReason = null;
        FileLog.Write($"[VoskSttService] Model loaded from {modelDir}");
    }

    public void StartSession()
    {
        FileLog.Write("[VoskSttService] StartSession");
        if (_model is null)
            throw new InvalidOperationException("VoskSttService not initialized. Call InitializeAsync() first.");

        _recognizer?.Dispose();
        _recognizer = new VoskRecognizer(_model, SampleRate);
        _completedUtterances.Clear();
    }

    public void ProcessAudioChunk(byte[] audioData)
    {
        if (_recognizer is null)
            throw new InvalidOperationException("No active session. Call StartSession() first.");

        if (_recognizer.AcceptWaveform(audioData, audioData.Length))
        {
            // Complete utterance detected
            var text = ExtractText(_recognizer.Result());
            if (!string.IsNullOrWhiteSpace(text))
            {
                _completedUtterances.Add(text);
                FileLog.Write($"[VoskSttService] Utterance completed: \"{text}\"");
            }

            // Fire partial with all accumulated text
            var accumulated = BuildAccumulatedText(null);
            OnPartialResult?.Invoke(accumulated);
        }
        else
        {
            // Still processing -- emit partial
            var partial = ExtractPartial(_recognizer.PartialResult());
            var accumulated = BuildAccumulatedText(partial);
            OnPartialResult?.Invoke(accumulated);
        }
    }

    public string EndSession()
    {
        FileLog.Write("[VoskSttService] EndSession");
        if (_recognizer is null)
            return string.Empty;

        // Get any remaining text
        var finalText = ExtractText(_recognizer.FinalResult());
        if (!string.IsNullOrWhiteSpace(finalText))
            _completedUtterances.Add(finalText);

        var result = string.Join(" ", _completedUtterances).Trim();
        FileLog.Write($"[VoskSttService] EndSession: {_completedUtterances.Count} utterances, result=\"{result}\"");

        _completedUtterances.Clear();
        _recognizer.Dispose();
        _recognizer = null;

        return result;
    }

    public void Dispose()
    {
        FileLog.Write("[VoskSttService] Dispose");
        _recognizer?.Dispose();
        _recognizer = null;
        _model?.Dispose();
        _model = null;
    }

    private string BuildAccumulatedText(string? currentPartial)
    {
        var parts = new List<string>(_completedUtterances);
        if (!string.IsNullOrWhiteSpace(currentPartial))
            parts.Add(currentPartial);
        return string.Join(" ", parts).Trim();
    }

    private async Task DownloadAndExtractModelAsync(string modelDir, CancellationToken ct)
    {
        var zipPath = Path.Combine(_modelsDir, ModelName + ".zip");
        FileLog.Write($"[VoskSttService] Downloading model from {ModelUrl}");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(zipPath);
        await stream.CopyToAsync(fileStream, ct);
        fileStream.Close();

        FileLog.Write("[VoskSttService] Download complete. Extracting...");
        ZipFile.ExtractToDirectory(zipPath, _modelsDir, overwriteFiles: true);

        // Clean up zip
        File.Delete(zipPath);

        if (!Directory.Exists(modelDir))
            throw new InvalidOperationException($"Model extraction failed: expected directory {modelDir} not found.");

        FileLog.Write("[VoskSttService] Model downloaded and extracted.");
    }

    private static string ExtractText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("text", out var prop)
                ? prop.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[VoskSttService] ExtractText JSON parse FAILED: {ex.Message}");
            return string.Empty;
        }
    }

    private static string ExtractPartial(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("partial", out var prop)
                ? prop.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[VoskSttService] ExtractPartial JSON parse FAILED: {ex.Message}");
            return string.Empty;
        }
    }
}
