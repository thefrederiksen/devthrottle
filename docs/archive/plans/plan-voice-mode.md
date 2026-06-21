# Voice Mode for CC Director

## Summary

Add local voice mode to CC Director where:
1. When Claude finishes (ActivityState -> WaitingForInput), extract a summary of the response and speak it via TTS
2. User presses a toggle key (F9) to start/stop voice recording
3. Transcribe voice input via local Whisper and send to the active session
4. Only operates on the currently focused session
5. **100% local/offline - no cloud APIs required**

## Local Model Options

### Speech-to-Text (STT)

| Option | Pros | Cons | Recommendation |
|--------|------|------|----------------|
| **Whisper.cpp** | Excellent accuracy, same as OpenAI Whisper, runs offline, free | Requires model download (~500MB-1.5GB), CPU/GPU intensive | **Recommended** |
| **Vosk** | Very fast, lightweight (~50MB models), real-time capable | Lower accuracy than Whisper | Good fallback |
| **Windows SAPI** | Built-in, no downloads | Poor accuracy, limited | Not recommended |

### Text-to-Speech (TTS)

| Option | Pros | Cons | Recommendation |
|--------|------|------|----------------|
| **Piper TTS** | Fast, good quality voices, ~15-100MB models, runs offline | Needs voice model download | **Recommended** |
| **Coqui XTTS** | Very natural voices | Slower, heavier, more complex | Overkill for this use |
| **Windows SAPI** | Built-in, no setup | Robotic quality | Not recommended |
| **edge-tts** | Free, good quality | Requires internet (uses Edge) | Defeats "local" goal |

**Recommended Stack: Whisper.cpp + Piper TTS**
- Both run 100% offline
- Both are free and open source
- Good quality for both directions
- Reasonable resource usage

## Architecture

```
[Claude Process] -> [Stop Hook] -> [Session.OnActivityStateChanged]
                                            |
                                            v
[VoiceModeController] <-- listens for WaitingForInput
         |
         |--> ExtractLastResponse(ClaudeSessionId) --> ClaudeResponseExtractor
         |--> Summarize(text) --> ClaudeSummarizer (claude -p "summarize...")
         |--> Piper TTS (local) --> NAudio playback
         |
         <-- F9 key toggle -->
         |--> Start recording (NAudio WaveInEvent)
         |--> Stop recording -> Whisper.cpp (local) -> transcription
         |--> SendTextAsync(transcription) to active session
```

**Key insight**: We use a separate Claude Code invocation as a "summarization service".
Claude reads the full response and produces a casual, conversational summary
suitable for TTS - like telling a friend what happened over coffee.

## Local Model Setup

### Whisper.cpp Setup

1. Download whisper.cpp Windows binaries or build from source
2. Download a model (recommend `ggml-base.en.bin` for English, ~150MB)
3. Place in `%LOCALAPPDATA%\CcDirector\voice\whisper\`

```
%LOCALAPPDATA%\CcDirector\voice\whisper\
    whisper.exe          (CLI executable)
    ggml-base.en.bin     (model file)
```

Usage: `whisper.exe -m ggml-base.en.bin -f audio.wav -otxt`

### Piper TTS Setup

1. Download Piper Windows release from GitHub
2. Download a voice model (recommend `en_US-lessac-medium`, ~100MB)
3. Place in `%LOCALAPPDATA%\CcDirector\voice\piper\`

```
%LOCALAPPDATA%\CcDirector\voice\piper\
    piper.exe                    (CLI executable)
    en_US-lessac-medium.onnx     (voice model)
    en_US-lessac-medium.onnx.json (config)
