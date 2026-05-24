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
public sealed class RecordingIngestService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _root;
    private readonly IRecordingTranscriber _transcriber;
    private readonly IVaultFiler _vaultFiler;
    private readonly string _collectionDir;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <param name="recordingsRoot">Root folder for received recordings.</param>
    /// <param name="transcriber">Transcription + cleanup engine.</param>
    /// <param name="vaultFiler">Vault filing back-end.</param>
    /// <param name="collectionDir">Folder where the final transcript + audio are placed for the vault.</param>
    public RecordingIngestService(
        string recordingsRoot,
        IRecordingTranscriber transcriber,
        IVaultFiler vaultFiler,
        string collectionDir)
    {
        _root = recordingsRoot;
        _transcriber = transcriber;
        _vaultFiler = vaultFiler;
        _collectionDir = collectionDir;
        Directory.CreateDirectory(_root);
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
            // existing result without re-transcribing. This makes it safe for
            // the phone to complete twice (in-app path + background worker).
            if (status.State == "transcribed")
            {
                FileLog.Write($"[RecordingIngestService] Complete: id={recordingId} already transcribed, returning existing result");
                return ToDto(status);
            }

            SaveManifest(recordingId, manifest);
            status.ChunksTotal = manifest.Chunks.Count;

            try
            {
                status.State = "transcribing";
                SaveStatus(status);

                var ext = CodecToExt(status.Codec);
                var (contentType, fileName) = CodecToHttp(status.Codec, ext);

                foreach (var chunk in manifest.Chunks.OrderBy(c => c.Index))
                {
                    var txtPath = Path.Combine(RecordingDir(recordingId), $"{chunk.Index:D4}.txt");
                    if (File.Exists(txtPath)) continue; // already transcribed (idempotent)

                    var audioPath = Path.Combine(RecordingDir(recordingId), $"{chunk.Index:D4}.{ext}");
                    if (!File.Exists(audioPath))
                        throw new InvalidOperationException($"Chunk {chunk.Index} audio missing at {audioPath}.");

                    var audio = await File.ReadAllBytesAsync(audioPath, ct);
                    var raw = await _transcriber.TranscribeChunkAsync(audio, contentType, fileName, ct);
                    await WriteAtomicAsync(txtPath, Encoding.UTF8.GetBytes(raw), ct);
                    FileLog.Write($"[RecordingIngestService] transcribed chunk {chunk.Index}: len={raw.Length}");
                }

                var assembledRaw = AssembleRaw(recordingId, manifest);

                status.State = "cleaning";
                SaveStatus(status);
                var cleanup = await _transcriber.CleanupAsync(assembledRaw, ct);
                status.Transcript = cleanup.Text;

                var markdown = BuildMarkdown(status, manifest, cleanup.Text, assembledRaw);
                var mdPath = Path.Combine(RecordingDir(recordingId), "transcript.md");
                await WriteAtomicAsync(mdPath, Encoding.UTF8.GetBytes(markdown), ct);

                // Transcripts are transient: they stay local and are NOT filed
                // into the vault here. The user promotes the ones worth keeping
                // via PromoteToVaultAsync.
                status.State = "transcribed";
                status.Error = null;
                SaveStatus(status);
                FileLog.Write($"[RecordingIngestService] Complete done: id={recordingId} (local transcript, not filed)");
                return ToDto(status);
            }
            catch (Exception ex)
            {
                // Record the failure for status polling, then rethrow so the
                // caller sees it. We do not swallow or substitute a result.
                status.State = "error";
                status.Error = ex.Message;
                SaveStatus(status);
                FileLog.Write($"[RecordingIngestService] Complete FAILED: id={recordingId}: {ex.Message}");
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
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
            s.RecordingId, s.Title, s.State, received, s.ChunksTotal, transcribed, s.VaultDocId, s.Error, s.Transcript);
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
    }
}
