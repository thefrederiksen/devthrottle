using System.Security.Cryptography;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Voice;

/// <summary>
/// Gateway-side resumable upload staging for the guaranteed audio-turn front door.
///
/// The phone records with a MediaRecorder timeslice, so the audio arrives as an ordered
/// sequence of container fragments. Each fragment is stored to a per-upload dir as it lands
/// (SHA256-idempotent, so a retried chunk after a dropped connection is a free no-op), then
/// <see cref="Assemble"/> concatenates them IN ORDER back into one blob. A fragment is NOT
/// independently decodable (only fragment 0 carries the container header), so
/// reassemble-then-forward is the correct model.
///
/// Why this lives on the Gateway (not reusing the Director's <c>/voice/utterance</c> path):
/// the Director's complete step transcribes and posts text to the session - a different flow
/// that produces no audio reply. Here the Gateway buffers the chunks itself and then feeds the
/// assembled clip into the existing async voice-turn worker, so the resumable upload and the
/// audio-reply turn become one pipeline behind a single Gateway URL.
///
/// Transient by design: the per-upload dir is deleted once the turn has been started.
/// </summary>
public sealed class VoiceUploadStore
{
    private readonly string _root;

    public VoiceUploadStore() : this(CcStorage.VoiceTurnUploads()) { }

    /// <summary>Test seam: stage under an explicit root instead of the shared storage dir.</summary>
    public VoiceUploadStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Begin (or re-open) an upload. The caller supplies a GUID id (it is also the
    /// idempotency key for the resulting turn); a missing/blank id mints a fresh one.
    /// Idempotent: re-registering the same id just ensures the folder exists.
    /// </summary>
    public string Register(string? uploadId)
    {
        var uid = NormalizeId(uploadId) ?? Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(DirFor(uid));
        FileLog.Write($"[VoiceUploadStore] Register: uploadId={uid}");
        return uid;
    }

    /// <summary>True once <see cref="Register"/> has staged this upload (and it has not been swept).</summary>
    public bool Exists(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        return uid is not null && Directory.Exists(DirFor(uid));
    }

    /// <summary>
    /// Store one chunk. Idempotent on (index, bytes): a chunk already on disk with the same
    /// SHA256 is accepted without rewriting, so retries are free. A supplied SHA that does not
    /// match the bytes is rejected so corruption never enters the assembly.
    /// </summary>
    public async Task StoreChunkAsync(string uploadId, int index, byte[] bytes, string? expectedSha, CancellationToken ct = default)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        if (index < 0) throw new InvalidOperationException("chunk index must be >= 0");
        if (bytes.Length == 0) throw new InvalidOperationException("empty chunk");

        var actualSha = Sha256Hex(bytes);
        if (!string.IsNullOrEmpty(expectedSha) &&
            !string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"chunk {index} SHA mismatch: header={expectedSha} actual={actualSha}");
        }

        var dir = DirFor(uid);
        Directory.CreateDirectory(dir);
        var path = ChunkPath(dir, index);

        // Idempotent: identical chunk already on disk -> no-op.
        if (File.Exists(path) &&
            string.Equals(Sha256Hex(await File.ReadAllBytesAsync(path, ct)), actualSha, StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[VoiceUploadStore] StoreChunk: uploadId={uid} index={index} already present (idempotent)");
            return;
        }

        // Atomic write (temp + move) so a half-written chunk never poisons the assembly.
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, path, overwrite: true);
        FileLog.Write($"[VoiceUploadStore] StoreChunk: uploadId={uid} index={index} bytes={bytes.Length}");
    }

    /// <summary>
    /// Reassemble chunks 0..totalChunks-1 in order. When a chunk is missing the partial upload
    /// is preserved (not discarded) and <see cref="AssembleResult.Missing"/> lists the indices the
    /// client must re-send. On success <see cref="AssembleResult.Audio"/> carries the full blob.
    /// </summary>
    public async Task<AssembleResult> AssembleAsync(string uploadId, int totalChunks, CancellationToken ct = default)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        var dir = DirFor(uid);
        if (!Directory.Exists(dir))
            return AssembleResult.Unknown();
        if (totalChunks <= 0)
            throw new InvalidOperationException("totalChunks must be > 0");

        // Completeness gate (issue #586 contract, applied here for the phone push-to-talk upload,
        // issue #592): every index 0..totalChunks-1 must be present AND non-empty. A missing OR
        // zero-byte chunk is "incomplete" - the result names the exact indices to re-send and NO
        // assembled clip is produced, so a truncated upload is refused, never transcribed.
        var missing = new List<int>();
        for (var i = 0; i < totalChunks; i++)
        {
            var path = ChunkPath(dir, i);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) missing.Add(i);
        }
        if (missing.Count > 0)
        {
            FileLog.Write($"[VoiceUploadStore] Assemble: uploadId={uid} INCOMPLETE missing={string.Join(',', missing)}");
            return AssembleResult.Incomplete(missing);
        }

        using var assembled = new MemoryStream();
        for (var i = 0; i < totalChunks; i++)
        {
            var part = await File.ReadAllBytesAsync(ChunkPath(dir, i), ct);
            assembled.Write(part, 0, part.Length);
        }
        var bytes = assembled.ToArray();
        FileLog.Write($"[VoiceUploadStore] Assemble: uploadId={uid} chunks={totalChunks} totalBytes={bytes.Length}");
        return AssembleResult.Ok(bytes);
    }

    /// <summary>Delete the staging dir for an upload. Best-effort; called once the turn is started.</summary>
    public void Delete(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return;
        try
        {
            var dir = DirFor(uid);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceUploadStore] Delete uploadId={uid} failed: {ex.Message}");
        }
    }

    // ====== internals ===============================================================

    private string DirFor(string uid) => Path.Combine(_root, uid);
    private static string ChunkPath(string dir, int index) => Path.Combine(dir, $"{index:D5}.part");

    /// <summary>Accept only GUID-shaped ids so the id can never escape the staging root.</summary>
    private static string? NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Guid.TryParse(id, out var g) ? g.ToString("N") : null;
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>Outcome of <see cref="VoiceUploadStore.AssembleAsync"/>.</summary>
public readonly record struct AssembleResult(string Status, byte[]? Audio, IReadOnlyList<int> Missing)
{
    public static AssembleResult Ok(byte[] audio) => new("ok", audio, Array.Empty<int>());
    public static AssembleResult Incomplete(IReadOnlyList<int> missing) => new("incomplete", null, missing);
    public static AssembleResult Unknown() => new("unknown_upload", null, Array.Empty<int>());
}