```

Usage: `echo "Hello world" | piper.exe --model en_US-lessac-medium.onnx --output_file out.wav`

## New Files to Create

### Core Layer: `CcDirector.Core/Voice/`

**ClaudeResponseExtractor.cs**
```csharp
public static class ClaudeResponseExtractor
{
    // Extract last assistant message text from JSONL file
    // Follows ClaudeSessionReader pattern with FileShare.ReadWrite
    public static string? ExtractLastAssistantResponse(string claudeSessionId, string repoPath);
}
```

**ClaudeSummarizer.cs**
```csharp
public class ClaudeSummarizer
{
    // Use Claude Code CLI to create a conversational summary
    // Invokes: claude -p "Summarize this response as if telling a friend over coffee: ..."
    public Task<string> SummarizeAsync(string fullResponse);
}
```

### WPF Layer: `CcDirector.Wpf/Voice/`

**VoiceModeController.cs**
- Central coordinator
- Properties: `IsEnabled`, `IsRecording`, `IsPlaying`
- Methods: `Enable(Session)`, `Disable()`, `ToggleRecording()`, `PlayResponseAsync()`
- Subscribes to active session's `OnActivityStateChanged`
- Uses Dispatcher for UI thread safety

**AudioRecorder.cs**
- Wraps NAudio `WaveInEvent` for microphone capture
- Methods: `StartRecording()`, `StopRecording() -> string` (returns temp WAV path)
- Outputs 16kHz mono WAV (Whisper's preferred format)

**AudioPlayer.cs**
- Wraps NAudio `WaveOutEvent` for WAV playback
- Methods: `PlayAsync(string wavPath)`, `Stop()`
- Fires `OnPlaybackComplete` event

**LocalWhisperClient.cs**
- Wraps whisper.cpp CLI
- Method: `Task<string> TranscribeAsync(string wavPath)`
- Runs process, captures stdout, returns transcription
- Handles errors (model not found, etc.)

**LocalPiperClient.cs**
- Wraps Piper CLI
- Method: `Task<string> SynthesizeAsync(string text)` (returns WAV path)
- Pipes text to stdin, outputs to temp WAV file
- Handles errors (model not found, etc.)

**VoiceModelManager.cs**
- Checks if models are installed
- Properties: `IsWhisperAvailable`, `IsPiperAvailable`
- Method: `GetMissingModels() -> List<string>`
- Could provide download links/instructions

## Files to Modify

**MainWindow.xaml.cs**
- Add `VoiceModeController` field
- Add keyboard handler for F9 (toggle recording)
- Subscribe to session changes to wire up voice controller
- Wire up voice mode toggle button

**MainWindow.xaml**
- Add voice mode toggle button in prompt bar (mic icon)
- Add visual indicator for recording/playing state

**App.xaml.cs**
- Add VoiceModeSettings to configuration
- Check for model availability on startup

**CcDirector.Wpf.csproj**
- Add: `<PackageReference Include="NAudio" Version="2.*" />`

## Implementation Sequence

### Phase 1: Response Extraction & Summarization
1. Create `ClaudeResponseExtractor.cs` following ClaudeSessionReader pattern
2. Create `ClaudeSummarizer.cs` - wraps Claude CLI for conversational summaries
3. Write unit tests

### Phase 2: Audio Infrastructure
1. Add NAudio dependency
2. Create `AudioRecorder.cs` with WaveInEvent (16kHz mono WAV)
3. Create `AudioPlayer.cs` with WaveOutEvent
4. Test audio capture and playback

### Phase 3: Local Model Integration
1. Create `LocalWhisperClient.cs` - wraps whisper.cpp CLI
2. Create `LocalPiperClient.cs` - wraps Piper CLI
3. Create `VoiceModelManager.cs` - checks model availability
4. Test end-to-end voice round-trip

### Phase 4: Voice Controller
1. Create `VoiceModeController.cs`
2. Wire to Session.OnActivityStateChanged
3. Implement TTS trigger on WaitingForInput
4. Implement recording toggle flow

### Phase 5: UI Integration
1. Add F9 keyboard shortcut
2. Add voice mode toggle button
3. Add recording/playing visual indicator
4. Show warning if models not installed
5. Wire to MainWindow

## Key Implementation Details

### Summarization via Claude Code

Instead of simple text truncation, we use Claude Code CLI as a summarization engine.
This produces natural, conversational summaries perfect for TTS.

```csharp
public class ClaudeSummarizer
{
    public async Task<string> SummarizeAsync(string fullResponse)
    {
        // Escape the response for command line
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, fullResponse);

