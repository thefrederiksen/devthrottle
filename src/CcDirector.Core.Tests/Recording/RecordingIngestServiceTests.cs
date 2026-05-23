using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Dictation;
using CcDirector.Core.Recording;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Recording;

/// <summary>
/// Unit tests for <see cref="RecordingIngestService"/>. The deterministic
/// tests use a fake transcriber + fake filer so they run without OpenAI or
/// cc-vault. One gated end-to-end test injects the real Phase 0 audio clips
/// through the real transcription pipeline and asserts company terms survive;
/// it self-skips when OPENAI_API_KEY is absent.
/// </summary>
public sealed class RecordingIngestServiceTests : IDisposable
{
    private readonly string _tmp;

    public RecordingIngestServiceTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "cc-rec-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private RecordingIngestService NewService(IRecordingTranscriber transcriber, FakeFiler filer)
        => new(
            recordingsRoot: Path.Combine(_tmp, "recordings"),
            transcriber: transcriber,
            vaultFiler: filer,
            collectionDir: Path.Combine(_tmp, "collection"));

    private static RecordingRegisterRequest Reg(string id, string codec = "mp3")
        => new(RecordingId: id, Title: "Test Call", DeviceId: "dev-1",
               StartedAt: "2026-05-23T09:00:00Z", Codec: codec, SampleRateHz: 16000, Channels: 1);

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public void Register_CreatesReceivingStatus()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        var status = svc.Register(Reg("rec1"));

