using System.Diagnostics;
using System.Net.Http;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Providers;
using Xunit;
using Xunit.Abstractions;

#nullable enable

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Live, end-to-end validation that the capture-first pipeline preserves the
/// OPENING words of a recording against the REAL OpenAI Realtime API - i.e.
/// the exact failure the user reported ("I lost several sentences from the
/// beginning") cannot happen anymore.
///
/// How it proves it: a <see cref="ReplayAudioSource"/> begins streaming a real
/// recorded clip the instant the pipeline starts, on its own thread - so the
/// first chunks are emitted WHILE the real WebSocket is still connecting,
/// exactly reproducing the timing that used to drop the opening. The pipeline
/// must buffer those chunks and deliver them, and the returned transcript must
/// still contain the clip's opening word.
///
/// Self-skips (returns) when OPENAI_API_KEY is unset, ffmpeg is not on PATH, or
/// the phase0 clips are missing, so CI without credentials still passes. These
/// tests COST real OpenAI credits; the clips are a few seconds each.
/// </summary>
public sealed class DictationPipelineLiveTests
{
    private readonly ITestOutputHelper _out;
    public DictationPipelineLiveTests(ITestOutputHelper output) => _out = output;

    private static bool HasApiKey()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

    [Theory]
    // clip text and its distinctive OPENING token (would vanish if the start were dropped):
    [InlineData("clip1.mp3", "sent")]  // "I sent the cc-director patch ..."
    [InlineData("clip3.mp3", "tell")]  // "Tell mindzie that the CenCon report ..."
    public async Task RealAudio_OpeningWordSurvives_ThroughCaptureFirstPipeline(string clip, string openingToken)
    {
        if (!HasApiKey()) { _out.WriteLine("SKIP: OPENAI_API_KEY not set"); return; }
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) { _out.WriteLine("SKIP: ffmpeg not on PATH"); return; }
        var mp3 = FindClip(clip);
        if (mp3 is null) { _out.WriteLine($"SKIP: {clip} not found"); return; }
        var pcm = DecodeMp3ToPcm16At24k(mp3, ffmpeg);
        if (pcm is null || pcm.Length == 0) { _out.WriteLine("SKIP: decode produced no audio"); return; }

        using var dict = new DictionaryLoader(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".yaml"), watch: false);
        // Offline cleanup: with an empty dictionary the cleanup pass is a no-op
        // and never touches the network, so we assert on the RAW transcript -
        // pure speech-to-text fidelity, nothing massaged.
        using var cleanup = new CleanupOrchestrator(
            "test-key-ignored", "gpt-4o-mini", new HttpClient(new FailHandler()));

        await using var provider = new OpenAiRealtimeProvider();
        await using var session = new DictationSession(dict, provider, cleanup);
        var src = new ReplayAudioSource(pcm, chunkBytes: 2400, chunkDelayMs: 50); // ~50ms PCM frames @ 24kHz
        await using var pipeline = new DictationPipeline(src, session);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        await pipeline.StartAsync("default", cts.Token);
        // Let the whole clip stream through (it began the moment we started).
        await src.Completed.WaitAsync(TimeSpan.FromSeconds(60), cts.Token);
        var result = await pipeline.StopAsync(cts.Token);

        var transcript = (result.RawTranscript ?? "").ToLowerInvariant();
        _out.WriteLine($"clip={clip} primed_chunks={pipeline.PrimedChunkCount} " +
                       $"captured={pipeline.CapturedBytes} delivered={pipeline.DeliveredBytes}");
        _out.WriteLine($"transcript: {result.RawTranscript}");

        // 1. No captured audio was lost in transit.
        Assert.Equal(pipeline.CapturedBytes, pipeline.DeliveredBytes);
        // 2. We actually exercised the priming window (chunks captured pre-connect).
        Assert.True(pipeline.PrimedChunkCount > 0,
            "expected at least one chunk captured before the connection completed");
        // 3. The opening word survived - the whole point.
        Assert.False(string.IsNullOrWhiteSpace(transcript), "transcript was empty");
        Assert.Contains(openingToken, transcript);
    }

    // ===== replay source ====================================================

    /// <summary>
    /// Streams a PCM16 buffer as if it were a live mic: begins emitting the
    /// instant <see cref="Start"/> is called, on a background thread, in
    /// fixed-size frames at a fixed cadence. Because emission starts before the
    /// real connection completes, the pipeline's priming path is exercised for
    /// real.
    /// </summary>
    private sealed class ReplayAudioSource : IAudioSource
    {
        private readonly byte[] _pcm;
        private readonly int _chunkBytes;
        private readonly int _chunkDelayMs;
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Thread? _thread;
        private volatile bool _stop;

        public event Action<byte[]>? OnAudioChunk;

        public string Description => "Replay Test Source";

        /// <summary>Completes when the entire clip has been emitted (or capture was stopped).</summary>
        public Task Completed => _completed.Task;

        public ReplayAudioSource(byte[] pcm, int chunkBytes, int chunkDelayMs)
        {
            _pcm = pcm;
            _chunkBytes = chunkBytes;
            _chunkDelayMs = chunkDelayMs;
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "replay-audio" };
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

    private sealed class FailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("offline cleanup");
    }

    // ===== helpers (mirrors OpenAiRealtimeProviderIntegrationTests) =========

    private static string? FindClip(string fileName)
    {
        var here = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && here is not null; i++)
        {
            var candidate = Path.Combine(here, "docs", "features", "dictation", "phase0", fileName);
            if (File.Exists(candidate)) return candidate;
            here = Path.GetDirectoryName(here);
        }
        return null;
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

    private static byte[]? DecodeMp3ToPcm16At24k(string mp3Path, string ffmpegExe)
    {
        var psi = new ProcessStartInfo(ffmpegExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");        psi.ArgumentList.Add(mp3Path);
        psi.ArgumentList.Add("-f");        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("-acodec");   psi.ArgumentList.Add("pcm_s16le");
        psi.ArgumentList.Add("-ar");       psi.ArgumentList.Add("24000");
        psi.ArgumentList.Add("-ac");       psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("pipe:1");

        using var proc = Process.Start(psi);
        if (proc is null) return null;
        using var ms = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(ms);
        proc.WaitForExit(20_000);
        return proc.ExitCode != 0 ? null : ms.ToArray();
    }
}