        var prompt = $@"Read the file at {tempFile} and summarize it in 2-3 sentences
as if you're telling a friend what happened over coffee. Be casual and conversational.
Skip any code details - just tell me what was accomplished or what the answer was.
Output ONLY the summary, nothing else.";

        // Use Haiku for fast, cheap summarization
        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            Arguments = $"--model haiku -p \"{prompt.Replace("\"", "\\\"")}\" --output-format text",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        var output = await process!.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        File.Delete(tempFile);
        return output.Trim();
    }
}
```

**Example input:**
```
I've analyzed the codebase and found the issue. The bug was in SessionManager.cs
line 145 where the null check was missing. I've added the following fix:

```csharp
if (session?.ClaudeSessionId == null) return;
```

I also updated the unit tests to cover this edge case. The tests are now passing.
```

**Example output (spoken via TTS):**
"Found the bug - there was a missing null check in the session manager.
Fixed it and the tests are passing now."

### Extracting Last Assistant Response

```csharp
// In ClaudeResponseExtractor.cs - follows ClaudeSessionReader pattern
public static string? ExtractLastAssistantResponse(string claudeSessionId, string repoPath)
{
    var jsonlPath = ClaudeSessionReader.GetJsonlPath(claudeSessionId, repoPath);
    if (!File.Exists(jsonlPath)) return null;

    using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(fs);

    string? lastResponse = null;
    while (reader.ReadLine() is { } line)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (root.TryGetProperty("type", out var t) && t.GetString() == "assistant")
        {
            // Extract text from message.content array (same pattern as user prompts)
            lastResponse = ExtractTextFromMessage(root);
        }
    }
    return lastResponse;
}
```

### Local Whisper Integration

```csharp
public class LocalWhisperClient
{
    private readonly string _whisperExePath;
    private readonly string _modelPath;

    public async Task<string> TranscribeAsync(string wavPath)
    {
        var outputPath = Path.ChangeExtension(wavPath, ".txt");

        var psi = new ProcessStartInfo
        {
            FileName = _whisperExePath,
            Arguments = $"-m \"{_modelPath}\" -f \"{wavPath}\" -otxt -of \"{Path.GetFileNameWithoutExtension(wavPath)}\"",
            WorkingDirectory = Path.GetDirectoryName(wavPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        await process!.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Whisper failed: {await process.StandardError.ReadToEndAsync()}");

        var transcription = await File.ReadAllTextAsync(outputPath);
        File.Delete(outputPath); // Cleanup
        return transcription.Trim();
    }
}
```

### Local Piper Integration

```csharp
public class LocalPiperClient
{
    private readonly string _piperExePath;
    private readonly string _modelPath;

    public async Task<string> SynthesizeAsync(string text)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"tts_{Guid.NewGuid()}.wav");

        var psi = new ProcessStartInfo
        {
            FileName = _piperExePath,
            Arguments = $"--model \"{_modelPath}\" --output_file \"{outputPath}\"",
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        await process!.StandardInput.WriteLineAsync(text);
        process.StandardInput.Close();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 || !File.Exists(outputPath))
            throw new InvalidOperationException($"Piper failed: {await process.StandardError.ReadToEndAsync()}");

        return outputPath;
    }
}
```

### TTS Trigger Hook

```csharp
// In VoiceModeController - subscribe to active session
private void OnActivityStateChanged(ActivityState oldState, ActivityState newState)
{
    if (newState == ActivityState.WaitingForInput && IsEnabled)
    {
        _ = PlayResponseAsync(); // Fire and forget
    }
}