        Assert.Equal("rec1", status.RecordingId);
        Assert.Equal("receiving", status.State);
        Assert.Equal(0, status.ChunksReceived);
    }

    [Fact]
    public void Register_IsIdempotent_DoesNotResetState()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        svc.Register(Reg("rec1"));
        var again = svc.Register(Reg("rec1"));
        Assert.Equal("rec1", again.RecordingId);
        Assert.Equal("Test Call", again.Title);
    }

    [Fact]
    public async Task StoreChunk_WritesAndCounts()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        svc.Register(Reg("rec1"));
        var bytes = Encoding.UTF8.GetBytes("fake-audio-0");
        await svc.StoreChunkAsync("rec1", 0, bytes, Sha(bytes));

        Assert.Equal(1, svc.GetStatus("rec1").ChunksReceived);
    }

    [Fact]
    public async Task StoreChunk_SameHash_IsIdempotent()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        svc.Register(Reg("rec1"));
        var bytes = Encoding.UTF8.GetBytes("fake-audio-0");
        await svc.StoreChunkAsync("rec1", 0, bytes, Sha(bytes));
        await svc.StoreChunkAsync("rec1", 0, bytes, Sha(bytes));

        Assert.Equal(1, svc.GetStatus("rec1").ChunksReceived);
    }

    [Fact]
    public async Task StoreChunk_HashMismatch_Throws()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        svc.Register(Reg("rec1"));
        var bytes = Encoding.UTF8.GetBytes("fake-audio-0");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.StoreChunkAsync("rec1", 0, bytes, "deadbeef"));
    }

    [Fact]
    public async Task StoreChunk_UnregisteredRecording_Throws()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        var bytes = Encoding.UTF8.GetBytes("x");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.StoreChunkAsync("nope", 0, bytes, Sha(bytes)));
    }

    [Fact]
    public async Task Complete_AssemblesCleansAndFiles()
    {
        var transcriber = new FakeTranscriber();
        var filer = new FakeFiler();
        var svc = NewService(transcriber, filer);
        svc.Register(Reg("rec1"));

        var c0 = Encoding.UTF8.GetBytes("audio-0");
        var c1 = Encoding.UTF8.GetBytes("audio-1");
        await svc.StoreChunkAsync("rec1", 0, c0, Sha(c0));
        await svc.StoreChunkAsync("rec1", 1, c1, Sha(c1));

        var manifest = new RecordingManifest(
            RecordingId: "rec1", Title: "Test Call", DeviceId: "dev-1",
            StartedAt: "2026-05-23T09:00:00Z", EndedAt: "2026-05-23T09:02:00Z",
            SampleRateHz: 16000, Channels: 1, Codec: "mp3",
            Chunks: new()
            {
                new RecordingChunkInfo(0, "0000.mp3", 0, 60000, c0.Length, Sha(c0)),
                new RecordingChunkInfo(1, "0001.mp3", 60000, 60000, c1.Length, Sha(c1)),
            },
            Notes: new() { new RecordingNote(65000, "Discussed pricing") });

        var status = await svc.CompleteAsync("rec1", manifest);

        Assert.Equal("filed", status.State);
        Assert.Equal(2, status.ChunksTranscribed);
        Assert.NotNull(status.VaultDocId);

        // Filer received a markdown that carries cleaned transcript + the note.
        Assert.Single(filer.Filed);
        var md = File.ReadAllText(filer.Filed[0].TranscriptMarkdownPath);
        Assert.Contains("CLEANED", md);          // FakeTranscriber stamps cleanup
        Assert.Contains("Discussed pricing", md); // note rendered
        Assert.Contains("[01:05]", md);           // note timestamp offset
    }

    [Fact]
    public async Task Complete_VaultFailure_RecordsErrorAndRethrows()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler { ThrowOnFile = true });
        svc.Register(Reg("rec1"));
        var c0 = Encoding.UTF8.GetBytes("audio-0");
        await svc.StoreChunkAsync("rec1", 0, c0, Sha(c0));

        var manifest = new RecordingManifest("rec1", "Test Call", "dev-1",
            "2026-05-23T09:00:00Z", null, 16000, 1, "mp3",
            new() { new RecordingChunkInfo(0, "0000.mp3", 0, 60000, c0.Length, Sha(c0)) },
            new());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CompleteAsync("rec1", manifest));

        var status = svc.GetStatus("rec1");
        Assert.Equal("error", status.State);
        Assert.NotNull(status.Error);
    }

    [Fact]
    public void GetStatus_Unknown_Throws()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        Assert.Throws<InvalidOperationException>(() => svc.GetStatus("nope"));
    }

    [Fact]
    public async Task EndToEnd_RealTranscription_Phase0Clips_PreservesCompanyTerms()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
            return; // self-skip without credentials

        var clips = FindPhase0Clips();
        if (clips.Count == 0) return; // clips not present in this checkout

        // Real transcription + cleanup, with the project vocabulary so company
        // terms survive. Fake filer so the test does not mutate the real vault.
        using var transcriber = new OpenAiRecordingTranscriber(
            dictionaryPath: WriteTestDictionary());
        var filer = new FakeFiler();
        var svc = NewService(transcriber, filer);
        svc.Register(Reg("e2e", codec: "mp3"));

        var chunkInfos = new List<RecordingChunkInfo>();
        for (int i = 0; i < clips.Count; i++)
        {
            var bytes = await File.ReadAllBytesAsync(clips[i]);
            var sha = Sha(bytes);
            await svc.StoreChunkAsync("e2e", i, bytes, sha);
            chunkInfos.Add(new RecordingChunkInfo(i, $"{i:D4}.mp3", i * 60000L, 60000, bytes.Length, sha));
        }

        var manifest = new RecordingManifest("e2e", "Phase0 Injected Call", "test-injector",
            "2026-05-23T09:00:00Z", "2026-05-23T09:03:00Z", 16000, 1, "mp3",
            chunkInfos, new() { new RecordingNote(1000, "injected audio test") });

        var status = await svc.CompleteAsync("e2e", manifest);

        Assert.Equal("filed", status.State);
        Assert.Equal(clips.Count, status.ChunksTranscribed);

        var md = File.ReadAllText(filer.Filed[0].TranscriptMarkdownPath);
        // At least one of the known Phase 0 company terms must survive cleanup.
        var hit = md.Contains("ConPTY", StringComparison.OrdinalIgnoreCase)
               || md.Contains("Avalonia", StringComparison.OrdinalIgnoreCase)
               || md.Contains("Soren", StringComparison.OrdinalIgnoreCase)
               || md.Contains("mindzie", StringComparison.OrdinalIgnoreCase);
        Assert.True(hit, "expected a known company term in the transcript markdown");
    }

    // ===== test helpers =====================================================

    private string WriteTestDictionary()
    {
        var path = Path.Combine(_tmp, "dict.yaml");
        File.WriteAllText(path,
            "vocabulary:\n  - mindzie\n  - ConPTY\n  - Avalonia\n  - Soren Frederiksen\n");
        return path;
    }

    private static List<string> FindPhase0Clips()
    {
        var here = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && here is not null; i++)
        {
            var dir = Path.Combine(here, "docs", "features", "dictation", "phase0");
            if (Directory.Exists(dir))
            {
                var clips = new List<string>();
                foreach (var name in new[] { "clip1.mp3", "clip2.mp3", "clip3.mp3" })
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p)) clips.Add(p);
                }
                return clips;
            }
            here = Path.GetDirectoryName(here);
        }
        return new();
    }

    private sealed class FakeTranscriber : IRecordingTranscriber
    {
        private int _n;
        public Task<string> TranscribeChunkAsync(byte[] audio, string contentType, string fileName, CancellationToken ct = default)
            => Task.FromResult($"segment {_n++} text");

        public Task<CleanupOutcome> CleanupAsync(string rawTranscript, CancellationToken ct = default)
            => Task.FromResult(new CleanupOutcome("CLEANED: " + rawTranscript.Replace("\n", " "), Applied: true, Reason: null));
    }

    private sealed class FakeFiler : IVaultFiler
    {
        public bool ThrowOnFile { get; init; }
        public List<VaultFilingRequest> Filed { get; } = new();

        public Task<string> FileTranscriptAsync(VaultFilingRequest request, CancellationToken ct = default)
        {
            if (ThrowOnFile) throw new InvalidOperationException("simulated vault failure");
            Filed.Add(request);
            return Task.FromResult("vault-doc-" + Filed.Count);
        }
    }
}
