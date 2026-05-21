using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Providers;

namespace DictationHarness;

/// <summary>
/// End-to-end smoke test for the cc-director dictation library.
///
/// Loads an audio file, drives it through the full pipeline
/// (DictionaryLoader -> OpenAiTranscriptionProvider -> CleanupOrchestrator)
/// via DictationSession, and prints raw vs cleaned transcripts.
///
/// Usage:
///   cc-dictate-harness &lt;audio-file&gt; [&lt;dictionary.yaml&gt;] [--profile NAME]
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

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--profile":
                    if (++i >= args.Length) { Usage(); return 2; }
                    profile = args[i];
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
        Console.Error.WriteLine("Usage: cc-dictate-harness <audio-file> [<dictionary.yaml>] [--profile NAME]");
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
            style_prompt: "tighten to professional prose"
        """;
}
