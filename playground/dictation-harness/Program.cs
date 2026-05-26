using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Providers;

namespace DictationHarness;

/// <summary>
/// End-to-end smoke test for the cc-director dictation library.
///
/// Two modes:
///
/// - DEFAULT (batch): loads an audio file and drives it through
///   DictionaryLoader -> OpenAiTranscriptionProvider -> CleanupOrchestrator via
///   DictationSession in one shot. Good for prompt/dictionary tuning.
///
/// - --stream: drives the REAL desktop path -
///   DictationPipeline -> OpenAiRealtimeProvider. A ReplayAudioSource streams
///   the clip frame by frame STARTING THE INSTANT the pipeline starts, so the
///   first frames are emitted while the WebSocket is still connecting. This is
///   the capture-first regression check: if the opening of the transcript is
///   intact, the "lost the first sentence" bug is gone. Requires ffmpeg on PATH
///   to decode to PCM16/24kHz/mono.
///
/// Usage:
///   cc-dictate-harness &lt;audio-file&gt; [&lt;dictionary.yaml&gt;] [--profile NAME] [--stream]
///
/// Defaults to the Phase 0 sample audio files when invoked without arguments.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        string? audioPath = null;
        string? dictionaryPath = null;
        string profile = "default";
        bool stream = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--profile":
                    if (++i >= args.Length) { Usage(); return 2; }
                    profile = args[i];
                    break;
                case "--stream":
                    stream = true;
                    break;
                case "-h":
                case "--help":
                    Usage();
                    return 0;
                default:
                    if (audioPath is null) audioPath = args[i];
                    else if (dictionaryPath is null) dictionaryPath = args[i];
                    else { Usage(); return 2; }
                    break;
            }
        }

        var repoRoot = ResolveRepoRoot();

        // Default to the Phase 0 sample audio + a generated default dictionary
        // so the harness runs with no arguments out of the box.
        if (audioPath is null)
        {
            audioPath = Path.Combine(repoRoot, "docs", "features", "dictation", "phase0", "clip2.mp3");
            Console.WriteLine($"[harness] No audio path given. Using Phase 0 sample: {audioPath}");
        }
        if (!File.Exists(audioPath))
        {
            Console.Error.WriteLine($"ERROR: audio file not found: {audioPath}");
            return 1;
        }

        if (dictionaryPath is null)
        {
            dictionaryPath = Path.Combine(repoRoot, "playground", "dictation-harness", "sample-dictionary.yaml");
            if (!File.Exists(dictionaryPath))
            {
                Console.WriteLine($"[harness] Writing sample dictionary to {dictionaryPath}");
                File.WriteAllText(dictionaryPath, SampleDictionaryYaml);
            }
        }

        Console.WriteLine($"[harness] audio       : {audioPath}");
        Console.WriteLine($"[harness] dictionary  : {dictionaryPath}");
        Console.WriteLine($"[harness] profile     : {profile}");
        Console.WriteLine($"[harness] cleanup     : OpenAI gpt-4o-mini");
        Console.WriteLine();

        using var dictionary = new DictionaryLoader(dictionaryPath, watch: false);
        var dict = dictionary.Current;
        Console.WriteLine($"[harness] vocab terms : {dict.Vocabulary.Count}");
        Console.WriteLine($"[harness] patterns    : {dict.CommonMistranscriptions.Count}");
        Console.WriteLine($"[harness] profiles    : {string.Join(", ", dict.Profiles.Keys)}");
        Console.WriteLine();

        if (stream)
            return await RunStreamingAsync(audioPath, dictionary, profile);

        var contentType = ContentTypeFor(audioPath);
        await using var provider = new OpenAiTranscriptionProvider(
            audioContentType: contentType,
            audioFileName: Path.GetFileName(audioPath));
        // Cleanup now uses OpenAI (gpt-4o-mini by default) over HTTP instead
        // of spawning the claude CLI. Faster (~1s vs 10-20s) and uses the
        // same OPENAI_API_KEY we already need for transcription.
        using var cleanup = new CleanupOrchestrator(
            model: "gpt-4o-mini");

        await using var session = new DictationSession(dictionary, provider, cleanup);
        session.OnPartial += t => Console.WriteLine($"[partial] {t}");

        await session.StartAsync(profile);

        // Feed the file as one big chunk. The batch provider buffers internally;
        // chunk granularity does not change the result for this provider.
        var audioBytes = await File.ReadAllBytesAsync(audioPath);
        await session.PushAudioAsync(audioBytes);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await session.StopAsync();
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"[harness] elapsed     : {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"[harness] cleanup_on  : {result.CleanupApplied}");
        if (!string.IsNullOrEmpty(result.CleanupFailureReason))
            Console.WriteLine($"[harness] cleanup_why : {result.CleanupFailureReason}");
        Console.WriteLine();
        Console.WriteLine("---- RAW ----");
        Console.WriteLine(result.RawTranscript);
        Console.WriteLine();
        Console.WriteLine("---- CLEANED ----");
        Console.WriteLine(result.CleanedTranscript);

        return 0;
    }

    /// <summary>
    /// Drive the real desktop streaming path (DictationPipeline +
    /// OpenAiRealtimeProvider) with a replay source that starts emitting the
    /// instant the pipeline starts - so the first frames hit the priming
    /// buffer while the connection is still opening. Proves the opening of the
    /// recording is never dropped.
    /// </summary>
    private static async Task<int> RunStreamingAsync(string audioPath, DictionaryLoader dictionary, string profile)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Console.Error.WriteLine("ERROR: --stream needs ffmpeg on PATH to decode audio to PCM16/24kHz/mono.");
            Console.Error.WriteLine("Install ffmpeg and retry. (winget install Gyan.FFmpeg)");
            return 1;
        }

        Console.WriteLine("[harness] mode        : STREAMING (capture-first pipeline, OpenAiRealtimeProvider)");
        var pcm = DecodeToPcm16At24k(audioPath, ffmpeg);
        if (pcm is null || pcm.Length == 0)
        {
            Console.Error.WriteLine($"ERROR: ffmpeg produced no audio from {audioPath}");
            return 1;
        }
        double seconds = pcm.Length / (24_000.0 * 2.0);
        Console.WriteLine($"[harness] decoded     : {pcm.Length} bytes (~{seconds:F1}s PCM16 24kHz mono)");

        await using var provider = new OpenAiRealtimeProvider();
        using var cleanup = new CleanupOrchestrator(model: "gpt-4o-mini");
        await using var session = new DictationSession(dictionary, provider, cleanup);
        var src = new ReplayAudioSource(pcm, chunkBytes: 2400, chunkDelayMs: 50);
        await using var pipeline = new DictationPipeline(src, session);
        pipeline.OnCaptureStarted += () => Console.WriteLine("[harness] capture started (connection still opening)");
        pipeline.OnConnected += () => Console.WriteLine($"[harness] connected after {src.EmittedChunks} primed frame(s)");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await pipeline.StartAsync(profile);
        await src.Completed.WaitAsync(TimeSpan.FromSeconds(120));
        var result = await pipeline.StopAsync();
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"[harness] elapsed       : {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"[harness] primed frames : {pipeline.PrimedChunkCount} (captured before the link was up)");
        Console.WriteLine($"[harness] captured bytes: {pipeline.CapturedBytes}");
        Console.WriteLine($"[harness] delivered byte: {pipeline.DeliveredBytes}");
        Console.WriteLine($"[harness] no audio lost : {(pipeline.CapturedBytes == pipeline.DeliveredBytes ? "PASS" : "FAIL")}");
        Console.WriteLine($"[harness] cleanup_on    : {result.CleanupApplied}");
        if (!string.IsNullOrEmpty(result.CleanupFailureReason))
            Console.WriteLine($"[harness] cleanup_why   : {result.CleanupFailureReason}");
        Console.WriteLine();
        Console.WriteLine("---- RAW ----");
        Console.WriteLine(result.RawTranscript);
        Console.WriteLine();
        Console.WriteLine("---- CLEANED ----");
        Console.WriteLine(result.CleanedTranscript);

        return pipeline.CapturedBytes == pipeline.DeliveredBytes ? 0 : 1;
    }

    /// <summary>
    /// Replay source for the harness: emits a PCM16 buffer frame by frame on a
    /// background thread, starting the moment Start() is called. Mirrors a live
    /// mic so the capture-first pipeline gets exercised end to end.
    /// </summary>
    private sealed class ReplayAudioSource : IAudioSource
    {
        private readonly byte[] _pcm;
        private readonly int _chunkBytes;
        private readonly int _chunkDelayMs;
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Thread? _thread;
        private volatile bool _stop;
        private int _emitted;

        public event Action<byte[]>? OnAudioChunk;
        public Task Completed => _completed.Task;
        public int EmittedChunks => Volatile.Read(ref _emitted);

        public ReplayAudioSource(byte[] pcm, int chunkBytes, int chunkDelayMs)
        {
            _pcm = pcm;
            _chunkBytes = chunkBytes;
            _chunkDelayMs = chunkDelayMs;
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "harness-replay" };
            _thread.Start();
        }

        private void Run()
        {
            try
            {
                for (int off = 0; off < _pcm.Length && !_stop; off += _chunkBytes)
                {
                    int len = Math.Min(_chunkBytes, _pcm.Length - off);
                    var chunk = new byte[len];
                    Buffer.BlockCopy(_pcm, off, chunk, 0, len);
                    OnAudioChunk?.Invoke(chunk);
                    Interlocked.Increment(ref _emitted);
                    Thread.Sleep(_chunkDelayMs);
                }
            }
            finally { _completed.TrySetResult(); }
        }

        public void Stop()
        {
            _stop = true;
            _completed.TrySetResult();
            _thread?.Join(1000);
        }
    }

    private static string? FindFfmpeg()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static byte[]? DecodeToPcm16At24k(string audioPath, string ffmpegExe)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpegExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");        psi.ArgumentList.Add(audioPath);
        psi.ArgumentList.Add("-f");        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("-acodec");   psi.ArgumentList.Add("pcm_s16le");
        psi.ArgumentList.Add("-ar");       psi.ArgumentList.Add("24000");
        psi.ArgumentList.Add("-ac");       psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("pipe:1");

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) return null;
        using var ms = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(ms);
        proc.WaitForExit(20_000);
        return proc.ExitCode != 0 ? null : ms.ToArray();
    }

    private static string ResolveRepoRoot()
    {
        // Walk up from the executable looking for the repo's known marker.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "CLAUDE.md"))
                && Directory.Exists(Path.Combine(dir, "docs", "features", "dictation")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        // Fallback: cwd
        return Environment.CurrentDirectory;
    }

    private static string ContentTypeFor(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",
            ".webm" => "audio/webm",
            ".ogg" => "audio/ogg",
            _ => "application/octet-stream",
        };

    private static void Usage()
    {
        Console.Error.WriteLine("Usage: cc-dictate-harness <audio-file> [<dictionary.yaml>] [--profile NAME] [--stream]");
        Console.Error.WriteLine("  --stream   Drive the real capture-first streaming pipeline (OpenAiRealtimeProvider).");
        Console.Error.WriteLine("             Replays the clip starting at connect time to prove the opening is not lost.");
        Console.Error.WriteLine("             Requires ffmpeg on PATH and OPENAI_API_KEY.");
    }

    private const string SampleDictionaryYaml = """
        vocabulary:
          - mindzie
          - CenCon
          - ConPTY
          - cc-director
          - Avalonia
          - Soren Frederiksen

        common_mistranscriptions:
          mindzie:
            - Minzy
            - Mindsy
            - Mindzy
            - Mindzie
          CenCon:
            - SenCon
            - SENCON
            - Sencon
          ConPTY:
            - Contui
            - ContUI
            - ContiUI
            - Conty
          cc-director:
            - CC Director
            - See Director
            - CC director
          Soren Frederiksen:
            - Soren Fredriksen
            - Soeren Frederiksen

        profiles:
          default:
            cleanup_enabled: true
          code:
            cleanup_enabled: false
          email:
            cleanup_enabled: true
        """;
}
