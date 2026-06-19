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

    private RecordingIngestService NewService(
        IRecordingTranscriber transcriber, FakeFiler filer,
        bool runWorker = false, int maxChunkAttempts = 3, int maxJobAttempts = 5)
        => NewServiceWithFactory(() => transcriber, filer, runWorker, maxChunkAttempts, maxJobAttempts);

    /// <summary>
    /// Build a service with a transcriber FACTORY, so a test can make the transcriber
    /// impossible to construct (the factory throws) and prove that ingest still succeeds.
    /// </summary>
    private RecordingIngestService NewServiceWithFactory(
        Func<IRecordingTranscriber> transcriberFactory, FakeFiler filer,
        bool runWorker = false, int maxChunkAttempts = 3, int maxJobAttempts = 5)
        => new(
            recordingsRoot: Path.Combine(_tmp, "recordings"),
            transcriberFactory: transcriberFactory,
            vaultFiler: filer,
            collectionDir: Path.Combine(_tmp, "collection"),
            runWorker: runWorker,
            maxChunkAttempts: maxChunkAttempts,
            maxJobAttempts: maxJobAttempts,
            chunkRetryDelay: TimeSpan.FromMilliseconds(1),
            workerTick: TimeSpan.FromMilliseconds(50));

    private static RecordingRegisterRequest Reg(string id, string codec = "mp3")
        => new(RecordingId: id, Title: "Test Call", DeviceId: "dev-1",
               StartedAt: "2026-05-23T09:00:00Z", Codec: codec, SampleRateHz: 16000, Channels: 1);

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Register, store one chunk, and complete + process so the recording
    /// reaches the "transcribed" state with a local transcript.md on disk.</summary>
    private static async Task TranscribeOneChunk(RecordingIngestService svc, string id)
    {
        svc.Register(Reg(id));
        var c0 = Encoding.UTF8.GetBytes("audio-0");
        await svc.StoreChunkAsync(id, 0, c0, Sha(c0));
        var manifest = new RecordingManifest(id, "Test Call", "dev-1",
            "2026-05-23T09:00:00Z", null, 16000, 1, "mp3",
            new() { new RecordingChunkInfo(0, "0000.mp3", 0, 60000, c0.Length, Sha(c0)) },
            new());
        await svc.CompleteAsync(id, manifest);   // enqueue
        await svc.ProcessRecordingAsync(id);     // run the queued job synchronously
    }

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

        // Complete only enqueues now and returns immediately.
        var queued = await svc.CompleteAsync("rec1", manifest);
        Assert.Equal("queued", queued.State);
        Assert.Equal(0, queued.ChunksTranscribed);

        // The worker (driven synchronously here) does the transcription.
        await svc.ProcessRecordingAsync("rec1");
        var status = svc.GetStatus("rec1");

        Assert.Equal("transcribed", status.State);
        Assert.Equal(2, status.ChunksTranscribed);

        // Transcripts are transient: completing does NOT file into the vault.
        Assert.Null(status.VaultDocId);
        Assert.Empty(filer.Filed);

        // The cleaned transcript markdown is written locally with the note.
        var md = File.ReadAllText(svc.LocalTranscriptPath("rec1")!);
        Assert.Contains("CLEANED", md);          // FakeTranscriber stamps cleanup
        Assert.Contains("Discussed pricing", md); // note rendered
        Assert.Contains("[01:05]", md);           // note timestamp offset
    }

    // ===== queue + background-worker behaviour ==============================

    [Fact]
    public async Task Complete_OnlyEnqueues_DoesNotTranscribeInline()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        var status = await EnqueueOneChunk(svc, "rec1");

        Assert.Equal("queued", status.State);
        Assert.Equal(0, status.ChunksTranscribed);
        Assert.Null(svc.LocalTranscriptPath("rec1")); // no transcript yet
    }

    [Fact]
    public async Task Complete_IsIdempotent_DoesNotRequeueInFlight()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        await EnqueueOneChunk(svc, "rec1");
        // A duplicate complete on a queued recording must keep it queued, not reset it.
        var again = await EnqueueOneChunk(svc, "rec1");
        Assert.Equal("queued", again.State);
    }

    [Fact]
    public async Task Process_RetriesFlakyChunk_ThenSucceeds()
    {
        // Fails the first two transcribe calls, succeeds on the third (within the
        // 3-attempt budget for the single chunk).
        var scripted = new ScriptedTranscriber(failFirst: 2);
        var svc = NewService(scripted, new FakeFiler(), maxChunkAttempts: 3);
        await EnqueueOneChunk(svc, "rec1");

        await svc.ProcessRecordingAsync("rec1");

        var status = svc.GetStatus("rec1");
        Assert.Equal("transcribed", status.State);
        Assert.Equal(3, scripted.Calls); // 2 failures + 1 success
    }

    [Fact]
    public async Task Process_ChunkExhaustsRetries_RecordsErrorAndSchedulesRetry()
    {
        var svc = NewService(new ScriptedTranscriber(failFirst: int.MaxValue), new FakeFiler(),
            maxChunkAttempts: 2, maxJobAttempts: 5);
        await EnqueueOneChunk(svc, "rec1");

        await svc.ProcessRecordingAsync("rec1");

        var status = svc.GetStatus("rec1");
        Assert.Equal("error", status.State);
        Assert.Equal(1, status.Attempts);               // one whole-job attempt burned
        Assert.NotNull(status.NextRetryAtUtc);          // retry scheduled (budget remains)
    }

    [Fact]
    public async Task Process_ExhaustsJobAttempts_LeavesErrorWithNoRetryScheduled()
    {
        var svc = NewService(new ScriptedTranscriber(failFirst: int.MaxValue), new FakeFiler(),
            maxChunkAttempts: 1, maxJobAttempts: 2);
        await EnqueueOneChunk(svc, "rec1");

        await svc.ProcessRecordingAsync("rec1"); // attempt 1 -> retry scheduled
        Assert.NotNull(svc.GetStatus("rec1").NextRetryAtUtc);

        await svc.ProcessRecordingAsync("rec1"); // attempt 2 -> budget exhausted

        var status = svc.GetStatus("rec1");
        Assert.Equal("error", status.State);
        Assert.Equal(2, status.Attempts);
        Assert.Null(status.NextRetryAtUtc); // no further retry will be scheduled
    }

    [Fact]
    public async Task Process_ResumesWithoutRetranscribingDoneChunks()
    {
        // Pre-seed the per-segment text as if a prior run had transcribed it.
        // The transcriber would throw if called, proving the chunk is skipped.
        var scripted = new ScriptedTranscriber(failFirst: int.MaxValue);
        var svc = NewService(scripted, new FakeFiler());
        await EnqueueOneChunk(svc, "rec1");
        var txt = Path.Combine(_tmp, "recordings", "rec1", "0000.txt");
        await File.WriteAllTextAsync(txt, "already done");

        await svc.ProcessRecordingAsync("rec1");

        var status = svc.GetStatus("rec1");
        Assert.Equal("transcribed", status.State);
        Assert.Equal(0, scripted.Calls); // the done chunk was not re-transcribed
    }

    [Fact]
    public async Task Process_AfterTranscribed_DeletesPerSegmentTempFiles()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        await TranscribeOneChunk(svc, "rec1");

        var dir = Path.Combine(_tmp, "recordings", "rec1");
        // The per-segment audio chunk and raw text cache are temporary scratch
        // and must be gone once the recording is transcribed.
        Assert.False(File.Exists(Path.Combine(dir, "0000.mp3")), "segment audio chunk should be deleted");
        Assert.False(File.Exists(Path.Combine(dir, "0000.txt")), "per-segment text cache should be deleted");
        // The durable artifacts must remain.
        Assert.True(File.Exists(Path.Combine(dir, "transcript.md")), "transcript.md must be kept");
        Assert.True(File.Exists(Path.Combine(dir, "status.json")), "status.json must be kept");
        Assert.True(File.Exists(Path.Combine(dir, "manifest.json")), "manifest.json must be kept");
    }

    [Fact]
    public async Task GetStatus_AfterSegmentCleanup_ReportsCompletedCountsFromTotal()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        await TranscribeOneChunk(svc, "rec1");

        // The per-segment files are deleted on completion, so the received/
        // transcribed counts cannot be derived from disk (that would report 0);
        // they must reflect the authoritative ChunksTotal.
        var status = svc.GetStatus("rec1");
        Assert.Equal("transcribed", status.State);
        Assert.Equal(1, status.ChunksTotal);
        Assert.Equal(1, status.ChunksReceived);
        Assert.Equal(1, status.ChunksTranscribed);
    }

    [Fact]
    public async Task Worker_TranscribesQueuedRecording_EndToEnd()
    {
        // The real background worker (runWorker: true) must drain the queue with
        // no further calls - this is the "upload and let go" path.
        using var svc = NewService(new FakeTranscriber(), new FakeFiler(), runWorker: true);
        await EnqueueOneChunk(svc, "rec1");

        var transcribed = await WaitForStateAsync(svc, "rec1", "transcribed", TimeSpan.FromSeconds(10));

        Assert.True(transcribed, "worker did not transcribe the queued recording in time");
        Assert.NotNull(svc.LocalTranscriptPath("rec1"));
    }

    [Fact]
    public async Task Promote_FilesTranscribedRecordingIntoVault()
    {
        var filer = new FakeFiler();
        var svc = NewService(new FakeTranscriber(), filer);
        await TranscribeOneChunk(svc, "rec1");

        var status = await svc.PromoteToVaultAsync("rec1");

        Assert.NotNull(status.VaultDocId);
        Assert.Single(filer.Filed);
        var md = File.ReadAllText(filer.Filed[0].TranscriptMarkdownPath);
        Assert.Contains("CLEANED", md);
    }

    [Fact]
    public async Task Promote_IsIdempotent_DoesNotReFile()
    {
        var filer = new FakeFiler();
        var svc = NewService(new FakeTranscriber(), filer);
        await TranscribeOneChunk(svc, "rec1");

        await svc.PromoteToVaultAsync("rec1");
        await svc.PromoteToVaultAsync("rec1");

        Assert.Single(filer.Filed); // filed exactly once
    }

    [Fact]
    public async Task Promote_BeforeTranscribed_Throws()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        svc.Register(Reg("rec1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PromoteToVaultAsync("rec1"));
    }

    [Fact]
    public async Task Promote_VaultFailure_Rethrows_AndStaysUnpromoted()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler { ThrowOnFile = true });
        await TranscribeOneChunk(svc, "rec1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PromoteToVaultAsync("rec1"));

        var status = svc.GetStatus("rec1");
        Assert.Equal("transcribed", status.State);
        Assert.Null(status.VaultDocId);
    }

    [Fact]
    public void GetStatus_Unknown_Throws()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        Assert.Throws<InvalidOperationException>(() => svc.GetStatus("nope"));
    }

    [Fact]
    public async Task DeleteRecording_RemovesLocalTranscript()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        await TranscribeOneChunk(svc, "rec1");
        Assert.NotNull(svc.LocalTranscriptPath("rec1"));

        svc.DeleteRecording("rec1");

        Assert.Null(svc.LocalTranscriptPath("rec1"));
        Assert.Throws<InvalidOperationException>(() => svc.GetStatus("rec1"));
        Assert.DoesNotContain(svc.ListAll(), i => i.RecordingId == "rec1");
    }

    [Fact]
    public async Task DeleteRecording_AfterPromote_KeepsVaultCopy()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        await TranscribeOneChunk(svc, "rec1");
        await svc.PromoteToVaultAsync("rec1");

        // The vault copy lives in the collection dir, separate from the local
        // transcripts root; deleting the transient transcript must not touch it.
        var collectionDir = Path.Combine(_tmp, "collection", "rec1");
        Assert.True(Directory.Exists(collectionDir));

        svc.DeleteRecording("rec1");

        Assert.Null(svc.LocalTranscriptPath("rec1"));
        Assert.True(Directory.Exists(collectionDir)); // vault copy preserved
    }

    [Fact]
    public void DeleteRecording_Unknown_Throws()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        Assert.Throws<InvalidOperationException>(() => svc.DeleteRecording("nope"));
    }

    [Fact]
    public void LocalTranscriptPath_BeforeTranscription_IsNull()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        svc.Register(Reg("rec1"));
        Assert.Null(svc.LocalTranscriptPath("rec1"));
    }

    [Fact]
    public void UpdateMeta_SetsTitleSubtitleSummary_AndPersists()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        svc.Register(Reg("rec1"));

        var item = svc.UpdateMeta("rec1", new RecordingMetaUpdate(
            Title: "Pricing call with Acme",
            Subtitle: "Q3 renewal",
            Summary: "Agreed to a 10 percent uplift; follow up next week."));

        Assert.Equal("Pricing call with Acme", item.Title);
        Assert.Equal("Q3 renewal", item.Subtitle);
        Assert.StartsWith("Agreed", item.Summary);

        // Persisted: a fresh listing reflects the same values.
        var listed = svc.ListAll().Single(i => i.RecordingId == "rec1");
        Assert.Equal("Pricing call with Acme", listed.Title);
        Assert.Equal("Q3 renewal", listed.Subtitle);
    }

    [Fact]
    public void UpdateMeta_NullFields_LeaveExistingUnchanged()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        svc.Register(Reg("rec1")); // title "Test Call"
        svc.UpdateMeta("rec1", new RecordingMetaUpdate(null, "sub", "sum"));

        // Title null -> keep "Test Call"; subtitle/summary unchanged when null.
        var item = svc.UpdateMeta("rec1", new RecordingMetaUpdate(Title: null, Subtitle: null, Summary: null));

        Assert.Equal("Test Call", item.Title);
        Assert.Equal("sub", item.Subtitle);
        Assert.Equal("sum", item.Summary);
    }

    [Fact]
    public void UpdateMeta_BlankTitle_IsIgnored()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        svc.Register(Reg("rec1"));
        var item = svc.UpdateMeta("rec1", new RecordingMetaUpdate(Title: "   ", Subtitle: null, Summary: null));
        Assert.Equal("Test Call", item.Title);
    }

    [Fact]
    public void UpdateMeta_Unknown_Throws()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        Assert.Throws<InvalidOperationException>(
            () => svc.UpdateMeta("nope", new RecordingMetaUpdate("t", null, null)));
    }

    [Fact]
    public void TranscriptsRoot_PointsAtRecordingsRoot()
    {
        var svc = NewService(new FakeTranscriber(), new FakeFiler());
        Assert.Equal(Path.Combine(_tmp, "recordings"), svc.TranscriptsRoot);
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

        await svc.CompleteAsync("e2e", manifest);  // enqueue
        await svc.ProcessRecordingAsync("e2e");     // run the queued job
        var status = svc.GetStatus("e2e");

        Assert.Equal("transcribed", status.State);
        Assert.Equal(clips.Count, status.ChunksTranscribed);

        // Promote into the (fake) vault so we can inspect the filed markdown.
        await svc.PromoteToVaultAsync("e2e");
        var md = File.ReadAllText(filer.Filed[0].TranscriptMarkdownPath);
        // At least one of the known Phase 0 company terms must survive cleanup.
        var hit = md.Contains("ConPTY", StringComparison.OrdinalIgnoreCase)
               || md.Contains("Avalonia", StringComparison.OrdinalIgnoreCase)
               || md.Contains("Soren", StringComparison.OrdinalIgnoreCase)
               || md.Contains("mindzie", StringComparison.OrdinalIgnoreCase);
        Assert.True(hit, "expected a known company term in the transcript markdown");
    }

    // ===== decoupling: audio + notes ingest never depends on transcription ==

    [Fact]
    public async Task Ingest_SucceedsAndPersistsNotes_EvenWhenTranscriberCannotBeBuilt()
    {
        // Simulate "transcription is unavailable" - e.g. no OpenAI key, or a not-yet-configured
        // transcription route on the Gateway: the transcriber FACTORY throws. Audio + notes
        // ingest MUST still fully succeed (register -> chunk -> complete) and the notes MUST be
        // persisted server-side. This is the core guarantee: the audio always uploads no matter
        // what happens to transcription.
        var svc = NewServiceWithFactory(
            () => throw new InvalidOperationException("OpenAI API key not set"),
            new FakeFiler());

        svc.Register(Reg("rec1"));
        var c0 = Encoding.UTF8.GetBytes("audio-0");
        await svc.StoreChunkAsync("rec1", 0, c0, Sha(c0)); // must not throw

        var manifest = new RecordingManifest("rec1", "Test Call", "dev-1",
            "2026-05-23T09:00:00Z", null, 16000, 1, "mp3",
            new() { new RecordingChunkInfo(0, "0000.mp3", 0, 60000, c0.Length, Sha(c0)) },
            new() { new RecordingNote(1234, "important note") });

        var queued = await svc.CompleteAsync("rec1", manifest); // must not throw
        Assert.Equal("queued", queued.State);

        // The notes were saved to manifest.json regardless of transcription being unavailable.
        var manifestPath = Path.Combine(_tmp, "recordings", "rec1", "manifest.json");
        Assert.True(File.Exists(manifestPath), "manifest.json (carrying the notes) must be persisted");
        Assert.Contains("important note", await File.ReadAllTextAsync(manifestPath));

        // Transcription itself fails gracefully - loud, recorded, and retryable. It does NOT
        // throw out of the worker, and the audio + notes are untouched.
        await svc.ProcessRecordingAsync("rec1");
        var status = svc.GetStatus("rec1");
        Assert.Equal("error", status.State);
        Assert.NotNull(status.NextRetryAtUtc); // will retry when a key/route appears
    }

    [Fact]
    public async Task Ingest_TranscriberBuiltLazily_RecoversOnceItBecomesAvailable()
    {
        // The factory throws on the FIRST job attempt (transcription not configured yet) and
        // succeeds on the next - exactly the "transcribe on the Gateway side afterwards if we
        // can" path. A failed build must NOT be cached, so the recording transcribes once the
        // engine is available, with no restart and no re-upload.
        var available = false;
        var svc = NewServiceWithFactory(
            () => available
                ? new FakeTranscriber()
                : throw new InvalidOperationException("transcription not configured yet"),
            new FakeFiler());

        await EnqueueOneChunk(svc, "rec1");

        await svc.ProcessRecordingAsync("rec1"); // transcription unavailable -> error + retry
        Assert.Equal("error", svc.GetStatus("rec1").State);

        available = true; // key/route configured in the meantime
        await svc.ProcessRecordingAsync("rec1"); // now succeeds against the same uploaded audio

        var status = svc.GetStatus("rec1");
        Assert.Equal("transcribed", status.State);
        Assert.NotNull(svc.LocalTranscriptPath("rec1"));
    }

    // ===== test helpers =====================================================

    /// <summary>Register, store one chunk, and enqueue (complete) WITHOUT
    /// processing, so a test can drive <see cref="RecordingIngestService.ProcessRecordingAsync"/>
    /// itself. Returns the queued status.</summary>
    private static async Task<RecordingStatusDto> EnqueueOneChunk(RecordingIngestService svc, string id)
    {
        svc.Register(Reg(id));
        var c0 = Encoding.UTF8.GetBytes("audio-0");
        await svc.StoreChunkAsync(id, 0, c0, Sha(c0));
        var manifest = new RecordingManifest(id, "Test Call", "dev-1",
            "2026-05-23T09:00:00Z", null, 16000, 1, "mp3",
            new() { new RecordingChunkInfo(0, "0000.mp3", 0, 60000, c0.Length, Sha(c0)) },
            new());
        return await svc.CompleteAsync(id, manifest);
    }

    private static async Task<bool> WaitForStateAsync(
        RecordingIngestService svc, string id, string state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (svc.GetStatus(id).State == state) return true;
            await Task.Delay(25);
        }
        return svc.GetStatus(id).State == state;
    }

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

    /// <summary>
    /// A transcriber that fails its first <c>failFirst</c> segment calls and then
    /// succeeds, for exercising per-chunk retry and whole-job retry. Counts calls
    /// so a test can assert how many transcribe attempts actually happened.
    /// </summary>
    private sealed class ScriptedTranscriber : IRecordingTranscriber
    {
        private readonly int _failFirst;
        private int _calls;
        public int Calls => _calls;
        public ScriptedTranscriber(int failFirst) => _failFirst = failFirst;

        public Task<string> TranscribeChunkAsync(byte[] audio, string contentType, string fileName, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n <= _failFirst)
                throw new InvalidOperationException($"simulated STT failure #{n}");
            return Task.FromResult($"segment {n} text");
        }

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
