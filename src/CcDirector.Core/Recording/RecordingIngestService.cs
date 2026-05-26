using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Recording;

/// <summary>
/// Server-side ingest for phone recordings. Receives finalized audio segments,
/// transcribes each through <see cref="IRecordingTranscriber"/>, then assembles
/// and cleans the full transcript into a local transcripts folder.
///
/// Transcripts are <b>transient by default</b>: they live only in the local
/// transcripts area and are never written to the vault automatically. The user
/// promotes the ones worth keeping into the vault explicitly via
/// <see cref="PromoteToVaultAsync"/>, which copies the markdown + audio into the
/// vault transcripts collection and files them through <see cref="IVaultFiler"/>.
/// Deleting a transient transcript (<see cref="DeleteRecording"/>) removes only
/// the local copy; a promoted vault copy is independent and is never touched.
///
/// On-disk layout, one directory per recording under the transcripts root:
/// <code>
///   &lt;root&gt;/&lt;recordingId&gt;/
///     status.json        ingest state machine + header fields
///     manifest.json      last manifest the phone sent (chunks + notes)
///     0000.m4a 0001.m4a  finalized audio segments (named by index)
///     0000.txt 0001.txt  per-segment raw transcript (idempotent cache)
///     transcript.md      final assembled + cleaned transcript
/// </code>
///
/// All operations are idempotent so the phone can retry safely: re-registering
/// is a no-op, re-uploading a segment with the same hash is a no-op,
/// re-completing reuses already-transcribed segments, and re-promoting a
/// recording already in the vault returns its existing result.
/// </summary>
public sealed class RecordingIngestService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // States that mean "this recording still has transcription work to do" and
    // is therefore eligible for the background worker to pick up.
    private const string StateQueued = "queued";
    private const string StateTranscribing = "transcribing";
    private const string StateCleaning = "cleaning";
    private const string StateTranscribed = "transcribed";
    private const string StateError = "error";

    private readonly string _root;
    private readonly IRecordingTranscriber _transcriber;
    private readonly IVaultFiler _vaultFiler;
    private readonly string _collectionDir;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    // Background worker: transcription runs here, decoupled from any HTTP request
    // so a long recording can never be killed by a request/proxy timeout. The
    // queue is the on-disk status.json state itself, so it survives a restart.
    private readonly int _maxChunkAttempts;
    private readonly int _maxJobAttempts;
    private readonly TimeSpan _chunkRetryDelay;
    private readonly TimeSpan _workerTick;
    private readonly CancellationTokenSource _workerCts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Task? _workerTask;

    /// <param name="recordingsRoot">Root folder for received recordings.</param>
    /// <param name="transcriber">Transcription + cleanup engine.</param>
    /// <param name="vaultFiler">Vault filing back-end.</param>
    /// <param name="collectionDir">Folder where the final transcript + audio are placed for the vault.</param>
    /// <param name="runWorker">Start the background transcription worker. Tests pass false to drive transcription deterministically via <see cref="ProcessRecordingAsync"/>.</param>
    /// <param name="maxChunkAttempts">How many times one segment is retried before the whole job fails.</param>
    /// <param name="maxJobAttempts">How many times a failed job is re-queued before it is left in error.</param>
    /// <param name="chunkRetryDelay">Base delay between segment retries (doubles each attempt). Tests pass a tiny value.</param>
    /// <param name="workerTick">How often the worker re-scans the queue for due retries.</param>
    public RecordingIngestService(
        string recordingsRoot,
        IRecordingTranscriber transcriber,
        IVaultFiler vaultFiler,
        string collectionDir,
        bool runWorker = true,
        int maxChunkAttempts = 3,
        int maxJobAttempts = 5,
        TimeSpan? chunkRetryDelay = null,
        TimeSpan? workerTick = null)
    {
        _root = recordingsRoot;
        _transcriber = transcriber;
        _vaultFiler = vaultFiler;
        _collectionDir = collectionDir;
        _maxChunkAttempts = Math.Max(1, maxChunkAttempts);
        _maxJobAttempts = Math.Max(1, maxJobAttempts);
        _chunkRetryDelay = chunkRetryDelay ?? TimeSpan.FromSeconds(2);
        _workerTick = workerTick ?? TimeSpan.FromSeconds(30);
        Directory.CreateDirectory(_root);

        if (runWorker)
            _workerTask = Task.Run(() => WorkerLoopAsync(_workerCts.Token));
    }

    public RecordingStatusDto Register(RecordingRegisterRequest req)
    {
        FileLog.Write($"[RecordingIngestService] Register: id={req.RecordingId}, title={req.Title}, codec={req.Codec}");
        if (string.IsNullOrWhiteSpace(req.RecordingId))
            throw new ArgumentException("RecordingId is required.", nameof(req));

        var dir = RecordingDir(req.RecordingId);
        Directory.CreateDirectory(dir);

        var status = LoadStatus(req.RecordingId) ?? new StatusModel();
        // Registration is idempotent: only seed header fields the first time.
        if (string.IsNullOrEmpty(status.RecordingId))
        {
            status.RecordingId = req.RecordingId;
            status.Title = req.Title;
            status.DeviceId = req.DeviceId;
            status.StartedAt = req.StartedAt;
            status.Codec = req.Codec;
            status.SampleRateHz = req.SampleRateHz;
            status.Channels = req.Channels;
            status.State = "receiving";
            SaveStatus(status);
        }
        return ToDto(status);
    }

    public async Task StoreChunkAsync(string recordingId, int index, byte[] bytes, string sha256, CancellationToken ct = default)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        var status = LoadStatus(recordingId)
            ?? throw new InvalidOperationException($"Recording '{recordingId}' is not registered.");

        var actual = Sha256Hex(bytes);
        if (!string.IsNullOrWhiteSpace(sha256) && !actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Chunk {index} hash mismatch: expected {sha256}, got {actual}.");

        var ext = CodecToExt(status.Codec);
        var chunkPath = Path.Combine(RecordingDir(recordingId), $"{index:D4}.{ext}");

        // Idempotent: if a byte-identical chunk is already here, do nothing.
        if (File.Exists(chunkPath) && Sha256Hex(await File.ReadAllBytesAsync(chunkPath, ct)) == actual)
        {
            FileLog.Write($"[RecordingIngestService] StoreChunk: id={recordingId} index={index} already present (idempotent)");
            return;
        }

        await WriteAtomicAsync(chunkPath, bytes, ct);
        FileLog.Write($"[RecordingIngestService] StoreChunk: id={recordingId} index={index} bytes={bytes.Length} sha={actual[..12]}");
    }

    /// <summary>
    /// Accepts a finished recording and queues it for transcription, returning
    /// immediately. The actual transcription runs in the background worker, so
    /// the caller (the phone's HTTP request) never holds a connection open for
    /// the length of a transcription and cannot be killed by a request or proxy
    /// timeout. The returned status reflects the queued state; the caller polls
    /// <see cref="GetStatus"/> to watch progress.
    ///
    /// Idempotent: an already-transcribed recording returns its existing result;
    /// one already queued or in flight is left alone; a previously failed one is
    /// re-queued for a fresh set of attempts.
    /// </summary>
    public async Task<RecordingStatusDto> CompleteAsync(string recordingId, RecordingManifest manifest, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(recordingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            FileLog.Write($"[RecordingIngestService] Complete: id={recordingId} chunks={manifest.Chunks.Count} notes={manifest.Notes.Count}");
            var status = LoadStatus(recordingId)
                ?? throw new InvalidOperationException($"Recording '{recordingId}' is not registered.");

            // Idempotent: a recording that already transcribed returns its
            // existing result without re-transcribing.
            if (status.State == StateTranscribed)
            {
                FileLog.Write($"[RecordingIngestService] Complete: id={recordingId} already transcribed, returning existing result");
                return ToDto(status);
            }

            SaveManifest(recordingId, manifest);
            status.ChunksTotal = manifest.Chunks.Count;

            // Already queued or actively being worked: leave the in-flight job
            // alone, just report current state. (A duplicate complete from the
            // phone must not reset progress or attempt counters.)
            if (status.State is StateQueued or StateTranscribing or StateCleaning)
            {
                FileLog.Write($"[RecordingIngestService] Complete: id={recordingId} already {status.State}, not re-queued");
                SaveStatus(status);
                return ToDto(status);
            }

            // Fresh enqueue (first complete, or a manual retry of a failed one):
            // reset the attempt budget and clear any pending retry schedule.
            status.State = StateQueued;
            status.Error = null;
            status.Attempts = 0;
            status.NextAttemptAtUtc = null;
            SaveStatus(status);
            FileLog.Write($"[RecordingIngestService] Complete: id={recordingId} queued for background transcription");

            SignalWorker();
            return ToDto(status);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Transcribes one queued recording to completion: transcribe every segment
    /// (each retried up to <c>maxChunkAttempts</c> times), assemble, clean, and
    /// write the local transcript. Idempotent and resumable - already-transcribed
    /// segments are skipped, so a job interrupted partway picks up where it left
    /// off. On a segment failure that exhausts its retries the whole job is
    /// recorded as failed; if attempts remain it is scheduled for a later retry.
    /// Does not throw on a transcription failure (the failure is recorded in the
    /// status for the worker and pollers); it only throws if the recording is
    /// unknown or the run is cancelled by shutdown.
    /// </summary>
    public async Task ProcessRecordingAsync(string recordingId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(recordingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var status = LoadStatus(recordingId)
                ?? throw new InvalidOperationException($"Recording '{recordingId}' is not registered.");

            // Idempotent: nothing to do if it already finished.
            if (status.State == StateTranscribed)
                return;

            var manifest = LoadManifest(recordingId)
                ?? throw new InvalidOperationException($"Manifest missing for recording '{recordingId}'.");
            status.ChunksTotal = manifest.Chunks.Count;

            try
            {
                status.State = StateTranscribing;
                SaveStatus(status);

                var ext = CodecToExt(status.Codec);
                var (contentType, fileName) = CodecToHttp(status.Codec, ext);

                foreach (var chunk in manifest.Chunks.OrderBy(c => c.Index))
                {
                    var txtPath = Path.Combine(RecordingDir(recordingId), $"{chunk.Index:D4}.txt");
                    if (File.Exists(txtPath)) continue; // already transcribed (idempotent, resumable)

                    var audioPath = Path.Combine(RecordingDir(recordingId), $"{chunk.Index:D4}.{ext}");
                    if (!File.Exists(audioPath))
                        throw new InvalidOperationException($"Chunk {chunk.Index} audio missing at {audioPath}.");

                    var audio = await File.ReadAllBytesAsync(audioPath, ct);
                    var raw = await TranscribeChunkWithRetryAsync(audio, contentType, fileName, chunk.Index, ct);
                    await WriteAtomicAsync(txtPath, Encoding.UTF8.GetBytes(raw), ct);
                    FileLog.Write($"[RecordingIngestService] transcribed chunk {chunk.Index}: len={raw.Length}");
                }

                var assembledRaw = AssembleRaw(recordingId, manifest);

                status.State = StateCleaning;
                SaveStatus(status);
                var cleanup = await _transcriber.CleanupAsync(assembledRaw, ct);
                status.Transcript = cleanup.Text;

                var markdown = BuildMarkdown(status, manifest, cleanup.Text, assembledRaw);
                var mdPath = Path.Combine(RecordingDir(recordingId), "transcript.md");
                await WriteAtomicAsync(mdPath, Encoding.UTF8.GetBytes(markdown), ct);

                // Transcripts are transient: they stay local and are NOT filed
                // into the vault here. The user promotes the ones worth keeping
                // via PromoteToVaultAsync.
                status.State = StateTranscribed;
                status.Error = null;
                status.NextAttemptAtUtc = null;
                SaveStatus(status);
                FileLog.Write($"[RecordingIngestService] Transcribe done: id={recordingId} (local transcript, not filed)");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a failure. Leave the state as-is (transcribing):
                // the already-written per-segment .txt files mean the next run
                // resumes from here. Do not burn an attempt for a shutdown.
                FileLog.Write($"[RecordingIngestService] Transcribe paused (shutdown): id={recordingId}");
                throw;
            }
            catch (Exception ex)
            {
                // A real transcription failure. Burn one job attempt and, if the
                // budget allows, schedule a retry the worker will pick up later.
                status.Attempts++;
                status.State = StateError;
                status.Error = ex.Message;
                status.NextAttemptAtUtc = status.Attempts < _maxJobAttempts
                    ? DateTime.UtcNow.Add(JobBackoff(status.Attempts)).ToString("O")
                    : null;
                SaveStatus(status);
                FileLog.Write($"[RecordingIngestService] Transcribe FAILED: id={recordingId} attempt={status.Attempts}/{_maxJobAttempts} "
                    + $"nextRetry={status.NextAttemptAtUtc ?? "(none - exhausted)"}: {ex.Message}");
                // Do not rethrow: there is no caller waiting on this, and the
                // worker must stay alive to process other recordings.
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Transcribes one segment, retrying a transient failure up to
    /// <c>maxChunkAttempts</c> times with a doubling backoff. A cancellation is
    /// never retried. If every attempt fails the last error is surfaced so the
    /// caller fails the whole job (which then becomes eligible for a job retry).
    /// </summary>
    private async Task<string> TranscribeChunkWithRetryAsync(
        byte[] audio, string contentType, string fileName, int chunkIndex, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= _maxChunkAttempts; attempt++)
        {
            try
            {
                return await _transcriber.TranscribeChunkAsync(audio, contentType, fileName, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                FileLog.Write($"[RecordingIngestService] chunk {chunkIndex} attempt {attempt}/{_maxChunkAttempts} failed: {ex.Message}");
                if (attempt < _maxChunkAttempts)
                    await Task.Delay(TimeSpan.FromTicks(_chunkRetryDelay.Ticks * (1L << (attempt - 1))), ct);
            }
        }
        throw new InvalidOperationException(
            $"Chunk {chunkIndex} failed after {_maxChunkAttempts} attempts: {last?.Message}", last);
    }

    // ===== background worker (the queue) ====================================

    /// <summary>
    /// Job-retry backoff: how long to wait before re-attempting a failed job,
    /// growing with the attempt count and capped at 30 minutes.
    /// </summary>
    private static TimeSpan JobBackoff(int attempts)
        => TimeSpan.FromMinutes(Math.Min(30, attempts * 5));

    private void SignalWorker()
    {
        // Release at most one permit (capacity 1): a pending wake already covers
        // "there is work", so extra signals collapse into the one tick.
        try { _wake.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
    }

    /// <summary>
    /// The transcription queue. Drains every recording that still has work, then
    /// sleeps until signalled by a new enqueue or the periodic tick (so failed
    /// jobs whose retry time has come are picked up without an external nudge).
    /// </summary>
    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        FileLog.Write($"[RecordingIngestService] worker started: maxChunkAttempts={_maxChunkAttempts}, maxJobAttempts={_maxJobAttempts}, tick={_workerTick}");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var id in FindEligibleRecordings())
                {
                    if (ct.IsCancellationRequested) break;
                    await ProcessRecordingAsync(id, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A scan/processing fault must never kill the worker.
                FileLog.Write($"[RecordingIngestService] worker tick error: {ex.Message}");
            }

            try { await _wake.WaitAsync(_workerTick, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
        FileLog.Write("[RecordingIngestService] worker stopped");
    }

    /// <summary>
    /// The queue contents, read from disk so it survives a restart: every
    /// recording that still has transcription work and is due now. That means
    /// queued jobs, jobs interrupted mid-run (transcribing/cleaning, e.g. by a
    /// crash), and failed jobs whose scheduled retry time has arrived and whose
    /// attempt budget is not exhausted. Oldest first.
    /// </summary>
    private IEnumerable<string> FindEligibleRecordings()
    {
        if (!Directory.Exists(_root)) yield break;
        var now = DateTime.UtcNow;
        var candidates = new List<(string id, string started)>();
        foreach (var dir in Directory.GetDirectories(_root))
        {
            StatusModel? s;
            try
            {
                var path = Path.Combine(dir, "status.json");
                if (!File.Exists(path)) continue;
                s = JsonSerializer.Deserialize<StatusModel>(File.ReadAllText(path), JsonOpts);
            }
            catch { continue; }
            if (s is null || string.IsNullOrEmpty(s.RecordingId)) continue;

            var eligible = s.State switch
            {
                StateQueued => true,
                // Interrupted mid-run (process died): resume it.
                StateTranscribing or StateCleaning => true,
                // Failed with attempts left and its retry time has come.
                StateError => s.Attempts < _maxJobAttempts
                              && DueNow(s.NextAttemptAtUtc, now),
                _ => false,
            };
            if (eligible) candidates.Add((s.RecordingId, s.StartedAt));
        }
        foreach (var c in candidates.OrderBy(c => c.started, StringComparer.Ordinal))
            yield return c.id;
    }

    private static bool DueNow(string? nextAttemptAtUtc, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(nextAttemptAtUtc)) return true; // no schedule => due
        return DateTime.TryParse(
            nextAttemptAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            ? at <= now
            : true;
    }

    public RecordingStatusDto GetStatus(string recordingId)
    {
        var status = LoadStatus(recordingId)
            ?? throw new InvalidOperationException($"Recording '{recordingId}' is not registered.");
        return ToDto(status);
    }

    /// <summary>Absolute path of the local transcripts root, for agent integration.</summary>
    public string TranscriptsRoot => _root;

    /// <summary>All recordings on this machine, newest first, for the Gateway transcripts page.</summary>
    public IReadOnlyList<RecordingListItem> ListAll()
    {
        if (!Directory.Exists(_root)) return Array.Empty<RecordingListItem>();
        var items = new List<RecordingListItem>();
        foreach (var dir in Directory.GetDirectories(_root))
        {
            var statusPath = Path.Combine(dir, "status.json");
            if (!File.Exists(statusPath)) continue;
            StatusModel? s;
            try { s = JsonSerializer.Deserialize<StatusModel>(File.ReadAllText(statusPath), JsonOpts); }
            catch { continue; }
            if (s is null) continue;
            items.Add(ToListItem(s));
        }
        return items.OrderByDescending(i => i.StartedAt, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Updates the human-readable metadata (title, subtitle, summary) a person
    /// or an external agent attaches to a transcript. Null fields are left
    /// unchanged; a blank title is ignored so a transcript never loses its
    /// title. Returns the updated list item. Throws if the recording is unknown.
    /// </summary>
    public RecordingListItem UpdateMeta(string recordingId, RecordingMetaUpdate update)
    {
        FileLog.Write($"[RecordingIngestService] UpdateMeta: id={recordingId}");
        var status = LoadStatus(recordingId)
            ?? throw new InvalidOperationException($"Recording '{recordingId}' is not registered.");

        if (!string.IsNullOrWhiteSpace(update.Title)) status.Title = update.Title.Trim();
        if (update.Subtitle is not null) status.Subtitle = update.Subtitle;
        if (update.Summary is not null) status.Summary = update.Summary;
        SaveStatus(status);
        return ToListItem(status);
    }

    private RecordingListItem ToListItem(StatusModel s)
    {
        int segments = s.ChunksTotal;
        long durationMs = 0;
        var manifestPath = Path.Combine(RecordingDir(s.RecordingId), "manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var m = JsonSerializer.Deserialize<RecordingManifest>(File.ReadAllText(manifestPath), JsonOpts);
                if (m is not null) { segments = m.Chunks.Count; durationMs = m.Chunks.Sum(c => c.DurationMs); }
            }
            catch { /* fall back to status counts for display only */ }
        }
        return new RecordingListItem(
            s.RecordingId, s.Title, s.StartedAt, s.State, segments, durationMs,
            !string.IsNullOrWhiteSpace(s.Transcript), LocalTranscriptPath(s.RecordingId),
            !string.IsNullOrWhiteSpace(s.VaultDocId), s.Subtitle, s.Summary);
    }

    /// <summary>
    /// Absolute path to the local transcript markdown for this recording, or
    /// null if it has not been transcribed yet. This is the transient on-disk
    /// copy, suitable for opening in code.
    /// </summary>
    public string? LocalTranscriptPath(string recordingId)
    {
        var path = Path.Combine(RecordingDir(recordingId), "transcript.md");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Copies a transcribed recording into the vault transcripts collection
    /// (markdown + audio) and files it through the vault. Idempotent: a
    /// recording already in the vault returns its existing result. Throws if the
    /// recording is unknown or has not finished transcribing.
    /// </summary>
    public async Task<RecordingStatusDto> PromoteToVaultAsync(string recordingId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(recordingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            FileLog.Write($"[RecordingIngestService] PromoteToVault: id={recordingId}");
            var status = LoadStatus(recordingId)
                ?? throw new InvalidOperationException($"Recording '{recordingId}' is not registered.");

            // Idempotent: already in the vault.
            if (!string.IsNullOrWhiteSpace(status.VaultDocId))
            {
                FileLog.Write($"[RecordingIngestService] PromoteToVault: id={recordingId} already in vault ({status.VaultDocId})");
                return ToDto(status);
            }

            if (status.State != "transcribed")
                throw new InvalidOperationException(
                    $"Recording '{recordingId}' is not ready to promote (state: {status.State}).");

            var mdPath = Path.Combine(RecordingDir(recordingId), "transcript.md");
            if (!File.Exists(mdPath))
                throw new InvalidOperationException($"Transcript markdown missing at {mdPath}.");

            var manifest = LoadManifest(recordingId)
                ?? throw new InvalidOperationException($"Manifest missing for recording '{recordingId}'.");

            var ext = CodecToExt(status.Codec);
            var markdown = await File.ReadAllTextAsync(mdPath, ct);
            var (collectionMdPath, audioPaths) = await PlaceInCollectionAsync(recordingId, status, manifest, markdown, ext, ct);

            var docId = await _vaultFiler.FileTranscriptAsync(new VaultFilingRequest(
                Title: status.Title,
                TranscriptMarkdownPath: collectionMdPath,
                AudioFilePaths: audioPaths,
                RecordedDateUtc: status.StartedAt), ct);

            status.VaultDocId = docId;
            SaveStatus(status);
            FileLog.Write($"[RecordingIngestService] PromoteToVault done: id={recordingId} vaultDoc={docId}");
            return ToDto(status);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Deletes the transient local transcript: its directory under the
    /// transcripts root (audio segments, per-segment text, status, manifest,
    /// markdown). A promoted copy in the vault is independent and is never
    /// touched here. Throws if no such local transcript exists.
    /// </summary>
    public void DeleteRecording(string recordingId)
    {
        FileLog.Write($"[RecordingIngestService] DeleteRecording: id={recordingId}");
        var recDir = RecordingDir(recordingId);
        if (!Directory.Exists(recDir))
            throw new InvalidOperationException($"Recording '{recordingId}' was not found.");

        Directory.Delete(recDir, recursive: true);
        _locks.TryRemove(recordingId, out _);
        FileLog.Write($"[RecordingIngestService] DeleteRecording done: id={recordingId}");
    }

    /// <summary>The cleaned transcript text, or the assembled markdown as a fallback.</summary>
    public string? GetTranscript(string recordingId)
    {
        var s = LoadStatus(recordingId);
        if (s is null) return null;
        if (!string.IsNullOrWhiteSpace(s.Transcript)) return s.Transcript;
        var md = Path.Combine(RecordingDir(recordingId), "transcript.md");
        return File.Exists(md) ? File.ReadAllText(md) : null;
    }

    /// <summary>Path + content type of one audio segment, for browser playback. Null if absent.</summary>
    public (string path, string contentType)? GetAudioFile(string recordingId, int index)
    {
        var s = LoadStatus(recordingId);
        if (s is null) return null;
        var ext = CodecToExt(s.Codec);
        var (contentType, _) = CodecToHttp(s.Codec, ext);
        var path = Path.Combine(RecordingDir(recordingId), $"{index:D4}.{ext}");
        return File.Exists(path) ? (path, contentType) : null;
    }

    // ===== assembly + markdown ==============================================

    private string AssembleRaw(string recordingId, RecordingManifest manifest)
    {
        var sb = new StringBuilder();
        foreach (var chunk in manifest.Chunks.OrderBy(c => c.Index))
        {
            var txtPath = Path.Combine(RecordingDir(recordingId), $"{chunk.Index:D4}.txt");
            if (!File.Exists(txtPath)) continue;
            var text = File.ReadAllText(txtPath).Trim();
            if (text.Length == 0) continue;
            sb.Append('[').Append(FormatOffset(chunk.StartMs)).Append("] ").AppendLine(text);
        }
        return sb.ToString().Trim();
    }

    private static string BuildMarkdown(StatusModel status, RecordingManifest manifest, string cleaned, string raw)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(status.Title);
        sb.AppendLine();
        sb.Append("- Recorded: ").AppendLine(status.StartedAt);
        if (!string.IsNullOrWhiteSpace(manifest.EndedAt))
            sb.Append("- Ended: ").AppendLine(manifest.EndedAt);
        sb.Append("- Device: ").AppendLine(status.DeviceId);
        sb.Append("- Segments: ").AppendLine(manifest.Chunks.Count.ToString());
        var totalMs = manifest.Chunks.Sum(c => c.DurationMs);
        sb.Append("- Duration: ").AppendLine(FormatOffset(totalMs));
        sb.AppendLine();

        if (manifest.Notes.Count > 0)
        {
            sb.AppendLine("## Notes");
            sb.AppendLine();
            foreach (var note in manifest.Notes.OrderBy(n => n.TMs))
                sb.Append("- [").Append(FormatOffset(note.TMs)).Append("] ").AppendLine(note.Text);
            sb.AppendLine();
        }

        sb.AppendLine("## Transcript");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(cleaned) ? "(empty)" : cleaned);
        sb.AppendLine();

        sb.AppendLine("## Raw transcript");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(string.IsNullOrWhiteSpace(raw) ? "(empty)" : raw);
        sb.AppendLine("```");
        return sb.ToString();
    }

    private async Task<(string mdPath, IReadOnlyList<string> audioPaths)> PlaceInCollectionAsync(
        string recordingId, StatusModel status, RecordingManifest manifest, string markdown, string ext, CancellationToken ct)
    {
        // Each recording gets its own subfolder in the transcripts collection so
        // the markdown and its audio travel together inside the vault.
        var dest = Path.Combine(_collectionDir, recordingId);
        Directory.CreateDirectory(dest);

        var safeTitle = MakeFileSafe(status.Title);
        var mdPath = Path.Combine(dest, safeTitle + ".md");
        await WriteAtomicAsync(mdPath, Encoding.UTF8.GetBytes(markdown), ct);

        var audioPaths = new List<string>();
        foreach (var chunk in manifest.Chunks.OrderBy(c => c.Index))
        {
            var src = Path.Combine(RecordingDir(recordingId), $"{chunk.Index:D4}.{ext}");
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(dest, $"{chunk.Index:D4}.{ext}");
            File.Copy(src, dst, overwrite: true);
            audioPaths.Add(dst);
        }
        return (mdPath, audioPaths);
    }

    // ===== persistence ======================================================

    private string RecordingDir(string id) => Path.Combine(_root, MakeFileSafe(id));

    private StatusModel? LoadStatus(string id)
    {
        var path = Path.Combine(RecordingDir(id), "status.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<StatusModel>(File.ReadAllText(path), JsonOpts);
    }

    private void SaveStatus(StatusModel status)
    {
        var path = Path.Combine(RecordingDir(status.RecordingId), "status.json");
        File.WriteAllText(path, JsonSerializer.Serialize(status, JsonOpts));
    }

    private void SaveManifest(string id, RecordingManifest manifest)
    {
        var path = Path.Combine(RecordingDir(id), "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOpts));
    }

    private RecordingManifest? LoadManifest(string id)
    {
        var path = Path.Combine(RecordingDir(id), "manifest.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<RecordingManifest>(File.ReadAllText(path), JsonOpts);
    }

    private RecordingStatusDto ToDto(StatusModel s)
    {
        var dir = RecordingDir(s.RecordingId);
        var received = Directory.Exists(dir)
            ? Directory.GetFiles(dir).Count(f => IsAudio(f))
            : 0;
        var transcribed = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.txt").Length
            : 0;
        return new RecordingStatusDto(
            s.RecordingId, s.Title, s.State, received, s.ChunksTotal, transcribed, s.VaultDocId, s.Error, s.Transcript,
            s.Attempts, s.NextAttemptAtUtc);
    }

    private static bool IsAudio(string path)
    {
        var e = Path.GetExtension(path).ToLowerInvariant();
        return e is ".m4a" or ".mp3" or ".wav" or ".webm" or ".ogg" or ".aac";
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    // ===== helpers ==========================================================

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string CodecToExt(string codec)
    {
        var c = (codec ?? "").ToLowerInvariant();
        if (c.Contains("m4a") || c.Contains("aac") || c.Contains("mp4")) return "m4a";
        if (c.Contains("mp3") || c.Contains("mpeg")) return "mp3";
        if (c.Contains("wav")) return "wav";
        if (c.Contains("webm")) return "webm";
        if (c.Contains("ogg") || c.Contains("opus")) return "ogg";
        return "m4a";
    }

    private static (string contentType, string fileName) CodecToHttp(string codec, string ext) => ext switch
    {
        "mp3" => ("audio/mpeg", "chunk.mp3"),
        "wav" => ("audio/wav", "chunk.wav"),
        "webm" => ("audio/webm", "chunk.webm"),
        "ogg" => ("audio/ogg", "chunk.ogg"),
        _ => ("audio/mp4", "chunk.m4a"),
    };

    private static string FormatOffset(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private static string MakeFileSafe(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        var cleaned = sb.ToString().Trim();
        return cleaned.Length == 0 ? "recording" : cleaned;
    }

    private sealed class StatusModel
    {
        public string RecordingId { get; set; } = "";
        public string Title { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string StartedAt { get; set; } = "";
        public string Codec { get; set; } = "aac-m4a";
        public int SampleRateHz { get; set; }
        public int Channels { get; set; }
        public string State { get; set; } = "receiving";
        public int ChunksTotal { get; set; }
        public string? VaultDocId { get; set; }
        public string? Error { get; set; }
        public string? Transcript { get; set; }
        public string? Subtitle { get; set; }
        public string? Summary { get; set; }

        // Queue/retry bookkeeping for the background transcription worker.
        public int Attempts { get; set; }
        public string? NextAttemptAtUtc { get; set; }
    }

    public void Dispose()
    {
        _workerCts.Cancel();
        try { _workerTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { FileLog.Write($"[RecordingIngestService] worker shutdown error: {ex.Message}"); }
        _workerCts.Dispose();
        _wake.Dispose();
    }
}
