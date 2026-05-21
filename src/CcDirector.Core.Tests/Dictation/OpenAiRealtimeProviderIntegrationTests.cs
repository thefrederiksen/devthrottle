using System.Diagnostics;
using CcDirector.Core.Dictation.Providers;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Live-network integration tests for <see cref="OpenAiRealtimeProvider"/>.
///
/// Self-skip when <c>OPENAI_API_KEY</c> is not set so CI without
/// credentials still passes. The real-audio test additionally needs
/// ffmpeg on PATH to decode the Phase 0 MP3 into PCM16; if ffmpeg is
/// missing that test self-skips too.
///
/// These tests COST real OpenAI credits per run. Keep the audio short.
/// </summary>
public sealed class OpenAiRealtimeProviderIntegrationTests
{
    private static bool HasApiKey()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

    [Fact]
    public async Task Smoke_OpensConnects_AcceptsSilence_AndCompletes()
    {
        if (!HasApiKey()) return;

        await using var provider = new OpenAiRealtimeProvider();
        var partials = new List<string>();
        provider.OnPartial += p => partials.Add(p);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await provider.StartAsync("Glossary: this is a test.", cts.Token);

        // 1 second of PCM16 silence at 24 kHz mono = 48000 bytes (24000 samples * 2 bytes).
        // The Realtime API requires at least ~100ms of audio before it will commit.
        var silence = new byte[48_000];
        // Chunk into 4 KB frames to exercise the multi-send path.
        for (int offset = 0; offset < silence.Length; offset += 4096)
        {
            var len = Math.Min(4096, silence.Length - offset);
            await provider.PushAudioAsync(silence.AsMemory(offset, len), cts.Token);
        }

        // StopAsync returns the final transcript when the API emits the
        // completed event. For pure silence the transcript may be empty
        // or hallucinated; we only assert that the protocol completes.
        var transcript = await provider.StopAsync(cts.Token);
        Assert.NotNull(transcript);
    }

    [Fact]
    public async Task RealAudio_TranscribesPhase0Clip_WithCompanyTerms()
    {
        if (!HasApiKey()) return;

        var mp3 = FindClip2Mp3();
        if (mp3 is null) return;
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;

        var pcm = DecodeMp3ToPcm16At24k(mp3, ffmpeg);
        if (pcm is null || pcm.Length == 0) return;

        await using var provider = new OpenAiRealtimeProvider();
        var deltas = new List<string>();
        provider.OnPartial += p => deltas.Add(p);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await provider.StartAsync(
            "Glossary of names and terms used by the speaker: "
            + "mindzie, CenCon, ConPTY, cc-director, Avalonia, Soren Frederiksen.",
            cts.Token);

        for (int offset = 0; offset < pcm.Length; offset += 4096)
        {
            var len = Math.Min(4096, pcm.Length - offset);
            await provider.PushAudioAsync(pcm.AsMemory(offset, len), cts.Token);
        }

        var transcript = await provider.StopAsync(cts.Token);
        Assert.NotNull(transcript);
        Assert.NotEmpty(transcript);
        // Either the partials list saw progressive deltas, or we got a
        // one-shot completed event with the full transcript. We only
        // assert the final text contains at least one company term to
        // confirm the prompt parameter is doing its job.
        var lowered = transcript.ToLowerInvariant();
        Assert.True(
            lowered.Contains("conpty", StringComparison.OrdinalIgnoreCase)
            || lowered.Contains("avalonia", StringComparison.OrdinalIgnoreCase)
            || lowered.Contains("frederiksen", StringComparison.OrdinalIgnoreCase),
            $"transcript did not contain expected company terms: {transcript}");
    }

    // ===== helpers =========================================================

    private static string? FindClip2Mp3()
    {
        var here = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && here is not null; i++)
        {
            var candidate = Path.Combine(here, "docs", "features", "dictation", "phase0", "clip2.mp3");
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
            var candidate = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir, "ffmpeg");
            if (File.Exists(candidate)) return candidate;
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
        if (proc.ExitCode != 0) return null;
        return ms.ToArray();
    }
}
