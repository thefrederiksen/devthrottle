using System.Security.Cryptography;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Voice;

/// <summary>
/// Resumable, low-latency voice upload for the in-car Voice tab.
///
/// The browser records with MediaRecorder timeslice, so the audio arrives as an
/// ordered sequence of webm container fragments. We store each fragment to a
/// per-utterance temp dir as it lands (SHA256-idempotent, so a retried chunk is a
/// no-op), then on <see cref="CompleteAsync"/> concatenate them IN ORDER back into
/// one webm blob and transcribe it once. A fragment is NOT independently decodable
/// (only fragment 0 carries the container header), so reassemble-then-transcribe is
/// the correct model here.
///
/// Why this exists instead of the single-shot /voice/command: on spotty car LTE a
/// whole-blob POST that fails near the end re-sends the entire clip. Here, every
/// chunk that lands stays landed; a dropped connection resumes at the next missing
/// chunk. Transient only: the temp dir is deleted after transcription. No vault.
/// </summary>
public sealed class VoiceUtteranceService
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cc-director", "voice-utterances");

    private readonly SessionManager _sessionManager;
    private readonly AgentOptions _options;

    public VoiceUtteranceService(SessionManager sessionManager, AgentOptions options)
    {
        _sessionManager = sessionManager;
        _options = options;
    }

    /// <summary>True when an OpenAI key is configured.</summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.ResolveOpenAiKey());

    /// <summary>
    /// Begin a new utterance. If the caller supplies an id it must be a GUID (we use
    /// it as the temp folder name, so it must be path-safe); otherwise we mint one.
    /// Idempotent: re-registering the same id just ensures the folder exists.
    /// </summary>
    public string Register(string? id)
    {
        var uid = NormalizeId(id) ?? Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(DirFor(uid));
        FileLog.Write($"[VoiceUtterance] Register: id={uid}");
        return uid;
    }

    /// <summary>
    /// Store one chunk. Idempotent on (index, bytes): a chunk that already exists with
    /// the same SHA256 is accepted without rewriting, so retries after a network drop
    /// are free. A supplied SHA that does not match the bytes is rejected.
    /// </summary>
    public async Task StoreChunkAsync(string id, int index, byte[] bytes, string? expectedSha, CancellationToken ct = default)
    {
        var uid = NormalizeId(id) ?? throw new InvalidOperationException("invalid utterance id");
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
        if (File.Exists(path) && string.Equals(Sha256Hex(await File.ReadAllBytesAsync(path, ct)), actualSha, StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[VoiceUtterance] StoreChunk: id={uid} index={index} already present (idempotent)");
            return;
        }

        // Atomic write (temp + move) so a half-written chunk never poisons the assembly.
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, path, overwrite: true);
        FileLog.Write($"[VoiceUtterance] StoreChunk: id={uid} index={index} bytes={bytes.Length}");
    }

    /// <summary>
    /// Reassemble chunks 0..totalChunks-1 in order and transcribe. If any chunk is
    /// missing, returns Status="incomplete" with the missing indices so the client can
    /// re-send them (the partial upload is preserved, not discarded). Deletes the temp
    /// dir on success.
    /// </summary>
    public async Task<VoiceCommandResponse> CompleteAsync(
        string id, int totalChunks, string mime, string repoPath,
        string sessionId = "", string sessionName = "", CancellationToken ct = default)
    {
        var uid = NormalizeId(id) ?? throw new InvalidOperationException("invalid utterance id");
        var dir = DirFor(uid);
        if (!Directory.Exists(dir))
            return new VoiceCommandResponse { Status = "unknown_utterance", Error = $"no utterance {uid}" };

        var missing = new List<int>();
        for (var i = 0; i < totalChunks; i++)
            if (!File.Exists(ChunkPath(dir, i))) missing.Add(i);
        if (missing.Count > 0)
        {
            FileLog.Write($"[VoiceUtterance] Complete: id={uid} INCOMPLETE missing={string.Join(',', missing)}");
            return new VoiceCommandResponse
            {
                Status = "incomplete",
                Error = $"missing chunks: {string.Join(',', missing)}",
            };
        }

        var assembled = new MemoryStream();
        for (var i = 0; i < totalChunks; i++)
        {
            var part = await File.ReadAllBytesAsync(ChunkPath(dir, i), ct);
            assembled.Write(part, 0, part.Length);
        }
        assembled.Position = 0;
        var audioBytes = assembled.ToArray();
        FileLog.Write($"[VoiceUtterance] Complete: id={uid} chunks={totalChunks} totalBytes={assembled.Length}");

        var fileName = mime.Contains("webm", StringComparison.OrdinalIgnoreCase) ? "utterance.webm"
            : mime.Contains("mp4", StringComparison.OrdinalIgnoreCase) || mime.Contains("m4a", StringComparison.OrdinalIgnoreCase) ? "utterance.m4a"
            : mime.Contains("ogg", StringComparison.OrdinalIgnoreCase) ? "utterance.ogg"
            : "utterance.webm";

        var voice = new VoiceService(_sessionManager, _options);
        var resp = await voice.TranscribeAndCleanAsync(assembled, fileName, repoPath, ct);

        // Record the user side of this turn (audio + transcripts) so it can be
        // compared against the wingman's spoken reply later. Best-effort: only when
        // transcription succeeded and we know which session it belongs to.
        if (string.Equals(resp.Status, "ok", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(sessionId))
        {
            VoiceTurnLog.WriteInbound(
                sessionId, sessionName, audioBytes, fileName,
                resp.Transcript, resp.CleanedTranscript ?? "", resp.CleanupReason ?? "");
        }

        TryDelete(dir);
        return resp;
    }

    // ====== internals ===============================================================

    private static string DirFor(string uid) => Path.Combine(Root, uid);
    private static string ChunkPath(string dir, int index) => Path.Combine(dir, $"{index:D5}.part");

    /// <summary>Accept only GUID-shaped ids so the id can never escape the temp root.</summary>
    private static string? NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Guid.TryParse(id, out var g) ? g.ToString("N") : null;
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { FileLog.Write($"[VoiceUtterance] cleanup failed for {dir}: {ex.Message}"); }
    }
}