public async Task PlayResponseAsync()
{
    var session = _activeSession;
    if (session?.ClaudeSessionId == null) return;

    // Extract Claude's last response from JSONL
    var response = ClaudeResponseExtractor.ExtractLastAssistantResponse(
        session.ClaudeSessionId, session.RepoPath);
    if (string.IsNullOrEmpty(response)) return;

    // Use Claude Code to create a conversational summary
    var summary = await _claudeSummarizer.SummarizeAsync(response);

    // Speak it via Piper TTS
    var wavPath = await _piperClient.SynthesizeAsync(summary);
    await _player.PlayAsync(wavPath);
    File.Delete(wavPath); // Cleanup
}
```

### Recording Toggle Flow

```csharp
// F9 key handler in MainWindow
private async void OnVoiceToggle()
{
    if (!_voiceController.IsEnabled) return;

    if (_voiceController.IsRecording)
    {
        // Stop and transcribe
        var wavPath = _voiceController.StopRecording();
        var transcription = await _voiceController.TranscribeAsync(wavPath);
        File.Delete(wavPath); // Cleanup

        if (!string.IsNullOrEmpty(transcription))
        {
            await _activeSession.SendTextAsync(transcription);
        }
    }
    else
    {
        _voiceController.StartRecording();
    }
}
```

## Configuration

appsettings.json:
```json
{
  "VoiceMode": {
    "Enabled": true,
    "WhisperModel": "ggml-base.en.bin",
    "PiperVoice": "en_US-lessac-medium",
    "SummaryModel": "haiku",
    "KeyboardShortcut": "F9",
    "ModelsPath": "%LOCALAPPDATA%\\CcDirector\\voice",
    "SkipShortResponses": true,
    "MinResponseLengthForTts": 50
  }
}
```

## Model Download Instructions

First-time setup requires downloading models (~250MB total):

### Whisper.cpp
1. Download from: https://github.com/ggerganov/whisper.cpp/releases
2. Get Windows binaries (whisper.exe)
3. Download model: https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin

### Piper TTS
1. Download from: https://github.com/rhasspy/piper/releases
2. Get Windows binaries (piper.exe)
3. Download voice: https://huggingface.co/rhasspy/piper-voices/tree/main/en/en_US/lessac/medium

Place files in: `%LOCALAPPDATA%\CcDirector\voice\whisper\` and `%LOCALAPPDATA%\CcDirector\voice\piper\`

## Testing Strategy

### Unit Tests
- `ClaudeResponseExtractorTests`: Extract from valid/empty/malformed JSONL
- `ClaudeSummarizerTests`: Integration test - verify Claude CLI invocation works
- `VoiceModelManagerTests`: Model detection, path resolution

### Manual Testing
1. Enable voice mode, wait for Claude response, verify TTS plays
2. Press F9, speak, press F9, verify transcription in prompt input
3. Send transcribed prompt, verify Claude processes it
4. Switch sessions, verify voice follows active session
5. Test with models missing - verify clear error message

## Verification

After implementation:
1. Download and install Whisper.cpp + Piper models
2. Build the solution: `dotnet build`
3. Run unit tests: `dotnet test`
4. Launch CC Director
5. Create a session and enable voice mode
6. Send a prompt and verify TTS plays when Claude responds
7. Press F9, speak a prompt, press F9, verify transcription and submission

## Resource Requirements

- Whisper.cpp (base.en model): ~150MB disk, ~500MB RAM during transcription
- Piper TTS (medium voice): ~100MB disk, ~200MB RAM during synthesis
- Audio recording: Minimal overhead
- Total: ~250MB disk for models, ~700MB peak RAM during voice operations

## Open Questions

1. **GPU acceleration**: Whisper.cpp supports CUDA - worth enabling for faster transcription?
2. **Voice selection**: Should users be able to choose different Piper voices?
3. **Continuous listening**: Should we support "always listening" mode instead of toggle?
4. **Summarization model**: Use Haiku for faster/cheaper summaries, or Sonnet for better quality?
5. **Summary length**: How long should the spoken summary be? 2-3 sentences? Configurable?
6. **Skip trivial responses**: Should we skip TTS for very short responses like "Done" or "OK"?

## Critical Files Reference

- `CcDirector.Core/Claude/ClaudeSessionReader.cs` - Pattern for JSONL reading
- `CcDirector.Core/Sessions/Session.cs` - OnActivityStateChanged event, ActivityState
- `CcDirector.Wpf/MainWindow.xaml.cs` - _activeSession, keyboard handling
